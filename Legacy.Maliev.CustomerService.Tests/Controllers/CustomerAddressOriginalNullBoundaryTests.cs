using System.Net;
using System.Text;
using System.Text.Json;
using Legacy.Maliev.CustomerService.Api.Controllers;
using Legacy.Maliev.CustomerService.Application.Interfaces;
using Legacy.Maliev.CustomerService.Application.Models;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.TestHost;
using Moq;

namespace Legacy.Maliev.CustomerService.Tests.Controllers;

/// <summary>Exercises original address null-body ordering for direct controller calls and MVC permission checks.</summary>
public sealed class CustomerAddressOriginalNullBoundaryTests
{
    /// <summary>Verifies that null address creation looks up the parent before returning an empty bad-request response.</summary>
    /// <param name="customerId">The parent customer identifier supplied to direct address creation.</param>
    /// <param name="parentExists">Whether the mocked parent lookup returns a customer.</param>
    /// <returns>A task representing completion of the address null-input and permission-boundary assertions.</returns>
    [Theory]
    [InlineData(101, true)]
    [InlineData(int.MaxValue, false)]
    public async Task NullCreate_PreservesOriginalParentLookupBeforeEmptyBadRequest(
        int customerId, bool parentExists)
    {
        var service = new Mock<ICustomerService>(MockBehavior.Strict);
        var parent = new CustomerResponse(customerId, "Fixture", "Customer", "Fixture Customer",
            null, null, null, "fixture@example.test", null, null, null, null, null, null, null, null, null);
        service.Setup(value => value.GetCustomerAsync(customerId, CancellationToken.None))
            .ReturnsAsync(parentExists ? parent : null);
        var controller = new AddressesController(service.Object);

        var result = await controller.CreateCustomerAddressAsync(customerId, null!, CancellationToken.None);

        if (parentExists) Assert.Equal(400, Assert.IsType<BadRequestResult>(result).StatusCode);
        else Assert.Equal(404, Assert.IsType<NotFoundResult>(result).StatusCode);
        service.Verify(value => value.GetCustomerAsync(customerId, CancellationToken.None), Times.Once);
        service.VerifyNoOtherCalls();
    }

    /// <summary>Verifies that null address updates return an empty bad-request response before any service lookup.</summary>
    /// <param name="addressId">The target address identifier supplied with a null update body.</param>
    /// <returns>A task representing completion of the address null-input and permission-boundary assertions.</returns>
    [Theory]
    [InlineData(101)]
    [InlineData(int.MaxValue)]
    public async Task NullUpdate_PreservesOriginalEmptyBadRequestBeforeLookup(int addressId)
    {
        var service = new Mock<ICustomerService>(MockBehavior.Strict);
        var controller = new AddressesController(service.Object);

        var result = await controller.UpdateAddressAsync(addressId, null!, CancellationToken.None);

        Assert.Equal(400, Assert.IsType<BadRequestResult>(result).StatusCode);
        service.VerifyNoOtherCalls();
    }

    /// <summary>Verifies MVC null-body validation and authorization on address create and update without service calls.</summary>
    /// <param name="identity">The test identity header value; null omits the header when supported by the scenario.</param>
    /// <param name="expected">The expected HTTP status for the supplied scenario.</param>
    /// <returns>A task representing completion of the address null-input and permission-boundary assertions.</returns>
    [Theory]
    [InlineData("allowed", HttpStatusCode.BadRequest)]
    [InlineData(null, HttpStatusCode.Unauthorized)]
    [InlineData("denied", HttpStatusCode.Forbidden)]
    public async Task JsonNull_CreateAndUpdate_PreserveMvcAndPermissionBoundaries(
        string? identity, HttpStatusCode expected)
    {
        var service = new Mock<ICustomerService>(MockBehavior.Strict);
        await using var app = await CompanyTaxOnlyHttpTests.StartAsync(service.Object);
        using var client = app.GetTestClient();
        if (identity is not null) client.DefaultRequestHeaders.Add("Test-Identity", identity);

        using var created = await client.PostAsync("/customers/101/addresses",
            new StringContent("null", Encoding.UTF8, "application/json"));
        using var updated = await client.PutAsync("/customers/addresses/101",
            new StringContent("null", Encoding.UTF8, "application/json"));

        foreach (var response in new[] { created, updated })
        {
            Assert.Equal(expected, response.StatusCode);
            if (expected == HttpStatusCode.BadRequest)
            {
                Assert.Equal("application/problem+json", response.Content.Headers.ContentType?.MediaType);
                using var problem = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
                Assert.Equal(400, problem.RootElement.GetProperty("status").GetInt32());
                var errors = problem.RootElement.GetProperty("errors");
                Assert.Equal(JsonValueKind.Object, errors.ValueKind);
                Assert.NotEmpty(errors.EnumerateObject());
            }
        }

        service.VerifyNoOtherCalls();
    }
}
