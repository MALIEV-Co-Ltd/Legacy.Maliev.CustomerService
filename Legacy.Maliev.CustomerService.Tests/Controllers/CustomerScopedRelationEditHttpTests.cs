using System.Data.Common;
using System.Net;
using System.Net.Http.Json;
using System.Text;
using System.Text.Json;
using Legacy.Maliev.CustomerService.Application.Models;
using Legacy.Maliev.CustomerService.Application.Interfaces;
using Legacy.Maliev.CustomerService.Domain;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
using Microsoft.Extensions.Caching.Distributed;
using Microsoft.Extensions.DependencyInjection;
using Npgsql;

namespace Legacy.Maliev.CustomerService.Tests.Controllers;

[CollectionDefinition("Customer scoped relation edits", DisableParallelization = true)]
public sealed class CustomerScopedRelationEditCollection;

[Collection("Customer scoped relation edits")]
public sealed class CustomerScopedRelationEditHttpTests(CustomerDetailAuthorityFixture fixture) : IClassFixture<CustomerDetailAuthorityFixture>
{
    [Theory]
    [InlineData("company")]
    [InlineData("billing")]
    [InlineData("shipping")]
    public async Task ExistingBoundEdit_UpdatesSharedMetadataEvictsLinkedCachesAndRejectsStaleForm(string kind)
    {
        await ResetSharedAsync();
        using var host = fixture.Start();
        using var client = fixture.Client(host, "relation-editor");
        using var initial = await ReadAsync(client, ReadPath(kind));
        Assert.Equal(HttpStatusCode.OK, initial.StatusCode);
        Assert.Equal("no-store", initial.Headers.CacheControl!.ToString());
        using var json = JsonDocument.Parse(await initial.Content.ReadAsStringAsync());
        Assert.Equal(1, json.RootElement.GetProperty("CustomerId").GetInt32());
        Assert.Equal(1, json.RootElement.GetProperty("RelationId").GetInt32());
        foreach (var name in new[] { "Email", "InternalRemark", "PasswordHash", "SecurityStamp", "ConcurrencyStamp", "Revision", "customerId" })
            Assert.False(json.RootElement.TryGetProperty(name, out _));
        await SeedCacheAsync(host.Services);
        using var saved = await PutAsync(client, kind, initial.Headers.ETag!.ToString(), "Updated relation");
        Assert.Equal(HttpStatusCode.NoContent, saved.StatusCode);
        Assert.Equal("1", saved.Headers.GetValues("X-Relation-Id").Single());
        await AssertCachesAsync(host.Services, false);
        using var reread = await ReadAsync(client, ReadPath(kind));
        Assert.Equal(saved.Headers.ETag, reread.Headers.ETag);
        await SeedCacheAsync(host.Services);
        var before = await SnapshotAsync();
        using var stale = await PutAsync(client, kind, initial.Headers.ETag!.ToString(), "Stale replacement");
        Assert.Equal(HttpStatusCode.PreconditionFailed, stale.StatusCode);
        Assert.Equal(before, await SnapshotAsync());
        await AssertCachesAsync(host.Services, true);
        await using var db = fixture.Context();
        var target = await db.Customers.AsNoTracking().SingleAsync(row => row.Id == 1);
        Assert.Equal("detail@example.test", target.Email);
        Assert.Equal("Before", target.FirstName);
        Assert.Equal("staff-only", target.InternalRemark);
        Assert.Equal(1, target.CompanyId);
        Assert.Equal(1, target.BillingAddressId);
        Assert.Equal(1, target.ShippingAddressId);
        Assert.Equal(2, await db.Companies.CountAsync());
        Assert.Equal(2, await db.Addresses.CountAsync());
    }

