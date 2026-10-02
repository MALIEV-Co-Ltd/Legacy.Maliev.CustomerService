using System.Net;
using System.Text.Json;
using Legacy.Maliev.CustomerService.Tests.Startup;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;

namespace Legacy.Maliev.CustomerService.Tests.Documentation;

[Collection("Customer startup process state")]
public sealed class CustomerDocumentationHttpTests
{
    [Fact]
    public async Task Production_does_not_serve_the_development_document()
    {
        await using var factory = Factory("Production");
        using var client = factory.CreateClient(new WebApplicationFactoryClientOptions { AllowAutoRedirect = false });
        using var response = await client.GetAsync("/customer/openapi/v1.json");
        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
    }

    [Fact]
    public async Task Development_serves_the_registered_customer_document_without_identity_operations()
    {
        await using var factory = Factory("Development");
        using var client = factory.CreateClient(new WebApplicationFactoryClientOptions { AllowAutoRedirect = false });
        using var document = await DocumentAsync(client);
        var root = document.RootElement;
        Assert.StartsWith("3.", root.GetProperty("openapi").GetString());
        Assert.Equal("Legacy MALIEV Customer Service API", root.GetProperty("info").GetProperty("title").GetString());
        Assert.Equal("Temporary .NET 10 compatibility service preserving legacy customer, company, address, and email contracts.",
            root.GetProperty("info").GetProperty("description").GetString());
        var paths = root.GetProperty("paths");
        Assert.True(paths.TryGetProperty("/customers", out _));
        Assert.True(paths.TryGetProperty("/customers/{id}", out _));
        Assert.True(paths.TryGetProperty("/customers/instant-quotation-profile", out _));
        foreach (var path in paths.EnumerateObject())
        {
            Assert.DoesNotContain("identity", path.Name, StringComparison.OrdinalIgnoreCase);
            Assert.DoesNotContain("credential", path.Name, StringComparison.OrdinalIgnoreCase);
            Assert.DoesNotContain("password", path.Name, StringComparison.OrdinalIgnoreCase);
            Assert.DoesNotContain("token", path.Name, StringComparison.OrdinalIgnoreCase);
        }
    }

    [Fact]
    public async Task Customer_create_documents_its_existing_profile_operation()
    {
        await using var factory = Factory("Development");
        using var client = factory.CreateClient();
        using var document = await DocumentAsync(client);
        var operation = Operation(document, "/customers", "post");
        AssertSummary(operation, "Creates a legacy customer profile from the supplied contact details.");
        Assert.True(operation.TryGetProperty("requestBody", out _));
    }

    [Fact]
    public async Task Customer_list_documents_the_existing_query_contract()
    {
        await using var factory = Factory("Development");
        using var client = factory.CreateClient();
        using var document = await DocumentAsync(client);
        var operation = Operation(document, "/customers", "get");
        AssertSummary(operation, "Searches and pages through legacy customer profiles.");
        AssertParameter(operation, "sort", "query", "The optional ordering applied to the customer results.");
        AssertParameter(operation, "search", "query", "The optional text used to filter matching customer profiles.");
        AssertParameter(operation, "index", "query", "The optional zero-based page index.");
        AssertParameter(operation, "size", "query", "The optional maximum number of profiles returned per page.");
    }

    [Fact]
    public async Task Nested_address_read_documents_both_ownership_identifiers()
    {
        await using var factory = Factory("Development");
        using var client = factory.CreateClient();
        using var document = await DocumentAsync(client);
        var operation = Operation(document, "/customers/{customerId}/addresses/{addressId}", "get");
        AssertSummary(operation, "Retrieves a specific address owned by a customer.");
        AssertParameter(operation, "customerId", "path", "The unique identifier of the customer that owns the address.");
        AssertParameter(operation, "addressId", "path", "The unique identifier of the address to retrieve.");
    }

    [Fact]
    public async Task Email_lookup_documents_the_existing_lookup_parameter()
    {
        await using var factory = Factory("Development");
        using var client = factory.CreateClient();
        using var document = await DocumentAsync(client);
        var operation = Operation(document, "/customers/Emails/{email}", "get");
        AssertSummary(operation, "Retrieves the customer profile associated with an email address.");
        AssertParameter(operation, "email", "path", "The customer email address to look up.");
    }

    private static WebApplicationFactory<Program> Factory(string environment) =>
        new WebApplicationFactory<Program>().WithWebHostBuilder(builder =>
        {
            builder.UseEnvironment(environment);
            foreach (var (key, value) in CustomerStartupProcess.Settings()) builder.UseSetting(key, value);
        });

    private static async Task<JsonDocument> DocumentAsync(HttpClient client)
    {
        using var response = await client.GetAsync("/customer/openapi/v1.json");
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal("application/json", response.Content.Headers.ContentType?.MediaType);
        // JsonDocument property lookup is case-sensitive; no Web-option dictionary comparison.
        return JsonDocument.Parse(await response.Content.ReadAsStringAsync());
    }

    private static JsonElement Operation(JsonDocument document, string path, string method) =>
        document.RootElement.GetProperty("paths").GetProperty(path).GetProperty(method);

    private static void AssertSummary(JsonElement operation, string expected)
    {
        Assert.True(operation.TryGetProperty("summary", out var summary), "The served operation must include its source XML summary.");
        Assert.Equal(expected, summary.GetString());
    }

    private static void AssertParameter(JsonElement operation, string name, string location, string description)
    {
        var parameter = Assert.Single(operation.GetProperty("parameters").EnumerateArray(), item =>
            item.GetProperty("name").GetString() == name && item.GetProperty("in").GetString() == location);
        Assert.True(parameter.TryGetProperty("description", out var documented), "The served parameter must include its source XML description.");
        Assert.Equal(description, documented.GetString());
        if (location == "path") Assert.True(parameter.GetProperty("required").GetBoolean());
    }
}
