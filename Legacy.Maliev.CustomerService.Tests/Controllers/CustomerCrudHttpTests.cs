using System.Net;
using System.Text;
using System.Text.Json;
using Legacy.Maliev.CustomerService.Application.Interfaces;
using Legacy.Maliev.CustomerService.Application.Models;
using Microsoft.AspNetCore.TestHost;
using Moq;

namespace Legacy.Maliev.CustomerService.Tests.Controllers;

public sealed class CustomerCrudHttpTests
{
    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task CustomerCrud_PreservesNamedRoutesAndMissingStatuses(bool found)
    {
        var profile = Profile();
        var request = new UpsertCustomerRequest("First", "Last", null, null, null, "sample@example.test", null, null, null, null);
        var service = new Mock<ICustomerService>(MockBehavior.Strict);
        service.Setup(value => value.CreateCustomerAsync(request, It.IsAny<CancellationToken>())).ReturnsAsync(profile);
        service.Setup(value => value.GetCustomerAsync(7, It.IsAny<CancellationToken>())).ReturnsAsync(found ? profile : null);
        service.Setup(value => value.GetCustomerByEmailAsync("sample@example.test", It.IsAny<CancellationToken>())).ReturnsAsync(found ? profile : null);
        service.Setup(value => value.UpdateCustomerAsync(7, request, It.IsAny<CancellationToken>())).ReturnsAsync(found);
        service.Setup(value => value.DeleteCustomerAsync(7, It.IsAny<CancellationToken>())).ReturnsAsync(found);
        service.Setup(value => value.GetInternalRemarkAsync(7, It.IsAny<CancellationToken>())).ReturnsAsync(found ? new CustomerInternalRemarkResponse(7, "staff only") : null);
        service.Setup(value => value.UpdateInternalRemarkAsync(7, new("staff only"), It.IsAny<CancellationToken>())).ReturnsAsync(found);
        service.Setup(value => value.GetCustomersAsync(CustomerSortType.CustomerId_Descending, "First", 2, 10, It.IsAny<CancellationToken>()))
            .ReturnsAsync(found ? new PaginatedResponse<CustomerResponse>([profile], 2, 3, 21) : null);
        await using var app = await CompanyTaxOnlyHttpTests.StartAsync(service.Object);
        using var client = app.GetTestClient();
        client.DefaultRequestHeaders.Add("Test-Identity", "allowed");
        using var created = await client.PostAsync("/customers", Body(request));
        Assert.Equal(HttpStatusCode.Created, created.StatusCode);
        Assert.Equal("/customers/7", created.Headers.Location?.AbsolutePath);
        using var json = JsonDocument.Parse(await created.Content.ReadAsStringAsync());
        Assert.Equal("First Last", json.RootElement.GetProperty("FullName").GetString());
        Assert.False(json.RootElement.TryGetProperty("Company", out _));
        foreach (var path in new[] { "/customers/7", "/customers/emails/sample@example.test", "/customers/7/internal-remark", "/customers?sort=CustomerId_Descending&search=First&index=2&size=10" })
        {
            using var read = await client.GetAsync(path);
            Assert.Equal(found ? HttpStatusCode.OK : HttpStatusCode.NotFound, read.StatusCode);
        }
        using var updated = await client.PutAsync("/customers/7", Body(request));
        using var remark = await client.PutAsync("/customers/7/internal-remark", Body(new UpdateCustomerInternalRemarkRequest("staff only")));
        using var deleted = await client.DeleteAsync("/customers/7");
        Assert.Equal(found ? HttpStatusCode.NoContent : HttpStatusCode.NotFound, updated.StatusCode);
        Assert.Equal(found ? HttpStatusCode.NoContent : HttpStatusCode.NotFound, remark.StatusCode);
        Assert.Equal(found ? HttpStatusCode.NoContent : HttpStatusCode.NotFound, deleted.StatusCode);
        service.VerifyAll();
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task AddressAndCompanyCrud_PreserveWireShapesAndMissingStatuses(bool found)
    {
        var request = new UpsertAddressRequest(null, "1 Example Road", null, "Bangkok", null, "10110", 764);
        var address = new AddressResponse(13, null, "1 Example Road", null, "Bangkok", null, "10110", 764, null, null);
        var company = new CompanyResponse(23, "", "0100000000000", null, null, null);
        var service = new Mock<ICustomerService>(MockBehavior.Strict);
        service.Setup(value => value.CreateAddressAsync(7, request, It.IsAny<CancellationToken>())).ReturnsAsync(found ? address : null);
        service.Setup(value => value.GetAddressAsync(7, 13, It.IsAny<CancellationToken>())).ReturnsAsync(found ? address : null);
        service.Setup(value => value.GetAddressesAsync(It.IsAny<CancellationToken>())).ReturnsAsync(found ? [address] : []);
        service.Setup(value => value.UpdateAddressAsync(13, request, It.IsAny<CancellationToken>())).ReturnsAsync(found);
        service.Setup(value => value.DeleteAddressAsync(7, 13, It.IsAny<CancellationToken>())).ReturnsAsync(found);
        service.Setup(value => value.GetCompanyAsync(23, It.IsAny<CancellationToken>())).ReturnsAsync(found ? company : null);
        service.Setup(value => value.DeleteCompanyAsync(23, It.IsAny<CancellationToken>())).ReturnsAsync(found);
        await using var app = await CompanyTaxOnlyHttpTests.StartAsync(service.Object);
        using var client = app.GetTestClient();
        client.DefaultRequestHeaders.Add("Test-Identity", "allowed");
        using var created = await client.PostAsync("/customers/7/addresses", Body(request));
        Assert.Equal(found ? HttpStatusCode.Created : HttpStatusCode.NotFound, created.StatusCode);
        if (found)
        {
            Assert.Equal("/customers/7/addresses/13", created.Headers.Location?.AbsolutePath);
            using var json = JsonDocument.Parse(await created.Content.ReadAsStringAsync());
            Assert.Equal(764, json.RootElement.GetProperty("CountryId").GetInt32());
            Assert.False(json.RootElement.TryGetProperty("Building", out _));
        }
        foreach (var path in new[] { "/customers/7/addresses/13", "/customers/addresses", "/customers/companies/23" })
        {
            using var read = await client.GetAsync(path);
            Assert.Equal(found ? HttpStatusCode.OK : HttpStatusCode.NotFound, read.StatusCode);
        }
        using var updated = await client.PutAsync("/customers/addresses/13", Body(request));
        using var deletedAddress = await client.DeleteAsync("/customers/7/addresses/13");
        using var deletedCompany = await client.DeleteAsync("/customers/companies/23");
        Assert.Equal(found ? HttpStatusCode.NoContent : HttpStatusCode.NotFound, updated.StatusCode);
        Assert.Equal(found ? HttpStatusCode.NoContent : HttpStatusCode.NotFound, deletedAddress.StatusCode);
        Assert.Equal(found ? HttpStatusCode.NoContent : HttpStatusCode.NotFound, deletedCompany.StatusCode);
        service.VerifyAll();
    }

    [Theory]
    [InlineData("", "Last", "sample@example.test")]
    [InlineData("First", " ", "sample@example.test")]
    [InlineData("First", "Last", "invalid")]
    public async Task InvalidCustomerOrAddress_RejectBeforePersistence(string first, string last, string email)
    {
        var service = new Mock<ICustomerService>(MockBehavior.Strict);
        await using var app = await CompanyTaxOnlyHttpTests.StartAsync(service.Object);
        using var client = app.GetTestClient();
        client.DefaultRequestHeaders.Add("Test-Identity", "allowed");
        var request = new UpsertCustomerRequest(first, last, null, null, null, email, null, null, null, null);
        using var created = await client.PostAsync("/customers", Body(request));
        using var updated = await client.PutAsync("/customers/7", Body(request));
        var address = new UpsertAddressRequest(null, " ", null, null, null, null, 764);
        using var addressCreated = await client.PostAsync("/customers/7/addresses", Body(address));
        using var addressUpdated = await client.PutAsync("/customers/addresses/13", Body(address));
        Assert.Equal(HttpStatusCode.BadRequest, created.StatusCode);
        Assert.Equal(HttpStatusCode.BadRequest, updated.StatusCode);
        Assert.Equal(HttpStatusCode.BadRequest, addressCreated.StatusCode);
        Assert.Equal(HttpStatusCode.BadRequest, addressUpdated.StatusCode);
        service.VerifyNoOtherCalls();
    }

    private static StringContent Body<T>(T value) => new(JsonSerializer.Serialize(value), Encoding.UTF8, "application/json");

    private static CustomerResponse Profile() => new(7, "First", "Last", "First Last", null, null, null,
        "sample@example.test", null, null, null, null, null, null, null, null, null);
}
