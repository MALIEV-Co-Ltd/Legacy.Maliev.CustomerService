using System.Buffers;
using CustomerOriginalOracle;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.Formatters;
using Microsoft.AspNetCore.Mvc.ModelBinding;
using Microsoft.AspNetCore.WebUtilities;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.ObjectPool;
using Newtonsoft.Json;

// This executable accepts synthetic test inputs only and returns observations, never application data.
if (Environment.Version != new Version(10, 0, 15)
    || !IsExactRelease(typeof(DefaultHttpContext), "10.0.15")
    || !IsExactRelease(typeof(NewtonsoftJsonInputFormatter), "10.0.12")
    || !IsExactRelease(typeof(JsonConvert), "13.0.3"))
    throw new InvalidOperationException("Unexpected original formatter oracle identity.");

var request = System.Text.Json.JsonSerializer.Deserialize<Request>(await Console.In.ReadToEndAsync())
    ?? throw new InvalidOperationException("Missing synthetic request.");
if (request.Bytes.Length > 2_000_000 || request.MaxErrors is < 1 or > 1000)
    throw new InvalidOperationException("Synthetic request exceeds oracle bounds.");
var modelType = request.Kind switch
{
    "address" => typeof(SourceAddress),
    "customer" => typeof(SourceCustomer),
    "company" => typeof(SourceCompany),
    _ => throw new InvalidOperationException("Unknown source model.")
};
var options = new MvcNewtonsoftJsonOptions();
options.SerializerSettings.NullValueHandling = NullValueHandling.Ignore;
options.SerializerSettings.ReferenceLoopHandling = ReferenceLoopHandling.Ignore;
var formatter = new NewtonsoftJsonInputFormatter(NullLogger.Instance, options.SerializerSettings,
    ArrayPool<char>.Shared, new DefaultObjectPoolProvider(), new MvcOptions(), options);
if (request.Scenario is not null)
{
    if (request.Scenario == "company-primitive")
    {
        var previous = System.Globalization.CultureInfo.CurrentCulture;
        try
        {
            System.Globalization.CultureInfo.CurrentCulture = System.Globalization.CultureInfo.GetCultureInfo(request.Culture ?? "en-US");
            var company = JsonConvert.DeserializeObject<SourceCompany>(System.Text.Encoding.UTF8.GetString(request.Bytes),
                new JsonSerializerSettings { NullValueHandling = request.IgnoreNull ? NullValueHandling.Ignore : NullValueHandling.Include });
            await Console.Out.WriteAsync(System.Text.Json.JsonSerializer.Serialize(new PrimitiveObservation(company?.Name, company?.TaxNumber, company?.Registrar, null, false, company is not null)));
        }
        catch (JsonException error)
        {
            await Console.Out.WriteAsync(System.Text.Json.JsonSerializer.Serialize(new PrimitiveObservation(null, null, null, error.GetType().FullName, true, false)));
        }
        finally { System.Globalization.CultureInfo.CurrentCulture = previous; }
        return;
    }
    if (request.Scenario == "company-primitive-batch")
    {
        if (request.PrimitiveCases is not { Length: > 0 and <= 10000 } cases
            || cases.Sum(item => (long)item.Json.Length) > 2_000_000)
            throw new InvalidOperationException("Primitive oracle batch exceeds bounds.");
        var results = new List<PrimitiveObservation>(cases.Length);
        var previous = System.Globalization.CultureInfo.CurrentCulture;
        try
        {
            foreach (var item in cases)
            {
                System.Globalization.CultureInfo.CurrentCulture = System.Globalization.CultureInfo.GetCultureInfo(item.Culture);
                try
                {
                    var company = JsonConvert.DeserializeObject<SourceCompany>(item.Json,
                        new JsonSerializerSettings { NullValueHandling = item.IgnoreNull ? NullValueHandling.Ignore : NullValueHandling.Include });
                    results.Add(new(company?.Name, company?.TaxNumber, company?.Registrar, null, false, company is not null));
                }
                catch (JsonException error) { results.Add(new(null, null, null, error.GetType().FullName, true, false)); }
            }
        }
        finally { System.Globalization.CultureInfo.CurrentCulture = previous; }
        await Console.Out.WriteAsync(System.Text.Json.JsonSerializer.Serialize(results));
        return;
    }
    if (request.Scenario == "contact-primitive-batch")
    {
        if (request.PrimitiveCases is not { Length: > 0 and <= 10000 } cases
            || cases.Sum(item => (long)item.Json.Length) > 2_000_000)
            throw new InvalidOperationException("Contact oracle batch exceeds bounds.");
        var results = new List<ContactObservation>(cases.Length);
        var previous = System.Globalization.CultureInfo.CurrentCulture;
        try
        {
            foreach (var item in cases)
            {
                System.Globalization.CultureInfo.CurrentCulture = System.Globalization.CultureInfo.GetCultureInfo(item.Culture);
                try
                {
                    var contact = JsonConvert.DeserializeObject<ContactReference>(item.Json, new JsonSerializerSettings
                    {
                        NullValueHandling = item.IgnoreNull ? NullValueHandling.Ignore : NullValueHandling.Include,
                        ReferenceLoopHandling = ReferenceLoopHandling.Ignore,
                    });
                    results.Add(new(contact?.Telephone, contact?.Mobile, contact?.Fax, null, false, contact is not null));
                }
                catch (JsonException error) { results.Add(new(null, null, null, error.GetType().FullName, true, false)); }
            }
        }
        finally { System.Globalization.CultureInfo.CurrentCulture = previous; }
        await Console.Out.WriteAsync(System.Text.Json.JsonSerializer.Serialize(results));
        return;
    }
    if (request.Scenario == "dictionary-primitive-batch")
    {
        if (request.PrimitiveCases is not { Length: > 0 and <= 10000 } cases
            || cases.Sum(item => (long)item.Json.Length) > 2_000_000)
            throw new InvalidOperationException("Dictionary oracle batch exceeds bounds.");
        var results = new List<Dictionary<string, string?>?>(cases.Length);
        var previous = System.Globalization.CultureInfo.CurrentCulture;
        try
        {
            foreach (var item in cases)
            {
                System.Globalization.CultureInfo.CurrentCulture = System.Globalization.CultureInfo.GetCultureInfo(item.Culture);
                results.Add(JsonConvert.DeserializeObject<Dictionary<string, string?>>(item.Json));
            }
        }
        finally { System.Globalization.CultureInfo.CurrentCulture = previous; }
        await Console.Out.WriteAsync(System.Text.Json.JsonSerializer.Serialize(results));
        return;
    }
    if (request.Scenario == "settings")
    {
        var settings = options.SerializerSettings;
        await Console.Out.WriteAsync(System.Text.Json.JsonSerializer.Serialize(new
        {
            NullValueHandling = settings.NullValueHandling.ToString(),
            ReferenceLoopHandling = settings.ReferenceLoopHandling.ToString(),
            TypeNameHandling = settings.TypeNameHandling.ToString(),
            DefaultValueHandling = settings.DefaultValueHandling.ToString(),
            ObjectCreationHandling = settings.ObjectCreationHandling.ToString(),
            MetadataPropertyHandling = settings.MetadataPropertyHandling.ToString(),
            DateParseHandling = settings.DateParseHandling.ToString(),
            settings.MaxDepth,
            settings.CheckAdditionalContent,
            options.ReadJsonWithRequestCulture,
            options.InputFormatterMemoryBufferThreshold,
        }));
        return;
    }
    var resource = await ResourceProbe.RunAsync(request.Kind, request.Scenario, formatter, modelType);
    await Console.Out.WriteAsync(System.Text.Json.JsonSerializer.Serialize(resource));
    return;
}
var http = new DefaultHttpContext();
using var body = new MemoryStream(request.Bytes, writable: false);
http.Request.Body = body;
http.Request.ContentType = request.ContentType;
http.Request.ContentLength = request.Bytes.LongLength;
var state = new ModelStateDictionary { MaxAllowedErrors = request.MaxErrors };
var context = new InputFormatterContext(http, request.Prefix, state,
    new EmptyModelMetadataProvider().GetMetadataForType(modelType),
    static (stream, encoding) => new HttpRequestStreamReader(stream, encoding), false);
