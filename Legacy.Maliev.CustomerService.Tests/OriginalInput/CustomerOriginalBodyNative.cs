using System.Reflection;
using Legacy.Maliev.CustomerService.Api.Controllers;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Http.Features;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.Controllers;
using Microsoft.AspNetCore.Mvc.Formatters;
using Microsoft.AspNetCore.Mvc.ModelBinding;
using Microsoft.AspNetCore.WebUtilities;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;

namespace Legacy.Maliev.CustomerService.Tests.OriginalInput;

internal static class CustomerOriginalBodyNative
{
    internal const string FormatterName = "Legacy.Maliev.CustomerService.Api.OrdinaryCustomerContactInputFormatter";
    internal const string RegistrationName = FormatterName + "+Registration";

    internal static bool IsOwned(IPostConfigureOptions<MvcOptions> configuration) =>
        configuration.GetType().Assembly == typeof(Program).Assembly
        && configuration.GetType().FullName == RegistrationName;

    internal static MvcOptions Create(IServiceProvider services)
    {
        var setups = services.GetServices<IConfigureOptions<MvcOptions>>().ToArray();
        var posts = services.GetServices<IPostConfigureOptions<MvcOptions>>().ToArray();
        var validators = services.GetServices<IValidateOptions<MvcOptions>>().ToArray();
        Assert.Single(posts, IsOwned);
        var retained = posts.Where(post => !IsOwned(post)).ToArray();
        Assert.Equal(posts.Length - 1, retained.Length);
        var index = 0;
        foreach (var post in posts)
        {
            if (IsOwned(post)) continue;
            Assert.Same(post, retained[index++]);
        }
        Assert.Equal(retained.Length, index);
        var native = new OptionsFactory<MvcOptions>(setups, retained, validators).Create(Options.DefaultName);
        Assert.DoesNotContain(native.InputFormatters, formatter => formatter.GetType().FullName == FormatterName);
        Assert.Single(native.InputFormatters, formatter => formatter.GetType() == typeof(SystemTextJsonInputFormatter));
        return native;
    }

    internal static async Task<CustomerBodyRead> ReadAsync(IServiceProvider services, MvcOptions options, string kind,
        byte[] bytes, string operation = "create", string contentType = "application/json", string prefix = "",
        int maxErrors = 200)
    {
        var context = Context(services, kind, bytes, operation, contentType, prefix, maxErrors);
        using var input = context.HttpContext.Request.Body;
        var formatter = options.InputFormatters.FirstOrDefault(item => item.CanRead(context));
        if (formatter is null) return new CustomerBodyRead("unsupported", null, [], 0, [], []);
        return await CustomerOriginalInputOracle.ObserveAsync(formatter, context);
    }

    // The caller owns and disposes Context.HttpContext.Request.Body.
    internal static InputFormatterContext Context(IServiceProvider services, string kind, byte[] bytes,
        string operation = "create", string contentType = "application/json", string prefix = "", int maxErrors = 200)
    {
        var http = new DefaultHttpContext();
        var input = new MemoryStream(bytes, writable: false);
        http.Request.Body = input;
        http.Request.ContentLength = bytes.LongLength;
        http.Request.ContentType = contentType;
        http.Request.Method = operation == "create" ? "POST" : "PUT";
        http.Features.Set<IHttpRequestBodyDetectionFeature>(new BodyFeature(bytes.Length > 0));
        var controller = kind == "address" ? typeof(AddressesController) : typeof(CustomersController);
        var method = kind == "address"
            ? operation == "create" ? nameof(AddressesController.CreateCustomerAddressAsync) : nameof(AddressesController.UpdateAddressAsync)
            : operation == "create" ? nameof(CustomersController.CreateCustomerAsync) : nameof(CustomersController.UpdateCustomerAsync);
        var descriptor = new ControllerActionDescriptor
        {
            ControllerTypeInfo = controller.GetTypeInfo(),
            MethodInfo = controller.GetMethod(method)!
        };
        http.SetEndpoint(new Endpoint(_ => Task.CompletedTask, new EndpointMetadataCollection(descriptor), "synthetic-reader"));
        var metadata = services.GetRequiredService<IModelMetadataProvider>()
            .GetMetadataForType(CustomerOriginalInputOracle.DtoType(kind));
        var state = new ModelStateDictionary { MaxAllowedErrors = maxErrors };
        return new InputFormatterContext(http, prefix, state, metadata,
            static (stream, encoding) => new HttpRequestStreamReader(stream, encoding), false);
    }

    private sealed record BodyFeature(bool CanHaveBody) : IHttpRequestBodyDetectionFeature;
}
