using System.Text.Json;
using Xunit.Abstractions;

namespace Legacy.Maliev.CustomerService.Tests.Startup;

[CollectionDefinition("Customer startup process state", DisableParallelization = true)]
public sealed class CustomerStartupProcessCollection;

[Collection("Customer startup process state")]
public sealed class CustomerStartupProcessTests(ITestOutputHelper output)
{
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Prehost_configuration_failure_exits_one_with_one_private_event(bool missingRoot)
    {
        using var fixture = new CustomerStartupProcess();
        fixture.WriteMalformedConfiguration();
        var root = missingRoot ? Path.Combine(fixture.ContentRoot, CustomerStartupProcess.Sentinel) : fixture.ContentRoot;
        var result = await fixture.RunAsync(root, failHostStart: false);
        WriteRaw(result);
        Assert.Equal(1, result.ExitCode);
        Assert.Equal(string.Empty, result.Stdout);
        var line = Assert.Single(Lines(result.Stderr));
        using var json = JsonDocument.Parse(line);
        AssertStartupEvent(json.RootElement);
        AssertPrivate(result);
    }

    [Fact]
    public async Task Host_start_failure_does_not_leak_before_or_after_private_reporting()
    {
        using var fixture = new CustomerStartupProcess();
        fixture.WriteValidConfiguration();
        var result = await fixture.RunAsync(fixture.ContentRoot, failHostStart: true, timeoutDiagnostics: WriteTimeoutDiagnostics);
        WriteRaw(result);
        Assert.Equal(1, result.ExitCode);
        AssertPrivate(result);
        var entries = Lines(result.Stdout + "\n" + result.Stderr).Select(line => JsonDocument.Parse(line)).ToArray();
        try
        {
            var startup = Assert.Single(entries, entry => entry.RootElement.TryGetProperty("eventId", out var id) && id.GetInt32() == 5102);
            AssertStartupEvent(startup.RootElement);
        }
        finally { foreach (var entry in entries) entry.Dispose(); }
    }

    private void WriteRaw(CustomerStartupProcess.ProcessResult result)
    {
        output.WriteLine("Actual API ExitCode: " + result.ExitCode);
        output.WriteLine("Raw stdout:\n" + result.Stdout);
        output.WriteLine("Raw stderr:\n" + result.Stderr);
    }

    private void WriteTimeoutDiagnostics(CustomerStartupProcess.ProcessResult result)
    {
        output.WriteLine("Fixture phase: owned child terminated after fixed 20-second deadline; not behavioral RED.");
        output.WriteLine("Terminated child ExitCode: " + result.ExitCode);
        output.WriteLine("Captured stdout lines: " + Lines(result.Stdout).Length);
        output.WriteLine("Captured stderr lines: " + Lines(result.Stderr).Length);
        var raw = result.Stdout + "\n" + result.Stderr;
        var exceptionTypes = System.Text.RegularExpressions.Regex.Matches(raw,
            @"\b(?:System|Microsoft|Legacy|Maliev)(?:\.[A-Za-z][A-Za-z0-9_]*)*Exception\b")
            .Select(match => match.Value).Distinct(StringComparer.Ordinal).Take(16);
        output.WriteLine("Captured exception types: " + string.Join(", ", exceptionTypes));
        var eventIds = System.Text.RegularExpressions.Regex.Matches(raw, @"""[Ee]vent[Ii]d""\s*:\s*(\d{1,5})")
            .Select(match => match.Groups[1].Value).Distinct(StringComparer.Ordinal).Take(16);
        output.WriteLine("Captured event IDs: " + string.Join(", ", eventIds));
        output.WriteLine("Diagnostic phase codes: " + string.Join(", ", result.PhaseCodes ?? []));
        output.WriteLine("Diagnostic phase probe error type: " + result.PhaseProbeErrorType);
        foreach (var sample in result.Samples ?? [])
        {
            output.WriteLine($"Owned child sample: elapsedMs={sample.ElapsedMilliseconds}; exited={sample.HasExited}; " +
                $"cpuMs={sample.CpuMilliseconds}; workingSetBytes={sample.WorkingSetBytes}; threads={sample.ThreadCount}; " +
                $"coreClrLoaded={sample.CoreClrLoaded}; hostPolicyLoaded={sample.HostPolicyLoaded}; errorType={sample.ErrorType}");
        }
    }

    private static string[] Lines(string text) => text.Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);

    private static void AssertStartupEvent(JsonElement entry)
    {
        Assert.Equal("CRITICAL", entry.GetProperty("severity").GetString());
        Assert.Equal(5102, entry.GetProperty("eventId").GetInt32());
        Assert.Equal("StartupFailure", entry.GetProperty("EventName").GetString());
        Assert.Equal("HostInitialization", entry.GetProperty("Operation").GetString());
        Assert.Matches("^[A-Za-z][A-Za-z0-9_.+`]{0,191}$", entry.GetProperty("exceptionType").GetString()!);
        Assert.False(entry.TryGetProperty("State", out _));
        Assert.False(entry.TryGetProperty("Scopes", out _));
    }

    private static void AssertPrivate(CustomerStartupProcess.ProcessResult result)
    {
        var raw = result.Stdout + result.Stderr;
        Assert.DoesNotContain(CustomerStartupProcess.Sentinel, raw, StringComparison.Ordinal);
        Assert.DoesNotContain("invalid-private-json", raw, StringComparison.Ordinal);
        Assert.DoesNotContain("Unhandled exception", raw, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("StackTrace", raw, StringComparison.OrdinalIgnoreCase);
    }
}
