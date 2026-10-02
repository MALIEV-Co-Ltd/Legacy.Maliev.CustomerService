using System.Diagnostics;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace Legacy.Maliev.CustomerService.Tests.Startup;

internal sealed class CustomerStartupProcess : IDisposable
{
    internal const string Sentinel = "private-customer-startup-sentinel";
    internal string ContentRoot { get; } = Path.Combine(Path.GetTempPath(), Sentinel + "-" + Guid.NewGuid().ToString("N"));

    internal CustomerStartupProcess() => Directory.CreateDirectory(ContentRoot);

    internal static Dictionary<string, string?> Settings()
    {
        using var rsa = RSA.Create(2048);
        return new()
        {
            ["ConnectionStrings:CustomerDbContext"] = "Host=127.0.0.1;Port=1;Database=startup_control;Username=fixture",
            ["Cache:RedisEnabled"] = "false",
            ["CORS:AllowedOrigins:0"] = "https://startup.example.test",
            ["Jwt:PublicKey"] = Convert.ToBase64String(Encoding.UTF8.GetBytes(rsa.ExportSubjectPublicKeyInfoPem())),
            ["Jwt:Issuer"] = "https://startup.example.test",
            ["Jwt:Audience"] = "startup-control",
            ["OTEL_EXPORTER_OTLP_ENDPOINT"] = "",
            ["Observability:TracingEnabled"] = "false",
            ["Observability:RuntimeMetricsEnabled"] = "false"
        };
    }

    internal void WriteMalformedConfiguration() => File.WriteAllText(Path.Combine(ContentRoot, "appsettings.json"), "{ invalid-private-json");

    internal void WriteValidConfiguration()
    {
        // Flat colon keys are supported by IConfiguration; all values are synthetic/public test inputs.
        File.WriteAllText(Path.Combine(ContentRoot, "appsettings.json"), JsonSerializer.Serialize(Settings()));
    }

