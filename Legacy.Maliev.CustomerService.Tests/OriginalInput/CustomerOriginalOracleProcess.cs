using System.Diagnostics;
using Newtonsoft.Json;

namespace Legacy.Maliev.CustomerService.Tests.OriginalInput;

// The isolated process accepts only fixture inputs; production requests never use this path.
internal static class CustomerOriginalOracleProcess
{
    private static readonly SemaphoreSlim Gate = new(1, 1);

    internal static async Task<CustomerBodyRead> ReadAsync(string kind, byte[] bytes, string contentType,
        string prefix = "", int maxErrors = 200)
    {
        if (bytes.Length > 2_000_000) throw new ArgumentOutOfRangeException(nameof(bytes));
        var text = await ExecuteAsync(new { Kind = kind, Bytes = bytes, ContentType = contentType, Prefix = prefix, MaxErrors = maxErrors });
        var observation = System.Text.Json.JsonSerializer.Deserialize<Observation>(text)
            ?? throw new InvalidOperationException("Customer formatter oracle returned no observation.");
        var model = observation.Graph is null ? null : JsonConvert.DeserializeObject(observation.Graph,
            CustomerOriginalInputOracle.SourceType(kind));
        return new(observation.Kind, model, observation.Keys, observation.ErrorCount,
            observation.ExceptionTypes, observation.Messages);
    }

    internal static async Task<ResourceObservation> ReadResourceAsync(string kind, string scenario)
    {
        var text = await ExecuteAsync(new { Kind = kind, Bytes = Array.Empty<byte>(), ContentType = "application/json", Prefix = "", MaxErrors = 200, Scenario = scenario });
        return System.Text.Json.JsonSerializer.Deserialize<ResourceObservation>(text)
            ?? throw new InvalidOperationException("Customer formatter oracle returned no resource observation.");
    }

    internal static async Task<SettingsObservation> ReadSettingsAsync()
    {
        var text = await ExecuteAsync(new { Kind = "company", Bytes = Array.Empty<byte>(), ContentType = "application/json", Prefix = "", MaxErrors = 200, Scenario = "settings" });
        return System.Text.Json.JsonSerializer.Deserialize<SettingsObservation>(text)
            ?? throw new InvalidOperationException("Customer formatter oracle returned no settings observation.");
    }

    internal static async Task<PrimitiveObservation> ReadPrimitiveAsync(string json, string culture, bool ignoreNull = true)
    {
        var text = await ExecuteAsync(new { Kind = "company", Bytes = System.Text.Encoding.UTF8.GetBytes(json), ContentType = "application/json", Prefix = "", MaxErrors = 200, Scenario = "company-primitive", Culture = culture, IgnoreNull = ignoreNull });
        return System.Text.Json.JsonSerializer.Deserialize<PrimitiveObservation>(text)
            ?? throw new InvalidOperationException("Customer formatter oracle returned no primitive observation.");
    }

    internal static async Task<PrimitiveObservation[]> ReadPrimitivesAsync(PrimitiveCase[] cases)
    {
        var text = await ExecuteAsync(new { Kind = "company", Bytes = Array.Empty<byte>(), ContentType = "application/json", Prefix = "", MaxErrors = 200, Scenario = "company-primitive-batch", PrimitiveCases = cases });
        return System.Text.Json.JsonSerializer.Deserialize<PrimitiveObservation[]>(text)
            ?? throw new InvalidOperationException("Customer formatter oracle returned no primitive observations.");
    }

    internal static async Task<ContactObservation[]> ReadContactsAsync(PrimitiveCase[] cases)
    {
        var text = await ExecuteAsync(new { Kind = "customer", Bytes = Array.Empty<byte>(), ContentType = "application/json", Prefix = "", MaxErrors = 200, Scenario = "contact-primitive-batch", PrimitiveCases = cases });
        return System.Text.Json.JsonSerializer.Deserialize<ContactObservation[]>(text)
            ?? throw new InvalidOperationException("Customer formatter oracle returned no contact observations.");
    }

    internal static async Task<Dictionary<string, string?>?[]> ReadDictionariesAsync(PrimitiveCase[] cases)
    {
        var text = await ExecuteAsync(new { Kind = "address", Bytes = Array.Empty<byte>(), ContentType = "application/json", Prefix = "", MaxErrors = 200, Scenario = "dictionary-primitive-batch", PrimitiveCases = cases });
        return System.Text.Json.JsonSerializer.Deserialize<Dictionary<string, string?>?[]>(text)
            ?? throw new InvalidOperationException("Customer formatter oracle returned no dictionary observations.");
    }