    [Theory]
    [InlineData("company")]
    [InlineData("billing")]
    [InlineData("shipping")]
    public async Task MissingBinding_CreatesAndAttachesOnlySelectedRelationAtomically(string kind)
    {
        await ResetSharedAsync();
        await using (var db = fixture.Context())
        {
            var target = await db.Customers.SingleAsync(row => row.Id == 1);
            if (kind == "company") target.CompanyId = null;
            else if (kind == "billing") target.BillingAddressId = null;
            else target.ShippingAddressId = null;
            await db.SaveChangesAsync();
        }
        using var host = fixture.Start();
        using var client = fixture.Client(host, "relation-editor");
        using var read = await ReadAsync(client, ReadPath(kind, "new"));
        Assert.Equal(HttpStatusCode.OK, read.StatusCode);
        using var saved = await PutAsync(client, kind, read.Headers.ETag!.ToString(), "New relation", "new");
        Assert.Equal(HttpStatusCode.NoContent, saved.StatusCode);
        Assert.Equal("3", saved.Headers.GetValues("X-Relation-Id").Single());
        await using var check = fixture.Context();
        var targetAfter = await check.Customers.AsNoTracking().SingleAsync(row => row.Id == 1);
        var relationId = kind == "company" ? targetAfter.CompanyId : kind == "billing" ? targetAfter.BillingAddressId : targetAfter.ShippingAddressId;
        Assert.Equal(3, relationId);
        if (kind != "company") Assert.Equal(1, targetAfter.CompanyId);
        if (kind != "billing") Assert.Equal(1, targetAfter.BillingAddressId);
        if (kind != "shipping") Assert.Equal(1, targetAfter.ShippingAddressId);
        Assert.Equal("detail@example.test", targetAfter.Email);
        Assert.Equal("staff-only", targetAfter.InternalRemark);
        using var reread = await ReadAsync(client, ReadPath(kind, "3"));
        Assert.Equal(saved.Headers.ETag, reread.Headers.ETag);
        var before = await SnapshotAsync();
        using var replay = await PutAsync(client, kind, read.Headers.ETag!.ToString(), "Duplicate new relation", "new");
        Assert.Equal(HttpStatusCode.PreconditionFailed, replay.StatusCode);
        Assert.Equal(before, await SnapshotAsync());
    }

    [Theory]
    [InlineData("company", false)]
    [InlineData("billing", false)]
    [InlineData("shipping", false)]
    [InlineData("company", true)]
    [InlineData("billing", true)]
    [InlineData("shipping", true)]
    public async Task MetadataOnlyOrProfileChange_MakesCapturedRelationVersionStale(string kind, bool profileChange)
    {
        await ResetSharedAsync();
        using var host = fixture.Start();
        using var client = fixture.Client(host, "relation-editor");
        using var read = await ReadAsync(client, ReadPath(kind));
        await using (var db = fixture.Context())
        {
            if (profileChange)
            {
                var target = await db.Customers.SingleAsync(row => row.Id == 1);
                target.Telephone = "another profile writer";
            }
            else if (kind == "company") (await db.Companies.SingleAsync(row => row.Id == 1)).Name = "another metadata writer";
            else (await db.Addresses.SingleAsync(row => row.Id == 1)).City = "another metadata writer";
            await db.SaveChangesAsync();
        }
        var before = await SnapshotAsync();
        using var response = await PutAsync(client, kind, read.Headers.ETag!.ToString(), "Stale replacement");
        Assert.Equal(HttpStatusCode.PreconditionFailed, response.StatusCode);
        Assert.Equal(before, await SnapshotAsync());
    }

    [Theory]
    [InlineData("company")]
    [InlineData("billing")]
    [InlineData("shipping")]
    public async Task UnboundRouteIdentifier_Is404EvenWithValidVersionAndBroadStaffPermissions(string kind)
    {
        await ResetSharedAsync();
        using var host = fixture.Start();
        using var client = fixture.Client(host, "relation-editor");
        using var read = await ReadAsync(client, ReadPath(kind));
        using var wrongRead = await ReadAsync(client, ReadPath(kind, "2"));
        Assert.Equal(HttpStatusCode.NotFound, wrongRead.StatusCode);
        var before = await SnapshotAsync();
        using var wrongWrite = await PutAsync(client, kind, read.Headers.ETag!.ToString(), "Unbound metadata", "2");
        Assert.Equal(HttpStatusCode.NotFound, wrongWrite.StatusCode);
        Assert.Equal(before, await SnapshotAsync());
    }

    [Theory]
    [InlineData("company", "anonymous", HttpStatusCode.Unauthorized)]
    [InlineData("billing", "anonymous", HttpStatusCode.Unauthorized)]
    [InlineData("shipping", "anonymous", HttpStatusCode.Unauthorized)]
    [InlineData("company", "relation-no-customer-update", HttpStatusCode.Forbidden)]
    [InlineData("billing", "relation-no-customer-update", HttpStatusCode.Forbidden)]
    [InlineData("shipping", "relation-no-customer-update", HttpStatusCode.Forbidden)]
    [InlineData("company", "relation-no-metadata-update", HttpStatusCode.Forbidden)]
    [InlineData("billing", "relation-no-metadata-update", HttpStatusCode.Forbidden)]
    [InlineData("shipping", "relation-no-metadata-update", HttpStatusCode.Forbidden)]
    public async Task BothCustomerAndMetadataAuthority_AreRequiredBeforeMutation(string kind, string authority, HttpStatusCode expected)
    {
        await ResetSharedAsync();
        using var host = fixture.Start();
        using var reader = fixture.Client(host, "relation-editor");
        using var read = await ReadAsync(reader, ReadPath(kind));
        var before = await SnapshotAsync();
        using var writer = fixture.Client(host, authority);
        using var response = await PutAsync(writer, kind, read.Headers.ETag!.ToString(), "Unauthorized");
        Assert.Equal(expected, response.StatusCode);
        Assert.Equal(before, await SnapshotAsync());
    }

