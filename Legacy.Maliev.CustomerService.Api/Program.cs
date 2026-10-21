using System.Text.Json.Serialization;
using Legacy.Maliev.CustomerService.Application.Interfaces;
using Legacy.Maliev.CustomerService.Application.Services;
using Legacy.Maliev.CustomerService.Data;
using Maliev.Aspire.ServiceDefaults;
using Maliev.Aspire.ServiceDefaults.Diagnostics;

try
{
    await RunHostAsync(args);
}
catch (Microsoft.Extensions.Hosting.HostAbortedException)
{
    throw;
}
catch (Exception exception)
{
    PrivateStartupBoundary.ReportFailure(exception);
}

static async Task RunHostAsync(string[] startupArgs)
{
    var builder = WebApplication.CreateBuilder(startupArgs);

    builder.AddServiceDefaults();
    builder.AddPrivateRequestObservation("customer");
    builder.AddDefaultApiVersioning();
    builder.AddPostgresDbContext<CustomerDbContext>(connectionName: "CustomerDbContext");
    builder.AddStandardCache("legacy:customer:");
    builder.AddStandardCors();
    builder.AddJwtAuthentication();
    builder.AddStandardMiddleware(options => options.EnableRequestLogging = true);
    builder.AddStandardOpenApi(
        title: "Legacy MALIEV Customer Service API",
        description: "Temporary .NET 10 compatibility service preserving legacy customer, company, address, and email contracts.");
    builder.Services.AddOpenApi("v1");

    builder.Services.AddControllers().AddJsonOptions(options =>
    {
        options.JsonSerializerOptions.DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull;
        options.JsonSerializerOptions.PropertyNamingPolicy = null;
        options.JsonSerializerOptions.DictionaryKeyPolicy = null;
        Legacy.Maliev.CustomerService.Api.OrdinaryCustomerContactInputFormatter.ComposeOwned(options.JsonSerializerOptions);
    });
    var ownedJsonConfiguration = builder.Services.Last(descriptor => descriptor.ServiceType ==
        typeof(Microsoft.Extensions.Options.IConfigureOptions<Microsoft.AspNetCore.Mvc.JsonOptions>));
    builder.Services.AddSingleton<Microsoft.Extensions.Options.IPostConfigureOptions<Microsoft.AspNetCore.Mvc.MvcOptions>,
        Legacy.Maliev.CustomerService.Api.OrdinaryCustomerContactInputFormatter.Registration>();
    builder.Services.AddSingleton(TimeProvider.System);
    builder.Services.AddScoped<ICustomerRepository, CustomerRepository>();
    builder.Services.AddScoped<ICustomerCache, DistributedCustomerCache>();
    builder.Services.AddScoped<ICustomerService, CustomerApplicationService>();
    builder.Services.AddScoped<ICustomerRelationRepository, CustomerRelationRepository>();
    builder.Services.AddScoped<ICustomerRelationService, CustomerRelationService>();
    builder.Services.AddScoped<Legacy.Maliev.CustomerService.Api.CustomerCreateReplayService>();

    Legacy.Maliev.CustomerService.Api.OrdinaryCustomerContactInputFormatter.TrackVersioningPostConfigure(
        builder.Services, ownedJsonConfiguration);
    var app = builder.Build();

    app.UseStandardMiddleware();
    app.UseCors();
    app.UseAuthentication();
    app.UseAuthorization();
    app.MapDefaultEndpoints("customer");
    app.MapControllers();
    app.MapApiDocumentation(servicePrefix: "customer");

    await app.RunAsync();
}

/// <summary>Legacy Customer Service entry point.</summary>
public partial class Program;
