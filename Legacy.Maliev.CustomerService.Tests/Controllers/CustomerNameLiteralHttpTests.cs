using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.EntityFrameworkCore;

namespace Legacy.Maliev.CustomerService.Tests.Controllers;

/// <summary>Checks literal customer names, keyed creation replay, provider retries, lost acknowledgments, and ordinary optional contact projections.</summary>
/// <param name="fixture">The shared authority fixture supplying isolated customer graph setup, HTTP hosts, signed clients, and cache observations.</param>
[Collection("Customer profile lifecycle")]
public sealed class CustomerNameLiteralHttpTests(CustomerDetailAuthorityFixture fixture)
    : IClassFixture<CustomerDetailAuthorityFixture>
{
    private const string First = " \tสมชาย\u00a0 ";
    private const string Last = "\u00a0 ใจดี\t ";

    /// <summary>Checks literal name persistence and readback during creation and optional keyed replay.</summary>
    /// <param name="keyed">Whether to include an Idempotency-Key and verify that a repeated create returns the same customer.</param>
    /// <returns>A task that completes after asserting exact names and location, one durable keyed operation when applicable, and restored original state after deletion.</returns>
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Create_PreservesLiteralNamesInStorageWireAndKeyedReplay(bool keyed)
    {
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(90));
        var token = timeout.Token;
        await fixture.ResetAsync();
        await using var host = fixture.Start();
        using var client = fixture.Client(host, "customer-lifecycle");
        var before = await fixture.SnapshotAsync();
        var key = Guid.NewGuid();
        using var request = new HttpRequestMessage(HttpMethod.Post, "/customers/")
        {
            Content = JsonContent.Create(Payload(First, Last)),
        };
        if (keyed) request.Headers.Add("Idempotency-Key", key.ToString());
        using var created = await client.SendAsync(request, token);
        Assert.True(created.StatusCode == HttpStatusCode.Created,
            $"keyed={keyed}: actual {created.StatusCode}; body {await created.Content.ReadAsStringAsync(token)}");
        using var wire = JsonDocument.Parse(await created.Content.ReadAsStringAsync(token));
        AssertNames(wire.RootElement, First, Last);
        var id = wire.RootElement.GetProperty("Id").GetInt32();
        Assert.True(id > 1);
        Assert.EndsWith($"/customers/{id}", created.Headers.Location!.ToString(), StringComparison.OrdinalIgnoreCase);
        await AssertStoredAsync(id, First, Last, token);
        using var read = await client.GetAsync(created.Headers.Location, token);
        Assert.Equal(HttpStatusCode.OK, read.StatusCode);
        using var readJson = JsonDocument.Parse(await read.Content.ReadAsStringAsync(token));
        AssertNames(readJson.RootElement, First, Last);
        if (keyed)
        {
            using var replayRequest = new HttpRequestMessage(HttpMethod.Post, "/customers/")
            {
                Content = JsonContent.Create(Payload(First, Last)),
            };
            replayRequest.Headers.Add("Idempotency-Key", key.ToString());
            using var replay = await client.SendAsync(replayRequest, token);
            Assert.Equal(HttpStatusCode.Created, replay.StatusCode);
            using var replayJson = JsonDocument.Parse(await replay.Content.ReadAsStringAsync(token));
            AssertNames(replayJson.RootElement, First, Last);
            Assert.Equal(id, replayJson.RootElement.GetProperty("Id").GetInt32());
            await using var check = fixture.Context();
            Assert.Equal(2, await check.Customers.CountAsync(token));
            Assert.Equal(1, await check.CustomerCreateOperations.CountAsync(token));
        }
        using var deleted = await client.DeleteAsync($"/customers/{id}", token);
        Assert.Equal(HttpStatusCode.NoContent, deleted.StatusCode);
        Assert.Equal(before, await fixture.SnapshotAsync());
    }

    /// <summary>Checks literal name updates, cache eviction, retained relations, and optional stale-version refusal.</summary>
    /// <param name="versioned">Whether to use the versioned update route with If-Match instead of an ordinary update.</param>
    /// <returns>A task that completes after asserting literal storage and readback, advanced revision, unchanged relations, and refusal of a stale version when applicable.</returns>
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Update_PreservesLiteralNamesAndInvalidatesOldCacheWithoutChangingRelations(bool versioned)
    {
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(90));
        var token = timeout.Token;
        await fixture.ResetAsync();
        await using var host = fixture.Start();
        using var client = fixture.Client(host);
        await fixture.SeedOldAsync(host);
        var relations = await RelationsAsync(token);
        await using var db = fixture.Context();
        var original = await db.Customers.AsNoTracking().SingleAsync(token);
        var oldRevision = await db.Customers.Select(row => EF.Property<uint>(row, "xmin")).SingleAsync(token);
        using var read = await client.GetAsync("/customers/1/versioned", token);
        Assert.Equal(HttpStatusCode.OK, read.StatusCode);
        var etag = read.Headers.ETag!.ToString();
        using var request = new HttpRequestMessage(HttpMethod.Put, versioned ? "/customers/1/versioned" : "/customers/1/")
        {
            Content = JsonContent.Create(Payload(First, Last)),
        };
        if (versioned) request.Headers.TryAddWithoutValidation("If-Match", etag);
        using var updated = await client.SendAsync(request, token);
        Assert.Equal(HttpStatusCode.NoContent, updated.StatusCode);
        Assert.False(await fixture.CacheExistsAsync());
        await AssertStoredAsync(1, First, Last, token);
        var stored = await db.Customers.AsNoTracking().SingleAsync(token);
        Assert.Equal(original.CreatedDate, stored.CreatedDate);
        Assert.Equal(original.InternalRemark, stored.InternalRemark);
        Assert.NotNull(stored.ModifiedDate);
        if (original.ModifiedDate.HasValue) Assert.True(stored.ModifiedDate >= original.ModifiedDate);
        Assert.NotEqual(oldRevision, await db.Customers.Select(row => EF.Property<uint>(row, "xmin")).SingleAsync(token));
        Assert.Equal(relations, await RelationsAsync(token));
        using var afterRead = await client.GetAsync("/customers/1", token);
        Assert.Equal(HttpStatusCode.OK, afterRead.StatusCode);
        using var afterJson = JsonDocument.Parse(await afterRead.Content.ReadAsStringAsync(token));
        AssertNames(afterJson.RootElement, First, Last);
        if (versioned)
        {
            var snapshot = await fixture.SnapshotAsync();
            using var staleRequest = new HttpRequestMessage(HttpMethod.Put, "/customers/1/versioned")
            {
                Content = JsonContent.Create(Payload("Stale", "Writer")),
            };
            staleRequest.Headers.TryAddWithoutValidation("If-Match", etag);
            using var stale = await client.SendAsync(staleRequest, token);
            Assert.Equal(HttpStatusCode.PreconditionFailed, stale.StatusCode);
            Assert.Equal(snapshot, await fixture.SnapshotAsync());
            await AssertStoredAsync(1, First, Last, token);
        }
    }

    /// <summary>Checks that padded customer names cannot bypass authentication or write permissions.</summary>
    /// <param name="authority">The anonymous, denied-permission, invalid-signature, or expired-token scenario used to construct the client.</param>
    /// <param name="status">The expected unauthorized or forbidden response for the selected actor.</param>
    /// <returns>A task that completes after asserting refused creation and update statuses, unchanged graph, and retained cache.</returns>
    [Theory]
    [InlineData("anonymous", HttpStatusCode.Unauthorized)]
    [InlineData("denied", HttpStatusCode.Forbidden)]
    [InlineData("wrong-signature", HttpStatusCode.Unauthorized)]
    [InlineData("expired", HttpStatusCode.Unauthorized)]
    public async Task PaddedNames_DoNotBypassAuthority(string authority, HttpStatusCode status)
    {
        await fixture.ResetAsync();
        await using var host = fixture.Start();
        using var client = fixture.Client(host, authority);
        client.Timeout = TimeSpan.FromSeconds(90);
        await fixture.SeedOldAsync(host);
        var before = await fixture.SnapshotAsync();
        using var created = await client.PostAsJsonAsync("/customers/", Payload(First, Last));
        using var updated = await client.PutAsJsonAsync("/customers/1", Payload(First, Last));
        using var versioned = await client.PutAsJsonAsync("/customers/1/versioned", Payload(First, Last));
        foreach (var response in new[] { created, updated, versioned }) Assert.Equal(status, response.StatusCode);
        Assert.Equal(before, await fixture.SnapshotAsync());
        Assert.True(await fixture.CacheExistsAsync());
    }

    /// <summary>Checks that whitespace-only first or last names remain invalid on all customer write paths.</summary>
    /// <param name="first">The submitted first name, containing only whitespace in the first-name rejection case.</param>
    /// <param name="last">The submitted last name, containing only whitespace in the last-name rejection case.</param>
    /// <returns>A task that completes after asserting bad-request responses and unchanged graph.</returns>
    [Theory]
    [InlineData(" \t\u00a0", "Valid")]
    [InlineData("Valid", " \t\u00a0")]
    public async Task WhitespaceOnlyNames_RemainRejected(string first, string last)
    {
        await fixture.ResetAsync();
        await using var host = fixture.Start();
        using var client = fixture.Client(host, "customer-lifecycle");
        client.Timeout = TimeSpan.FromSeconds(90);
        var before = await fixture.SnapshotAsync();
        using var created = await client.PostAsJsonAsync("/customers/", Payload(first, last));
        using var updated = await client.PutAsJsonAsync("/customers/1", Payload(first, last));
        using var versioned = await client.PutAsJsonAsync("/customers/1/versioned", Payload(first, last));
        foreach (var response in new[] { created, updated, versioned }) Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        Assert.Equal(before, await fixture.SnapshotAsync());
    }

    /// <summary>Checks keyed creation and replay through the actual host retry-enabled provider while preserving literal names.</summary>
    /// <returns>A task that completes after asserting retry-enabled configuration, equal replay identifiers, literal names, and one durable create receipt.</returns>
    [Fact]
    public async Task KeyedCreate_RealHostRetryProvider_PreservesLiteralNamesAndReplay()
    {
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(90));
        var token = timeout.Token;
        await fixture.ResetAsync();
        await using var host = fixture.Start();
        await using var scope = Microsoft.Extensions.DependencyInjection.ServiceProviderServiceExtensions.CreateAsyncScope(host.Services);
        var db = Microsoft.Extensions.DependencyInjection.ServiceProviderServiceExtensions.GetRequiredService<Legacy.Maliev.CustomerService.Data.CustomerDbContext>(scope.ServiceProvider);
        Assert.True(db.Database.CreateExecutionStrategy().RetriesOnFailure);
        var service = Microsoft.Extensions.DependencyInjection.ServiceProviderServiceExtensions.GetRequiredService<Legacy.Maliev.CustomerService.Api.CustomerCreateReplayService>(scope.ServiceProvider);
        var request = JsonSerializer.Deserialize<Legacy.Maliev.CustomerService.Application.Models.UpsertCustomerRequest>(JsonSerializer.Serialize(Payload(First, Last)))!;
        var key = Guid.NewGuid();
        Legacy.Maliev.CustomerService.Api.CustomerCreateReplayResult first;
        try
        {
            first = await service.CreateAsync(key, "controlled-test-actor", request, token);
        }
        catch (InvalidOperationException exception)
        {
            var category = exception.Message.Contains("does not support user-initiated transactions", StringComparison.Ordinal)
                ? "RetryProviderRejectsUserTransaction" : "OtherInvalidOperation";
            var methods = string.Join(" -> ", new System.Diagnostics.StackTrace(exception).GetFrames()
                .Select(frame => frame.GetMethod()?.DeclaringType?.FullName + "." + frame.GetMethod()?.Name));
            Assert.Fail($"Controlled keyed failure: {category}; type={exception.GetType().FullName}; methods={methods}");
            throw;
        }
        Assert.False(first.ConflictingKey);
        Assert.Equal(First, first.Customer!.FirstName);
        Assert.Equal(Last, first.Customer.LastName);
        var replay = await service.CreateAsync(key, "controlled-test-actor", request, token);
        Assert.False(replay.ConflictingKey);
        Assert.Equal(first.Customer.Id, replay.Customer!.Id);
        Assert.Equal(First, replay.Customer.FirstName);
        Assert.Equal(Last, replay.Customer.LastName);
        Assert.Equal(2, await db.Customers.CountAsync(token));
        Assert.Equal(1, await db.CustomerCreateOperations.CountAsync(token));
    }

    /// <summary>Checks keyed create rollback and fresh-context retry for permanent and transient save failures.</summary>
    /// <param name="transient">Whether the first saved-change fault is a transient provider exception instead of a permanent invalid-operation failure.</param>
    /// <returns>A task that completes after asserting rollback or retry behavior, two distinct attempt contexts, one customer creation, and one durable receipt.</returns>
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task KeyedCreate_RealHostRetryProvider_RollsBackAndUsesFreshAttempt(bool transient)
    {
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(90));
        var token = timeout.Token;
        await fixture.ResetAsync();
        var failure = new KeyedSaveFailure(transient);
        await using var host = fixture.Start(interceptor: failure);
        await using var scope = Microsoft.Extensions.DependencyInjection.ServiceProviderServiceExtensions.CreateAsyncScope(host.Services);
        var service = Microsoft.Extensions.DependencyInjection.ServiceProviderServiceExtensions.GetRequiredService<Legacy.Maliev.CustomerService.Api.CustomerCreateReplayService>(scope.ServiceProvider);
        var request = JsonSerializer.Deserialize<Legacy.Maliev.CustomerService.Application.Models.UpsertCustomerRequest>(JsonSerializer.Serialize(Payload(First, Last)))!;
        var key = Guid.NewGuid();
        if (!transient)
        {
            await Assert.ThrowsAsync<InvalidOperationException>(() => service.CreateAsync(key, "controlled-test-actor", request, token));
            await using var beforeRetry = fixture.Context();
            Assert.Equal(1, await beforeRetry.Customers.CountAsync(token));
            Assert.False(await beforeRetry.CustomerCreateOperations.AnyAsync(row => row.Key == key, token));
        }

        var created = await service.CreateAsync(key, "controlled-test-actor", request, token);
        Assert.False(created.ConflictingKey);
        Assert.Equal(First, created.Customer!.FirstName);
        Assert.Equal(Last, created.Customer.LastName);
        var replay = await service.CreateAsync(key, "controlled-test-actor", request, token);
        Assert.Equal(created.Customer.Id, replay.Customer!.Id);
        Assert.Equal(First, replay.Customer.FirstName);
        Assert.Equal(Last, replay.Customer.LastName);
        Assert.Equal(2, failure.Contexts.Count);
        Assert.Equal(2, failure.Contexts.Distinct().Count());
        await using var stored = fixture.Context();
        Assert.Equal(2, await stored.Customers.CountAsync(token));
        Assert.Equal(1, await stored.CustomerCreateOperations.CountAsync(row => row.Key == key, token));
    }

    /// <summary>Checks recovery of a keyed create after a lost commit acknowledgment and optional cancellation.</summary>
    /// <param name="cancel">Whether the first commit acknowledgment cancels the caller instead of throwing a transient provider failure.</param>
    /// <returns>A task that completes after asserting one commit, literal replayed names, correct receipt binding, and conflicts for a different actor or payload.</returns>
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task KeyedCreate_RealHostRetryProvider_LostCommitAckReplaysAndHonorsCancellation(bool cancel)
    {
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(90));
        await fixture.ResetAsync();
        var failure = new KeyedCommitFailure(cancel ? timeout : null);
        await using var host = fixture.Start(interceptor: failure);
        await using var scope = Microsoft.Extensions.DependencyInjection.ServiceProviderServiceExtensions.CreateAsyncScope(host.Services);
        var service = Microsoft.Extensions.DependencyInjection.ServiceProviderServiceExtensions.GetRequiredService<Legacy.Maliev.CustomerService.Api.CustomerCreateReplayService>(scope.ServiceProvider);
        var request = JsonSerializer.Deserialize<Legacy.Maliev.CustomerService.Application.Models.UpsertCustomerRequest>(JsonSerializer.Serialize(Payload(First, Last)))!;
        var key = Guid.NewGuid();
        if (cancel)
            await Assert.ThrowsAnyAsync<OperationCanceledException>(() => service.CreateAsync(key, "controlled-test-actor", request, timeout.Token));
        else
            Assert.False((await service.CreateAsync(key, "controlled-test-actor", request, timeout.Token)).ConflictingKey);

        using var verification = new CancellationTokenSource(TimeSpan.FromSeconds(90));
        var token = verification.Token;
        var replay = await service.CreateAsync(key, "controlled-test-actor", request, token);
        Assert.False(replay.ConflictingKey);
        Assert.Equal(First, replay.Customer!.FirstName);
        Assert.Equal(Last, replay.Customer.LastName);
        Assert.True((await service.CreateAsync(key, "other-controlled-actor", request, token)).ConflictingKey);
        Assert.True((await service.CreateAsync(key, "controlled-test-actor", request with { FirstName = "Changed" }, token)).ConflictingKey);
        await using var stored = fixture.Context();
        Assert.Equal(2, await stored.Customers.CountAsync(token));
        var receipt = await stored.CustomerCreateOperations.SingleAsync(row => row.Key == key, token);
        Assert.Equal(replay.Customer.Id, receipt.CustomerId);
        Assert.Equal(1, failure.Commits);
    }

    private sealed class KeyedCommitFailure(CancellationTokenSource? cancellation) : Microsoft.EntityFrameworkCore.Diagnostics.DbTransactionInterceptor
    {
        private int commits;
        public int Commits => Volatile.Read(ref commits);

        public override Task TransactionCommittedAsync(System.Data.Common.DbTransaction transaction,
            Microsoft.EntityFrameworkCore.Diagnostics.TransactionEndEventData eventData, CancellationToken cancellationToken = default)
        {
            if (Interlocked.Increment(ref commits) == 1)
            {
                if (cancellation is not null)
                {
                    cancellation.Cancel();
                    throw new OperationCanceledException(cancellation.Token);
                }

                throw new Npgsql.NpgsqlException("controlled lost commit acknowledgement", new TimeoutException("controlled acknowledgement"));
            }

            return Task.CompletedTask;
        }
    }

    private sealed class KeyedSaveFailure(bool transient) : Microsoft.EntityFrameworkCore.Diagnostics.SaveChangesInterceptor
    {
        private int calls;
        public List<Guid> Contexts { get; } = [];

        public override ValueTask<int> SavedChangesAsync(Microsoft.EntityFrameworkCore.Diagnostics.SaveChangesCompletedEventData eventData,
            int result, CancellationToken cancellationToken = default)
        {
            Contexts.Add(eventData.Context!.ContextId.InstanceId);
            if (Interlocked.Increment(ref calls) == 1)
            {
                if (transient) throw new Npgsql.NpgsqlException("controlled transient failure", new IOException("controlled test transport"));
                throw new InvalidOperationException("controlled rollback failure");
            }

            return ValueTask.FromResult(result);
        }
    }


    /// <summary>Checks ordinary optional contact writes against the pinned public Newtonsoft comparison projection.</summary>
    /// <param name="update">Whether to update the seeded customer with PUT instead of creating a customer with POST.</param>
    /// <returns>A task that completes after asserting source-shaped scalar and null projections, rejected-container isolation, literal persistence and readback, and retained relations.</returns>
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task SourceOptionalContact_OrdinaryCreateAndUpdateComparePinnedReference(bool update)
    {
        // This is an explicitly pinned comparator, not an executed historical
        // net8/SQL Server contract or a versioned/replay adoption claim.
        using var lifetime = new CancellationTokenSource(TimeSpan.FromSeconds(120));
        var token = lifetime.Token;
        await fixture.ResetAsync();
        await using var host = fixture.Start();
        using var client = fixture.Client(host, "customer-lifecycle");
        var fields = new[] { "Telephone", "Mobile", "Fax" };
        var cases = new (string Name, string? Value)[]
        {
            ("boolean-true", "true"),
            ("boolean-false", "false"),
            ("integer", "42"),
            ("fraction", "1.5"),
            ("exponent", "1E+03"),
            ("fraction-padding", "1.500"),
            ("negative-zero", "-0"),
            ("large-integer", "9223372036854775808123456789"),
            ("leading-zero-string", "\"001234\""),
            ("empty-string", "\"\""),
            ("literal-unicode", "\" \\tโทรศัพท์\\u00a0\\ud83d\\ude80 \""),
            ("missing", (string?)null),
            ("null", "null"),
            ("string-then-null", "\"001234\""),
            ("primitive-then-null", "true"),
            ("object", "{}"),
            ("array", "[]"),
        };
        foreach (var field in fields)
        {
            foreach (var item in cases)
            {
                token.ThrowIfCancellationRequested();
                await fixture.ResetAsync();
                await fixture.SeedOldAsync(host);
                var fragment = item.Value is null ? string.Empty : $",\"{field}\":{item.Value}";
                if (item.Name is "string-then-null" or "primitive-then-null")
                    fragment += $",\"{field}\":null";
                var body = "{\"FirstName\":\"Contact\",\"LastName\":\"Baseline\"," +
                    "\"Email\":\"optional-reference@example.test\",\"CompanyId\":1," +
                    "\"BillingAddressId\":1,\"ShippingAddressId\":1" + fragment + "}";
                OptionalContactReference? reference;
                try
                {
                    reference = Newtonsoft.Json.JsonConvert.DeserializeObject<OptionalContactReference>(body,
                        new Newtonsoft.Json.JsonSerializerSettings
                        {
                            NullValueHandling = Newtonsoft.Json.NullValueHandling.Ignore,
                            ReferenceLoopHandling = Newtonsoft.Json.ReferenceLoopHandling.Ignore,
                        });
                    Assert.NotNull(reference);
                }
                catch (Newtonsoft.Json.JsonException)
                {
                    reference = null;
                }

                using var cachedRead = await client.GetAsync("/customers/1", token);
                Assert.Equal(HttpStatusCode.OK, cachedRead.StatusCode);
                Assert.True(await fixture.CacheExistsAsync());
                var before = await OptionalContactWholeStateAsync(token);
                var relations = await RelationsAsync(token);
                using var request = new HttpRequestMessage(update ? HttpMethod.Put : HttpMethod.Post,
                    update ? "/customers/1" : "/customers")
                {
                    Content = new StringContent(body, System.Text.Encoding.UTF8, "application/json"),
                };
                // One actual attempt only. No Idempotency-Key, If-Match,
                // versioned route, retry, or identity provider is introduced.
                using var response = await client.SendAsync(request, token);
                if (reference is null)
                {
                    Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
                    Assert.Equal(before, await OptionalContactWholeStateAsync(token));
                    Assert.True(await fixture.CacheExistsAsync());
                    continue;
                }

                foreach (var value in new[] { reference.Telephone, reference.Mobile, reference.Fax })
                    Assert.True(value is null || value.Length <= 256);
                Assert.True(response.StatusCode == (update ? HttpStatusCode.NoContent : HttpStatusCode.Created),
                    $"Comparator {field}/{item.Name}: actual HTTP {(int)response.StatusCode}; update={update}");
                var id = 1;
                if (update)
                {
                    Assert.False(await fixture.CacheExistsAsync());
                }
                else
                {
                    using var created = JsonDocument.Parse(await response.Content.ReadAsStringAsync(token));
                    id = created.RootElement.GetProperty("Id").GetInt32();
                    Assert.True(id > 1);
                    Assert.NotNull(response.Headers.Location);
                    using var namedRead = await client.GetAsync(response.Headers.Location, token);
                    Assert.Equal(HttpStatusCode.OK, namedRead.StatusCode);
                    using var namedJson = JsonDocument.Parse(await namedRead.Content.ReadAsStringAsync(token));
                    Assert.Equal(id, namedJson.RootElement.GetProperty("Id").GetInt32());
                    AssertOptionalContactWire(namedJson.RootElement, reference);
                }

                await using (var db = fixture.Context())
                {
                    var stored = await db.Customers.AsNoTracking().SingleAsync(row => row.Id == id, token);
                    Assert.Equal(reference.Telephone, stored.Telephone);
                    Assert.Equal(reference.Mobile, stored.Mobile);
                    Assert.Equal(reference.Fax, stored.Fax);
                    Assert.Equal("Contact", stored.FirstName);
                    Assert.Equal("Baseline", stored.LastName);
                    Assert.Equal("optional-reference@example.test", stored.Email);
                    Assert.Equal(1, stored.CompanyId);
                    Assert.Equal(1, stored.BillingAddressId);
                    Assert.Equal(1, stored.ShippingAddressId);
                    Assert.Equal(update ? 1 : 2, await db.Customers.CountAsync(token));
                    Assert.Equal(0, await db.CustomerCreateOperations.CountAsync(token));
                }
                Assert.Equal(relations, await RelationsAsync(token));
                using var readback = await client.GetAsync($"/customers/{id}", token);
                Assert.Equal(HttpStatusCode.OK, readback.StatusCode);
                using var wire = JsonDocument.Parse(await readback.Content.ReadAsStringAsync(token));
                AssertOptionalContactWire(wire.RootElement, reference);
            }
        }
    }

    private static void AssertOptionalContactWire(JsonElement item, OptionalContactReference expected)
    {
        foreach (var (field, value) in new[]
        {
            ("Telephone", expected.Telephone), ("Mobile", expected.Mobile), ("Fax", expected.Fax),
        })
        {
            if (value is null) Assert.False(item.TryGetProperty(field, out _));
            else Assert.Equal(value, item.GetProperty(field).GetString());
            Assert.False(item.TryGetProperty(char.ToLowerInvariant(field[0]) + field[1..], out _));
        }
    }

    private async Task<string> OptionalContactWholeStateAsync(CancellationToken token)
    {
        await using var db = fixture.Context();
        return JsonSerializer.Serialize(new
        {
            Customers = await db.Customers.AsNoTracking().OrderBy(row => row.Id).Select(row => new
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
                row.CreatedDate,
                row.ModifiedDate,
                row.InternalRemark,
                Revision = EF.Property<uint>(row, "xmin"),
            }).ToArrayAsync(token),
            Companies = await db.Companies.AsNoTracking().OrderBy(row => row.Id).ToArrayAsync(token),
            Addresses = await db.Addresses.AsNoTracking().OrderBy(row => row.Id).ToArrayAsync(token),
            ReplayRows = await db.CustomerCreateOperations.AsNoTracking().CountAsync(token),
        });
    }

    private sealed class OptionalContactReference
    {
        public OptionalContactReference() { }
        public string? Telephone { get; set; }
        public string? Mobile { get; set; }
        public string? Fax { get; set; }
    }

    private static object Payload(string first, string last) => new
    {
        FirstName = first,
        LastName = last,
        Email = "literal@example.test",
        Telephone = "020000001",
        Mobile = "0800000001",
        Fax = "020000002",
        DateOfBirth = new DateTime(1980, 1, 1),
        CompanyId = 1,
        BillingAddressId = 1,
        ShippingAddressId = 1,
    };

    private static void AssertNames(JsonElement item, string first, string last)
    {
        Assert.Equal(first, item.GetProperty("FirstName").GetString());
        Assert.Equal(last, item.GetProperty("LastName").GetString());
        Assert.Equal((first + " " + last).Trim(' '), item.GetProperty("FullName").GetString());
        Assert.False(item.TryGetProperty("firstName", out _));
        Assert.False(item.TryGetProperty("InternalRemark", out _));
        foreach (var field in new[] { "PasswordHash", "SecurityStamp", "Revision", "Token" })
            Assert.False(item.TryGetProperty(field, out _));
    }

    private async Task AssertStoredAsync(int id, string first, string last, CancellationToken token)
    {
        await using var db = fixture.Context();
        var stored = await db.Customers.AsNoTracking().SingleAsync(row => row.Id == id, token);
        Assert.Equal(first, stored.FirstName); Assert.Equal(last, stored.LastName);
        Assert.Equal((first + " " + last).Trim(' '), stored.FullName);
        Assert.Equal("literal@example.test", stored.Email);
        Assert.Equal(1, stored.CompanyId); Assert.Equal(1, stored.BillingAddressId); Assert.Equal(1, stored.ShippingAddressId);
        Assert.Equal("020000001", stored.Telephone); Assert.Equal("0800000001", stored.Mobile); Assert.Equal("020000002", stored.Fax);
        Assert.Equal(new DateTime(1980, 1, 1), stored.DateOfBirth);
    }

    private async Task<string> RelationsAsync(CancellationToken token)
    {
        await using var db = fixture.Context();
        return JsonSerializer.Serialize(new
        {
            Companies = await db.Companies.AsNoTracking().OrderBy(row => row.Id).ToArrayAsync(token),
            Addresses = await db.Addresses.AsNoTracking().OrderBy(row => row.Id).ToArrayAsync(token),
        });
    }
}
