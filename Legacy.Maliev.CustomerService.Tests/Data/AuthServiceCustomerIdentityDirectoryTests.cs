using System.Net;
using System.Text.Json;
using Legacy.Maliev.CustomerService.Application.Models;
using Legacy.Maliev.CustomerService.Data;

namespace Legacy.Maliev.CustomerService.Tests.Data;

public sealed class AuthServiceCustomerIdentityDirectoryTests
{
    [Fact]
    public async Task CreateAsync_PasswordIsSentInBodyAndNeverInRequestUri()
    {
        var handler = new RecordingHandler();
        using var client = new HttpClient(handler) { BaseAddress = new Uri("http://authservice/auth/v1/legacy/customers/") };
        var directory = new AuthServiceCustomerIdentityDirectory(client);

        await directory.CreateAsync(42, SampleIdentity(), "secret value / ?", CancellationToken.None);

        Assert.Equal("http://authservice/auth/v1/legacy/customers/42", handler.Uri?.AbsoluteUri);
        Assert.DoesNotContain("secret", handler.Uri?.AbsoluteUri, StringComparison.OrdinalIgnoreCase);
        using var document = JsonDocument.Parse(handler.Body!);
        Assert.Equal("secret value / ?", document.RootElement.GetProperty("password").GetString());
    }

    [Fact]
    public async Task ValidateCredentialsAsync_AllFailuresUseNonEnumeratingError()
    {
        var handler = new RecordingHandler(HttpStatusCode.NotFound);
        using var client = new HttpClient(handler) { BaseAddress = new Uri("http://authservice/auth/v1/legacy/customers/") };
        var directory = new AuthServiceCustomerIdentityDirectory(client);

        var result = await directory.ValidateCredentialsAsync(new("unknown", "wrong"), CancellationToken.None);

        Assert.False(result.Succeeded);
        Assert.Equal(["Invalid username or password"], result.Errors);
    }

    private static CustomerIdentityRequest SampleIdentity() => new(null, "test@example.com", "test@example.com", false, null, false, false, null, true, 0, 42, null, null);

    private sealed class RecordingHandler(HttpStatusCode status = HttpStatusCode.Created) : HttpMessageHandler
    {
        public Uri? Uri { get; private set; }
        public string? Body { get; private set; }

        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            Uri = request.RequestUri;
            Body = request.Content is null ? null : await request.Content.ReadAsStringAsync(cancellationToken);
            return new HttpResponseMessage(status) { Content = new StringContent(string.Empty) };
        }
    }
}