string kind;
object? model = null;
try
{
    if (!formatter.CanRead(context)) kind = "unsupported";
    else
    {
        var result = await formatter.ReadAsync(context);
        kind = result.HasError ? "failure" : result.IsModelSet ? "success" : "no-value";
        model = result.Model;
    }
}
catch (Exception error) { kind = "throw:" + error.GetType().FullName; }
var graph = model is null ? null : JsonConvert.SerializeObject(model, new JsonSerializerSettings
{
    PreserveReferencesHandling = PreserveReferencesHandling.Objects,
    ReferenceLoopHandling = ReferenceLoopHandling.Serialize,
});
var errors = state.Values.SelectMany(value => value.Errors).ToArray();
var output = new Readback(kind, graph, state.Keys.Order(StringComparer.Ordinal).ToArray(), state.ErrorCount,
    errors.Select(error => error.Exception?.GetType().FullName ?? "").ToArray(),
    errors.Select(error => error.ErrorMessage.Length == 0 ? error.Exception?.Message ?? "" : error.ErrorMessage).ToArray());
await Console.Out.WriteAsync(System.Text.Json.JsonSerializer.Serialize(output));

static string VersionOf(Type type) => type.Assembly
    .GetCustomAttributes(typeof(System.Reflection.AssemblyInformationalVersionAttribute), false)
    .Cast<System.Reflection.AssemblyInformationalVersionAttribute>().Single().InformationalVersion;

static bool IsExactRelease(Type type, string expected)
{
    var actual = VersionOf(type);
    return actual == expected || actual.StartsWith(expected + "+", StringComparison.Ordinal);
}

internal sealed record Request(string Kind, byte[] Bytes, string ContentType, string Prefix, int MaxErrors,
    string? Scenario = null, string? Culture = null, bool IgnoreNull = true, PrimitiveCase[]? PrimitiveCases = null);
internal sealed record Readback(string Kind, string? Graph, string[] Keys, int ErrorCount,
    string[] ExceptionTypes, string[] Messages);
internal sealed record PrimitiveObservation(string? Name, string? TaxNumber, string? Registrar, string? ExceptionType, bool IsJsonException, bool ModelPresent);
internal sealed record PrimitiveCase(string Json, string Culture, bool IgnoreNull);
internal sealed record ContactObservation(string? Telephone, string? Mobile, string? Fax, string? ExceptionType, bool IsJsonException, bool ModelPresent);
internal sealed class ContactReference { public string? Telephone { get; set; } public string? Mobile { get; set; } public string? Fax { get; set; } }
