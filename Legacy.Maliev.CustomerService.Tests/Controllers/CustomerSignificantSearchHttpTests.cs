using System.Net;
using System.Text.Json;
using Legacy.Maliev.CustomerService.Domain;

namespace Legacy.Maliev.CustomerService.Tests.Controllers;

public sealed class CustomerSignificantSearchHttpTests(CustomerDetailAuthorityFixture fixture)
    : IClassFixture<CustomerDetailAuthorityFixture>
{
    private const string Literal = " \u0e0a\u0e34\u0e49\u0e19 ";

    [Theory]
    [InlineData(" ")]
    [InlineData("\t")]
    public async Task WhitespaceOnly_NormalQueryBindingRetainsUnfilteredList(string search)
    {
        await fixture.ResetAsync();
        await using var host = fixture.Start();
        using var client = fixture.Client(host, "directory");
        using var response = await client.GetAsync(Path(search));
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        await AssertPageAsync(response, [1]);
    }

    [Theory]
    [InlineData("FirstName")]
    [InlineData("LastName")]
    [InlineData("Email")]
    [InlineData("Mobile")]
    [InlineData("Telephone")]
    [InlineData("Company")]
    public async Task PaddedText_MatchesOnlyStoredLiteralAcrossSourceFields(string field)
    {
        await fixture.ResetAsync();
        int targetId;
        await using (var db = fixture.Context())
        {
            var target = Customer("Target", "target@example.test");
            SetField(target, field, Literal);
            var decoy = Customer("Decoy", "decoy@example.test");
            SetField(decoy, field, Literal.Trim());
            db.Customers.AddRange(target, decoy);
            await db.SaveChangesAsync();
            targetId = target.Id;
        }
        await using var host = fixture.Start();
        using var client = fixture.Client(host, "directory");
        var before = await fixture.SnapshotAsync();
        using var response = await client.GetAsync(Path(Literal));
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        await AssertPageAsync(response, [targetId]);
        Assert.Equal(before, await fixture.SnapshotAsync());
    }

    [Theory]
    [InlineData(" detail@example.test")]
    [InlineData("detail@example.test ")]
    [InlineData(" detail@example.test ")]
    public async Task AbsentLeadingTrailingOrBothSpaces_ReturnSourceNotFound(string search)
    {
        await fixture.ResetAsync();
        await using var host = fixture.Start();
        using var client = fixture.Client(host, "directory");
        var before = await fixture.SnapshotAsync();
        using var response = await client.GetAsync(Path(search));
        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
        Assert.Equal(before, await fixture.SnapshotAsync());
    }

    [Fact]
    public async Task AlternatingPlainAndPaddedText_PreservesIndependentResultSetsAndPaging()
    {
        await fixture.ResetAsync();
        int targetId, decoyId;
        await using (var db = fixture.Context())
        {
            var target = Customer("Target", "target@example.test");
            target.Mobile = Literal;
            var decoy = Customer("Decoy", "decoy@example.test");
            decoy.Mobile = Literal.Trim();
            db.Customers.AddRange(target, decoy);
            await db.SaveChangesAsync();
            targetId = target.Id; decoyId = decoy.Id;
        }
        await using var host = fixture.Start();
        using var client = fixture.Client(host, "directory");
        var before = await fixture.SnapshotAsync();
        foreach (var search in new[] { Literal, Literal.Trim(), Literal })
        {
            using var response = await client.GetAsync(Path(search));
            Assert.Equal(HttpStatusCode.OK, response.StatusCode);
            await AssertPageAsync(response, search == Literal ? [targetId] : [targetId, decoyId]);
        }
        using var absentPage = await client.GetAsync(Path(Literal) + "&index=2&size=1");
        Assert.Equal(HttpStatusCode.NotFound, absentPage.StatusCode);
        Assert.Equal(before, await fixture.SnapshotAsync());
    }

    [Fact]
    public async Task PaddedNumericSearch_PreservesExactIdOrLiteralTextWithoutPlainDecoy()
    {
        await fixture.ResetAsync();
        int literalId;
        await using (var db = fixture.Context())
        {
            var literal = Customer("Literal", "literal@example.test");
            literal.Mobile = " 1 ";
            var decoy = Customer("Decoy", "decoy@example.test");
            decoy.Mobile = "1";
            db.Customers.AddRange(literal, decoy);
            await db.SaveChangesAsync();
            literalId = literal.Id;
        }
        await using var host = fixture.Start();
        using var client = fixture.Client(host, "directory");
        var before = await fixture.SnapshotAsync();
        using var response = await client.GetAsync(Path(" 1 "));
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        await AssertPageAsync(response, [1, literalId]);
        Assert.Equal(before, await fixture.SnapshotAsync());
    }

    [Theory]
    [InlineData("anonymous", HttpStatusCode.Unauthorized)]
    [InlineData("denied", HttpStatusCode.Forbidden)]
    public async Task PaddedSearch_DoesNotBypassNormalDirectoryAuthorization(string authority, HttpStatusCode status)
    {
        await fixture.ResetAsync();
        await using var host = fixture.Start();
        using var client = fixture.Client(host, authority);
        var before = await fixture.SnapshotAsync();
        using var response = await client.GetAsync(Path(Literal));
        Assert.Equal(status, response.StatusCode);
        Assert.Equal(before, await fixture.SnapshotAsync());
    }

    private static string Path(string search) => "/customers?search=" + Uri.EscapeDataString(search);

    private static Customer Customer(string name, string email) => new() { FirstName = name, LastName = "Control", Email = email };

    private static void SetField(Customer customer, string field, string value)
    {
        switch (field)
        {
            case "FirstName": customer.FirstName = value; break;
            case "LastName": customer.LastName = value; break;
            case "Email": customer.Email = value; break;
            case "Mobile": customer.Mobile = value; break;
            case "Telephone": customer.Telephone = value; break;
            case "Company": customer.Company = new Company { Name = value }; break;
            default: throw new ArgumentOutOfRangeException(nameof(field));
        }
    }

    private static async Task AssertPageAsync(HttpResponseMessage response, int[] ids)
    {
        using var json = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        var page = json.RootElement;
        Assert.Equal(ids.Length, page.GetProperty("TotalRecords").GetInt32());
        Assert.Equal(1, page.GetProperty("PageIndex").GetInt32());
        Assert.Equal(1, page.GetProperty("TotalPages").GetInt32());
        Assert.False(page.GetProperty("HasNextPage").GetBoolean());
        var items = page.GetProperty("Items").EnumerateArray().ToArray();
        Assert.Equal(ids, items.Select(item => item.GetProperty("Id").GetInt32()).ToArray());
        foreach (var item in items)
        {
            Assert.False(item.TryGetProperty("InternalRemark", out _));
            Assert.False(item.TryGetProperty("PasswordHash", out _));
        }
    }
}
