using System.Text.Json;
using Maliev.Aspire.ServiceDefaults.Middleware;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace Legacy.Maliev.CustomerService.Tests.Controllers;

public sealed class ExceptionBoundaryContractTests
{
    [Fact]
    public async Task UnhandledFailure_EmitsCorrelatedGenericResponseWithoutLoggingLiteralCustomerPath()
    {
        const string sensitive = "customer-secret@example.com";
        var logger = new CapturingLogger();
        var environment = new TestEnvironment();
        var middleware = new ExceptionHandlingMiddleware(
            _ => throw new Exception(sensitive), logger, environment);
        var context = new DefaultHttpContext();
        context.Request.Method = "GET";
        context.Request.Path = "/customers/" + sensitive;
        context.TraceIdentifier = "incident-123";
        context.Response.Body = new MemoryStream();

        await middleware.InvokeAsync(context);

        Assert.Equal(StatusCodes.Status500InternalServerError, context.Response.StatusCode);
        context.Response.Body.Position = 0;
        using var response = await JsonDocument.ParseAsync(context.Response.Body);
        Assert.Equal("incident-123", response.RootElement.GetProperty("traceId").GetString());
        Assert.Equal(500, response.RootElement.GetProperty("statusCode").GetInt32());
        Assert.Equal("An internal server error occurred", response.RootElement.GetProperty("error").GetString());
        Assert.Equal(JsonValueKind.Null, response.RootElement.GetProperty("details").ValueKind);
        Assert.DoesNotContain(sensitive, logger.Message, StringComparison.Ordinal);
        Assert.Contains("UnhandledRequestFailure", logger.Message, StringComparison.Ordinal);
        Assert.Contains("incident-123", logger.Message, StringComparison.Ordinal);
        Assert.Contains("Path=/", logger.Message, StringComparison.Ordinal);
    }

    private sealed class TestEnvironment : IHostEnvironment
    {
        public string EnvironmentName { get; set; } = Environments.Production;
        public string ApplicationName { get; set; } = "Legacy.Maliev.CustomerService";
        public string ContentRootPath { get; set; } = ".";
        public Microsoft.Extensions.FileProviders.IFileProvider ContentRootFileProvider { get; set; } =
            new Microsoft.Extensions.FileProviders.NullFileProvider();
    }

    private sealed class CapturingLogger : ILogger<ExceptionHandlingMiddleware>
    {
        public string Message { get; private set; } = string.Empty;

        public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;

        public bool IsEnabled(LogLevel logLevel) => true;

        public void Log<TState>(LogLevel logLevel, EventId eventId, TState state,
            Exception? exception, Func<TState, Exception?, string> formatter)
        {
            Message = formatter(state, exception);
        }
    }
}
