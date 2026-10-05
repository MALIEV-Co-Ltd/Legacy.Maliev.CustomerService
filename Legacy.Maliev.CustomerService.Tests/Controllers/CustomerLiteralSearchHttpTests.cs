using System.Net;
using System.Text.Json;
using Legacy.Maliev.CustomerService.Domain;
using Microsoft.EntityFrameworkCore;

namespace Legacy.Maliev.CustomerService.Tests.Controllers;

public sealed class CustomerLiteralSearchHttpTests(CustomerDetailAuthorityFixture fixture)
    : IClassFixture<CustomerDetailAuthorityFixture>
{
    public static IEnumerable<object[]> LiteralFields()
    {
        foreach (var field in new[] { "FirstName", "LastName", "Email", "Mobile", "Telephone", "Company" })
            foreach (var literal in new[] { "%", "_", "\\" })
                yield return [field, literal];
    }

    [Theory]
    [MemberData(nameof(LiteralFields))]
    public async Task Source_contains_search_matches_literal_characters_in_normal_http(string field, string literal)
    {
        await fixture.ResetAsync();
        int targetId;
        await using (var db = fixture.Context())
        {
            var target = new Customer { FirstName = "Literal", LastName = "Customer", Email = "literal@example.test" };
            switch (field)
            {
                case "FirstName": target.FirstName = "Literal" + literal + "Name"; break;
                case "LastName": target.LastName = "Literal" + literal + "Name"; break;
                case "Email": target.Email = "literal" + literal + "@example.test"; break;
                case "Mobile": target.Mobile = "08" + literal + "00"; break;
                case "Telephone": target.Telephone = "02" + literal + "00"; break;
                case "Company": target.Company = new Company { Name = "Literal" + literal + "Company" }; break;
            }
            db.Customers.Add(target);
            await db.SaveChangesAsync();
            targetId = target.Id;
        }
        await using var host = fixture.Start();
        using var client = fixture.Client(host, "directory");
        using var response = await client.GetAsync("/customers?index=1&size=50&search=" + Uri.EscapeDataString(literal));
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        using var json = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        var root = json.RootElement;
        Assert.Equal(1, root.GetProperty("TotalRecords").GetInt32());
        var item = Assert.Single(root.GetProperty("Items").EnumerateArray());
        Assert.Equal(targetId, item.GetProperty("Id").GetInt32());
        Assert.False(item.TryGetProperty("InternalRemark", out _));
        Assert.False(item.TryGetProperty("PasswordHash", out _));
    }

    [Theory]
    [InlineData("%")]
    [InlineData("_")]
    [InlineData("\\")]
    public async Task Literal_absent_from_storage_returns_source_not_found(string literal)
    {
        await fixture.ResetAsync();
        await using var host = fixture.Start();
        using var client = fixture.Client(host, "directory");
        var before = await fixture.SnapshotAsync();
        using var response = await client.GetAsync("/customers?search=" + Uri.EscapeDataString(literal));
        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
        Assert.Equal(before, await fixture.SnapshotAsync());
    }

    [Theory]
    [InlineData(null)]
    [InlineData("Before")]
    public async Task Page_beyond_matching_rows_returns_source_not_found(string? search)
    {
        await fixture.ResetAsync();
        await using var host = fixture.Start();
        using var client = fixture.Client(host, "directory");
        var snapshot = await fixture.SnapshotAsync();
        var path = "/customers?index=2&size=1" + (search is null ? "" : "&search=" + Uri.EscapeDataString(search));
        using var response = await client.GetAsync(path);
        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
        Assert.Equal(snapshot, await fixture.SnapshotAsync());
    }

    [Theory]
    [InlineData("FirstName")]
    [InlineData("Email")]
    [InlineData("Mobile")]
    [InlineData("Telephone")]
    [InlineData("Company")]
    public async Task Numeric_customer_search_preserves_id_or_text_matches(string field)
    {
        await fixture.ResetAsync();
        int matchingId;
        await using (var db = fixture.Context())
        {
            var matching = new Customer { FirstName = "Text", LastName = "Match", Email = "text@example.test" };
            switch (field)
            {
                case "FirstName": matching.FirstName = "Text1"; break;
                case "Email": matching.Email = "text1@example.test"; break;
                case "Mobile": matching.Mobile = "080100"; break;
                case "Telephone": matching.Telephone = "020100"; break;
                case "Company": matching.Company = new Company { Name = "Company1" }; break;
            }
            db.Customers.Add(matching);
            db.Customers.Add(new Customer { FirstName = "Unrelated", LastName = "Person", Email = "unrelated@example.test" });
            await db.SaveChangesAsync();
            matchingId = matching.Id;
        }
        await using var host = fixture.Start();
        using var client = fixture.Client(host, "directory");
        using var response = await client.GetAsync("/customers?search=1&index=1&size=50");
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        using var json = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        Assert.Equal(2, json.RootElement.GetProperty("TotalRecords").GetInt32());
        Assert.Equal(new[] { 1, matchingId }, json.RootElement.GetProperty("Items").EnumerateArray()
            .Select(item => item.GetProperty("Id").GetInt32()).ToArray());
    }
    [Fact]
    public async Task Empty_page_after_literal_filter_does_not_return_unrelated_row_or_empty_success()
    {
        await fixture.ResetAsync();
        await using (var db = fixture.Context())
        {
            db.Customers.Add(new Customer { FirstName = "Literal%Name", LastName = "Customer", Email = "literal@example.test" });
            await db.SaveChangesAsync();
        }
        await using var host = fixture.Start();
        using var client = fixture.Client(host, "directory");
        using var first = await client.GetAsync("/customers?search=%25&index=1&size=1");
        Assert.Equal(HttpStatusCode.OK, first.StatusCode);
        using var json = JsonDocument.Parse(await first.Content.ReadAsStringAsync());
        Assert.Equal(1, json.RootElement.GetProperty("TotalRecords").GetInt32());
        Assert.Equal(1, json.RootElement.GetProperty("TotalPages").GetInt32());
        Assert.False(json.RootElement.GetProperty("HasNextPage").GetBoolean());
        using var beyond = await client.GetAsync("/customers?search=%25&index=2&size=1");
        Assert.Equal(HttpStatusCode.NotFound, beyond.StatusCode);
    }
    [Fact]
    public async Task Escaped_literal_search_remains_case_insensitive()
    {
        await fixture.ResetAsync();
        await using (var db = fixture.Context())
        {
            await db.Customers.ExecuteUpdateAsync(setters => setters.SetProperty(row => row.FirstName, "Engineer%Lead"));
        }
        await using var host = fixture.Start();
        using var client = fixture.Client(host, "directory");
        using var response = await client.GetAsync("/customers?search=" + Uri.EscapeDataString("engineer%lead"));
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        using var json = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        Assert.Equal(1, json.RootElement.GetProperty("TotalRecords").GetInt32());
        Assert.Equal(1, Assert.Single(json.RootElement.GetProperty("Items").EnumerateArray()).GetProperty("Id").GetInt32());
    }
}