    [Theory]
    [InlineData("company", null, 428)]
    [InlineData("billing", null, 428)]
    [InlineData("shipping", null, 428)]
    [InlineData("company", "*", 400)]
    [InlineData("billing", "W/\"weak\"", 400)]
    [InlineData("shipping", "malformed", 400)]
    public async Task MissingOrMalformedPrecondition_IsRefusedWithoutMutation(string kind, string? version, int expected)
    {
        await ResetSharedAsync();
        using var host = fixture.Start();
        using var client = fixture.Client(host, "relation-editor");
        var before = await SnapshotAsync();
        using var response = await PutAsync(client, kind, version, "Invalid precondition");
        Assert.Equal((HttpStatusCode)expected, response.StatusCode);
        Assert.Equal(before, await SnapshotAsync());
    }

    [Theory]
    [InlineData("company")]
    [InlineData("billing")]
    [InlineData("shipping")]
    public async Task UnknownRelationIdBodyField_Is400NotMassAssignment(string kind)
    {
        await ResetSharedAsync();
        using var host = fixture.Start();
        using var client = fixture.Client(host, "relation-editor");
        using var read = await ReadAsync(client, ReadPath(kind));
        var payload = JsonSerializer.SerializeToNode(Payload(kind, "Unknown field"))!.AsObject();
        payload["RelationId"] = 2;
        var before = await SnapshotAsync();
        using var request = new HttpRequestMessage(HttpMethod.Put, WritePath(kind)) { Content = JsonContent.Create(payload) };
        request.Headers.TryAddWithoutValidation("If-Match", read.Headers.ETag!.ToString());
        request.Headers.TryAddWithoutValidation("X-Customer-If-Match", read.Headers.GetValues("X-Customer-ETag").Single());
        using var response = await client.SendAsync(request);
        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        Assert.Equal(before, await SnapshotAsync());
    }

    [Theory]
    [InlineData("company")]
    [InlineData("billing")]
    [InlineData("shipping")]
    public async Task ConcurrentRelationSaves_ExactlyOneCommits(string kind)
    {
        await ResetSharedAsync();
        using var host = fixture.Start();
        using var client = fixture.Client(host, "relation-editor");
        using var read = await ReadAsync(client, ReadPath(kind));
        var version = read.Headers.ETag!.ToString();
        var responses = await Task.WhenAll(PutAsync(client, kind, version, "Writer A"), PutAsync(client, kind, version, "Writer B"));
        try
        {
            Assert.Single(responses, item => item.StatusCode == HttpStatusCode.NoContent);
            Assert.Single(responses, item => item.StatusCode == HttpStatusCode.PreconditionFailed);
        }
        finally { foreach (var response in responses) response.Dispose(); }
    }

