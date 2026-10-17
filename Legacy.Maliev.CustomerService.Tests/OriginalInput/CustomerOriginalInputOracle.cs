using System.Buffers;
using System.Text;
using Legacy.Maliev.CustomerService.Application.Models;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.Formatters;
using Microsoft.AspNetCore.Mvc.ModelBinding;
using Microsoft.AspNetCore.WebUtilities;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.ObjectPool;
using Newtonsoft.Json;
using Stj = System.Text.Json.JsonSerializer;

namespace Legacy.Maliev.CustomerService.Tests.OriginalInput;

internal sealed record CustomerBodyRead(string Kind, object? Model, string[] Keys, int ErrorCount,
    string[] ExceptionTypes, string[] Messages);

internal static class CustomerOriginalInputOracle
{
    // Frozen source declarations: Address/Customer/Company at 5fac, 72eb and 770cb.
    // This independent graph is an input oracle, never an EF or identity object.
    internal static MvcNewtonsoftJsonOptions OriginalOptions()
    {
        var options = new MvcNewtonsoftJsonOptions();
        options.SerializerSettings.NullValueHandling = NullValueHandling.Ignore;
        options.SerializerSettings.ReferenceLoopHandling = ReferenceLoopHandling.Ignore;
        return options;
    }

    internal static NewtonsoftJsonInputFormatter Formatter()
    {
        var original = OriginalOptions();
        return new NewtonsoftJsonInputFormatter(NullLogger.Instance, original.SerializerSettings,
            ArrayPool<char>.Shared, new DefaultObjectPoolProvider(), new MvcOptions(), original);
    }

    internal static Type SourceType(string kind) => kind switch
    {
        "address" => typeof(SourceAddress),
        "customer" => typeof(SourceCustomer),
        "company" => typeof(SourceCompany),
        _ => throw new ArgumentOutOfRangeException(nameof(kind))
    };

    internal static Type DtoType(string kind) => kind switch
    {
        "address" => typeof(UpsertAddressRequest),
        "customer" => typeof(UpsertCustomerRequest),
        _ => throw new ArgumentOutOfRangeException(nameof(kind))
    };

    internal static Task<CustomerBodyRead> ReadAsync(string kind, string json, string contentType = "application/json",
        string prefix = "", int maxErrors = 200) =>
        ReadBytesAsync(Formatter(), SourceType(kind), new EmptyModelMetadataProvider(),
            Encoding.UTF8.GetBytes(json), contentType, prefix, maxErrors);

    internal static async Task<CustomerBodyRead> ReadBytesAsync(IInputFormatter formatter, Type modelType,
        IModelMetadataProvider metadataProvider, byte[] bytes, string contentType, string prefix = "", int maxErrors = 200)
    {
        var http = new DefaultHttpContext();
        using var input = new MemoryStream(bytes, writable: false);
        http.Request.Body = input;
        http.Request.ContentType = contentType;
        http.Request.ContentLength = bytes.LongLength;
        var state = new ModelStateDictionary { MaxAllowedErrors = maxErrors };
        var context = new InputFormatterContext(http, prefix, state, metadataProvider.GetMetadataForType(modelType),
            static (stream, encoding) => new HttpRequestStreamReader(stream, encoding), false);
        return await ObserveAsync(formatter, context);
    }

    internal static async Task<CustomerBodyRead> ObserveAsync(IInputFormatter formatter, InputFormatterContext context)
    {
        try
        {
            if (!formatter.CanRead(context)) return Result("unsupported", null, context.ModelState);
            var result = await formatter.ReadAsync(context);
            return Result(result.HasError ? "failure" : result.IsModelSet ? "success" : "no-value",
                result.Model, context.ModelState);
        }
        catch (Exception error)
        {
            // Observation only. Each caller supplies an exact expected outcome;
            // an arbitrary exception is never interchangeable with parse failure.
            return Result("throw:" + error.GetType().FullName, null, context.ModelState);
        }
    }

