using System.Reflection;
using System.Runtime.CompilerServices;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Text.Json.Serialization.Metadata;
using Legacy.Maliev.CustomerService.Api.Controllers;
using Legacy.Maliev.CustomerService.Application.Models;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.Controllers;
using Microsoft.AspNetCore.Mvc.Formatters;
using Microsoft.Extensions.Options;

namespace Legacy.Maliev.CustomerService.Api;

/// <summary>Reads only ordinary unkeyed customer writes with isolated contact compatibility metadata.</summary>
internal sealed class OrdinaryCustomerContactInputFormatter : TextInputFormatter, IInputFormatterExceptionPolicy
{
    private static readonly ConditionalWeakTable<JsonSerializerOptions, Ownership> Owners = new();
    private static readonly JsonConverter<string> DefaultString =
        (JsonConverter<string>)JsonSerializerOptions.Default.GetConverter(typeof(string));
    private static readonly MethodInfo Create = typeof(CustomersController).GetMethod(
        nameof(CustomersController.CreateCustomerAsync), [typeof(UpsertCustomerRequest), typeof(CancellationToken)])!;
    private static readonly MethodInfo Update = typeof(CustomersController).GetMethod(
        nameof(CustomersController.UpdateCustomerAsync), [typeof(int), typeof(UpsertCustomerRequest), typeof(CancellationToken)])!;
    private readonly JsonOptions source;
    private readonly SystemTextJsonInputFormatter original;
    private readonly ILogger<SystemTextJsonInputFormatter> logger;
    private readonly bool streamParsing;

    internal OrdinaryCustomerContactInputFormatter(JsonOptions source, SystemTextJsonInputFormatter original,
        ILogger<SystemTextJsonInputFormatter> logger)
    {
        this.source = source;
        this.original = original;
        this.logger = logger;
        foreach (var encoding in original.SupportedEncodings) SupportedEncodings.Add(encoding);
        foreach (var mediaType in original.SupportedMediaTypes) SupportedMediaTypes.Add(mediaType);
        streamParsing = AppContext.TryGetSwitch("Microsoft.AspNetCore.UseStreamBasedJsonParsing", out var enabled) && enabled;
    }

    InputFormatterExceptionPolicy IInputFormatterExceptionPolicy.ExceptionPolicy =>
        InputFormatterExceptionPolicy.MalformedInputExceptions;

    internal static void ComposeOwned(JsonSerializerOptions options)
    {
        var resolver = options.TypeInfoResolver;
        var composed = OrdinaryCompanyMvcJsonMetadata.Compose(resolver);
        options.TypeInfoResolver = composed;
        Owners.Remove(options);
        if (resolver?.GetType() == typeof(DefaultJsonTypeInfoResolver)
            && resolver is DefaultJsonTypeInfoResolver reflection
            && reflection.Modifiers.Count == 0 && composed is not null)
            Owners.Add(options, new Ownership(reflection, composed));
    }

    protected override bool CanReadType(Type type) => type == typeof(UpsertCustomerRequest);

    public override bool CanRead(InputFormatterContext context) =>
        Eligible(context) && OwnsPolicy() && base.CanRead(context);

    private static bool Eligible(InputFormatterContext context)
    {
        if (context.ModelType != typeof(UpsertCustomerRequest)
            || context.HttpContext.GetEndpoint()?.Metadata.GetMetadata<ControllerActionDescriptor>() is not { } action
            || action.ControllerTypeInfo.AsType() != typeof(CustomersController))
            return false;
        var request = context.HttpContext.Request;
        return action.MethodInfo == Create && HttpMethods.IsPost(request.Method)
                && !request.Headers.ContainsKey("Idempotency-Key")
            || action.MethodInfo == Update && HttpMethods.IsPut(request.Method);
    }

    private bool OwnsPolicy()
    {
        var options = original.SerializerOptions;
        return ReferenceEquals(options, source.JsonSerializerOptions)
            && Owners.TryGetValue(options, out var ownership)
            && ReferenceEquals(options.TypeInfoResolver, ownership.Composed)
            && ownership.Reflection.Modifiers.Count == 0
            && options.DefaultIgnoreCondition == JsonIgnoreCondition.WhenWritingNull
            && options.PropertyNameCaseInsensitive
            && options.PropertyNamingPolicy is null
            && options.DictionaryKeyPolicy is null
            && options.ReferenceHandler is null
            && !options.RespectRequiredConstructorParameters
            && !options.RespectNullableAnnotations
            && options.PreferredObjectCreationHandling == JsonObjectCreationHandling.Replace
            && !options.IgnoreReadOnlyProperties
            && options.Converters.Count == 0;
    }