    [Theory]
    [InlineData("company")]
    [InlineData("billing")]
    [InlineData("shipping")]
    public async Task LostCommitAcknowledgment_IsUncertain500NoReplayAndEvictsEveryLinkedCache(string kind)
    {
        await ResetSharedAsync();
        var fault = new LostCommitAcknowledgment();
        using var host = fixture.Start(interceptor: fault);
        using var client = fixture.Client(host, "relation-editor");
        using var read = await ReadAsync(client, ReadPath(kind));
        await SeedCacheAsync(host.Services);
        using var response = await PutAsync(client, kind, read.Headers.ETag!.ToString(), "Committed once");
        Assert.Equal(HttpStatusCode.InternalServerError, response.StatusCode);
        Assert.Equal(1, fault.Commits);
        using var changed = await ReadAsync(client, ReadPath(kind));
        Assert.NotEqual(read.Headers.ETag, changed.Headers.ETag);
        await AssertCachesAsync(host.Services, false);
        Assert.DoesNotContain("synthetic-relation-ack", await response.Content.ReadAsStringAsync(), StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("company")]
    [InlineData("billing")]
    [InlineData("shipping")]
    public async Task CallerCancellationAtCommit_PreservesTokenAndEvictsEveryLinkedCacheWithoutReplay(string kind)
    {
        await ResetSharedAsync();
        using var cancellation = new CancellationTokenSource();
        var fault = new LostCommitAcknowledgment(cancellation);
        using var host = fixture.Start(interceptor: fault);
        using var client = fixture.Client(host, "relation-editor");
        using var read = await ReadAsync(client, ReadPath(kind));
        await SeedCacheAsync(host.Services);
        using var scope = host.Services.CreateScope();
        var service = scope.ServiceProvider.GetRequiredService<ICustomerRelationService>();
        var version = read.Headers.ETag!.ToString();
        var error = await Assert.ThrowsAnyAsync<OperationCanceledException>(async () =>
        {
            if (kind == "company") await service.SaveCompanyAsync(1, 1, version, Revision(read), (CustomerCompanyRelationRequest)Payload(kind, "Canceled after commit"), cancellation.Token);
            else await service.SaveAddressAsync(1, kind == "billing" ? CustomerRelationKind.Billing : CustomerRelationKind.Shipping,
                1, version, Revision(read), (CustomerAddressRelationRequest)Payload(kind, "Canceled after commit"), cancellation.Token);
        });
        Assert.Equal(cancellation.Token, error.CancellationToken);
        Assert.Equal(1, fault.Commits);
        using var changed = await ReadAsync(client, ReadPath(kind));
        Assert.NotEqual(read.Headers.ETag, changed.Headers.ETag);
        await AssertCachesAsync(host.Services, false);
    }

    [Theory]
    [InlineData("company")]
    [InlineData("billing")]
    [InlineData("shipping")]
    public async Task InvalidReplacement_Is400WithoutChangingAttachmentOrMetadata(string kind)
    {
        await ResetSharedAsync();
        using var host = fixture.Start();
        using var client = fixture.Client(host, "relation-editor");
        using var read = await ReadAsync(client, ReadPath(kind));
        var payload = JsonSerializer.SerializeToNode(Payload(kind, "Invalid fields"))!.AsObject();
        if (kind == "company") payload["Name"] = "";
        else if (kind == "billing") payload["CountryId"] = 0;
        else payload["AddressLine1"] = "";
        var before = await SnapshotAsync();
        using var request = new HttpRequestMessage(HttpMethod.Put, WritePath(kind)) { Content = JsonContent.Create(payload) };
        request.Headers.TryAddWithoutValidation("If-Match", read.Headers.ETag!.ToString());
        request.Headers.TryAddWithoutValidation("X-Customer-If-Match", read.Headers.GetValues("X-Customer-ETag").Single());
        using var response = await client.SendAsync(request);
        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        Assert.Equal(before, await SnapshotAsync());
    }

    [Theory]
    [InlineData("company")]
    [InlineData("billing")]
    [InlineData("shipping")]
    public async Task MissingCustomer_Returns404OnReadAndWriteWithoutMetadataCreation(string kind)
    {
        await ResetSharedAsync();
        using var host = fixture.Start();
        using var client = fixture.Client(host, "relation-editor");
        var before = await SnapshotAsync();
        using var read = await ReadAsync(client, ReadPath(kind).Replace("/customers/1/", "/customers/999/", StringComparison.Ordinal));
        Assert.Equal(HttpStatusCode.NotFound, read.StatusCode);
        using var request = new HttpRequestMessage(HttpMethod.Put, WritePath(kind, "new").Replace("/customers/1/", "/customers/999/", StringComparison.Ordinal))
        { Content = JsonContent.Create(Payload(kind, "Missing customer")) };
        request.Headers.TryAddWithoutValidation("If-Match", '"' + new string('0', 64) + '"');
        request.Headers.TryAddWithoutValidation("X-Customer-If-Match", "\"00000000\"");
        using var write = await client.SendAsync(request);
        Assert.Equal(HttpStatusCode.NotFound, write.StatusCode);
        Assert.Equal(before, await SnapshotAsync());
    }

    [Theory]
    [InlineData("company", "billing")]
    [InlineData("billing", "shipping")]
    [InlineData("shipping", "company")]
    public async Task DifferentRelationKindToken_Is412EvenWhenAddressBindingsShareOneRow(string kind, string otherKind)
    {
        await ResetSharedAsync();
        using var host = fixture.Start();
        using var client = fixture.Client(host, "relation-editor");
        using var other = await ReadAsync(client, ReadPath(otherKind));
        var before = await SnapshotAsync();
        using var response = await PutAsync(client, kind, other.Headers.ETag!.ToString(), "Wrong relation kind");
        Assert.Equal(HttpStatusCode.PreconditionFailed, response.StatusCode);
        Assert.Equal(before, await SnapshotAsync());
    }

    private readonly Dictionary<string, string> capturedRevisions = new(StringComparer.Ordinal);

    [Theory]
    [InlineData("company")]
    [InlineData("billing")]
    [InlineData("shipping")]
    public async Task ConcurrentProfileRelationMove_BeforeCustomerFence_Is412AndChangesNoMetadata(string kind)
    {
        await ResetSharedAsync();
        var gate = new CustomerFenceGate(held: false);
        using var host = fixture.Start(interceptor: gate);
        using var normalHost = fixture.Start();
        using var writer = fixture.Client(host, "relation-editor");
        using var mover = fixture.Client(normalHost);
        using var read = await ReadAsync(writer, ReadPath(kind));
        var movingProfile = await ProfileForMoveAsync(kind);
        var save = PutAsync(writer, kind, read.Headers.ETag!.ToString(), "Stale bound metadata");
        try
        {
            await gate.Entered.Task.WaitAsync(TimeSpan.FromSeconds(30));
            using var moved = await mover.PutAsJsonAsync("/customers/1", movingProfile);
            Assert.Equal(HttpStatusCode.NoContent, moved.StatusCode);
            var afterMove = await SnapshotAsync();
            gate.Release.TrySetResult();
            using var rejected = await save;
            Assert.Equal(HttpStatusCode.PreconditionFailed, rejected.StatusCode);
            Assert.Equal(afterMove, await SnapshotAsync());
        }
        finally
        {
            gate.Release.TrySetResult();
            if (!save.IsCompleted) (await save).Dispose();
        }
    }

    [Theory]
    [InlineData("company")]
    [InlineData("billing")]
    [InlineData("shipping")]
    public async Task CustomerFence_BlocksOrdinaryRelationMoveUntilSelectedMetadataCommit(string kind)
    {
        await ResetSharedAsync();
        var gate = new CustomerFenceGate(held: true);
        using var host = fixture.Start(interceptor: gate);
        using var normalHost = fixture.Start();
        using var writer = fixture.Client(host, "relation-editor");
        using var mover = fixture.Client(normalHost);
        using var read = await ReadAsync(writer, ReadPath(kind));
        var movingProfile = await ProfileForMoveAsync(kind);
        var save = PutAsync(writer, kind, read.Headers.ETag!.ToString(), "Committed before move");
        Task<HttpResponseMessage>? move = null;
        try
        {
            await gate.Entered.Task.WaitAsync(TimeSpan.FromSeconds(30));
            move = mover.PutAsJsonAsync("/customers/1", movingProfile);
            await using var observer = fixture.Context();
            using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(30));
            while (await observer.Database.SqlQueryRaw<int>(
                "SELECT COUNT(*)::int AS \"Value\" FROM pg_stat_activity WHERE wait_event_type = 'Lock' AND query LIKE '%UPDATE \"Customer\"%'")
                .SingleAsync(timeout.Token) == 0)
                await Task.Delay(25, timeout.Token);
            Assert.False(move.IsCompleted);
            gate.Release.TrySetResult();
            using var saved = await save;
            using var moved = await move;
            Assert.Equal(HttpStatusCode.NoContent, saved.StatusCode);
            Assert.Equal(HttpStatusCode.NoContent, moved.StatusCode);
            await using var db = fixture.Context();
            var customer = await db.Customers.SingleAsync(row => row.Id == 1);
            Assert.Equal(2, kind == "company" ? customer.CompanyId : kind == "billing" ? customer.BillingAddressId : customer.ShippingAddressId);
            Assert.Equal("staff-only", customer.InternalRemark);
            if (kind == "company")
            {
                Assert.Equal("Committed before move", (await db.Companies.SingleAsync(row => row.Id == 1)).Name);
                Assert.Equal("Unrelated company", (await db.Companies.SingleAsync(row => row.Id == 2)).Name);
            }
            else
            {
                Assert.Equal("Committed before move", (await db.Addresses.SingleAsync(row => row.Id == 1)).AddressLine1);
                Assert.Equal("Unrelated road", (await db.Addresses.SingleAsync(row => row.Id == 2)).AddressLine1);
            }
        }
        finally
        {
            gate.Release.TrySetResult();
            if (!save.IsCompleted) (await save).Dispose();
            if (move is not null && !move.IsCompleted) (await move).Dispose();
        }
    }