    private static CustomerBodyRead Result(string kind, object? model, ModelStateDictionary state) => new(
        kind, model, state.Keys.Order(StringComparer.Ordinal).ToArray(), state.ErrorCount,
        state.Values.SelectMany(value => value.Errors).Select(error => error.Exception?.GetType().FullName ?? "").ToArray(),
        state.Values.SelectMany(value => value.Errors).Select(error => error.ErrorMessage.Length == 0
            ? error.Exception?.Message ?? "" : error.ErrorMessage).ToArray());

    internal static string Projection(object? model) => model switch
    {
        null => "null",
        SourceAddress value => Stj.Serialize(new object?[] { value.Building, value.AddressLine1, value.AddressLine2,
            value.City, value.State, value.PostalCode, value.CountryId }),
        UpsertAddressRequest value => Stj.Serialize(new object?[] { value.Building, value.AddressLine1, value.AddressLine2,
            value.City, value.State, value.PostalCode, value.CountryId }),
        SourceCustomer value => Stj.Serialize(new object?[] { value.FirstName, value.LastName, value.Telephone,
            value.Mobile, value.Fax, value.Email, value.DateOfBirth, value.CompanyId, value.BillingAddressId, value.ShippingAddressId }),
        UpsertCustomerRequest value => Stj.Serialize(new object?[] { value.FirstName, value.LastName, value.Telephone,
            value.Mobile, value.Fax, value.Email, value.DateOfBirth, value.CompanyId, value.BillingAddressId, value.ShippingAddressId }),
        _ => throw new InvalidOperationException("Unexpected synthetic projection type.")
    };

    internal static string NestedUnknown(int depth) =>
        "{\"Unknown\":" + new string('[', depth - 1) + "0" + new string(']', depth - 1) + "}";
}

// These source models intentionally compile with oblivious reference annotations,
// have mutable public setters, and retain the original collection constructors.
#nullable disable
internal sealed class SourceAddress
{
    public SourceAddress()
    {
        CustomerBillingAddress = new HashSet<SourceCustomer>();
        CustomerShippingAddress = new HashSet<SourceCustomer>();
    }

    public int Id { get; set; }
    public string Building { get; set; }
    public string AddressLine1 { get; set; }
    public string AddressLine2 { get; set; }
    public string City { get; set; }
    public string State { get; set; }
    public string PostalCode { get; set; }
    public int CountryId { get; set; }
    public DateTime? CreatedDate { get; set; }
    public DateTime? ModifiedDate { get; set; }
    public ICollection<SourceCustomer> CustomerBillingAddress { get; set; }
    public ICollection<SourceCustomer> CustomerShippingAddress { get; set; }
}

internal sealed class SourceCustomer
{
    public int Id { get; set; }
    public string FirstName { get; set; }
    public string LastName { get; set; }
    public string FullName { get; set; }
    public string Telephone { get; set; }
    public string Mobile { get; set; }
    public string Fax { get; set; }
    public string Email { get; set; }
    public DateTime? DateOfBirth { get; set; }
    public int? CompanyId { get; set; }
    public int? BillingAddressId { get; set; }
    public int? ShippingAddressId { get; set; }
    public DateTime? CreatedDate { get; set; }
    public DateTime? ModifiedDate { get; set; }
    public SourceAddress BillingAddress { get; set; }
    public SourceCompany Company { get; set; }
    public SourceAddress ShippingAddress { get; set; }
}

internal sealed class SourceCompany
{
    public SourceCompany()
    {
        Customer = new HashSet<SourceCustomer>();
    }

    public int Id { get; set; }
    public string Name { get; set; }
    public string TaxNumber { get; set; }
    public string Registrar { get; set; }
    public DateTime? CreatedDate { get; set; }
    public DateTime? ModifiedDate { get; set; }
    public ICollection<SourceCustomer> Customer { get; set; }
}
#nullable restore
