using System.Net;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using Legacy.Maliev.CustomerService.Application.Models;
using Legacy.Maliev.CustomerService.Tests.OriginalInput;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.Options;
using Legacy.Maliev.CustomerService.Domain;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Caching.Distributed;
using Microsoft.Extensions.DependencyInjection;
using Xunit.Abstractions;

namespace Legacy.Maliev.CustomerService.Tests.Controllers;

[Collection("Customer company lifecycle")]
public sealed class CustomerCompanyOrdinaryDuplicateNullHttpTests(CustomerDetailAuthorityFixture fixture, ITestOutputHelper output)
    : IClassFixture<CustomerDetailAuthorityFixture>
{
    [Fact]
    public Task OrdinaryDuplicateNames_PersistLastNonNullNameAndOnlyIntendedGraphAndCacheEffects() => WriteMatrixAsync(PositiveCases());

    [Fact]
    public Task OrdinaryTaxRegistrar_PersistLastNonNullStringsAndOnlyIntendedGraphAndCacheEffects() => WriteMatrixAsync(NullablePositiveCases());

    [Fact]
    public async Task OrdinaryTaxRegistrar_InvalidAuthorityTokensAndScopedPolicyCannotMutateGraphOrCache()
    {
        using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(180));
        var token = deadline.Token;
        await using var host = fixture.Start();
        AssertActualMvc(host);
        var cache = host.Services.GetRequiredService<IDistributedCache>();
        using var editor = fixture.Client(host, "relation-editor");
        editor.Timeout = TimeSpan.FromSeconds(15);
        const string duplicate = "{\"Name\":\"ok\",\"TaxNumber\":\"tax\",\"TaxNumber\":null,\"Registrar\":\"r\",\"Registrar\":null}";
        foreach (var route in new[] { "create", "update", "scoped-create", "scoped-update" })
        {
            await SeedAsync(route, token);
            var versions = route.StartsWith("scoped-", StringComparison.Ordinal) ? await VersionsAsync(editor, route, token) : default;
            var before = await PhysicalSnapshotAsync(token);
            var cached = await PrimeAsync(cache, token);
            foreach (var kind in new[] { "anonymous", "denied", "wrong-signature", "expired" })
            {
                using var client = fixture.Client(host, kind);
                client.Timeout = TimeSpan.FromSeconds(15);
                await RefuseAsync(client, cache, route, "tax-registrar-authority/" + kind, duplicate, versions,
                    kind == "denied" ? HttpStatusCode.Forbidden : HttpStatusCode.Unauthorized, before, cached, token);
            }
            if (!route.StartsWith("scoped-", StringComparison.Ordinal))
            {
                foreach (var field in new[] { "TaxNumber", "Registrar" })
                    foreach (var invalid in new[] { "{}", "[]", "true", "42", "1e309" })
                    {
                        var json = "{\"Name\":\"ok\",\"" + field + "\":\"first\",\"" + field + "\":" + invalid
                            + ",\"" + field + "\":null,\"" + field + "\":\"later\"}";
                        if (invalid is not ("{}" or "[]")) continue; // Retained primitive cases move to successful ordinary writes below.
                        await RefuseAsync(editor, cache, route, "tax-registrar-invalid/" + field + "/" + invalid,
                            json, versions, HttpStatusCode.BadRequest, before, cached, token);
                    }
                await RefuseAsync(editor, cache, route, "tax-registrar-details-still-required",
                    "{\"Name\":\" \",\"TaxNumber\":null,\"Registrar\":\"r\",\"Registrar\":null}", versions,
                    HttpStatusCode.BadRequest, before, cached, token);
                continue;
            }

            // Scoped strings deliberately keep their existing last-null policy, including ETag enforcement.
            using var request = Request(route, duplicate, versions);
            using var response = await editor.SendAsync(request, token);
            Assert.Equal(HttpStatusCode.NoContent, response.StatusCode);
            Assert.Equal("", await response.Content.ReadAsStringAsync(token));
            var id = int.Parse(response.Headers.GetValues("X-Relation-Id").Single(), System.Globalization.CultureInfo.InvariantCulture);
            var after = await PhysicalSnapshotAsync(token);
            AssertPreservedGraph(before, after, id, true, route == "scoped-create");
            await AssertCacheAsync(cache, cached, route == "scoped-create" ? [1] : [1, 2], token);
            await using (var db = fixture.Context())
            {
                var stored = await db.Companies.AsNoTracking().SingleAsync(value => value.Id == id, token);
                Assert.Equal("ok", stored.Name);
                Assert.Null(stored.TaxNumber);
                Assert.Null(stored.Registrar);
            }
            foreach (var path in new[] { $"/customers/companies/{id}", $"/customers/1/relations/company/{id}/edit" })
            {
                using var read = await editor.GetAsync(path, token);
                Assert.Equal(HttpStatusCode.OK, read.StatusCode);
                using var body = JsonDocument.Parse(await read.Content.ReadAsStringAsync(token));
                var value = body.RootElement.TryGetProperty("Company", out var company) ? company : body.RootElement;
                AssertLiteral(value, new("scoped-tax-registrar-last-null", "", "ok", null, null));
            }
            var replayCache = await PrimeAsync(cache, token);
            using var staleRequest = Request(route, duplicate, versions);
            using var stale = await editor.SendAsync(staleRequest, token);
            Assert.Equal(HttpStatusCode.PreconditionFailed, stale.StatusCode);
            Assert.Equal(after, await PhysicalSnapshotAsync(token));
            await AssertCacheAsync(cache, replayCache, [], token);
            output.WriteLine(JsonSerializer.Serialize(new { Route = route, Case = "tax-registrar-scoped-last-null", Status = (int)response.StatusCode }));
            output.WriteLine(JsonSerializer.Serialize(new { Route = route, Case = "tax-registrar-scoped-stale", Status = (int)stale.StatusCode }));
        }
        await WriteMatrixAsync(PrimitiveEarlierCases());
    }

    [Fact]
    public async Task OrdinaryDuplicateNames_InvalidOccurrenceCannotHideBehindLaterValuesOrMutateGraph()
    {
        using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(120));
        var token = deadline.Token;
        await using var host = fixture.Start();
        AssertActualMvc(host);
        using var client = fixture.Client(host, "relation-editor");
        client.Timeout = TimeSpan.FromSeconds(15);
        var cache = host.Services.GetRequiredService<IDistributedCache>();
        foreach (var route in new[] { "create", "update" })
        {
            await SeedAsync(route, token);
            var before = await PhysicalSnapshotAsync(token);
            var cached = await PrimeAsync(cache, token);
            foreach (var item in RefusalCases())
                if (item.Label is not ("other-field-control-33" or "other-field-control-34"
                    or "immediate-binding-error-26" or "immediate-binding-error-27" or "immediate-binding-error-28"))
                    await RefuseAsync(client, cache, route, item.Label, item.Json, default, HttpStatusCode.BadRequest, before, cached, token);
        }
        var sourceAccepted = RefusalCases().Where(item => item.Label is "other-field-control-33" or "other-field-control-34"
            or "immediate-binding-error-26" or "immediate-binding-error-27" or "immediate-binding-error-28")
            .Select(item => new PositiveCase(item.Label, item.Json, item.Label switch
            {
                "immediate-binding-error-26" => "last",
                "immediate-binding-error-27" => "\uFFFD",
                "immediate-binding-error-28" => "first",
                _ => "ok"
            }, item.Label == "other-field-control-33" ? "42" : null,
                item.Label == "other-field-control-34" ? "true" : null)).ToArray();
        foreach (var item in sourceAccepted)
        {
            // Keep every original input; only the obsolete native-reader expectation changes.
            var source = await CustomerOriginalInputOracle.ReadAsync("company", item.Json);
            Assert.Equal("success", source.Kind);
            var model = Assert.IsType<SourceCompany>(source.Model);
            Assert.Equal(item.Name, model.Name);
            Assert.Equal(item.Tax, model.TaxNumber);
            Assert.Equal(item.Registrar, model.Registrar);
        }
        await WriteMatrixAsync(sourceAccepted);
    }

    [Fact]
    public async Task OrdinaryDuplicateNames_InvalidAuthorityAndScopedLiteralPolicyCannotChangeGraphOrCache()
    {
        using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(120));
        var token = deadline.Token;
        await using var host = fixture.Start();
        AssertActualMvc(host);
        var cache = host.Services.GetRequiredService<IDistributedCache>();
        using var editor = fixture.Client(host, "relation-editor");
        editor.Timeout = TimeSpan.FromSeconds(15);
        foreach (var route in new[] { "create", "update", "scoped-create", "scoped-update" })
        {
            await SeedAsync(route, token);
            var versions = route.StartsWith("scoped-", StringComparison.Ordinal) ? await VersionsAsync(editor, route, token) : default;
            var before = await PhysicalSnapshotAsync(token);
            var cached = await PrimeAsync(cache, token);
            foreach (var kind in new[] { "anonymous", "denied", "wrong-signature", "expired" })
            {
                using var client = fixture.Client(host, kind);
                client.Timeout = TimeSpan.FromSeconds(15);
                foreach (var item in AuthorityCases())
                    await RefuseAsync(client, cache, route, kind + "/" + item.Label, item.Json, versions,
                        kind == "denied" ? HttpStatusCode.Forbidden : HttpStatusCode.Unauthorized, before, cached, token);
            }
        }
        foreach (var route in new[] { "scoped-create", "scoped-update" })
        {
            await SeedAsync(route, token);
            var versions = await VersionsAsync(editor, route, token);
            var before = await PhysicalSnapshotAsync(token);
            var cached = await PrimeAsync(cache, token);
            foreach (var item in AuthorityCases().Where(item => item.Label != "authority-scoped-control-41"))
                await RefuseAsync(editor, cache, route, "scoped/" + item.Label, item.Json, versions, HttpStatusCode.BadRequest, before, cached, token);
            // Scoped policy keeps its existing last assignment: null first then literal is valid, not blanket duplicate refusal.
            using var request = Request(route, "{\"Name\":null,\"Name\":\"ok\"}", versions);
            var started = DateTime.UtcNow;
            using var response = await editor.SendAsync(request, token);
            var ended = DateTime.UtcNow;
            Assert.Equal(HttpStatusCode.NoContent, response.StatusCode);
            Assert.Equal("", await response.Content.ReadAsStringAsync(token));
            var id = int.Parse(response.Headers.GetValues("X-Relation-Id").Single(), System.Globalization.CultureInfo.InvariantCulture);
            var after = await PhysicalSnapshotAsync(token);
            AssertPreservedGraph(before, after, id, true, route == "scoped-create");
            await AssertCacheAsync(cache, cached, route == "scoped-create" ? [1] : [1, 2], token);
            await using var db = fixture.Context();
            var saved = await db.Companies.AsNoTracking().SingleAsync(value => value.Id == id, token);
            Assert.Equal("ok", saved.Name);
            Assert.Null(saved.TaxNumber);
            Assert.Null(saved.Registrar);
            var modifiedDate = Assert.NotNull<DateTime>(saved.ModifiedDate);
            Assert.InRange(modifiedDate, started.AddSeconds(-1), ended.AddSeconds(1));
            using var oldGraph = JsonDocument.Parse(before);
            using var newGraph = JsonDocument.Parse(after);
            var stored = newGraph.RootElement.GetProperty("companies").EnumerateArray().Single(value => value.GetProperty("ID").GetInt32() == id);
            if (route == "scoped-create")
            {
                Assert.True(id > 2);
                Assert.Equal(saved.CreatedDate, saved.ModifiedDate);
                Assert.False(string.IsNullOrWhiteSpace(stored.GetProperty("xmin").GetString()));
            }
            else
            {
                var oldCompany = oldGraph.RootElement.GetProperty("companies").EnumerateArray().Single(value => value.GetProperty("ID").GetInt32() == id);
                foreach (var field in oldCompany.EnumerateObject())
                    if (field.Name is not ("Name" or "TaxNumber" or "Registrar" or "ModifiedDate" or "xmin"))
                        Assert.Equal(field.Value.GetRawText(), stored.GetProperty(field.Name).GetRawText());
                Assert.NotEqual(oldCompany.GetProperty("xmin").GetString(), stored.GetProperty("xmin").GetString());
            }
            var customerPostimage = newGraph.RootElement.GetProperty("customers").EnumerateArray().Single(value => value.GetProperty("ID").GetInt32() == 1);
            Assert.InRange(customerPostimage.GetProperty("ModifiedDate").GetDateTime(), started.AddSeconds(-1), ended.AddSeconds(1));
            foreach (var customerId in route == "scoped-create" ? new[] { 1 } : new[] { 1, 2 })
            {
                using var projection = await editor.GetAsync($"/customers/{customerId}", token);
                Assert.Equal(HttpStatusCode.OK, projection.StatusCode);
                using var projectionBody = JsonDocument.Parse(await projection.Content.ReadAsStringAsync(token));
                AssertLiteral(projectionBody.RootElement.GetProperty("Company"), new("scoped-null-first", "", "ok", null, null));
                Assert.False(projectionBody.RootElement.TryGetProperty("InternalRemark", out _));
            }
            using var companyRead = await editor.GetAsync($"/customers/companies/{id}", token);
            Assert.Equal(HttpStatusCode.OK, companyRead.StatusCode);
            using var companyBody = JsonDocument.Parse(await companyRead.Content.ReadAsStringAsync(token));
            AssertLiteral(companyBody.RootElement, new("scoped-null-first", "", "ok", null, null));
            using var read = await editor.GetAsync($"/customers/1/relations/company/{id}/edit", token);
            Assert.Equal(HttpStatusCode.OK, read.StatusCode);
            Assert.Equal(response.Headers.ETag, read.Headers.ETag);
            Assert.Equal(response.Headers.GetValues("X-Customer-ETag").Single(), read.Headers.GetValues("X-Customer-ETag").Single());
            using var body = JsonDocument.Parse(await read.Content.ReadAsStringAsync(token));
            AssertLiteral(body.RootElement.GetProperty("Company"), new("scoped-null-first", "", "ok", null, null));
            var replayCache = await PrimeAsync(cache, token);
            using var staleRequest = Request(route, "{\"Name\":\"stale\"}", versions);
            using var stale = await editor.SendAsync(staleRequest, token);
            Assert.Equal(HttpStatusCode.PreconditionFailed, stale.StatusCode);
            Assert.Equal(after, await PhysicalSnapshotAsync(token));
            await AssertCacheAsync(cache, replayCache, [], token);
            output.WriteLine(JsonSerializer.Serialize(new { Route = route, Case = "scoped-null-first-literal-last", Status = (int)response.StatusCode }));
        }
    }

    private static void AssertActualMvc(WebApplicationFactory<Program> host)
    {
        var options = host.Services.GetRequiredService<IOptions<JsonOptions>>().Value.JsonSerializerOptions;
        Assert.Equal(JsonIgnoreCondition.WhenWritingNull, options.DefaultIgnoreCondition);
        Assert.True(options.PropertyNameCaseInsensitive);
        Assert.Null(options.PropertyNamingPolicy);
        Assert.Null(options.DictionaryKeyPolicy);
        Assert.Null(options.ReferenceHandler);
        Assert.False(options.RespectRequiredConstructorParameters);
        Assert.False(options.RespectNullableAnnotations);
        var resolver = options.TypeInfoResolver;
        Assert.NotNull(resolver);
        Assert.NotEqual("DefaultJsonTypeInfoResolver", resolver.GetType().Name);
        var ordinary = options.GetTypeInfo(typeof(UpsertCompanyRequest));
        Assert.NotNull(ordinary.CreateObject);
        Assert.IsType<UpsertCompanyRequest>(ordinary.CreateObject!());
        Assert.NotNull(ordinary.Properties.Single(value => value.Name == "Name").Set);
        Assert.Null(options.GetTypeInfo(typeof(CustomerCompanyRelationRequest)).CreateObject);
        Assert.Empty(typeof(UpsertCompanyRequest).GetCustomAttributes(typeof(JsonConverterAttribute), inherit: false));
    }

    private async Task RefuseAsync(HttpClient client, IDistributedCache cache, string route, string label, string json,
        (string? Metadata, string? Customer) versions, HttpStatusCode expected, string before, Dictionary<int, byte[]> cached, CancellationToken token)
    {
        using var request = Request(route, json, versions);
        using var response = await client.SendAsync(request, token);
        Assert.Equal(expected, response.StatusCode);
        var raw = await response.Content.ReadAsStringAsync(token);
        if (expected == HttpStatusCode.BadRequest)
        {
            Assert.Equal("application/problem+json", response.Content.Headers.ContentType!.MediaType);
            using var problem = JsonDocument.Parse(raw);
            Assert.Equal(400, problem.RootElement.GetProperty("status").GetInt32());
            foreach (var key in new[] { "type", "title", "traceId" }) Assert.False(string.IsNullOrWhiteSpace(problem.RootElement.GetProperty(key).GetString()));
            if (problem.RootElement.TryGetProperty("errors", out var errors))
            {
                Assert.Equal(JsonValueKind.Object, errors.ValueKind);
                Assert.NotEmpty(errors.EnumerateObject());
                foreach (var error in errors.EnumerateObject())
                {
                    Assert.Equal(JsonValueKind.Array, error.Value.ValueKind);
                    Assert.NotEmpty(error.Value.EnumerateArray());
                    foreach (var message in error.Value.EnumerateArray()) Assert.False(string.IsNullOrWhiteSpace(message.GetString()));
                }
            }
        }
        foreach (var value in new[] { "staff-only", "PasswordHash", "SecurityStamp", "ConnectionStrings", "Npgsql", "SELECT ", "synthetic-private-member" })
            Assert.DoesNotContain(value, raw, StringComparison.OrdinalIgnoreCase);
        Assert.Equal(before, await PhysicalSnapshotAsync(token));
        await AssertCacheAsync(cache, cached, [], token);
        output.WriteLine(JsonSerializer.Serialize(new { Route = route, Case = label, Status = (int)response.StatusCode }));
    }

    private async Task WriteMatrixAsync(PositiveCase[] cases)
    {
        using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(120));
        var token = deadline.Token;
        await using var host = fixture.Start();
        using var client = fixture.Client(host, "relation-editor");
        client.Timeout = TimeSpan.FromSeconds(15);
        AssertActualMvc(host);
        var cache = host.Services.GetRequiredService<IDistributedCache>();
        foreach (var route in new[] { "create", "update" })
            foreach (var item in cases)
            {
                await SeedAsync(route, token);
                var create = route == "create";
                var before = await PhysicalSnapshotAsync(token);
                var cached = await PrimeAsync(cache, token);
                using var request = Request(route, item.Json, default);
                var started = DateTime.UtcNow;
                using var response = await client.SendAsync(request, token);
                var ended = DateTime.UtcNow;
                Assert.Equal(create ? HttpStatusCode.Created : HttpStatusCode.NoContent, response.StatusCode);
                output.WriteLine(JsonSerializer.Serialize(new { Route = route, Case = item.Label, Status = (int)response.StatusCode }));
                var id = 1;
                if (create)
                {
                    using var created = JsonDocument.Parse(await response.Content.ReadAsStringAsync(token));
                    id = created.RootElement.GetProperty("Id").GetInt32();
                    AssertLiteral(created.RootElement, item);
                    Assert.True(id > 2);
                    Assert.EndsWith($"/customers/Companies/{id}", response.Headers.Location!.ToString(), StringComparison.OrdinalIgnoreCase);
                }
                else Assert.Equal("", await response.Content.ReadAsStringAsync(token));

                var after = await PhysicalSnapshotAsync(token);
                AssertPreservedGraph(before, after, id, false, create);
                using var postimage = JsonDocument.Parse(after);
                var changed = postimage.RootElement.GetProperty("companies").EnumerateArray().Single(row => row.GetProperty("ID").GetInt32() == id);
                Assert.Equal(item.Name, changed.GetProperty("Name").GetString());
                Assert.Equal(item.Tax, changed.GetProperty("TaxNumber").GetString());
                Assert.Equal(item.Registrar, changed.GetProperty("Registrar").GetString());
                Assert.InRange(changed.GetProperty("ModifiedDate").GetDateTime(), started.AddSeconds(-1), ended.AddSeconds(1));
                if (create)
                {
                    Assert.Equal(changed.GetProperty("CreatedDate").GetRawText(), changed.GetProperty("ModifiedDate").GetRawText());
                    Assert.False(string.IsNullOrWhiteSpace(changed.GetProperty("xmin").GetString()));
                }
                else
                {
                    using var original = JsonDocument.Parse(before);
                    var previous = original.RootElement.GetProperty("companies").EnumerateArray().Single(row => row.GetProperty("ID").GetInt32() == id);
                    foreach (var field in previous.EnumerateObject())
                        if (field.Name is not ("Name" or "TaxNumber" or "Registrar" or "ModifiedDate" or "xmin"))
                            Assert.Equal(field.Value.GetRawText(), changed.GetProperty(field.Name).GetRawText());
                    Assert.NotEqual(previous.GetProperty("xmin").GetString(), changed.GetProperty("xmin").GetString());
                }
                await AssertCacheAsync(cache, cached, create ? [] : [1, 2], token);
                using var read = await client.GetAsync($"/customers/companies/{id}", token);
                Assert.Equal(HttpStatusCode.OK, read.StatusCode);
                using var body = JsonDocument.Parse(await read.Content.ReadAsStringAsync(token));
                AssertLiteral(body.RootElement, item);
                if (!create)
                {
                    foreach (var customerId in new[] { 1, 2 })
                    {
                        using var customer = await client.GetAsync($"/customers/{customerId}", token);
                        Assert.Equal(HttpStatusCode.OK, customer.StatusCode);
                        using var customerBody = JsonDocument.Parse(await customer.Content.ReadAsStringAsync(token));
                        AssertLiteral(customerBody.RootElement.GetProperty("Company"), item);
                        Assert.False(customerBody.RootElement.TryGetProperty("InternalRemark", out _));
                    }
                }
            }
    }

    private static HttpRequestMessage Request(string route, string json, (string? Metadata, string? Customer) versions)
    {
        var path = route switch
        {
            "create" => "/customers/companies/",
            "update" => "/customers/companies/1/",
            "scoped-create" => "/customers/1/relations/company/new/versioned",
            "scoped-update" => "/customers/1/relations/company/1/versioned",
            _ => throw new ArgumentOutOfRangeException(nameof(route)),
        };
        var request = new HttpRequestMessage(route == "create" ? HttpMethod.Post : HttpMethod.Put, path)
        {
            Content = new StringContent(json, Encoding.UTF8, "application/json"),
        };
        if (versions.Metadata is not null) request.Headers.TryAddWithoutValidation("If-Match", versions.Metadata);
        if (versions.Customer is not null) request.Headers.TryAddWithoutValidation("X-Customer-If-Match", versions.Customer);
        return request;
    }

    private static async Task<(string? Metadata, string? Customer)> VersionsAsync(HttpClient client, string route, CancellationToken token)
    {
        using var read = await client.GetAsync("/customers/1/relations/company/" + (route == "scoped-create" ? "new" : "1/edit"), token);
        Assert.Equal(HttpStatusCode.OK, read.StatusCode);
        return (read.Headers.ETag!.ToString(), read.Headers.GetValues("X-Customer-ETag").Single());
    }

    private static void AssertLiteral(JsonElement value, PositiveCase item)
    {
        Assert.Equal(item.Name, value.GetProperty("Name").GetString());
        AssertNullable(value, "TaxNumber", item.Tax);
        AssertNullable(value, "Registrar", item.Registrar);
        Assert.False(value.TryGetProperty("name", out _));
        foreach (var key in new[] { "InternalRemark", "PasswordHash", "SecurityStamp", "Token" }) Assert.False(value.TryGetProperty(key, out _));
    }

    private static void AssertNullable(JsonElement value, string field, string? expected)
    {
        if (expected is null) Assert.False(value.TryGetProperty(field, out _));
        else Assert.Equal(expected, value.GetProperty(field).GetString());
    }

    private sealed record PositiveCase(string Label, string Json, string Name, string? Tax, string? Registrar);

    private static PositiveCase[] PrimitiveEarlierCases() =>
        (from field in new[] { "TaxNumber", "Registrar" }
         from primitive in new[] { "true", "42", "1e309" }
         let json = "{\"Name\":\"ok\",\"" + field + "\":\"first\",\"" + field + "\":" + primitive
             + ",\"" + field + "\":null,\"" + field + "\":\"later\"}"
         select new PositiveCase("tax-registrar-invalid/" + field + "/" + primitive, json, "ok",
             field == "TaxNumber" ? "later" : null, field == "Registrar" ? "later" : null)).ToArray();

    private static PositiveCase[] NullablePositiveCases() =>
    [
        new("tax-registrar-missing", "{\"Name\":\"ok\"}", "ok", null, null),
        new("tax-registrar-null-only", "{\"Name\":\"ok\",\"TaxNumber\":null,\"Registrar\":null}", "ok", null, null),
        new("tax-null-first-last", "{\"Name\":\"ok\",\"TaxNumber\":null,\"TaxNumber\":\"tax\",\"TaxNumber\":null}", "ok", "tax", null),
        new("registrar-null-first-last", "{\"Name\":\"ok\",\"Registrar\":null,\"Registrar\":\"r\",\"Registrar\":null}", "ok", null, "r"),
        new("tax-registrar-both-last-null", "{\"Name\":\"ok\",\"Name\":null,\"TaxNumber\":\"tax\",\"TaxNumber\":null,\"Registrar\":\"r\",\"Registrar\":null}", "ok", "tax", "r"),
        new("tax-registrar-null-between", "{\"Name\":\"ok\",\"TaxNumber\":\"first\",\"TaxNumber\":null,\"TaxNumber\":\"last\",\"Registrar\":\"first-r\",\"Registrar\":null,\"Registrar\":\"last-r\"}", "ok", "last", "last-r"),
        new("tax-registrar-repeated-last-null", "{\"Name\":\"ok\",\"TaxNumber\":\"last\",\"TaxNumber\":null,\"TaxNumber\":null,\"Registrar\":\"r\",\"Registrar\":null,\"Registrar\":null}", "ok", "last", "r"),
        new("tax-registrar-final-empty", "{\"Name\":\"ok\",\"TaxNumber\":\"first\",\"TaxNumber\":\"\",\"TaxNumber\":null,\"Registrar\":\"first\",\"Registrar\":\"\",\"Registrar\":null}", "ok", "", ""),
        new("tax-registrar-final-whitespace", "{\"Name\":\"ok\",\"TaxNumber\":\" \\t \",\"TaxNumber\":null,\"Registrar\":\" \\t \",\"Registrar\":null}", "ok", " \t ", " \t "),
        new("tax-registrar-case-variants", "{\"Name\":\"ok\",\"taxnumber\":\"first\",\"TAXNUMBER\":\"tax\",\"TaxNumber\":null,\"REGISTRAR\":\"first\",\"registrar\":\"r\",\"Registrar\":null}", "ok", "tax", "r"),
        new("tax-registrar-escaped-first-key", "{\"Name\":\"ok\",\"\\u0054axNumber\":\"tax\",\"\\u0074axNumber\":null,\"\\u0052egistrar\":\"r\",\"\\u0072egistrar\":null}", "ok", "tax", "r"),
        new("tax-registrar-escaped-middle-key", "{\"Name\":\"ok\",\"Tax\\u004Eumber\":\"tax\",\"taxnumber\":null,\"Regi\\u0073trar\":\"r\",\"REGISTRAR\":null}", "ok", "tax", "r"),
        new("tax-registrar-literal-unicode", "{\"Name\":\"ok\",\"TaxNumber\":\" ก้ 😀 \",\"TaxNumber\":null,\"Registrar\":\" บริษัท 😀 \",\"Registrar\":null}", "ok", " ก้ 😀 ", " บริษัท 😀 "),
        new("tax-registrar-escaped-unicode", "{\"Name\":\"ok\",\"TaxNumber\":\"\\u0E01\\u0E49 \\uD83D\\uDE00\",\"TaxNumber\":null,\"Registrar\":\"\\u0E01 \\uD83D\\uDE00\",\"Registrar\":null}", "ok", "ก้ 😀", "ก 😀"),
        new("tax-registrar-earlier257-final-bounded", "{\"Name\":\"ok\",\"TaxNumber\":" + JsonSerializer.Serialize(new string('x', 257)) + ",\"TaxNumber\":\"tax\",\"Registrar\":" + JsonSerializer.Serialize(new string('x', 257)) + ",\"Registrar\":\"r\"}", "ok", "tax", "r"),
        new("tax-last-null-details", "{\"Name\":\" \",\"TaxNumber\":\"tax\",\"TaxNumber\":null,\"Registrar\":null}", " ", "tax", null),
    ];

    private static PositiveCase[] PositiveCases() =>
    [
        new("positive-name-1", "{\"Name\":\" \u0e01\u0e49 \ud83d\ude00 \\t \",\"Name\":null}", " \u0e01\u0e49 \ud83d\ude00 \t ", null, null),
        new("positive-name-2", "{\"Name\":null,\"Name\":\" \u0e01\u0e49 \ud83d\ude00 \\t \"}", " \u0e01\u0e49 \ud83d\ude00 \t ", null, null),
        new("positive-name-3", "{\"Name\":null,\"Name\":\" \u0e01\u0e49 \ud83d\ude00 \\t \",\"Name\":null,\"Name\":null}", " \u0e01\u0e49 \ud83d\ude00 \t ", null, null),
        new("positive-name-4", "{\"Name\":\" \u0e01\u0e49 \ud83d\ude00 \\t \",\"name\":null}", " \u0e01\u0e49 \ud83d\ude00 \t ", null, null),
        new("positive-name-5", "{\"nAmE\":\" \u0e01\u0e49 \ud83d\ude00 \\t \",\"NAME\":null}", " \u0e01\u0e49 \ud83d\ude00 \t ", null, null),
        new("positive-name-6", "{\"\\u004eame\":\" \u0e01\u0e49 \ud83d\ude00 \\t \",\"\\u006eame\":null}", " \u0e01\u0e49 \ud83d\ude00 \t ", null, null),
        new("positive-name-7", "{\"Name\":\"first\",\"name\":\"last\",\"NAME\":null}", "last", null, null),
        new("positive-name-8", "{\"Name\":\" \u0e01\u0e49 \ud83d\ude00 \\t \",\"Name\":true,\"Name\":null}", "true", null, null),
        new("positive-name-9", "{\"Name\":42,\"name\":null}", "42", null, null),
        new("positive-name-10", "{\"Name\":1E+03,\"Name\":null}", "1E+03", null, null),
        new("positive-name-11", "{\"Name\":1e309,\"Name\":null}", "1e309", null, null),
        new("positive-name-12", "{\"Name\":false,\"Name\":null,\"Name\":\"last\"}", "last", null, null),
        new("positive-name-13", "{\"Name\":\"1111111111111111111111111111111111111111111111111111111111111111111111111111111111111111111111111111111111111111111111111111111111111111111111111111111111111111111111111111111111111111111111111111111111111111111111111111111111111111111111111111111111111111\",\"Name\":null}", "1111111111111111111111111111111111111111111111111111111111111111111111111111111111111111111111111111111111111111111111111111111111111111111111111111111111111111111111111111111111111111111111111111111111111111111111111111111111111111111111111111111111111111", null, null),
        new("positive-name-14", "{\"Name\":\"xxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxx\",\"Name\":\"last\"}", "last", null, null),
        // Intentional repair of accepted Name-only behavior; retain this Fact identity and every existing case.
        new("other-field-control-31", "{\"Name\":\"ok\",\"Name\":null,\"TaxNumber\":\"first\",\"TaxNumber\":null,\"Registrar\":\"first\",\"Registrar\":null}", "ok", "first", "first"),
        new("other-field-control-32", "{\"Name\":\"ok\",\"name\":null,\"taxnumber\":\"tax\",\"REGISTRAR\":\"registrar\"}", "ok", "tax", "registrar"),
        new("other-field-control-37", "{\"Name\":\"ok\",\"Name\":null,\"unknown\":{\"nested\":[null,true,42]}}", "ok", null, null),
        new("other-field-control-38", "{\"Name\":\"ok\",\"Name\":null,\"$type\":\"unknown-metadata\",\"$id\":\"irrelevant\"}", "ok", null, null),
    ];

    private static (string Label, string Json)[] RefusalCases() =>
    [
        ("final-validation-15", "{}"),
        ("final-validation-16", "{\"Name\":null}"),
        ("final-validation-17", "{\"Name\":null,\"name\":null}"),
        ("final-validation-18", "{\"Name\":\"\",\"Name\":null}"),
        ("final-validation-19", "{\"Name\":\"   \",\"name\":null}"),
        ("final-validation-20", "{\"Name\":\"xxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxx\",\"Name\":null}"),
        ("final-validation-21", "{\"Name\":\"xxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxx\" ,\"Name\":\"xxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxx\",\"Name\":null}"),
        ("immediate-binding-error-22", "{\"Name\":{},\"Name\":\"last\",\"Name\":null}"),
        ("immediate-binding-error-23", "{\"Name\":[],\"Name\":\"last\"}"),
        ("immediate-binding-error-24", "{\"Name\":\"first\",\"Name\":{}}"),
        ("immediate-binding-error-25", "{\"Name\":\"first\",\"Name\":null,\"Name\":[]}"),
        ("immediate-binding-error-26", "{\"Name\":\"\\uD800\",\"Name\":\"last\"}"),
        ("immediate-binding-error-27", "{\"Name\":\"first\",\"Name\":\"\\uDC00\",\"Name\":null}"),
        ("immediate-binding-error-28", "{\"Name\":\"first\",\"Name\":null,}"),
        ("immediate-binding-error-29", "[\"first\",null]"),
        ("immediate-binding-error-30", "true"),
        ("other-field-control-33", "{\"Name\":\"ok\",\"Name\":null,\"TaxNumber\":42}"),
        ("other-field-control-34", "{\"Name\":\"ok\",\"Name\":null,\"Registrar\":true}"),
        ("other-field-control-35", "{\"Name\":\"ok\",\"Name\":null,\"TaxNumber\":{},\"TaxNumber\":\"last\"}"),
        ("other-field-control-36", "{\"Name\":\"ok\",\"Name\":null,\"Registrar\":[],\"Registrar\":\"last\"}"),
        ("final-nul", "{\"Name\":\"ok\",\"Name\":\"a\\u0000b\",\"Name\":null}"),
    ];

    private static (string Label, string Json)[] AuthorityCases() =>
    [
        ("authority-scoped-control-39", "{\"Name\":\"ok\",\"Name\":null}"),
        ("authority-scoped-control-40", "{\"Name\":true,\"Name\":null}"),
        ("authority-scoped-control-41", "{\"Name\":null,\"Name\":\"ok\"}"),
        ("authority-scoped-control-42", "{\"Name\":\"first\",\"name\":\"last\",\"NAME\":null}"),
    ];

    private static void AssertPreservedGraph(string before, string after, int changedCompany, bool scoped, bool create)
    {
        using var old = JsonDocument.Parse(before);
        using var current = JsonDocument.Parse(after);
        Assert.Equal(old.RootElement.GetProperty("addresses").GetRawText(), current.RootElement.GetProperty("addresses").GetRawText());
        Assert.Equal(old.RootElement.GetProperty("replay").GetRawText(), current.RootElement.GetProperty("replay").GetRawText());
        var companies = current.RootElement.GetProperty("companies").EnumerateArray().ToArray();
        Assert.Equal(create ? 3 : 2, companies.Length);
        foreach (var row in old.RootElement.GetProperty("companies").EnumerateArray())
            if (row.GetProperty("ID").GetInt32() != changedCompany)
                Assert.Equal(row.GetRawText(), companies.Single(value => value.GetProperty("ID").GetInt32() == row.GetProperty("ID").GetInt32()).GetRawText());
        var customers = current.RootElement.GetProperty("customers").EnumerateArray().ToArray();
        Assert.Equal(3, customers.Length);
        foreach (var row in old.RootElement.GetProperty("customers").EnumerateArray())
        {
            var id = row.GetProperty("ID").GetInt32();
            var saved = customers.Single(value => value.GetProperty("ID").GetInt32() == id);
            if (!scoped || id != 1) Assert.Equal(row.GetRawText(), saved.GetRawText());
            else
            {
                foreach (var property in row.EnumerateObject())
                    if (property.Name is not ("CompanyID" or "ModifiedDate" or "xmin"))
                        Assert.Equal(property.Value.GetRawText(), saved.GetProperty(property.Name).GetRawText());
                Assert.Equal(changedCompany, saved.GetProperty("CompanyID").GetInt32());
                Assert.NotEqual(row.GetProperty("xmin").GetString(), saved.GetProperty("xmin").GetString());
            }
        }
    }

    private static async Task<Dictionary<int, byte[]>> PrimeAsync(IDistributedCache cache, CancellationToken token)
    {
        var entries = new Dictionary<int, byte[]>();
        foreach (var id in new[] { 1, 2, 3 })
        {
            var bytes = Encoding.UTF8.GetBytes($"{{\"id\":{id},\"firstName\":\"Cached\",\"lastName\":\"Synthetic\",\"email\":\"cached-{id}@example.invalid\"}}");
            await cache.SetAsync($"customer:{id}", bytes, new DistributedCacheEntryOptions { AbsoluteExpirationRelativeToNow = TimeSpan.FromMinutes(5) }, token);
            entries.Add(id, bytes);
        }
        return entries;
    }

    private static async Task AssertCacheAsync(IDistributedCache cache, Dictionary<int, byte[]> before, int[] evicted, CancellationToken token)
    {
        foreach (var pair in before)
            if (evicted.Contains(pair.Key)) Assert.Null(await cache.GetAsync($"customer:{pair.Key}", token));
            else Assert.Equal(pair.Value, await cache.GetAsync($"customer:{pair.Key}", token));
    }

    private async Task SeedAsync(string route, CancellationToken token)
    {
        await fixture.ResetAsync();
        await using var db = fixture.Context();
        var original = await db.Companies.SingleAsync(row => row.Id == 1, token);
        original.CreatedDate = new DateTime(2020, 1, 1);
        db.Customers.Add(new Customer { FirstName = "Shared", LastName = "Synthetic", Email = "shared@example.invalid", CompanyId = 1, ShippingAddressId = 1 });
        await db.SaveChangesAsync(token);
        db.Customers.Add(new Customer { FirstName = "Unrelated", LastName = "Synthetic", Email = "unrelated@example.invalid", Company = new Company { Name = "Unrelated company" } });
        if (route == "scoped-create") (await db.Customers.SingleAsync(row => row.Id == 1, token)).CompanyId = null;
        await db.SaveChangesAsync(token);
    }

    private async Task<string> PhysicalSnapshotAsync(CancellationToken token)
    {
        await using var db = fixture.Context();
        return await db.Database.SqlQueryRaw<string>("""
            SELECT jsonb_build_object(
              'customers', (SELECT jsonb_agg(to_jsonb(c) || jsonb_build_object('xmin', c.xmin::text) ORDER BY c."ID") FROM "Customer" c),
              'companies', (SELECT jsonb_agg(to_jsonb(c) || jsonb_build_object('xmin', c.xmin::text) ORDER BY c."ID") FROM "Company" c),
              'addresses', (SELECT jsonb_agg(to_jsonb(a) || jsonb_build_object('xmin', a.xmin::text) ORDER BY a."ID") FROM "Address" a),
              'replay', (SELECT jsonb_agg(to_jsonb(r) || jsonb_build_object('xmin', r.xmin::text) ORDER BY r."Key") FROM "CustomerCreateOperation" r)
            )::text AS "Value"
            """).SingleAsync(token);
    }

}
