using System.Net.Http.Json;
using Legacy.Maliev.CustomerService.Application.Interfaces;
using Legacy.Maliev.CustomerService.Application.Models;

namespace Legacy.Maliev.CustomerService.Data;

/// <summary>
/// Provides the HTTP boundary for customer identity operations owned by AuthService.
/// </summary>
/// <param name="client">The HTTP client configured for the AuthService identity API.</param>
public sealed class AuthServiceCustomerIdentityDirectory(HttpClient client) : ICustomerIdentityDirectory
{
    /// <inheritdoc />
    public async Task<IdentityOperationResult> ValidateCredentialsAsync(UserValidationRequest request, CancellationToken cancellationToken)
    {
        using var response = await client.PostAsJsonAsync("validate", request, cancellationToken);
        return response.IsSuccessStatusCode
            ? new IdentityOperationResult(true)
            : new IdentityOperationResult(false, Errors: ["Invalid username or password"]);
    }

    /// <inheritdoc />
    public async Task<IdentityOperationResult> CreateAsync(int customerId, CustomerIdentityRequest request, string? legacyPassword, CancellationToken cancellationToken)
    {
        using var response = await client.PostAsJsonAsync(customerId.ToString(System.Globalization.CultureInfo.InvariantCulture), new IdentityCreateEnvelope(request, legacyPassword), cancellationToken);
        return await ReadResultAsync(response, cancellationToken);
    }

    /// <inheritdoc />
    public async Task<CustomerIdentityResponse?> GetAsync(int customerId, CancellationToken cancellationToken)
    {
        using var response = await client.GetAsync(customerId.ToString(System.Globalization.CultureInfo.InvariantCulture), cancellationToken);
        return response.IsSuccessStatusCode
            ? await response.Content.ReadFromJsonAsync<CustomerIdentityResponse>(cancellationToken)
            : null;
    }

    /// <inheritdoc />
    public async Task<IdentityOperationResult> UpdateAsync(int customerId, CustomerIdentityRequest request, CancellationToken cancellationToken)
    {
        using var response = await client.PutAsJsonAsync(customerId.ToString(System.Globalization.CultureInfo.InvariantCulture), request, cancellationToken);
        return await ReadResultAsync(response, cancellationToken);
    }

    /// <inheritdoc />
    public async Task<IdentityOperationResult> DeleteAsync(int customerId, CancellationToken cancellationToken)
    {
        using var response = await client.DeleteAsync(customerId.ToString(System.Globalization.CultureInfo.InvariantCulture), cancellationToken);
        return await ReadResultAsync(response, cancellationToken);
    }

    private static async Task<IdentityOperationResult> ReadResultAsync(HttpResponseMessage response, CancellationToken cancellationToken)
    {
        if (response.IsSuccessStatusCode)
        {
            var identity = response.Content.Headers.ContentLength > 0
                ? await response.Content.ReadFromJsonAsync<CustomerIdentityResponse>(cancellationToken)
                : null;
            return new IdentityOperationResult(true, identity);
        }

        return new IdentityOperationResult(false, Errors: ["AuthService rejected the identity operation"]);
    }

    private sealed record IdentityCreateEnvelope(CustomerIdentityRequest Identity, string? Password);
}
