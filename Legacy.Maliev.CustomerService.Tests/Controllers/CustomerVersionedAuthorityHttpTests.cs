using System.Net;
using System.Text;
using System.Text.Json;

namespace Legacy.Maliev.CustomerService.Tests.Controllers;

public sealed class CustomerVersionedAuthorityHttpTests(CustomerDetailAuthorityFixture fixture)
    : IClassFixture<CustomerDetailAuthorityFixture>
{
    [Fact]
    public async Task Two_production_hosts_with_real_permissions_reject_stale_write_and_preserve_committed_graph()
    {
        await fixture.ResetAsync();
        await using var first = fixture.Start();
        await using var second = fixture.Start();
        using var writer = fixture.Client(first);
        using var staleWriter = fixture.Client(second);
        using var firstRead = await writer.GetAsync("/customers/1/versioned");
        using var secondRead = await staleWriter.GetAsync("/customers/1/versioned");
        Assert.Equal(HttpStatusCode.OK, firstRead.StatusCode);
        Assert.Equal(HttpStatusCode.OK, secondRead.StatusCode);
        Assert.Equal("no-store", firstRead.Headers.CacheControl?.ToString());
        var revision = firstRead.Headers.ETag!.ToString();
        Assert.Equal(revision, secondRead.Headers.ETag!.ToString());
        using var winner = await PutAsync(writer, "Winner", revision);
        Assert.Equal(HttpStatusCode.NoContent, winner.StatusCode);
        var committed = await fixture.SnapshotAsync();
        using var loser = await PutAsync(staleWriter, "Loser", revision);
        Assert.Equal(HttpStatusCode.PreconditionFailed, loser.StatusCode);
        Assert.Equal(committed, await fixture.SnapshotAsync());
        using var freshRead = await staleWriter.GetAsync("/customers/1/versioned");
        Assert.Equal(HttpStatusCode.OK, freshRead.StatusCode);
        Assert.NotEqual(revision, freshRead.Headers.ETag!.ToString());
        using var json = JsonDocument.Parse(await freshRead.Content.ReadAsStringAsync());
        Assert.Equal("Winner", json.RootElement.GetProperty("FirstName").GetString());
        Assert.False(json.RootElement.TryGetProperty("Revision", out _));
        Assert.False(json.RootElement.TryGetProperty("InternalRemark", out _));
        Assert.False(json.RootElement.TryGetProperty("PasswordHash", out _));
        using var retry = await PutAsync(staleWriter, "Fresh", freshRead.Headers.ETag!.ToString());
        Assert.Equal(HttpStatusCode.NoContent, retry.StatusCode);
        Assert.Equal("Fresh", await fixture.StoredAsync("customer"));
    }

    [Theory]
    [InlineData("anonymous", HttpStatusCode.Unauthorized)]
    [InlineData("wrong-signature", HttpStatusCode.Unauthorized)]
    [InlineData("expired", HttpStatusCode.Unauthorized)]
    [InlineData("denied", HttpStatusCode.Forbidden)]
    public async Task Actual_authentication_and_permissions_precede_preconditions(string authority, HttpStatusCode expected)
    {
        await fixture.ResetAsync();
        await using var host = fixture.Start();
        using var client = fixture.Client(host, authority);
        var snapshot = await fixture.SnapshotAsync();
        using var read = await client.GetAsync("/customers/1/versioned");
        using var missing = await PutAsync(client, "Unauthorized", null);
        using var malformed = await PutAsync(client, "Unauthorized", "W/\"00000001\"");
        Assert.Equal(expected, read.StatusCode);
        Assert.Equal(expected, missing.StatusCode);
        Assert.Equal(expected, malformed.StatusCode);
        Assert.Equal(snapshot, await fixture.SnapshotAsync());
    }

    [Theory]
    [InlineData(null, 428)]
    [InlineData("W/\"00000001\"", 400)]
    [InlineData("*", 400)]
    [InlineData("\"00000001\",\"00000002\"", 400)]
    public async Task Authorized_invalid_precondition_cannot_mutate_graph(string? revision, int status)
    {
        await fixture.ResetAsync();
        await using var host = fixture.Start();
        using var client = fixture.Client(host);
        var snapshot = await fixture.SnapshotAsync();
        using var result = await PutAsync(client, "Invalid", revision);
        Assert.Equal((HttpStatusCode)status, result.StatusCode);
        Assert.Equal(snapshot, await fixture.SnapshotAsync());
    }

    private static Task<HttpResponseMessage> PutAsync(HttpClient client, string firstName, string? revision)
    {
        var request = new HttpRequestMessage(HttpMethod.Put, "/customers/1/versioned")
        {
            Content = new StringContent(JsonSerializer.Serialize(new
            {
                FirstName = firstName, LastName = "Customer", Email = "detail@example.test",
                CompanyId = 1, BillingAddressId = 1, ShippingAddressId = 1
            }), Encoding.UTF8, "application/json")
        };
        if (revision is not null) request.Headers.TryAddWithoutValidation("If-Match", revision);
        return SendAsync(client, request);
    }

    private static async Task<HttpResponseMessage> SendAsync(HttpClient client, HttpRequestMessage request)
    {
        using (request) return await client.SendAsync(request);
    }
}
