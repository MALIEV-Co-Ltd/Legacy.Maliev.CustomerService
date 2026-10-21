using System.Net;
using System.Text;
using System.Text.Json;

namespace Legacy.Maliev.CustomerService.Tests.Controllers;

/// <summary>Exercises database authority for customer detail reads across mutations, cache failures, competing hosts, and cancellation.</summary>
/// <param name="fixture">The shared fixture providing customer hosts and disposable persistence for these scenarios.</param>
[Collection("Customer scaffold runtime")]
public sealed class CustomerDetailAuthorityHttpTests(CustomerDetailAuthorityFixture fixture) : IClassFixture<CustomerDetailAuthorityFixture>
{
    /// <summary>Verifies the migrated owned graph through the EF scaffold runtime proof and generated queries.</summary>
    /// <returns>A task representing completion of the database-authoritative customer detail assertions.</returns>
    [Fact]
    public async Task Scaffold_ActualEfPreviewBuildAndGeneratedQueriesPreserveMigratedOwnedGraph()
    {
        await fixture.ResetAsync();
        await CustomerScaffoldRuntimeProof.RunAsync(fixture);
    }

    /// <summary>Verifies that committed detail remains authoritative when Redis refuses cache removal after a mutation.</summary>
    /// <param name="kind">The customer, company, or address resource mutated by the scenario.</param>
    /// <param name="expected">The committed field value expected in both storage and customer detail.</param>
    /// <returns>A task representing completion of the database-authoritative customer detail assertions.</returns>
    [Theory]
    [InlineData("customer", "After customer")]
    [InlineData("company", "After company")]
    [InlineData("address", "After road")]
    public async Task AcknowledgedMutation_WithRealRedisInvalidationFailure_ReturnsCommittedDetail(string kind, string expected)
    {
        await fixture.ResetAsync();
        await using var host = fixture.Start(restricted: true);
        using var client = fixture.Client(host);
        await fixture.SeedOldAsync(host);
        await AssertDetailAsync(client, kind, Before(kind));
        await fixture.DenyRemovalAsync();
        await fixture.AssertRemovalDeniedAsync(host);
        using var changed = await PutAsync(client, kind);
        Assert.Equal(HttpStatusCode.NoContent, changed.StatusCode);
        Assert.Equal(expected, await fixture.StoredAsync(kind));
        Assert.True(await fixture.CacheExistsAsync());
        await AssertDetailAsync(client, kind, expected);
    }

    /// <summary>Verifies that a delayed stale cache fill cannot override committed detail in either application host.</summary>
    /// <param name="kind">The customer, company, or address resource mutated by the scenario.</param>
    /// <param name="expected">The committed field value expected in both storage and customer detail.</param>
    /// <returns>A task representing completion of the database-authoritative customer detail assertions.</returns>
    [Theory]
    [InlineData("customer", "After customer")]
    [InlineData("company", "After company")]
    [InlineData("address", "After road")]
    public async Task LateOldWriterFill_AfterAcknowledgedMutation_CannotOverrideEitherAppInstance(string kind, string expected)
    {
        await fixture.ResetAsync();
        await using var first = fixture.Start();
        await using var second = fixture.Start();
        using var writer = fixture.Client(first);
        using var reader = fixture.Client(second);
        using var changed = await PutAsync(writer, kind);
        Assert.Equal(HttpStatusCode.NoContent, changed.StatusCode);
        Assert.Equal(expected, await fixture.StoredAsync(kind));
        // Simulate a delayed old writer's fill AFTER the successful mutation/invalidation.
        await fixture.SeedOldAsync(second);
        Assert.True(await fixture.CacheExistsAsync());
        var firstValue = await ReadDetailAsync(writer, kind);
        var secondValue = await ReadDetailAsync(reader, kind);
        Assert.Equal(new[] { expected, expected }, new[] { firstValue, secondValue });
    }