    internal async Task<ProcessResult> RunAsync(string contentRoot, bool failHostStart, Action<ProcessResult>? timeoutDiagnostics = null)
    {
        var directory = RepositoryRoot();
        var apiDirectory = Path.Combine(directory, "Legacy.Maliev.CustomerService.Api", "bin", "Release", "net10.0");
        var dll = Path.Combine(apiDirectory, "Legacy.Maliev.CustomerService.Api.dll");
        var runtime = Path.Combine(apiDirectory, "Legacy.Maliev.CustomerService.Api.runtimeconfig.json");
        var deps = Path.Combine(apiDirectory, "Legacy.Maliev.CustomerService.Api.deps.json");
        Assert.True(File.Exists(dll) && File.Exists(runtime) && File.Exists(deps), "Build the actual API Release artifacts before running startup tests.");

        var start = new ProcessStartInfo(Environment.GetEnvironmentVariable("DOTNET_HOST_PATH") ?? "dotnet")
        {
            WorkingDirectory = apiDirectory,
            UseShellExecute = false,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            CreateNoWindow = true
        };
        // Do not allow inherited runtime credentials, connection strings, exporters or hosting overrides.
        var benign = new[] { "PATH", "SystemRoot", "WINDIR", "TEMP", "TMP", "DOTNET_ROOT", "HOME", "USERPROFILE" }
            .Select(key => (Key: key, Value: Environment.GetEnvironmentVariable(key))).ToArray();
        start.Environment.Clear();
        foreach (var (key, value) in benign)
            if (value is not null) start.Environment[key] = value;
        foreach (var argument in new[] { "exec", "--runtimeconfig", runtime, "--depsfile", deps, dll,
                     "--contentRoot=" + contentRoot, "--environment=Production" })
            start.ArgumentList.Add(argument);
        if (failHostStart) start.ArgumentList.Add("--urls=http://127.0.0.1:0/" + Sentinel);

        var phaseProbe = failHostStart && Environment.GetEnvironmentVariable("MALIEV_TEST_CUSTOMER_STARTUP_PHASE_PROBE") == "1";
        var phaseFile = Path.Combine(ContentRoot, "startup-phase-codes.txt");
        if (phaseProbe)
        {
            var hook = Path.GetFullPath(Path.Combine(directory, "tooling", "CustomerStartupPhaseProbe", "bin", "Release", "net10.0", "CustomerStartupPhaseProbe.dll"));
            Assert.True(hook.StartsWith(Path.GetFullPath(directory) + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase)
                && File.Exists(hook), "Build the isolated private Release phase probe before an explicit diagnostic run.");
            File.WriteAllText(phaseFile, string.Empty);
            start.Environment["DOTNET_STARTUP_HOOKS"] = hook;
            start.Environment["MALIEV_TEST_CUSTOMER_PHASE_FILE"] = phaseFile;
        }

        using var process = Process.Start(start) ?? throw new InvalidOperationException("Actual API child process did not start.");
        var stdout = process.StandardOutput.ReadToEndAsync();
        var stderr = process.StandardError.ReadToEndAsync();
        using var sampleCancellation = new CancellationTokenSource();
        var samples = CaptureMetadataAsync(process, sampleCancellation.Token);
        using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(20));
        try
        {
            await process.WaitForExitAsync(deadline.Token);
        }
        catch (OperationCanceledException)
        {
            if (!process.HasExited) process.Kill(entireProcessTree: true);
            await process.WaitForExitAsync();
            sampleCancellation.Cancel();
            var capturedSamples = await samples;
            var capturedStdout = await stdout;
            var capturedStderr = await stderr;
            var phase = ReadPhaseCodes(phaseProbe, phaseFile);
            timeoutDiagnostics?.Invoke(new(process.ExitCode, capturedStdout, capturedStderr, capturedSamples, phase.Codes, phase.ErrorType));
            throw new TimeoutException("Startup fixture exceeded its fixed deadline; not a behavioral RED.");
        }
        sampleCancellation.Cancel();
        var completedPhase = ReadPhaseCodes(phaseProbe, phaseFile);
        return new(process.ExitCode, await stdout, await stderr, await samples, completedPhase.Codes, completedPhase.ErrorType);
    }

    private static (IReadOnlyList<int>? Codes, string? ErrorType) ReadPhaseCodes(bool enabled, string phaseFile)
    {
        if (!enabled) return (null, null);
        try
        {
            if (new FileInfo(phaseFile).Length > 64) throw new InvalidDataException();
            var raw = File.ReadAllText(phaseFile);
            // Only exact ordered, deduplicated prefixes of the three fixed codes are valid.
            IReadOnlyList<int> codes = raw switch
            {
                "1\n" => [1],
                "1\n2\n" => [1, 2],
                "1\n2\n3\n" => [1, 2, 3],
                _ => throw new InvalidDataException()
            };
            return (codes, null);
        }
        catch (Exception exception)
        {
            // A missing/invalid probe is diagnostic setup evidence, never product RED.
            return (null, exception.GetType().Name);
        }
    }

    private static async Task<IReadOnlyList<ProcessSample>> CaptureMetadataAsync(Process process, CancellationToken cancellationToken)
    {
        var elapsed = Stopwatch.StartNew();
        List<ProcessSample> samples = [];
        foreach (var seconds in new[] { 1, 5, 10, 19 })
        {
            var remaining = TimeSpan.FromSeconds(seconds) - elapsed.Elapsed;
            try
            {
                if (remaining > TimeSpan.Zero) await Task.Delay(remaining, cancellationToken);
                cancellationToken.ThrowIfCancellationRequested();
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                break;
            }

            try
            {
                process.Refresh();
                var exited = process.HasExited;
                if (exited) break;
                var modules = process.Modules.Cast<ProcessModule>().Select(module => module.ModuleName).ToArray();
                samples.Add(new(elapsed.ElapsedMilliseconds, exited, (long)process.TotalProcessorTime.TotalMilliseconds,
                    process.WorkingSet64, process.Threads.Count,
                    modules.Contains("coreclr.dll", StringComparer.OrdinalIgnoreCase),
                    modules.Contains("hostpolicy.dll", StringComparer.OrdinalIgnoreCase), null));
            }
            catch (Exception exception)
            {
                // Never copy native error messages, paths, command lines or environment values.
                samples.Add(new(elapsed.ElapsedMilliseconds, null, null, null, null, null, null, exception.GetType().Name));
            }
        }
        return samples;
    }

    internal static string RepositoryRoot()
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);
        while (directory is not null && !File.Exists(Path.Combine(directory.FullName, "Legacy.Maliev.CustomerService.slnx")))
            directory = directory.Parent;
        return directory?.FullName ?? throw new DirectoryNotFoundException("Customer repository root not found.");
    }

    public void Dispose()
    {
        var full = Path.GetFullPath(ContentRoot);
        var temp = Path.GetFullPath(Path.GetTempPath()).TrimEnd(Path.DirectorySeparatorChar) + Path.DirectorySeparatorChar;
        if (!full.StartsWith(temp, StringComparison.OrdinalIgnoreCase) || !Path.GetFileName(full).StartsWith(Sentinel + "-", StringComparison.Ordinal))
            throw new InvalidOperationException("Refusing cleanup outside this fixture's temporary content root.");
        Directory.Delete(full, recursive: true);
    }

    internal sealed record ProcessResult(int ExitCode, string Stdout, string Stderr, IReadOnlyList<ProcessSample>? Samples = null,
        IReadOnlyList<int>? PhaseCodes = null, string? PhaseProbeErrorType = null);
    internal sealed record ProcessSample(long ElapsedMilliseconds, bool? HasExited, long? CpuMilliseconds,
        long? WorkingSetBytes, int? ThreadCount, bool? CoreClrLoaded, bool? HostPolicyLoaded, string? ErrorType);
}
