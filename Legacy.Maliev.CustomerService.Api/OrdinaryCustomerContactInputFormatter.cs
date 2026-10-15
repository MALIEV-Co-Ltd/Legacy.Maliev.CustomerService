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

    internal static void TrackVersioningPostConfigure(IServiceCollection services, ServiceDescriptor ownConfiguration)
    {
        var profile = new VersioningOwnershipProfile(services, ownConfiguration);
        profile.Install();
    }

    private sealed record Ownership(DefaultJsonTypeInfoResolver Reflection, IJsonTypeInfoResolver Composed, bool VersioningApplied = false);

    // This is a closed registration profile, not permission to adopt an arbitrary final resolver.
    // Template registration creates descriptor metadata only: no provider, factory invocation, or service resolution.
    private sealed class VersioningOwnershipProfile(IServiceCollection services, ServiceDescriptor ownConfiguration)
    {
        private static readonly Type Annotation = typeof(Asp.Versioning.IAnnotation<MemberInfo, Asp.Versioning.ApiVersionRange>);
        private static readonly Type PostConfigure = typeof(IPostConfigureOptions<JsonOptions>);
        private static readonly Type Configure = typeof(IConfigureOptions<JsonOptions>);
        private readonly ServiceDescriptor[] captured = services.ToArray();
        private ServiceDescriptor? vendorDescriptor;
        private ServiceDescriptor? trackedDescriptor;
        private ServiceDescriptor? denialDescriptor;
        private ServiceDescriptor? annotationDescriptor;
        private ServiceDescriptor? accessorDescriptor;
        private Type? annotationImplementation;
        private bool supported;

        internal void Install()
        {
            var mvcAssembly = typeof(Asp.Versioning.MvcApiVersioningOptions).Assembly;
            var httpAssembly = typeof(Asp.Versioning.ApiVersioningFeature).Assembly;
            if (mvcAssembly.ManifestModule.ModuleVersionId == new Guid("ae14e30a-df28-4e58-9f5a-1254d1b7817b")
                && httpAssembly.ManifestModule.ModuleVersionId == new Guid("e23c6dbb-ef2c-42b8-8bab-c8e6a254ec5c"))
            {
                var template = new ServiceCollection();
                template.AddApiVersioning().AddMvc();
                var vendor = template.Single(item => item.ServiceType == PostConfigure);
                var annotation = template.Single(item => item.ServiceType == Annotation);
                var accessor = template.Single(item => item.ServiceType == typeof(IHttpContextAccessor));
                annotationImplementation = httpAssembly.GetType("Asp.Versioning.AnnotationCache", throwOnError: false);
                vendorDescriptor = Single(PostConfigure);
                annotationDescriptor = Single(Annotation);
                accessorDescriptor = Single(typeof(IHttpContextAccessor));
                supported = annotationImplementation is not null
                    && SameShape(vendorDescriptor, vendor) && vendorDescriptor!.ImplementationType is not null
                    && vendorDescriptor.Lifetime == ServiceLifetime.Transient
                    && SameShape(annotationDescriptor, annotation) && SameShape(accessorDescriptor, accessor)
                    && ReferenceEquals(Single(Configure), ownConfiguration)
                    && services.IndexOf(ownConfiguration) > services.IndexOf(vendorDescriptor)
                    && services.Where(item => item.ServiceType == PostConfigure || item.ServiceType == Annotation || item.ServiceType == typeof(IHttpContextAccessor))
                        .Select(item => item.ServiceType).SequenceEqual(template.Where(item => item.ServiceType == PostConfigure || item.ServiceType == Annotation || item.ServiceType == typeof(IHttpContextAccessor)).Select(item => item.ServiceType));
                if (supported)
                {
                    var implementation = vendorDescriptor!.ImplementationType!;
                    trackedDescriptor = ServiceDescriptor.Transient(PostConfigure, provider =>
                    {
                        // Normal activation remains unconditional. Unsupported dependencies retain vendor behavior.
                        var original = (IPostConfigureOptions<JsonOptions>)ActivatorUtilities.CreateInstance(provider, implementation);
                        var qualified = FinalProfile() && DefaultProvider(provider)
                            && provider.GetRequiredService(Annotation).GetType() == annotationImplementation
                            && provider.GetRequiredService<IHttpContextAccessor>().GetType() == typeof(HttpContextAccessor);
                        return new TrackedPostConfigure(original, this, qualified);
                    });
                    services[services.IndexOf(vendorDescriptor)] = trackedDescriptor;
                }
            }
            denialDescriptor = ServiceDescriptor.Transient<IPostConfigureOptions<JsonOptions>>(_ => new DenyUnqualified(this));
            services.Add(denialDescriptor);
        }

        private ServiceDescriptor? Single(Type type)
        {
            var descriptors = services.Where(item => item.ServiceType == type).ToArray();
            return descriptors.Length == 1 && !descriptors[0].IsKeyedService ? descriptors[0] : null;
        }

        private static bool SameShape(ServiceDescriptor? actual, ServiceDescriptor expected)
        {
            if (actual is null || actual.IsKeyedService || expected.IsKeyedService
                || actual.ServiceType != expected.ServiceType || actual.Lifetime != expected.Lifetime
                || actual.ImplementationType != expected.ImplementationType
                || !ReferenceEquals(actual.ImplementationInstance, expected.ImplementationInstance)) return false;
            var left = actual.ImplementationFactory;
            var right = expected.ImplementationFactory;
            if (left is null || right is null) return left is null && right is null;
            return left.GetInvocationList().Length == 1 && right.GetInvocationList().Length == 1
                && left.Method == right.Method && ReferenceEquals(left.Target, right.Target);
        }

        private bool FinalProfile()
        {
            if (!supported || !services.IsReadOnly || trackedDescriptor is null || denialDescriptor is null
                || !ReferenceEquals(Single(Configure), ownConfiguration)
                || !ReferenceEquals(Single(Annotation), annotationDescriptor)
                || !ReferenceEquals(Single(typeof(IHttpContextAccessor)), accessorDescriptor)) return false;
            var callbacks = services.Where(item => item.ServiceType == PostConfigure).ToArray();
            if (callbacks.Length != 2 || !ReferenceEquals(callbacks[0], trackedDescriptor)
                || !ReferenceEquals(callbacks[1], denialDescriptor)) return false;
            // Preserve every captured relevant descriptor and its relative registration position.
            var relevant = new[] { Configure, PostConfigure, Annotation, typeof(IHttpContextAccessor) };
            var expected = captured.Where(item => relevant.Contains(item.ServiceType))
                .Select(item => ReferenceEquals(item, vendorDescriptor) ? trackedDescriptor : item).Append(denialDescriptor).ToArray();
            var actual = services.Where(item => relevant.Contains(item.ServiceType)).ToArray();
            return expected.Length == actual.Length && expected.Zip(actual).All(pair => ReferenceEquals(pair.First, pair.Second));
        }

        private static bool DefaultProvider(IServiceProvider provider) =>
            provider.GetType() == typeof(ServiceProvider).Assembly.GetType("Microsoft.Extensions.DependencyInjection.ServiceLookup.ServiceProviderEngineScope", false)
            || provider.GetType() == typeof(ServiceProvider);

        private static bool OrdinaryPolicy(JsonSerializerOptions options) =>
            options.DefaultIgnoreCondition == JsonIgnoreCondition.WhenWritingNull
            && options.PropertyNameCaseInsensitive && options.PropertyNamingPolicy is null
            && options.DictionaryKeyPolicy is null && options.ReferenceHandler is null
            && !options.RespectRequiredConstructorParameters && !options.RespectNullableAnnotations
            && options.PreferredObjectCreationHandling == JsonObjectCreationHandling.Replace
            && !options.IgnoreReadOnlyProperties && options.Converters.Count == 0;

        private sealed class TrackedPostConfigure(IPostConfigureOptions<JsonOptions> original, VersioningOwnershipProfile profile, bool qualified)
            : IPostConfigureOptions<JsonOptions>
        {
            public void PostConfigure(string? name, JsonOptions options)
            {
                var serializer = options.JsonSerializerOptions;
                var before = serializer.TypeInfoResolver;
                var owned = qualified && profile.FinalProfile() && Owners.TryGetValue(serializer, out var ownership)
                    && !ownership.VersioningApplied && ownership.Reflection.Modifiers.Count == 0
                    && ReferenceEquals(before, ownership.Composed) && OrdinaryPolicy(serializer);
                Owners.TryGetValue(serializer, out var previous);
                // Revoke first; failures or partial mutations cannot leave an accepted marker behind.
                Owners.Remove(serializer);
                original.PostConfigure(name, options);
                if (owned && profile.FinalProfile() && previous is not null
                    && previous.Reflection.Modifiers.Count == 0 && OrdinaryPolicy(serializer)
                    && serializer.TypeInfoResolver is { } after && !ReferenceEquals(before, after))
                    Owners.Add(serializer, new Ownership(previous.Reflection, after, VersioningApplied: true));
            }
        }

        private sealed class DenyUnqualified(VersioningOwnershipProfile profile) : IPostConfigureOptions<JsonOptions>
        {
            public void PostConfigure(string? name, JsonOptions options)
            {
                if (!profile.FinalProfile()) Owners.Remove(options.JsonSerializerOptions);
            }
        }
    }

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
