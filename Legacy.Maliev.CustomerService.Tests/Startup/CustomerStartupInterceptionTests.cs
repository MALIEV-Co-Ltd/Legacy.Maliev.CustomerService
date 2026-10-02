using System.Diagnostics;
using System.Net;
using System.Reflection;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.Hosting;

namespace Legacy.Maliev.CustomerService.Tests.Startup;

[Collection("Customer startup process state")]
public sealed class CustomerStartupInterceptionTests
{
    [Fact]
    public async Task Actual_factory_host_serves_controls_without_corrupting_exit_code()
    {
        var previous = Environment.ExitCode;
        var oldOut = Console.Out;
        var oldError = Console.Error;
        using var stdout = new StringWriter();
        using var stderr = new StringWriter();
        try
        {
            Environment.ExitCode = 37;
            Console.SetOut(stdout);
            Console.SetError(stderr);
            await using (var factory = new WebApplicationFactory<Program>().WithWebHostBuilder(builder =>
            {
                builder.UseEnvironment("Production");
                foreach (var (key, value) in CustomerStartupProcess.Settings()) builder.UseSetting(key, value);
            }))
            {
                using var client = factory.CreateClient(new WebApplicationFactoryClientOptions { AllowAutoRedirect = false });
                using var liveness = await client.GetAsync("/customer/liveness");
                Assert.Equal(HttpStatusCode.OK, liveness.StatusCode);
                Assert.Equal("Healthy", await liveness.Content.ReadAsStringAsync());
                using var anonymous = await client.GetAsync("/customers/7");
                Assert.Equal(HttpStatusCode.Unauthorized, anonymous.StatusCode);
            }
            Assert.Equal(37, Environment.ExitCode);
            Assert.DoesNotContain("StartupFailure", stdout.ToString() + stderr, StringComparison.Ordinal);
        }
        finally
        {
            Console.SetOut(oldOut);
            Console.SetError(oldError);
            Environment.ExitCode = previous;
        }
    }

    [Fact]
    public async Task Actual_entrypoint_preserves_public_host_abort_for_tool_interception()
    {
        using var fixture = new CustomerStartupProcess();
        fixture.WriteValidConfiguration();
        var previous = Environment.ExitCode;
        var oldOut = Console.Out;
        var oldError = Console.Error;
        using var stdout = new StringWriter();
        using var stderr = new StringWriter();
        using var interceptor = new HostBuiltInterceptor();
        try
        {
            Environment.ExitCode = 37;
            Console.SetOut(stdout);
            Console.SetError(stderr);
            interceptor.Enter();
            var escaped = await Record.ExceptionAsync(async () =>
            {
                var entry = typeof(Program).Assembly.EntryPoint ?? throw new InvalidOperationException("Actual API entry point missing.");
                try
                {
                    var returned = entry.Invoke(null, [new[] { "--contentRoot=" + fixture.ContentRoot, "--environment=Production" }]);
                    if (returned is Task task) await task;
                }
                catch (TargetInvocationException exception) when (exception.InnerException is not null)
                {
                    System.Runtime.ExceptionServices.ExceptionDispatchInfo.Capture(exception.InnerException).Throw();
                    throw;
                }
            });
            Assert.Equal(1, interceptor.Calls);
            Assert.Same(interceptor.Abort, escaped);
            Assert.Equal(37, Environment.ExitCode);
            Assert.DoesNotContain("StartupFailure", stdout.ToString() + stderr, StringComparison.Ordinal);
        }
        finally
        {
            interceptor.Exit();
            Console.SetOut(oldOut);
            Console.SetError(oldError);
            Environment.ExitCode = previous;
        }
    }

    private sealed class HostBuiltInterceptor : IObserver<DiagnosticListener>, IObserver<KeyValuePair<string, object?>>, IDisposable
    {
        private readonly AsyncLocal<bool> ownedExecution = new();
        private readonly IDisposable allListeners;
        private readonly List<IDisposable> subscriptions = [];
        private IHost? host;
        internal HostAbortedException Abort { get; } = new();
        internal int Calls { get; private set; }
        internal HostBuiltInterceptor() => allListeners = DiagnosticListener.AllListeners.Subscribe(this);
        internal void Enter() => ownedExecution.Value = true;
        internal void Exit() => ownedExecution.Value = false;
        public void OnNext(DiagnosticListener listener)
        {
            if (ownedExecution.Value && listener.Name == "Microsoft.Extensions.Hosting") subscriptions.Add(listener.Subscribe(this));
        }
        public void OnNext(KeyValuePair<string, object?> value)
        {
            if (!ownedExecution.Value || value.Key != "HostBuilt") return;
            host = Assert.IsAssignableFrom<IHost>(value.Value);
            Calls++;
            throw Abort;
        }
        public void OnCompleted() { }
        public void OnError(Exception error) { }
        public void Dispose()
        {
            foreach (var subscription in subscriptions) subscription.Dispose();
            allListeners.Dispose();
            host?.Dispose();
        }
    }
}
