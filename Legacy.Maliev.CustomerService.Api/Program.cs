using System.Text.Json.Serialization;
using Legacy.Maliev.CustomerService.Application.Interfaces;
using Legacy.Maliev.CustomerService.Application.Services;
using Legacy.Maliev.CustomerService.Data;
using Maliev.Aspire.ServiceDefaults;

var builder = WebApplication.CreateBuilder(args);

builder.AddServiceDefaults();
builder.AddDefaultApiVersioning();
builder.AddPostgresDbContext<CustomerDbContext>(connectionName: "CustomerDbContext");
builder.AddStandardCache("legacy:customer:");
builder.AddStandardCors();
builder.AddJwtAuthentication();
builder.AddStandardMiddleware(options => options.EnableRequestLogging = true);
builder.AddStandardOpenApi(
    title: "Legacy MALIEV Customer Service API",
    description: "Temporary .NET 10 compatibility service preserving legacy customer, company, address, and identity-proxy contracts.");

builder.Services.AddControllers().AddJsonOptions(options =>
{
    options.JsonSerializerOptions.DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull;
    options.JsonSerializerOptions.PropertyNamingPolicy = null;
    options.JsonSerializerOptions.DictionaryKeyPolicy = null;
});
builder.Services.AddSingleton(TimeProvider.System);
builder.Services.AddHttpClient<ICustomerIdentityDirectory, AuthServiceCustomerIdentityDirectory>(client =>
{
    client.BaseAddress = new Uri(builder.Configuration["AuthService:LegacyCustomerIdentityBaseUrl"]
        ?? "http://authservice/auth/v1/legacy/customers/");
    client.Timeout = TimeSpan.FromSeconds(15);
}).AddStandardResilienceHandler();
builder.Services.AddScoped<ICustomerRepository, CustomerRepository>();
builder.Services.AddScoped<ICustomerCache, DistributedCustomerCache>();
builder.Services.AddScoped<ICustomerService, CustomerApplicationService>();

var app = builder.Build();

app.UseStandardMiddleware();
app.UseCors();
app.UseAuthentication();
app.UseAuthorization();
app.MapDefaultEndpoints("customer");
app.MapControllers();
app.MapApiDocumentation(servicePrefix: "customer");

await app.RunAsync();

/// <summary>Legacy Customer Service entry point.</summary>
public partial class Program;
