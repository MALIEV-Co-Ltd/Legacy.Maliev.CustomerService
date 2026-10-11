using System.Net;
using System.Text.Json;
using Legacy.Maliev.CustomerService.Domain;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Caching.Distributed;
using Microsoft.Extensions.DependencyInjection;

namespace Legacy.Maliev.CustomerService.Tests.Controllers;

public sealed class CustomerDefaultPageHttpTests(CustomerDetailAuthorityFixture fixture)
    : IClassFixture<CustomerDetailAuthorityFixture>
{
    private const string Needle = "default-page-match";

    [Theory]
    [InlineData(60, 100)]
    [InlineData(100, 900)]
    [InlineData(300, 900)]
    public async Task OmittedSize_ReturnsFilteredCohortUpToApprovedCapWithExplicitPagingUnchanged(
        int matchCount, int distractorCount)
    {
        using var lifetime = new CancellationTokenSource(TimeSpan.FromSeconds(90));
        var cancellation = lifetime.Token;
        await fixture.ResetAsync();
        int[] matchingIds;
        int[] allIds;
        int companyId;
        await using (var db = fixture.Context())
        {
            var company = new Company { Name = "Shared default-page company", TaxNumber = "Synthetic tax" };
            var matches = Enumerable.Range(0, matchCount).Select(value => new Customer
            {
                FirstName = Needle,
                LastName = "Member",
                Email = $"match-{value}@example.test",
                Company = company,
                BillingAddressId = 1,
                ShippingAddressId = 1,
                InternalRemark = "synthetic staff-only match",
            }).ToArray();
            var distractors = Enumerable.Range(0, distractorCount).Select(value => new Customer
            {
                FirstName = "Distractor",
                LastName = "Member",
                Email = $"distractor-{value}@example.test",
                InternalRemark = "synthetic staff-only distractor",
            }).ToArray();
            db.Customers.AddRange(matches);
            db.Customers.AddRange(distractors);
            await db.SaveChangesAsync(cancellation);
            companyId = company.Id;
            matchingIds = matches.Select(row => row.Id).Order().ToArray();
            allIds = await db.Customers.OrderBy(row => row.Id).Select(row => row.Id).ToArrayAsync(cancellation);
            Assert.Equal(matchCount + distractorCount + 1, allIds.Length);
            Assert.Equal(2, await db.Companies.CountAsync(cancellation));
            Assert.Equal(1, await db.Addresses.CountAsync(cancellation));
        }

        var before = await SnapshotAsync(cancellation);
        await using var host = fixture.Start();
        await fixture.SeedOldAsync(host);
        var cache = host.Services.GetRequiredService<IDistributedCache>();
        var cachedBefore = await cache.GetAsync("customer:1", cancellation);
        Assert.NotNull(cachedBefore);
        using var client = fixture.Client(host, "directory");
        foreach (var (query, ids) in new[]
        {
            ("/customers?", allIds),
            ("/customers?search=" + Uri.EscapeDataString(Needle) + "&", matchingIds),
        })
        {
            var totalPages = (int)Math.Ceiling(ids.Length / 250d);
            await AssertPageAsync(client, query, ids, matchingIds, companyId, 1, 250, cancellation);
            var combined = new List<int>();
            for (var page = 1; page <= totalPages; page++)
            {
                combined.AddRange(await AssertPageAsync(client, query + $"index={page}", ids,
                    matchingIds, companyId, page, 250, cancellation));
            }

            Assert.Equal(ids, combined.ToArray());
            Assert.Equal(ids.Length, combined.Distinct().Count());
            using var exhausted = await client.GetAsync(query + $"index={totalPages + 1}", cancellation);
            Assert.Equal(HttpStatusCode.NotFound, exhausted.StatusCode);

            // The Intranet's explicit25 remains distinct from the omitted-size default.
            var explicitPages = (int)Math.Ceiling(ids.Length / 25d);
            await AssertPageAsync(client, query + "index=1&size=25", ids,
                matchingIds, companyId, 1, 25, cancellation);
            await AssertPageAsync(client, query + $"index={explicitPages}&size=25", ids,
                matchingIds, companyId, explicitPages, 25, cancellation);
            using var explicitExhausted = await client.GetAsync(query + $"index={explicitPages + 1}&size=25", cancellation);
            Assert.Equal(HttpStatusCode.NotFound, explicitExhausted.StatusCode);

            foreach (var requestedSize in new[] { 0, -1, 251, 1000 })
            {
                await AssertPageAsync(client, query + $"index=1&size={requestedSize}", ids,
                    matchingIds, companyId, 1, Math.Clamp(requestedSize, 1, 250), cancellation);
            }

            foreach (var requestedIndex in new[] { 0, -1 })
            {
                await AssertPageAsync(client, query + $"index={requestedIndex}&size=25", ids,
                    matchingIds, companyId, 1, 25, cancellation);
            }
        }

        using var noMatch = await client.GetAsync("/customers?search=absent-default-page-witness", cancellation);
        Assert.Equal(HttpStatusCode.NotFound, noMatch.StatusCode);
        Assert.Equal(before, await SnapshotAsync(cancellation));
        Assert.Equal(cachedBefore, await cache.GetAsync("customer:1", cancellation));
    }

    [Theory]
    [InlineData("anonymous", HttpStatusCode.Unauthorized)]
    [InlineData("full", HttpStatusCode.Forbidden)]
    [InlineData("wrong-signature", HttpStatusCode.Unauthorized)]
    [InlineData("expired", HttpStatusCode.Unauthorized)]
    public async Task OmittedSize_InvalidDirectoryAuthorityCannotReadOrMutateProfiles(
        string authority, HttpStatusCode expected)
    {
        using var lifetime = new CancellationTokenSource(TimeSpan.FromSeconds(90));
        var cancellation = lifetime.Token;
        await fixture.ResetAsync();
        var before = await SnapshotAsync(cancellation);
        await using var host = fixture.Start();
        await fixture.SeedOldAsync(host);
        var cache = host.Services.GetRequiredService<IDistributedCache>();
        var cachedBefore = await cache.GetAsync("customer:1", cancellation);
        Assert.NotNull(cachedBefore);
        using var client = fixture.Client(host, authority);
        foreach (var path in new[] { "/customers", "/customers?search=Before", "/customers?size=25" })
        {
            using var response = await client.GetAsync(path, cancellation);
            Assert.Equal(expected, response.StatusCode);
        }

        Assert.Equal(before, await SnapshotAsync(cancellation));
        Assert.Equal(cachedBefore, await cache.GetAsync("customer:1", cancellation));
    }

    [Theory]
    [InlineData(int.MaxValue, null)]
    [InlineData(int.MaxValue - 1, null)]
    [InlineData(int.MaxValue, 1)]
    [InlineData(int.MaxValue - 1, 1)]
    [InlineData(int.MaxValue, 250)]
    [InlineData(int.MaxValue - 1, 250)]
    public async Task DistantPage_ReturnsNotFoundWithoutOffsetOverflowOrStoredMutation(
        int index, int? size)
    {
        using var lifetime = new CancellationTokenSource(TimeSpan.FromSeconds(90));
        var cancellation = lifetime.Token;
        await fixture.ResetAsync();
        await using (var db = fixture.Context())
        {
            db.Customers.Add(new Customer
            {
                FirstName = "Distant",
                LastName = "Page",
                Email = "distant-page@example.test",
            });
            await db.SaveChangesAsync(cancellation);
        }

        var before = await SnapshotAsync(cancellation);
        await using var host = fixture.Start();
        await fixture.SeedOldAsync(host);
        var cache = host.Services.GetRequiredService<IDistributedCache>();
        var cachedBefore = await cache.GetAsync("customer:1", cancellation);
        Assert.NotNull(cachedBefore);
        using var client = fixture.Client(host, "directory");
        var sizeQuery = size is null ? string.Empty : $"&size={size}";
        using var response = await client.GetAsync($"/customers?index={index}{sizeQuery}", cancellation);
        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
        Assert.Equal(before, await SnapshotAsync(cancellation));
        Assert.Equal(cachedBefore, await cache.GetAsync("customer:1", cancellation));
    }

    private static async Task<int[]> AssertPageAsync(
        HttpClient client, string path, int[] ids, int[] matchingIds, int companyId,
        int page, int size, CancellationToken cancellation)
    {
        using var response = await client.GetAsync(path, cancellation);
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        using var json = JsonDocument.Parse(await response.Content.ReadAsStringAsync(cancellation));
        var root = json.RootElement;
        string[] keys = ["Items", "PageIndex", "TotalPages", "TotalRecords", "HasNextPage", "HasPreviousPage"];
        Assert.Equal(keys.Order(StringComparer.Ordinal),
            root.EnumerateObject().Select(property => property.Name).Order(StringComparer.Ordinal));
        var totalPages = (int)Math.Ceiling(ids.Length / (double)size);
        Assert.Equal(ids.Length, root.GetProperty("TotalRecords").GetInt32());
        Assert.Equal(totalPages, root.GetProperty("TotalPages").GetInt32());
        Assert.Equal(page, root.GetProperty("PageIndex").GetInt32());
        Assert.Equal(page < totalPages, root.GetProperty("HasNextPage").GetBoolean());
        Assert.Equal(page > 1, root.GetProperty("HasPreviousPage").GetBoolean());
        var items = root.GetProperty("Items").EnumerateArray().ToArray();
        var actualIds = items.Select(item => item.GetProperty("Id").GetInt32()).ToArray();
        Assert.Equal(ids.Skip((page - 1) * size).Take(size).ToArray(), actualIds);
        foreach (var item in items)
        {
            Assert.False(item.TryGetProperty("InternalRemark", out _));
            Assert.False(item.TryGetProperty("CustomerIdentity", out _));
            Assert.False(item.TryGetProperty("PasswordHash", out _));
            if (matchingIds.Contains(item.GetProperty("Id").GetInt32()))
            {
                Assert.Equal(companyId, item.GetProperty("CompanyId").GetInt32());
                Assert.Equal(companyId, item.GetProperty("Company").GetProperty("Id").GetInt32());
                Assert.Equal("Shared default-page company", item.GetProperty("Company").GetProperty("Name").GetString());
                Assert.Equal(1, item.GetProperty("BillingAddressId").GetInt32());
                Assert.Equal(1, item.GetProperty("ShippingAddressId").GetInt32());
                Assert.Equal(1, item.GetProperty("BillingAddress").GetProperty("Id").GetInt32());
                Assert.Equal(1, item.GetProperty("ShippingAddress").GetProperty("Id").GetInt32());
            }
            else if (item.GetProperty("Id").GetInt32() != 1)
            {
                Assert.False(item.TryGetProperty("Company", out _));
                Assert.False(item.TryGetProperty("CompanyId", out _));
                Assert.False(item.TryGetProperty("BillingAddress", out _));
                Assert.False(item.TryGetProperty("ShippingAddress", out _));
                Assert.False(item.TryGetProperty("BillingAddressId", out _));
                Assert.False(item.TryGetProperty("ShippingAddressId", out _));
            }
        }

        return actualIds;
    }

    private async Task<string> SnapshotAsync(CancellationToken cancellation)
    {
        await using var db = fixture.Context();
        return JsonSerializer.Serialize(new
        {
            Customers = await db.Customers.AsNoTracking().OrderBy(row => row.Id).Select(row => new
            {
                row.Id, row.FirstName, row.LastName, row.FullName, row.Email, row.Mobile, row.Telephone,
                row.Fax, row.DateOfBirth, row.InternalRemark, row.CompanyId, row.BillingAddressId,
                row.ShippingAddressId, row.CreatedDate, row.ModifiedDate,
                Revision = EF.Property<uint>(row, "xmin"),
            }).ToArrayAsync(cancellation),
            Companies = await db.Companies.AsNoTracking().OrderBy(row => row.Id).Select(row => new
            {
                row.Id, row.Name, row.TaxNumber, row.Registrar, row.CreatedDate, row.ModifiedDate,
            }).ToArrayAsync(cancellation),
            Addresses = await db.Addresses.AsNoTracking().OrderBy(row => row.Id).Select(row => new
            {
                row.Id, row.Building, row.AddressLine1, row.AddressLine2, row.City, row.State,
                row.PostalCode, row.CountryId, row.CreatedDate, row.ModifiedDate,
            }).ToArrayAsync(cancellation),
        });
    }
}