    public override async Task<InputFormatterResult> ReadRequestBodyAsync(InputFormatterContext context, Encoding encoding)
    {
        ArgumentNullException.ThrowIfNull(context);
        ArgumentNullException.ThrowIfNull(encoding);
        if (!Eligible(context) || !OwnsPolicy())
            return await original.ReadRequestBodyAsync(context, encoding);

        // Copy all serializer settings, then modify only private metadata. Never mutate the shared resolver or options.
        var options = new JsonSerializerOptions(original.SerializerOptions);
        options.TypeInfoResolver = options.TypeInfoResolver!.WithAddedModifier(ConfigureContacts);
        options.MakeReadOnly();
        object? model;
        Stream? transcoder = null;
        try
        {
            // Match the ASP.NET Core v10 formatter's pipe/stream switch and exception contract (MIT licensed framework source).
            if (encoding.CodePage == Encoding.UTF8.CodePage)
            {
                model = streamParsing
                    ? await JsonSerializer.DeserializeAsync(context.HttpContext.Request.Body, context.ModelType, options)
                    : await JsonSerializer.DeserializeAsync(context.HttpContext.Request.BodyReader, context.ModelType, options);
            }
            else
            {
                transcoder = Encoding.CreateTranscodingStream(context.HttpContext.Request.Body, encoding, Encoding.UTF8, leaveOpen: true);
                model = await JsonSerializer.DeserializeAsync(transcoder, context.ModelType, options);
            }
        }
        catch (JsonException exception)
        {
            Exception safe = source.AllowInputFormatterExceptionMessages
                ? new InputFormatterException(exception.Message, exception) : exception;
            context.ModelState.TryAddModelError(exception.Path ?? string.Empty, safe, context.Metadata);
            logger.LogDebug(new EventId(1, "SystemTextJsonInputException"), "JSON input formatter threw an exception: {Message}", exception.Message);
            return InputFormatterResult.Failure();
        }
        catch (Exception exception) when (exception is FormatException or OverflowException)
        {
            context.ModelState.TryAddModelError(string.Empty, exception, context.Metadata);
            logger.LogDebug(new EventId(1, "SystemTextJsonInputException"), "JSON input formatter threw an exception: {Message}", exception.Message);
            return InputFormatterResult.Failure();
        }
        finally
        {
            if (transcoder is not null) await transcoder.DisposeAsync();
        }
        if (model is null && !context.TreatEmptyInputAsDefaultValue)
            return InputFormatterResult.NoValue();
        logger.LogDebug(new EventId(2, "SystemTextJsonInputSuccess"), "JSON input formatter succeeded, deserializing to type '{TypeName}'", context.ModelType.FullName);
        return InputFormatterResult.Success(model);
    }

    private static void ConfigureContacts(JsonTypeInfo info)
    {
        if (info.Type != typeof(UpsertCustomerRequest) || info.Kind != JsonTypeInfoKind.Object
            || info.IsReadOnly || info.CreateObject is not null)
            return;
        var slots = new[] { nameof(UpsertCustomerRequest.Telephone), nameof(UpsertCustomerRequest.Mobile), nameof(UpsertCustomerRequest.Fax) }
            .Select(name => info.Properties.SingleOrDefault(property => property.Name == name)).ToArray();
        if (slots.Any(property => property is null || property.PropertyType != typeof(string)
            || property.Set is null || property.CustomConverter is not null)
            || info.Options.Converters.Count != 0
            || !ReferenceEquals(info.Options.GetConverter(typeof(string)), DefaultString))
            return;
        foreach (var property in slots)
        {
            var slot = property!;
            // Every read owns fresh options/metadata and a distinct converter for each contact slot.
            // Keep constructor argument binding so framework rejection diagnostics retain their context.
            slot.CustomConverter = new ContactPrimitiveJsonConverter(DefaultString);
        }
    }

    private sealed class ContactPrimitiveJsonConverter(JsonConverter<string> literal) : JsonConverter<string>
    {
        private readonly OrdinaryCompanyFieldPrimitiveJsonConverter primitive = new(literal);
        private string? lastNonNull;

        public override bool HandleNull => true;

        public override string? Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options)
        {
            if (reader.TokenType == JsonTokenType.Null) return lastNonNull;
            var value = primitive.Read(ref reader, typeToConvert, options);
            if (value is not null) lastNonNull = value;
            return value;
        }

        public override void Write(Utf8JsonWriter writer, string value, JsonSerializerOptions options)
        {
            // Write behavior is independent of read history, including the explicit HandleNull path.
            if (value is null) writer.WriteNullValue();
            else literal.Write(writer, value, options);
        }
    }

    private sealed record Ownership(DefaultJsonTypeInfoResolver Reflection, IJsonTypeInfoResolver Composed);

    internal sealed class Registration(IOptions<JsonOptions> source, ILoggerFactory loggers) : IPostConfigureOptions<MvcOptions>
    {
        public void PostConfigure(string? name, MvcOptions options)
        {
            var originals = options.InputFormatters.Where(formatter => formatter.GetType() == typeof(SystemTextJsonInputFormatter)).ToArray();
            if (originals.Length != 1 || options.InputFormatters.Any(formatter => formatter is OrdinaryCustomerContactInputFormatter))
                return;
            var original = (SystemTextJsonInputFormatter)originals[0];
            options.InputFormatters.Insert(options.InputFormatters.IndexOf(original),
                new OrdinaryCustomerContactInputFormatter(source.Value, original, loggers.CreateLogger<SystemTextJsonInputFormatter>()));
        }
    }
}