    internal static async Task AssertResourceAsync(string kind, string scenario)
    {
        var result = await ReadResourceAsync(kind, scenario);
        if (scenario == "invalid-utf8")
        {
            Assert.Equal(typeof(System.Text.DecoderFallbackException).FullName, result.ExceptionType);
        }
        else
        {
            Assert.True(result.CanRead);
            Assert.True(result.IsSet);
            Assert.False(result.HasError);
            Assert.Null(result.ExceptionType);
            if (scenario == "fragmented")
            {
                Assert.Equal("N", result.First);
                Assert.Equal(new string('ก', 40000), result.Second);
                Assert.Equal(0, result.StateCount);
                Assert.Equal(0, result.ErrorCount);
                Assert.Equal(1, result.BufferCount);
                Assert.Equal(1, result.PathCount);
                Assert.Equal(1, result.CompletionCount);
                Assert.False(result.BufferOpen);
                Assert.False(result.PathExists);
            }
            else
            {
                Assert.Equal(result.ByteLength, result.BytesRead);
                Assert.False(result.CancellableTokenObserved);
            }
        }
        Assert.True(result.Released);
        Assert.False(result.BodyDisposed);
    }

    private static async Task<string> ExecuteAsync(object request)
    {
        if (!await Gate.WaitAsync(TimeSpan.FromSeconds(30)))
            throw new TimeoutException("Waiting for the isolated customer formatter oracle timed out.");
        try
        {
            var configuration = new DirectoryInfo(AppContext.BaseDirectory).Parent?.Name
                ?? throw new InvalidOperationException("Missing test build configuration.");
            var oracle = Path.GetFullPath(Path.Combine(AppContext.BaseDirectory,
                "../../../../tools/CustomerOriginalOracle/bin", configuration, "net10.0/CustomerOriginalOracle.dll"));
            var root = Environment.GetEnvironmentVariable("CUSTOMER_ORACLE_DOTNET_ROOT")
                ?? Environment.GetEnvironmentVariable("DOTNET_ROOT");
            var host = root is null ? "dotnet" : Path.Combine(root, OperatingSystem.IsWindows() ? "dotnet.exe" : "dotnet");
            var start = new ProcessStartInfo(host)
            {
                RedirectStandardInput = true,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                UseShellExecute = false,
                CreateNoWindow = true,
            };
            start.ArgumentList.Add(oracle);
            start.Environment["DOTNET_ROLL_FORWARD"] = "LatestPatch";
            using var process = Process.Start(start) ?? throw new InvalidOperationException("Could not start the customer formatter oracle.");
            using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(20));
            try
            {
                process.StandardInput.AutoFlush = true;
                var input = System.Text.Json.JsonSerializer.Serialize(request);
                await process.StandardInput.WriteAsync(input.AsMemory(), deadline.Token);
                process.StandardInput.Close();
                var output = process.StandardOutput.ReadToEndAsync(deadline.Token);
                var errors = process.StandardError.ReadToEndAsync(deadline.Token);
                await process.WaitForExitAsync(deadline.Token);
                var text = await output;
                _ = await errors;
                if (process.ExitCode != 0) throw new InvalidOperationException("Customer formatter oracle failed.");
                return text;
            }
            finally
            {
                if (!process.HasExited)
                {
                    try { process.Kill(); }
                    catch (InvalidOperationException) when (process.HasExited) { }
                    await process.WaitForExitAsync().WaitAsync(TimeSpan.FromSeconds(10));
                }
            }
        }
        finally { Gate.Release(); }
    }

    private sealed record Observation(string Kind, string? Graph, string[] Keys, int ErrorCount,
        string[] ExceptionTypes, string[] Messages);

    internal sealed record ResourceObservation(bool CanRead, bool IsSet, bool HasError, string? First, string? Second,
        int StateCount, int ErrorCount, string? ExceptionType, int BufferCount, int PathCount, int CompletionCount, bool BufferOpen,
        bool PathExists, bool Released, bool BodyDisposed, long ByteLength, long BytesRead, bool CancellableTokenObserved);

    internal sealed record SettingsObservation(string NullValueHandling, string ReferenceLoopHandling, string TypeNameHandling,
        string DefaultValueHandling, string ObjectCreationHandling, string MetadataPropertyHandling, string DateParseHandling,
        int? MaxDepth, bool CheckAdditionalContent, bool ReadJsonWithRequestCulture, int InputFormatterMemoryBufferThreshold);

    internal sealed record PrimitiveObservation(string? Name, string? TaxNumber, string? Registrar, string? ExceptionType, bool IsJsonException, bool ModelPresent);
    internal sealed record PrimitiveCase(string Json, string Culture, bool IgnoreNull = true);
    internal sealed record ContactObservation(string? Telephone, string? Mobile, string? Fax, string? ExceptionType, bool IsJsonException, bool ModelPresent);
}