    /// <summary>Verifies that successful cache invalidation exposes the committed nested customer projection.</summary>
    /// <param name="kind">The customer, company, or address resource mutated by the scenario.</param>
    /// <param name="expected">The committed field value expected in both storage and customer detail.</param>
    /// <returns>A task representing completion of the database-authoritative customer detail assertions.</returns>
    [Theory]
    [InlineData("customer", "After customer")]
    [InlineData("company", "After company")]
    [InlineData("address", "After road")]
    public async Task SuccessfulInvalidation_ReturnsCommittedNestedProjection(string kind, string expected)
    {
        await fixture.ResetAsync();
        await using var host = fixture.Start();
        using var client = fixture.Client(host);
        await fixture.SeedOldAsync(host);
        using var changed = await PutAsync(client, kind);
        Assert.Equal(HttpStatusCode.NoContent, changed.StatusCode);
        Assert.Equal(expected, await fixture.StoredAsync(kind));
        Assert.False(await fixture.CacheExistsAsync());
        await AssertDetailAsync(client, kind, expected);
    }

    /// <summary>Verifies that invalid authority cannot read customer detail or mutate any part of the stored graph.</summary>
    /// <param name="authority">The authentication and permission scenario used by the test client.</param>
    /// <param name="expected">The expected HTTP status for the supplied scenario.</param>
    /// <returns>A task representing completion of the database-authoritative customer detail assertions.</returns>
    [Theory]
    [InlineData("anonymous", HttpStatusCode.Unauthorized)]
    [InlineData("wrong-signature", HttpStatusCode.Unauthorized)]
    [InlineData("expired", HttpStatusCode.Unauthorized)]
    [InlineData("denied", HttpStatusCode.Forbidden)]
    public async Task InvalidAuthority_CannotReadProjectionOrMutateAnyGraph(string authority, HttpStatusCode expected)
    {
        await fixture.ResetAsync();
        await using var host = fixture.Start();
        using var client = fixture.Client(host, authority);
        var snapshot = await fixture.SnapshotAsync();
        using var read = await client.GetAsync("/customers/1");
        Assert.Equal(expected, read.StatusCode);
        Assert.DoesNotContain("detail@example.test", await read.Content.ReadAsStringAsync(), StringComparison.Ordinal);
        foreach (var kind in new[] { "customer", "company", "address" })
        {
            using var changed = await PutAsync(client, kind);
            Assert.Equal(expected, changed.StatusCode);
        }
        Assert.Equal(snapshot, await fixture.SnapshotAsync());
        Assert.False(await fixture.CacheExistsAsync());
    }

    /// <summary>Verifies that invalid mutation input preserves the database and existing customer detail projection.</summary>
    /// <param name="kind">The customer, company, or address resource targeted by the mutation.</param>
    /// <returns>A task representing completion of the database-authoritative customer detail assertions.</returns>
    [Theory]
    [InlineData("customer")]
    [InlineData("company")]
    [InlineData("address")]
    public async Task InvalidMutation_PreservesDatabaseAndExistingProjection(string kind)
    {
        await fixture.ResetAsync();
        await using var host = fixture.Start();
        using var client = fixture.Client(host);
        await fixture.SeedOldAsync(host);
        var snapshot = await fixture.SnapshotAsync();
        using var changed = await PutAsync(client, kind, invalid: true);
        Assert.Equal(HttpStatusCode.BadRequest, changed.StatusCode);
        Assert.Equal(snapshot, await fixture.SnapshotAsync());
        await AssertDetailAsync(client, kind, Before(kind));
    }

    /// <summary>Verifies that mutations targeting missing records leave the existing customer graph unchanged.</summary>
    /// <param name="kind">The customer, company, or address resource targeted by the mutation.</param>
    /// <returns>A task representing completion of the database-authoritative customer detail assertions.</returns>
    [Theory]
    [InlineData("customer")]
    [InlineData("company")]
    [InlineData("address")]
    public async Task MissingMutation_DoesNotChangeExistingGraph(string kind)
    {
        await fixture.ResetAsync();
        await using var host = fixture.Start();
        using var client = fixture.Client(host);
        var snapshot = await fixture.SnapshotAsync();
        using var changed = await PutAsync(client, kind, id: int.MaxValue);
        Assert.Equal(HttpStatusCode.NotFound, changed.StatusCode);
        using var missing = await client.GetAsync($"/customers/{int.MaxValue}");
        Assert.Equal(HttpStatusCode.NotFound, missing.StatusCode);
        Assert.Equal(snapshot, await fixture.SnapshotAsync());
        Assert.False(await fixture.CacheExistsAsync());
    }

