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

/// <summary>Reads the original inert Customer, Address and Company envelopes only for owned ordinary write actions.</summary>
internal sealed class OrdinaryCustomerContactInputFormatter : TextInputFormatter, IInputFormatterExceptionPolicy
{
    private static readonly ConditionalWeakTable<JsonSerializerOptions, Ownership> Owners = new();
    private static readonly MethodInfo Create = typeof(CustomersController).GetMethod(
        nameof(CustomersController.CreateCustomerAsync), [typeof(UpsertCustomerRequest), typeof(CancellationToken)])!;
    private static readonly MethodInfo Update = typeof(CustomersController).GetMethod(
        nameof(CustomersController.UpdateCustomerAsync), [typeof(int), typeof(UpsertCustomerRequest), typeof(CancellationToken)])!;
    private readonly JsonOptions source;
    private readonly SystemTextJsonInputFormatter original;
    private readonly CustomerOriginalBodyReader reader;

    internal OrdinaryCustomerContactInputFormatter(JsonOptions source, SystemTextJsonInputFormatter original,
        ILogger<SystemTextJsonInputFormatter> logger)
    {
        this.source = source;
        this.original = original;
        reader = CustomerOriginalBodyReader.Create(logger);
        foreach (var encoding in original.SupportedEncodings) SupportedEncodings.Add(encoding);
        foreach (var mediaType in original.SupportedMediaTypes) SupportedMediaTypes.Add(mediaType);
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

    protected override bool CanReadType(Type type) => type == typeof(UpsertCustomerRequest)
        || type == typeof(UpsertAddressRequest) || type == typeof(UpsertCompanyRequest);

    public override bool CanRead(InputFormatterContext context) =>
        Eligible(context) && OwnsPolicy() && base.CanRead(context);

    private static readonly MethodInfo AddressCreate = typeof(AddressesController).GetMethod(
        nameof(AddressesController.CreateCustomerAddressAsync), [typeof(int), typeof(UpsertAddressRequest), typeof(CancellationToken)])!;
    private static readonly MethodInfo AddressUpdate = typeof(AddressesController).GetMethod(
        nameof(AddressesController.UpdateAddressAsync), [typeof(int), typeof(UpsertAddressRequest), typeof(CancellationToken)])!;

    private static readonly MethodInfo CompanyCreate = typeof(CompaniesController).GetMethod(
        nameof(CompaniesController.CreateCompanyAsync), [typeof(UpsertCompanyRequest), typeof(CancellationToken)])!;
    private static readonly MethodInfo CompanyUpdate = typeof(CompaniesController).GetMethod(
        nameof(CompaniesController.UpdateCompanyAsync), [typeof(int), typeof(UpsertCompanyRequest), typeof(CancellationToken)])!;

    private static bool Eligible(InputFormatterContext context)
    {
        if (context.HttpContext.GetEndpoint()?.Metadata.GetMetadata<ControllerActionDescriptor>() is not { } action)
            return false;
        var request = context.HttpContext.Request;
        if (context.ModelType == typeof(UpsertCustomerRequest)
            && action.ControllerTypeInfo.AsType() == typeof(CustomersController))
            return action.MethodInfo == Create && HttpMethods.IsPost(request.Method)
                    && !request.Headers.ContainsKey("Idempotency-Key")
                || action.MethodInfo == Update && HttpMethods.IsPut(request.Method);
        if (context.ModelType == typeof(UpsertCompanyRequest)
            && action.ControllerTypeInfo.AsType() == typeof(CompaniesController))
            return action.MethodInfo == CompanyCreate && HttpMethods.IsPost(request.Method)
                || action.MethodInfo == CompanyUpdate && HttpMethods.IsPut(request.Method);
        return context.ModelType == typeof(UpsertAddressRequest)
            && action.ControllerTypeInfo.AsType() == typeof(AddressesController)
            && (action.MethodInfo == AddressCreate && HttpMethods.IsPost(request.Method)
                || action.MethodInfo == AddressUpdate && HttpMethods.IsPut(request.Method));
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

    public override Task<InputFormatterResult> ReadAsync(InputFormatterContext context)
    {
        ArgumentNullException.ThrowIfNull(context);
        // Let the local stock reader select encoding and sanitize its failures
        // before the outer formatter can add caller-controlled charset text.
        return Eligible(context) && OwnsPolicy() ? reader.ReadAsync(context) : base.ReadAsync(context);
    }

    public override async Task<InputFormatterResult> ReadRequestBodyAsync(InputFormatterContext context, Encoding encoding)
    {
        ArgumentNullException.ThrowIfNull(context);
        ArgumentNullException.ThrowIfNull(encoding);
        if (!Eligible(context) || !OwnsPolicy())
            return await original.ReadRequestBodyAsync(context, encoding);
        return await reader.ReadAsync(context);
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
                template.AddApiVersioning(options => options.ApiVersionReader = new Asp.Versioning.QueryStringApiVersionReader()).AddMvc();
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