    private async Task<UpsertCustomerRequest> ProfileForMoveAsync(string kind)
    {
        await using var db = fixture.Context();
        var customer = await db.Customers.AsNoTracking().SingleAsync(row => row.Id == 1);
        return new(customer.FirstName, customer.LastName, customer.Telephone, customer.Mobile, customer.Fax, customer.Email,
            customer.DateOfBirth, kind == "company" ? 2 : customer.CompanyId,
            kind == "billing" ? 2 : customer.BillingAddressId, kind == "shipping" ? 2 : customer.ShippingAddressId);
    }

    private sealed class CustomerFenceGate(bool held) : DbCommandInterceptor
    {
        private int entries;
        internal TaskCompletionSource Entered { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        internal TaskCompletionSource Release { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        private bool IsFirstCustomerFence(DbCommand command) => command.CommandText.Contains("FROM \"Customer\"", StringComparison.Ordinal) &&
            command.CommandText.Contains("FOR UPDATE", StringComparison.Ordinal) && Interlocked.Increment(ref entries) == 1;

        public override async ValueTask<InterceptionResult<DbDataReader>> ReaderExecutingAsync(DbCommand command, CommandEventData eventData,
            InterceptionResult<DbDataReader> result, CancellationToken cancellationToken = default)
        {
            if (!held && IsFirstCustomerFence(command))
            {
                Entered.TrySetResult();
                await Release.Task.WaitAsync(cancellationToken);
            }
            return result;
        }

        public override async ValueTask<DbDataReader> ReaderExecutedAsync(DbCommand command, CommandExecutedEventData eventData,
            DbDataReader result, CancellationToken cancellationToken = default)
        {
            if (held && IsFirstCustomerFence(command))
            {
                Entered.TrySetResult();
                await Release.Task.WaitAsync(cancellationToken);
            }
            return result;
        }
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task ConfirmedRelationWrites_CarryCustomerRevisionWithoutRefreshingOriginalMetadataVersions(bool missing)
    {
        await ResetSharedAsync();
        await using (var db = fixture.Context())
        {
            var customer = await db.Customers.SingleAsync(row => row.Id == 1);
            customer.CompanyId = missing ? null : 1;
            customer.BillingAddressId = missing ? null : 1;
            customer.ShippingAddressId = missing ? null : 2;
            await db.SaveChangesAsync();
        }
        using var host = fixture.Start();
        using var client = fixture.Client(host, "relation-editor");
        var kinds = new[] { "company", "billing", "shipping" };
        var original = new Dictionary<string, string>();
        var relationIds = new Dictionary<string, int>();
        string? customerVersion = null;
        foreach (var kind in kinds)
        {
            using var read = await ReadAsync(client, ReadPath(kind, missing ? "new" : kind == "shipping" ? "2" : "1"));
            Assert.Equal(HttpStatusCode.OK, read.StatusCode);
            original[kind] = read.Headers.ETag!.ToString();
            var captured = read.Headers.GetValues("X-Customer-ETag").Single();
            if (customerVersion is not null) Assert.Equal(customerVersion, captured);
            customerVersion = captured;
        }
        var initialCustomerVersion = customerVersion;
        foreach (var kind in kinds)
        {
            using var saved = await PutAsync(client, kind, original[kind], $"Confirmed {kind}",
                missing ? "new" : kind == "shipping" ? "2" : "1", customerVersion);
            Assert.Equal(HttpStatusCode.NoContent, saved.StatusCode);
            var next = saved.Headers.GetValues("X-Customer-ETag").Single();
            Assert.NotEqual(customerVersion, next);
            customerVersion = next;
            relationIds[kind] = int.Parse(saved.Headers.GetValues("X-Relation-Id").Single(), System.Globalization.CultureInfo.InvariantCulture);
        }
        var body = new UpsertCustomerRequest("After metadata", "Fixture", null, null, null, "detail@example.test", null,
            relationIds["company"], relationIds["billing"], relationIds["shipping"]);
        var before = await SnapshotAsync();
        using (var staleRequest = new HttpRequestMessage(HttpMethod.Put, "/customers/1/versioned") { Content = JsonContent.Create(body) })
        {
            staleRequest.Headers.TryAddWithoutValidation("If-Match", initialCustomerVersion);
            using var stale = await client.SendAsync(staleRequest);
            Assert.Equal(HttpStatusCode.PreconditionFailed, stale.StatusCode);
            Assert.Equal(before, await SnapshotAsync());
        }
        using var profileRequest = new HttpRequestMessage(HttpMethod.Put, "/customers/1/versioned") { Content = JsonContent.Create(body) };
        profileRequest.Headers.TryAddWithoutValidation("If-Match", customerVersion);
        using var profileSaved = await client.SendAsync(profileRequest);
        Assert.Equal(HttpStatusCode.NoContent, profileSaved.StatusCode);
        await using var check = fixture.Context();
        var stored = await check.Customers.SingleAsync(row => row.Id == 1);
        Assert.Equal("After metadata", stored.FirstName);
        Assert.Equal("staff-only", stored.InternalRemark);
        Assert.Equal(relationIds["company"], stored.CompanyId);
        Assert.Equal(relationIds["billing"], stored.BillingAddressId);
        Assert.Equal(relationIds["shipping"], stored.ShippingAddressId);
    }

    [Fact]
    public async Task SharedAddressWrite_ReturnsOnlyKnownCommittedCounterpartValidatorForIntentionalNextEdit()
    {
        await ResetSharedAsync();
        using var host = fixture.Start();
        using var client = fixture.Client(host, "relation-editor");
        using var billing = await ReadAsync(client, ReadPath("billing"));
        using var shipping = await ReadAsync(client, ReadPath("shipping"));
        using var saved = await PutAsync(client, "billing", billing.Headers.ETag!.ToString(), "Billing then shipping");
        Assert.Equal(HttpStatusCode.NoContent, saved.StatusCode);
        Assert.Equal(saved.Headers.ETag!.ToString(), saved.Headers.GetValues("X-Billing-ETag").Single());
        var nextCustomer = saved.Headers.GetValues("X-Customer-ETag").Single();
        var nextShipping = saved.Headers.GetValues("X-Shipping-ETag").Single();
        Assert.NotEqual(shipping.Headers.ETag!.ToString(), nextShipping);
        var before = await SnapshotAsync();
        using var staleShipping = await PutAsync(client, "shipping", shipping.Headers.ETag.ToString(), "Old state", customerVersion: nextCustomer);
        Assert.Equal(HttpStatusCode.PreconditionFailed, staleShipping.StatusCode);
        Assert.Equal(before, await SnapshotAsync());
        using var intentionalNext = await PutAsync(client, "shipping", nextShipping, "Intentional shipping edit", customerVersion: nextCustomer);
        Assert.Equal(HttpStatusCode.NoContent, intentionalNext.StatusCode);
        await using var db = fixture.Context();
        Assert.Equal("Intentional shipping edit", (await db.Addresses.SingleAsync(row => row.Id == 1)).AddressLine1);
        Assert.Equal(1, (await db.Customers.SingleAsync(row => row.Id == 1)).BillingAddressId);
    }

    [Theory]
    [InlineData("company", null, 428)]
    [InlineData("billing", null, 428)]
    [InlineData("shipping", null, 428)]
    [InlineData("company", "*", 400)]
    [InlineData("billing", "*", 400)]
    [InlineData("shipping", "*", 400)]
    [InlineData("company", "\"00000000\"", 412)]
    [InlineData("billing", "\"00000000\"", 412)]
    [InlineData("shipping", "\"00000000\"", 412)]
    public async Task SeparateCustomerPrecondition_IsRequiredAndFenced(string kind, string? customerVersion, int status)
    {
        await ResetSharedAsync();
        using var host = fixture.Start();
        using var client = fixture.Client(host, "relation-editor");
        using var read = await ReadAsync(client, ReadPath(kind));
        var before = await SnapshotAsync();
        using var request = new HttpRequestMessage(HttpMethod.Put, WritePath(kind)) { Content = JsonContent.Create(Payload(kind, "Invalid customer version")) };
        request.Headers.TryAddWithoutValidation("If-Match", read.Headers.ETag!.ToString());
        if (customerVersion is not null) request.Headers.TryAddWithoutValidation("X-Customer-If-Match", customerVersion);
        using var response = await client.SendAsync(request);
        Assert.Equal((HttpStatusCode)status, response.StatusCode);
        Assert.Equal(before, await SnapshotAsync());
    }

    private async Task<HttpResponseMessage> ReadAsync(HttpClient client, string path)
    {
        var response = await client.GetAsync(path);
        if (response.IsSuccessStatusCode && response.Headers.ETag is not null)
            capturedRevisions[response.Headers.ETag.ToString()] = response.Headers.GetValues("X-Customer-ETag").Single();
        return response;
    }

    private static uint Revision(HttpResponseMessage response) => uint.Parse(
        response.Headers.GetValues("X-Customer-ETag").Single().AsSpan(1, 8), System.Globalization.NumberStyles.AllowHexSpecifier,
        System.Globalization.CultureInfo.InvariantCulture);

    private async Task ResetSharedAsync()
    {
        await fixture.ResetAsync();
        await using var db = fixture.Context();
        db.Customers.Add(new Customer { FirstName = "Shared", LastName = "Fixture", Email = "shared@example.test", CompanyId = 1, BillingAddressId = 1, ShippingAddressId = 1 });
        db.Customers.Add(new Customer
        {
            FirstName = "Other",
            LastName = "Fixture",
            Email = "other@example.test",
            Company = new Company { Name = "Unrelated company" },
            BillingAddress = new Address { AddressLine1 = "Unrelated road", CountryId = 764 },
        });
        await db.SaveChangesAsync();
    }

    private static string ReadPath(string kind, string relationId = "1") => kind == "company"
        ? $"/customers/1/relations/company/{relationId}" + (relationId == "new" ? "" : "/edit")
        : $"/customers/1/relations/{kind}/address/{relationId}" + (relationId == "new" ? "" : "/edit");

    private static string WritePath(string kind, string relationId = "1") => kind == "company"
        ? $"/customers/1/relations/company/{relationId}/versioned"
        : $"/customers/1/relations/{kind}/address/{relationId}/versioned";

    private static object Payload(string kind, string value) => kind == "company"
        ? new CustomerCompanyRelationRequest(value, null, "  literal registrar  ")
        : new CustomerAddressRelationRequest(null, value, null, "  literal city  ", null, null, 764);

    private async Task<HttpResponseMessage> PutAsync(HttpClient client, string kind, string? version, string value, string relationId = "1", string? customerVersion = null)
    {
        using var request = new HttpRequestMessage(HttpMethod.Put, WritePath(kind, relationId)) { Content = JsonContent.Create(Payload(kind, value)) };
        if (version is not null) request.Headers.TryAddWithoutValidation("If-Match", version);
        request.Headers.TryAddWithoutValidation("X-Customer-If-Match", customerVersion ?? (version is not null && capturedRevisions.TryGetValue(version, out var captured) ? captured : "\"00000000\""));
        return await client.SendAsync(request);
    }

    private static async Task SeedCacheAsync(IServiceProvider services)
    {
        var cache = services.GetRequiredService<IDistributedCache>();
        for (var id = 1; id <= 3; id++) await cache.SetAsync($"customer:{id}", Encoding.UTF8.GetBytes("sentinel"));
    }

    private static async Task AssertCachesAsync(IServiceProvider services, bool retained)
    {
        var cache = services.GetRequiredService<IDistributedCache>();
        for (var id = 1; id <= 3; id++)
        {
            if (retained || id == 3) Assert.NotNull(await cache.GetAsync($"customer:{id}"));
            else Assert.Null(await cache.GetAsync($"customer:{id}"));
        }
    }

    private async Task<string> SnapshotAsync()
    {
        await using var db = fixture.Context();
        var customer = await db.Customers.AsNoTracking().OrderBy(row => row.Id)
            .Select(row => new
            {
                row.Id,
                row.FirstName,
                row.LastName,
                row.FullName,
                row.Email,
                row.Telephone,
                row.Mobile,
                row.Fax,
                row.DateOfBirth,
                row.CompanyId,
                row.BillingAddressId,
                row.ShippingAddressId,
                row.InternalRemark,
                row.CreatedDate,
                row.ModifiedDate,
            }).ToArrayAsync();
        var companies = await db.Companies.AsNoTracking().OrderBy(row => row.Id).Select(row => new
        {
            row.Id,
            row.Name,
            row.TaxNumber,
            row.Registrar,
            row.CreatedDate,
            row.ModifiedDate,
        }).ToArrayAsync();
        var addresses = await db.Addresses.AsNoTracking().OrderBy(row => row.Id).Select(row => new
        {
            row.Id,
            row.Building,
            row.AddressLine1,
            row.AddressLine2,
            row.City,
            row.State,
            row.PostalCode,
            row.CountryId,
            row.CreatedDate,
            row.ModifiedDate,
        }).ToArrayAsync();
        return JsonSerializer.Serialize(new { customer, companies, addresses });
    }

    private sealed class LostCommitAcknowledgment(CancellationTokenSource? cancellation = null) : DbTransactionInterceptor
    {
        private int commits;
        internal int Commits => Volatile.Read(ref commits);

        public override Task TransactionCommittedAsync(DbTransaction transaction, TransactionEndEventData eventData, CancellationToken cancellationToken = default)
        {
            if (Interlocked.Increment(ref commits) == 1)
            {
                if (cancellation is not null)
                {
                    cancellation.Cancel();
                    throw new OperationCanceledException(cancellation.Token);
                }
                throw new NpgsqlException("synthetic-relation-ack", new TimeoutException("synthetic-relation-ack"));
            }
            return Task.CompletedTask;
        }
    }
}
