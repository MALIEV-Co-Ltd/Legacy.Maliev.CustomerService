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


    // These source-parity cases are additive during the hosted RED stage. The original
    // approved-cap cases above remain intact until Root reviews the actual failures.
    [Theory]
    [InlineData(false, false, false)]
    [InlineData(false, false, true)]
    [InlineData(false, true, false)]
    [InlineData(false, true, true)]
    [InlineData(true, false, false)]
    [InlineData(true, false, true)]
    [InlineData(true, true, false)]
    [InlineData(true, true, true)]
    public async Task OrdinaryDefaultSize_SourceParityReturnsEntireCohort(
        bool filtered, bool emptySize, bool descending)
    {
        using var lifetime = new CancellationTokenSource(TimeSpan.FromSeconds(90));
        var cancellation = lifetime.Token;
        var (allIds, matchingIds, companyId) = await SeedSourcePagingCohortAsync(cancellation);
        var ids = filtered ? matchingIds : allIds;
        if (descending) ids = ids.Reverse().ToArray();
        Assert.Equal(filtered ? 1000 : 1010, ids.Length);
        var query = "/customers?sort=" + (descending ? "CustomerId_Descending" : "CustomerId_Ascending")
            + (filtered ? "&search=" + Uri.EscapeDataString(Needle) : string.Empty)
            + (emptySize ? "&size=" : string.Empty);
        await AssertSourcePagingReadOnlyAsync(async (client, token) =>
        {
            var actual = await AssertPageAsync(client, query, ids, matchingIds, companyId, 1, ids.Length, token);
            Assert.Equal(ids.Length, actual.Length);
            Assert.Equal(ids[0], actual[0]);
            Assert.Equal(ids[^1], actual[^1]);
            using var exhausted = await client.GetAsync(query + "&index=2", token);
            Assert.Equal(HttpStatusCode.NotFound, exhausted.StatusCode);
        }, cancellation);
    }

    [Theory]
    [InlineData(false, 251)]
    [InlineData(false, 1000)]
    [InlineData(false, int.MaxValue)]
    [InlineData(true, 251)]
    [InlineData(true, 1000)]
    [InlineData(true, int.MaxValue)]
    public async Task OrdinaryPositiveSize_SourceParityDoesNotApplyUpperCap(bool filtered, int size)
    {
        using var lifetime = new CancellationTokenSource(TimeSpan.FromSeconds(90));
        var cancellation = lifetime.Token;
        var (allIds, matchingIds, companyId) = await SeedSourcePagingCohortAsync(cancellation);
        var ids = filtered ? matchingIds : allIds;
        var query = "/customers?size=" + size
            + (filtered ? "&search=" + Uri.EscapeDataString(Needle) : string.Empty);
        var pages = size == 251 ? (filtered ? 4 : 5) : size == 1000 && !filtered ? 2 : 1;
        await AssertSourcePagingReadOnlyAsync(async (client, token) =>
        {
            var combined = new List<int>();
            for (var page = 1; page <= pages; page++)
            {
                combined.AddRange(await AssertPageAsync(client, query + "&index=" + page,
                    ids, matchingIds, companyId, page, size, token));
            }

            Assert.Equal(ids, combined.ToArray());
            Assert.Equal(ids.Length, combined.Distinct().Count());
            using var exhausted = await client.GetAsync(query + "&index=" + (pages + 1), token);
            Assert.Equal(HttpStatusCode.NotFound, exhausted.StatusCode);
        }, cancellation);
    }

    [Theory]
    [InlineData(0, false)]
    [InlineData(1, false)]
    [InlineData(int.MaxValue, false)]
    [InlineData(0, true)]
    [InlineData(1, true)]
    [InlineData(int.MaxValue, true)]
    public async Task OrdinaryZeroSize_SourceParityReturnsNotFound(int index, bool noMatch)
    {
        using var lifetime = new CancellationTokenSource(TimeSpan.FromSeconds(90));
        var cancellation = lifetime.Token;
        await fixture.ResetAsync();
        var query = "/customers?size=0&index=" + index
            + (noMatch ? "&search=absent-source-paging-witness" : string.Empty);
        await AssertSourcePagingReadOnlyAsync(async (client, token) =>
        {
            using var response = await client.GetAsync(query, token);
            Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
        }, cancellation);
    }

    [Theory]
    [InlineData("anonymous", HttpStatusCode.Unauthorized)]
    [InlineData("full", HttpStatusCode.Forbidden)]
    [InlineData("wrong-signature", HttpStatusCode.Unauthorized)]
    [InlineData("expired", HttpStatusCode.Unauthorized)]
    public async Task OrdinarySourcePaging_InvalidAuthorityStillCannotRead(
        string authority, HttpStatusCode expected)
    {
        using var lifetime = new CancellationTokenSource(TimeSpan.FromSeconds(90));
        var cancellation = lifetime.Token;
        await fixture.ResetAsync();
        await AssertSourcePagingReadOnlyAsync(async (client, token) =>
        {
            foreach (var query in new[]
            {
                "/customers?size=", "/customers?size=0", "/customers?size=251",
                "/customers?size=1000", "/customers?size=2147483647&search=Before",
            })
            {
                using var response = await client.GetAsync(query, token);
                Assert.Equal(expected, response.StatusCode);
            }
        }, cancellation, authority);
    }

    [Theory]
    [InlineData(-1)]
    [InlineData(int.MinValue)]
    public async Task OrdinaryNegativeSize_PreservesAcceptedLowerClamp(int size)
    {
        using var lifetime = new CancellationTokenSource(TimeSpan.FromSeconds(90));
        var cancellation = lifetime.Token;
        await fixture.ResetAsync();
        await AssertSourcePagingReadOnlyAsync(async (client, token) =>
        {
            foreach (var index in new[] { 0, -1, 1 })
            {
                await AssertPageAsync(client, $"/customers?size={size}&index={index}",
                    [1], [], 0, 1, 1, token);
            }

            using var distant = await client.GetAsync($"/customers?size={size}&index={int.MaxValue}", token);
            Assert.Equal(HttpStatusCode.NotFound, distant.StatusCode);
        }, cancellation);
    }

    private async Task<(int[] AllIds, int[] MatchingIds, int CompanyId)> SeedSourcePagingCohortAsync(
        CancellationToken cancellation)
    {
        await fixture.ResetAsync();
        await using var db = fixture.Context();
        var company = new Company { Name = "Shared default-page company", TaxNumber = "Synthetic tax" };
        var matches = Enumerable.Range(0, 1000).Select(value => new Customer
        {
            FirstName = Needle,
            LastName = "Member",
            Email = $"source-match-{value}@example.test",
            Company = company,
            BillingAddressId = 1,
            ShippingAddressId = 1,
            InternalRemark = "synthetic staff-only match",
        }).ToArray();
        db.Customers.AddRange(matches);
        db.Customers.AddRange(Enumerable.Range(0, 9).Select(value => new Customer
        {
            FirstName = "Distractor",
            LastName = "Member",
            Email = $"source-distractor-{value}@example.test",
            InternalRemark = "synthetic staff-only distractor",
        }));
        await db.SaveChangesAsync(cancellation);
        var matchingIds = matches.Select(row => row.Id).Order().ToArray();
        var allIds = await db.Customers.OrderBy(row => row.Id).Select(row => row.Id).ToArrayAsync(cancellation);
        Assert.Equal(1000, matchingIds.Length);
        Assert.Equal(1010, allIds.Length);
        return (allIds, matchingIds, company.Id);
    }

    private async Task AssertSourcePagingReadOnlyAsync(
        Func<HttpClient, CancellationToken, Task> read,
        CancellationToken cancellation,
        string authority = "directory")
    {
        var before = await SourcePagingPhysicalSnapshotAsync(cancellation);
        await using var host = fixture.Start();
        await fixture.SeedOldAsync(host);
        var cache = host.Services.GetRequiredService<IDistributedCache>();
        byte[] unrelatedCache = [0, 1, 127, 255];
        await cache.SetAsync("customer:2", unrelatedCache, cancellation);
        var cachedBefore = await cache.GetAsync("customer:1", cancellation);
        Assert.NotNull(cachedBefore);
        using var client = fixture.Client(host, authority);
        try
        {
            await read(client, cancellation);
        }
        finally
        {
            Assert.Equal(before, await SourcePagingPhysicalSnapshotAsync(cancellation));
            Assert.Equal(cachedBefore, await cache.GetAsync("customer:1", cancellation));
            Assert.Equal(unrelatedCache, await cache.GetAsync("customer:2", cancellation));
        }
    }

    private async Task<string> SourcePagingPhysicalSnapshotAsync(CancellationToken cancellation)
    {
        await using var db = fixture.Context();
        await db.Database.OpenConnectionAsync(cancellation);
        var snapshots = new List<string>();
        foreach (var table in new[]
        {
            "Customer", "Company", "Address", "CustomerCreateOperation", "QuotationProfileCompletionOperation",
        })
        {
            await using var command = db.Database.GetDbConnection().CreateCommand();
            command.CommandText = "SELECT COALESCE(jsonb_agg(to_jsonb(t) || "
                + "jsonb_build_object('_physical_xmin', t.xmin::text) ORDER BY to_jsonb(t)::text), "
                + "'[]'::jsonb)::text FROM \"" + table + "\" AS t";
            snapshots.Add(table + ":" + (string)(await command.ExecuteScalarAsync(cancellation))!);
        }

        return string.Join("\n", snapshots);
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
                row.Id,
                row.FirstName,
                row.LastName,
                row.FullName,
                row.Email,
                row.Mobile,
                row.Telephone,
                row.Fax,
                row.DateOfBirth,
                row.InternalRemark,
                row.CompanyId,
                row.BillingAddressId,
                row.ShippingAddressId,
                row.CreatedDate,
                row.ModifiedDate,
                Revision = EF.Property<uint>(row, "xmin"),
            }).ToArrayAsync(cancellation),
            Companies = await db.Companies.AsNoTracking().OrderBy(row => row.Id).Select(row => new
            {
                row.Id,
                row.Name,
                row.TaxNumber,
                row.Registrar,
                row.CreatedDate,
                row.ModifiedDate,
            }).ToArrayAsync(cancellation),
            Addresses = await db.Addresses.AsNoTracking().OrderBy(row => row.Id).Select(row => new
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
            }).ToArrayAsync(cancellation),
        });
    }
}