    /// <summary>Verifies that caller cancellation before the root save preserves the previously committed graph.</summary>
    /// <param name="kind">The customer, company, or address resource targeted by the mutation.</param>
    /// <returns>A task representing completion of the database-authoritative customer detail assertions.</returns>
    [Theory]
    [InlineData("customer")]
    [InlineData("company")]
    [InlineData("address")]
    public async Task CallerAbort_BeforeRootSave_PreservesCommittedGraph(string kind)
    {
        await fixture.ResetAsync();
        var barrier = new CustomerDetailSaveBarrier();
        await using var host = fixture.Start(barrier: barrier);
        using var client = fixture.Client(host);
        var snapshot = await fixture.SnapshotAsync();
        using var cancellation = new CancellationTokenSource();
        var request = PutAsync(client, kind, token: cancellation.Token);
        await barrier.Entered.Task.WaitAsync(TimeSpan.FromSeconds(10));
        cancellation.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(async () => await request);
        await barrier.Cancelled.Task.WaitAsync(TimeSpan.FromSeconds(10));
        Assert.Equal(snapshot, await fixture.SnapshotAsync());
        Assert.False(await fixture.CacheExistsAsync());
    }

    private static string Before(string kind) => kind switch { "customer" => "Before", "company" => "Before company", _ => "Before road" };
    private static Task<HttpResponseMessage> PutAsync(HttpClient client, string kind, bool invalid = false, int id = 1, CancellationToken token = default)
    {
        var route = kind switch { "customer" => $"/customers/{id}", "company" => $"/customers/Companies/{id}", _ => $"/customers/Addresses/{id}" };
        var json = kind switch
        {
            "customer" => """{"FirstName":"After customer","LastName":"Customer","Email":"detail@example.test","CompanyId":1,"BillingAddressId":1,"ShippingAddressId":1}""",
            "company" => """{"Name":"After company","TaxNumber":"123"}""",
            _ => """{"AddressLine1":"After road","City":"Bangkok","CountryId":764}"""
        };
        if (invalid) json = kind switch { "customer" => """{"FirstName":"","LastName":"Customer","Email":"detail@example.test"}""", "company" => """{"Name":""}""", _ => JsonSerializer.Serialize(new { AddressLine1 = new string('a', 257), CountryId = 764 }) };
        return client.PutAsync(route, new StringContent(json, Encoding.UTF8, "application/json"), token);
    }

    private static async Task AssertDetailAsync(HttpClient client, string kind, string expected) =>
        Assert.Equal(expected, await ReadDetailAsync(client, kind));

    private static async Task<string?> ReadDetailAsync(HttpClient client, string kind)
    {
        using var response = await client.GetAsync("/customers/1");
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var body = await response.Content.ReadAsStringAsync();
        using var document = JsonDocument.Parse(body);
        var value = document.RootElement;
        Assert.Equal(1, value.GetProperty("Id").GetInt32());
        Assert.False(value.TryGetProperty("id", out _));
        Assert.False(value.TryGetProperty("Mobile", out _));
        Assert.DoesNotContain("InternalRemark", body, StringComparison.Ordinal);
        Assert.DoesNotContain("PasswordHash", body, StringComparison.Ordinal);
        Assert.Equal(1, value.GetProperty("Company").GetProperty("Id").GetInt32());
        Assert.Equal(764, value.GetProperty("BillingAddress").GetProperty("CountryId").GetInt32());
        Assert.Equal(1, value.GetProperty("ShippingAddress").GetProperty("Id").GetInt32());
        var actual = kind switch { "customer" => value.GetProperty("FirstName").GetString(), "company" => value.GetProperty("Company").GetProperty("Name").GetString(), _ => value.GetProperty("BillingAddress").GetProperty("AddressLine1").GetString() };
        if (kind == "address") Assert.Equal(actual, value.GetProperty("ShippingAddress").GetProperty("AddressLine1").GetString());
        return actual;
    }
}
