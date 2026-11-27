using System.Globalization;
using System.Net;
using Npgsql;
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


/// <summary>Verifies ordinary company TaxNumber and Registrar primitives against equivalent strings through real HTTP persistence.</summary>
/// <param name="fixture">The shared Customer authority fixture supplying disposable PostgreSQL and Redis, production hosts, and signed request clients.</param>
/// <param name="output">The xUnit output sink recording reader, HTTP, graph, and cache comparison evidence.</param>
[Collection("Customer company lifecycle")]
public sealed class CustomerCompanyFieldPrimitiveHttpTests(CustomerDetailAuthorityFixture fixture, ITestOutputHelper output)
    : IClassFixture<CustomerDetailAuthorityFixture>
{
    /// <summary>Checks culture-independent primitive lexeme persistence against equivalent strings for contiguous and segmented bodies.</summary>
    /// <returns>A task that completes after verifying culture-independent primitive lexeme persistence against equivalent strings for contiguous and segmented bodies.</returns>
    [Fact]
    public async Task OrdinaryPairedFields_PersistPrimitiveLexemesAndMatchEquivalentStrings()
    {
        using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(180));
        var token = deadline.Token;
        await using var host = fixture.Start();
        AssertActualMvc(host);
        using var client = fixture.Client(host, "relation-editor");
        client.Timeout = TimeSpan.FromSeconds(15);
        var cache = host.Services.GetRequiredService<IDistributedCache>();
        var culture = CultureInfo.CurrentCulture;
        var uiCulture = CultureInfo.CurrentUICulture;
        try
        {
            foreach (var language in new[] { "en-US", "de-DE", "th-TH" })
            {
                CultureInfo.CurrentCulture = CultureInfo.GetCultureInfo(language);
                CultureInfo.CurrentUICulture = CultureInfo.GetCultureInfo(language);
                foreach (var field in new[] { "TaxNumber", "Registrar" })
                    foreach (var route in new[] { "create", "update" })
                        foreach (var segmented in new[] { false, true })
                            foreach (var item in PositivePrimitives())
                            {
                                var primitive = Payload(field, item.Token, item.Suffix);
                                var literal = Payload(field, JsonSerializer.Serialize(item.Lexeme), item.Suffix);
                                if (item.StorageRejects)
                                {
                                    var refused = await RollbackAsync(client, cache, route, primitive, segmented, true, token);
                                    var refusedLiteral = await RollbackAsync(client, cache, route, literal, segmented, true, token);
                                    Assert.Equal(refused, refusedLiteral);
                                    Assert.True(refused.Status == 500 || refused.Shape == PostgresErrorCodes.CheckViolation);
                                    continue;
                                }
                                var expected = new PositiveCase(item.Label, "", "ok", field == "TaxNumber" ? item.Final : null,
                                    field == "Registrar" ? item.Final : null);
                                var first = await PersistAsync(client, cache, route, primitive, expected, segmented, token);
                                var second = await PersistAsync(client, cache, route, literal, expected, segmented, token);
                                Assert.Equal(first, second);
                                output.WriteLine(JsonSerializer.Serialize(new
                                {
                                    Kind = "http-primitive-string-pair",
                                    Culture = language,
                                    Field = field,
                                    Route = route,
                                    Case = item.Label,
                                    BodyWrittenInOneByteSegments = segmented,
                                    Primitive = first,
                                    EquivalentString = second,
                                    Graph = "intended effects only",
                                    Cache = "exact eviction"
                                }));
                            }
            }
        }
        finally
        {
            CultureInfo.CurrentCulture = culture;
            CultureInfo.CurrentUICulture = uiCulture;
        }
    }

    /// <summary>Checks structured-value rejection and storage boundaries against equivalent-string behavior without unintended graph or cache changes.</summary>
    /// <returns>A task that completes after verifying structured-value rejection and storage boundaries against equivalent-string behavior without unintended graph or cache changes.</returns>
    [Fact]
    public async Task OrdinaryPairedFields_RefusedShapesAndStorageBoundariesMatchEquivalentStringPipeline()
    {
        using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(180));
        var token = deadline.Token;
        await using var host = fixture.Start();
        AssertActualMvc(host);
        using var client = fixture.Client(host, "relation-editor");
        client.Timeout = TimeSpan.FromSeconds(15);
        var cache = host.Services.GetRequiredService<IDistributedCache>();
        foreach (var field in new[] { "TaxNumber", "Registrar" })
            foreach (var route in new[] { "create", "update" })
                foreach (var segmented in new[] { false, true })
                {
                    foreach (var shape in new[] { "{}", "[]" })
                    {
                        var observation = await RollbackAsync(client, cache, route, Payload(field, shape, ",\"FIELD\":\"later\",\"FIELD\":null"),
                            segmented, false, token);
                        Assert.Equal(400, observation.Status);
                        output.WriteLine(JsonSerializer.Serialize(new
                        {
                            Kind = "http-structured-refusal",
                            Field = field,
                            Route = route,
                            Case = shape,
                            BodyWrittenInOneByteSegments = segmented,
                            Observation = observation,
                            Graph = "unchanged",
                            Cache = "unchanged"
                        }));
                    }
                    foreach (var item in BoundaryCases())
                    {
                        if (item.SourceValue is { } normalized)
                        {
                            var json = Payload(field, item.Token, "");
                            var source = await CustomerOriginalInputOracle.ReadAsync("company", json);
                            Assert.Equal("success", source.Kind);
                            var model = Assert.IsType<SourceCompany>(source.Model);
                            Assert.Equal(normalized, field == "TaxNumber" ? model.TaxNumber : model.Registrar);
                            var expected = new PositiveCase(item.Label, "", "ok",
                                field == "TaxNumber" ? normalized : null, field == "Registrar" ? normalized : null);
                            var accepted = await PersistAsync(client, cache, route, json, expected, segmented, token);
                            var equivalent = await PersistAsync(client, cache, route,
                                Payload(field, JsonSerializer.Serialize(normalized), ""), expected, segmented, token);
                            Assert.Equal(accepted, equivalent);
                            output.WriteLine(JsonSerializer.Serialize(new
                            {
                                Kind = "http-source-normalized-string-pair",
                                Field = field,
                                Route = route,
                                Case = item.Label,
                                BodyWrittenInOneByteSegments = segmented,
                                PrimitiveOrLiteral = accepted,
                                EquivalentString = equivalent,
                                Graph = "intended effects only",
                                Cache = "exact eviction"
                            }));
                            continue;
                        }
                        var first = await RollbackAsync(client, cache, route, Payload(field, item.Token, ""), segmented, item.Storage, token);
                        var second = await RollbackAsync(client, cache, route, Payload(field, item.StringToken, ""), segmented, item.Storage, token);
                        Assert.Equal(first, second);
                        output.WriteLine(JsonSerializer.Serialize(new
                        {
                            Kind = "http-boundary-string-pair",
                            Field = field,
                            Route = route,
                            Case = item.Label,
                            BodyWrittenInOneByteSegments = segmented,
                            PrimitiveOrLiteral = first,
                            EquivalentString = second,
                            Graph = "unchanged",
                            Cache = "unchanged"
                        }));
                    }
                }
    }

    /// <summary>Checks company primitive authority failures, scoped literal policy, and stale-version refusal without unintended mutation.</summary>
    /// <returns>A task that completes after verifying company primitive authority failures, scoped literal policy, and stale-version refusal without unintended mutation.</returns>
    [Fact]
    public async Task OrdinaryPairedFields_AuthorityScopedPolicyAndStaleVersionsPreserveGraphCache()
    {
        using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(120));
        var token = deadline.Token;
        await using var host = fixture.Start();
        AssertActualMvc(host);
        using var editor = fixture.Client(host, "relation-editor");
        editor.Timeout = TimeSpan.FromSeconds(15);
        var cache = host.Services.GetRequiredService<IDistributedCache>();
        foreach (var route in new[] { "create", "update", "scoped-create", "scoped-update" })
            foreach (var field in new[] { "TaxNumber", "Registrar" })
            {
                await SeedAsync(route, token);
                var versions = route.StartsWith("scoped-", StringComparison.Ordinal) ? await VersionsAsync(editor, route, token) : default;
                var before = await PhysicalSnapshotAsync(token);
                var cached = await PrimeAsync(cache, token);
                foreach (var kind in new[] { "anonymous", "denied", "wrong-signature", "expired" })
                {
                    using var client = fixture.Client(host, kind);
                    client.Timeout = TimeSpan.FromSeconds(15);
                    using var request = Request(route, Payload(field, "42", "", scoped: route.StartsWith("scoped-", StringComparison.Ordinal)), versions);
                    using var response = await client.SendAsync(request, token);
                    Assert.Equal(kind == "denied" ? HttpStatusCode.Forbidden : HttpStatusCode.Unauthorized, response.StatusCode);
                    Assert.Equal(before, await PhysicalSnapshotAsync(token));
                    await AssertCacheAsync(cache, cached, [], token);
                    output.WriteLine(JsonSerializer.Serialize(new
                    {
                        Kind = "http-primitive-authority",
                        Field = field,
                        Route = route,
                        Authority = kind,
                        Status = (int)response.StatusCode,
                        Graph = "unchanged",
                        Cache = "unchanged"
                    }));
                }
                if (!route.StartsWith("scoped-", StringComparison.Ordinal)) continue;
                using (var request = Request(route, Payload(field, "42", ",\"FIELD\":\"later\",\"FIELD\":null", scoped: true), versions))
                using (var response = await editor.SendAsync(request, token))
                {
                    Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
                    Assert.Equal(before, await PhysicalSnapshotAsync(token));
                    await AssertCacheAsync(cache, cached, [], token);
                }
                var literal = Payload(field, "\"later\"", ",\"FIELD\":null", scoped: true);
                using var acceptedRequest = Request(route, literal, versions);
                using var accepted = await editor.SendAsync(acceptedRequest, token);
                Assert.Equal(HttpStatusCode.NoContent, accepted.StatusCode);
                var id = int.Parse(accepted.Headers.GetValues("X-Relation-Id").Single(), CultureInfo.InvariantCulture);
                var after = await PhysicalSnapshotAsync(token);
                AssertPreservedGraph(before, after, id, true, route == "scoped-create");
                await AssertCacheAsync(cache, cached, route == "scoped-create" ? [1] : [1, 2], token);
                await using (var db = fixture.Context())
                {
                    var saved = await db.Companies.AsNoTracking().SingleAsync(row => row.Id == id, token);
                    Assert.Equal("ok", saved.Name);
                    Assert.Null(saved.TaxNumber);
                    Assert.Null(saved.Registrar);
                }
                using var read = await editor.GetAsync($"/customers/1/relations/company/{id}/edit", token);
                Assert.Equal(HttpStatusCode.OK, read.StatusCode);
                Assert.Equal(accepted.Headers.ETag, read.Headers.ETag);
                using var readBody = JsonDocument.Parse(await read.Content.ReadAsStringAsync(token));
                AssertLiteral(readBody.RootElement.GetProperty("Company"), new("scoped-literal", "", "ok", null, null));
                var replayCache = await PrimeAsync(cache, token);
                using var staleRequest = Request(route, literal, versions);
                using var stale = await editor.SendAsync(staleRequest, token);
                Assert.Equal(HttpStatusCode.PreconditionFailed, stale.StatusCode);
                Assert.Equal(after, await PhysicalSnapshotAsync(token));
                await AssertCacheAsync(cache, replayCache, [], token);
                output.WriteLine(JsonSerializer.Serialize(new
                {
                    Kind = "http-primitive-scoped",
                    Field = field,
                    Route = route,
                    PrimitiveStatus = 400,
                    StringStatus = (int)accepted.StatusCode,
                    StaleStatus = (int)stale.StatusCode
                }));
            }
    }

    private async Task<int> PersistAsync(HttpClient client, IDistributedCache cache, string route, string json,
        PositiveCase expected, bool segmented, CancellationToken token)
    {
        await SeedAsync(route, token);
        var before = await PhysicalSnapshotAsync(token);
        var cached = await PrimeAsync(cache, token);
        using var request = Request(route, json, default);
        if (segmented) { request.Content!.Dispose(); request.Content = new OneByteContent(Encoding.UTF8.GetBytes(json)); }
        var started = DateTime.UtcNow;
        using var response = await client.SendAsync(request, token);
        var ended = DateTime.UtcNow;
        AssertSegments(request, json, segmented);
        var create = route == "create";
        Assert.Equal(create ? HttpStatusCode.Created : HttpStatusCode.NoContent, response.StatusCode);
        var id = 1;
        if (create)
        {
            using var created = JsonDocument.Parse(await response.Content.ReadAsStringAsync(token));
            id = created.RootElement.GetProperty("Id").GetInt32();
            AssertLiteral(created.RootElement, expected);
            Assert.True(id > 2);
        }
        else Assert.Equal("", await response.Content.ReadAsStringAsync(token));
        var after = await PhysicalSnapshotAsync(token);
        AssertPreservedGraph(before, after, id, false, create);
        using var postimage = JsonDocument.Parse(after);
        var changed = postimage.RootElement.GetProperty("companies").EnumerateArray().Single(row => row.GetProperty("ID").GetInt32() == id);
        Assert.Equal(expected.Name, changed.GetProperty("Name").GetString());
        Assert.Equal(expected.Tax, changed.GetProperty("TaxNumber").GetString());
        Assert.Equal(expected.Registrar, changed.GetProperty("Registrar").GetString());
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
        AssertLiteral(body.RootElement, expected);
        if (!create)
            foreach (var customerId in new[] { 1, 2 })
            {
                using var customer = await client.GetAsync($"/customers/{customerId}", token);
                Assert.Equal(HttpStatusCode.OK, customer.StatusCode);
                using var customerBody = JsonDocument.Parse(await customer.Content.ReadAsStringAsync(token));
                AssertLiteral(customerBody.RootElement.GetProperty("Company"), expected);
                Assert.False(customerBody.RootElement.TryGetProperty("InternalRemark", out _));
            }
        return (int)response.StatusCode;
    }

    private async Task<FailureObservation> RollbackAsync(HttpClient client, IDistributedCache cache, string route,
        string json, bool segmented, bool storage, CancellationToken token)
    {
        await SeedAsync(route, token);
        var before = await PhysicalSnapshotAsync(token);
        var cached = await PrimeAsync(cache, token);
        using var request = Request(route, json, default);
        if (segmented) { request.Content!.Dispose(); request.Content = new OneByteContent(Encoding.UTF8.GetBytes(json)); }
        FailureObservation observation;
        try
        {
            using var response = await client.SendAsync(request, token);
            Assert.InRange((int)response.StatusCode, 400, 599);
            var raw = await response.Content.ReadAsStringAsync(token);
            foreach (var value in new[] { "staff-only", "PasswordHash", "SecurityStamp", "ConnectionStrings", "Npgsql", "SELECT ", "synthetic-private-member" })
                Assert.DoesNotContain(value, raw, StringComparison.OrdinalIgnoreCase);
            var shape = "empty";
            var category = "empty-error";
            if (raw.Length != 0)
            {
                var mediaType = response.Content.Headers.ContentType?.MediaType;
                if (mediaType is not null && (mediaType.EndsWith("/json", StringComparison.OrdinalIgnoreCase)
                    || mediaType.EndsWith("+json", StringComparison.OrdinalIgnoreCase)))
                {
                    using var parsed = JsonDocument.Parse(raw);
                    shape = Shape(parsed.RootElement);
                    category = parsed.RootElement.ValueKind == JsonValueKind.Object && parsed.RootElement.TryGetProperty("errors", out _)
                        ? "model-state-error" : "json-error";
                }
                else
                {
                    shape = "nonempty-text";
                    category = "text-error";
                }
            }
            observation = new((int)response.StatusCode, response.Content.Headers.ContentType?.MediaType, category, shape);
        }
        catch (DbUpdateException error) when (storage && error.InnerException is PostgresException postgres &&
            (postgres.SqlState is "22001" or "22021" || postgres.SqlState == PostgresErrorCodes.CheckViolation &&
                postgres.ConstraintName is "CK_Company_TaxNumber_SourceLength" or "CK_Company_Registrar_SourceLength"))
        {
            observation = new(null, null, "DbUpdateException/PostgresException", ((PostgresException)error.InnerException!).SqlState);
        }
        AssertSegments(request, json, segmented);
        Assert.Equal(before, await PhysicalSnapshotAsync(token));
        await AssertCacheAsync(cache, cached, [], token);
        return observation;
    }

    private static string Shape(JsonElement value) => value.ValueKind switch
    {
        JsonValueKind.Object => "{" + string.Join(",", value.EnumerateObject().OrderBy(item => item.Name, StringComparer.Ordinal)
            .Select(item => JsonSerializer.Serialize(item.Name) + ":" + Shape(item.Value))) + "}",
        JsonValueKind.Array => "[" + string.Join(",", value.EnumerateArray().Select(Shape)) + "]",
        _ => value.ValueKind.ToString(),
    };

    private static string Payload(string field, string value, string suffix, bool scoped = false) =>
        "{\"Name\":\"ok\"," + (scoped ? "" : "\"Name\":null,") + "\"" + field + "\":" + value
        + suffix.Replace("FIELD", field, StringComparison.Ordinal) + "}";

    private static PrimitiveCase[] PositivePrimitives()
    {
        var longNumber = new string('1', 257);
        var emoji = string.Concat(Enumerable.Repeat("😀", 129));
        return
        [
            new("boolean-true", "true", "true", "true", ",\"FIELD\":null"),
            new("boolean-false", "false", "false", "false", ",\"FIELD\":null"),
            new("integer", "42", "42", "42", ",\"FIELD\":null"),
            new("exponent-lexical", "1E+03", "1E+03", "1E+03", ",\"FIELD\":null"),
            new("exponent-overflow", "1e309", "1e309", "1e309", ",\"FIELD\":null"),
            new("exponent-larger", "1e400", "1e400", "1e400", ",\"FIELD\":null"),
            new("decimal-lexical", "1.500", "1.500", "1.500", ",\"FIELD\":null"),
            new("earlier257-bounded-null", longNumber, longNumber, "bounded", ",\"FIELD\":\"bounded\",\"FIELD\":null"),
            new("unicode129-pairs", JsonSerializer.Serialize(emoji), emoji, emoji, ",\"FIELD\":null", StorageRejects: true),
        ];
    }

    private static BoundaryCase[] BoundaryCases()
    {
        var number = new string('1', 257);
        var ascii = JsonSerializer.Serialize(new string('x', 257));
        return
        [
            new("final257-number", number, JsonSerializer.Serialize(number), true),
            new("final257-string", ascii, ascii, true),
            new("literal-nul", "\"a\\u0000b\"", "\"a\\u0000b\"", true),
            new("malformed-high", "\"\\uD800\"", "\"\\uD800\"", false, "\uFFFD"),
            new("malformed-low", "\"\\uDC00\"", "\"\\uDC00\"", false, "\uFFFD"),
            new("malformed-reversed", "\"\\uDC00\\uD800\"", "\"\\uDC00\\uD800\"", false, "\uFFFD\uFFFD"),
        ];
    }

    private static void AssertSegments(HttpRequestMessage request, string json, bool segmented)
    {
        if (segmented) Assert.Equal(Encoding.UTF8.GetByteCount(json), Assert.IsType<OneByteContent>(request.Content).Writes);
    }

    private sealed class OneByteContent : HttpContent
    {
        private readonly byte[] bytes;
        public OneByteContent(byte[] bytes)
        {
            this.bytes = bytes;
            Headers.ContentType = new("application/json");
        }
        public int Writes { get; private set; }
        protected override bool TryComputeLength(out long length) { length = bytes.Length; return true; }
        protected override async Task SerializeToStreamAsync(Stream stream, TransportContext? context)
        {
            foreach (var index in Enumerable.Range(0, bytes.Length))
            {
                await stream.WriteAsync(bytes.AsMemory(index, 1));
                Writes++;
            }
        }
    }

    private sealed record PrimitiveCase(string Label, string Token, string Lexeme, string? Final, string Suffix, bool StorageRejects = false);
    private sealed record BoundaryCase(string Label, string Token, string StringToken, bool Storage, string? SourceValue = null);
    private sealed record FailureObservation(int? Status, string? MediaType, string Category, string Shape);

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
