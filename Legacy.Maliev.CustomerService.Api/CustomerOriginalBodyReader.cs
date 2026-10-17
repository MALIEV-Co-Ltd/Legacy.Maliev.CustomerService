using System.Buffers;
using System.Text;
using Legacy.Maliev.CustomerService.Application.Models;
using Microsoft.AspNetCore.Http.Features;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.Formatters;
using Microsoft.AspNetCore.Mvc.ModelBinding;
using Microsoft.AspNetCore.WebUtilities;
using Microsoft.Extensions.ObjectPool;
using Newtonsoft.Json;

namespace Legacy.Maliev.CustomerService.Api;

// Parse the complete recognized source graph before projecting its root scalars.
// These objects never enter EF, repositories, identity services or response metadata.
internal sealed class CustomerOriginalBodyReader(NewtonsoftJsonInputFormatter source)
{
    internal static CustomerOriginalBodyReader Create(ILogger logger)
    {
        var options = new MvcNewtonsoftJsonOptions();
        options.SerializerSettings.NullValueHandling = NullValueHandling.Ignore;
        options.SerializerSettings.ReferenceLoopHandling = ReferenceLoopHandling.Ignore;
        return new CustomerOriginalBodyReader(new BufferedSourceFormatter(
            new SourceBodyLogger(logger), options, new DefaultObjectPoolProvider(), new MvcOptions()));
    }

    internal async Task<InputFormatterResult> ReadAsync(InputFormatterContext context)
    {
        var type = context.ModelType == typeof(UpsertCustomerRequest) ? typeof(OriginalCustomer)
            : context.ModelType == typeof(UpsertAddressRequest) ? typeof(OriginalAddress)
            : throw new InvalidOperationException("The original body reader received an unowned contract.");
        var metadata = new EmptyModelMetadataProvider().GetMetadataForType(type);
        var state = new ModelStateDictionary(context.ModelState.MaxAllowedErrors);
        var original = new InputFormatterContext(context.HttpContext, context.ModelName, state, metadata,
            context.ReaderFactory, context.TreatEmptyInputAsDefaultValue);
        var result = await source.ReadAsync(original);
        if (result.HasError)
        {
            var key = context.ModelName is "" or "item" or "request" ? context.ModelName : "$";
            if (state.Values.SelectMany(value => value.Errors).Any(error => error.Exception is UnsupportedContentTypeException))
                context.ModelState.TryAddModelError(key,
                    new UnsupportedContentTypeException("The request content type is unsupported."), context.Metadata);
            else
                context.ModelState.TryAddModelError(key, "The request body contains an invalid value.");
            return InputFormatterResult.Failure();
        }
        if (!result.IsModelSet) return result;
        if (result.Model is null) return InputFormatterResult.Success(null);
        // Normal DTO validation and controller storage/permission guards still run.
        return result.Model switch
        {
            OriginalAddress address => InputFormatterResult.Success(new UpsertAddressRequest(
                address.Building, address.AddressLine1, address.AddressLine2, address.City,
                address.State, address.PostalCode, address.CountryId)),
            OriginalCustomer customer => InputFormatterResult.Success(new UpsertCustomerRequest(
                customer.FirstName, customer.LastName, customer.Telephone, customer.Mobile,
                customer.Fax, customer.Email, customer.DateOfBirth, customer.CompanyId,
                customer.BillingAddressId, customer.ShippingAddressId)),
            _ => throw new InvalidOperationException("The original body reader returned an unowned contract."),
        };
    }

    private sealed class SourceBodyLogger(ILogger target) : ILogger
    {
        public IDisposable? BeginScope<TState>(TState state) where TState : notnull =>
            target.BeginScope("Customer source body admission");
        public bool IsEnabled(LogLevel logLevel) => target.IsEnabled(logLevel);
        public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception, Func<TState, Exception?, string> formatter)
        {
            if (target.IsEnabled(logLevel))
                target.Log(logLevel, eventId, "SourceBodyAdmissionFailure ExceptionType={ExceptionType}",
                    exception?.GetType().Name ?? "InputFormatter");
        }
    }

    private sealed class BufferedSourceFormatter(
        ILogger logger, MvcNewtonsoftJsonOptions json, ObjectPoolProvider pools, MvcOptions mvc)
        : NewtonsoftJsonInputFormatter(logger, json.SerializerSettings, ArrayPool<char>.Shared, pools, mvc, json)
    {
        private const long DefaultRequestLimit = 30_000_000;
        private readonly int memoryThreshold = json.InputFormatterMemoryBufferThreshold;

        public override async Task<InputFormatterResult> ReadRequestBodyAsync(InputFormatterContext context, Encoding encoding)
        {
            // Encoding selection precedes the bounded asynchronous drain.
            var request = context.HttpContext.Request;
            var original = request.Body;
            var position = original.CanSeek ? original.Position : (long?)null;
            var limit = context.HttpContext.Features.Get<IHttpMaxRequestBodySizeFeature>()?.MaxRequestBodySize ?? DefaultRequestLimit;
            await using var buffered = new FileBufferingReadStream(original, memoryThreshold, limit,
                static () => Environment.GetEnvironmentVariable("ASPNETCORE_TEMP") ?? Path.GetTempPath());
            try
            {
                request.Body = buffered;
                await buffered.DrainAsync(context.HttpContext.RequestAborted);
                buffered.Position = 0;
                return await base.ReadRequestBodyAsync(context, encoding);
            }
            finally
            {
                request.Body = original;
                if (position.HasValue) original.Position = position.Value;
            }
        }
    }

    // Source projects did not enable nullable annotations. Keep their declared
    // reference defaults, constructors and typed recursive navigation envelope.
#nullable disable
    private sealed class OriginalAddress
    {
        public OriginalAddress()
        {
            CustomerBillingAddress = new HashSet<OriginalCustomer>();
            CustomerShippingAddress = new HashSet<OriginalCustomer>();
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
        public ICollection<OriginalCustomer> CustomerBillingAddress { get; set; }
        public ICollection<OriginalCustomer> CustomerShippingAddress { get; set; }
    }

    private sealed class OriginalCustomer
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
        public OriginalAddress BillingAddress { get; set; }
        public OriginalCompany Company { get; set; }
        public OriginalAddress ShippingAddress { get; set; }
    }

    private sealed class OriginalCompany
    {
        public OriginalCompany() => Customer = new HashSet<OriginalCustomer>();

        public int Id { get; set; }
        public string Name { get; set; }
        public string TaxNumber { get; set; }
        public string Registrar { get; set; }
        public DateTime? CreatedDate { get; set; }
        public DateTime? ModifiedDate { get; set; }
        public ICollection<OriginalCustomer> Customer { get; set; }
    }
#nullable restore
}
