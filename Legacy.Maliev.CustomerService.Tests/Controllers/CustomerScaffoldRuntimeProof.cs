using System.Diagnostics;
using System.Globalization;
using System.Security.Cryptography;
using System.Runtime.InteropServices;
using System.Text.RegularExpressions;
using System.Text.Json;
using Legacy.Maliev.CustomerService.Data;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;

namespace Legacy.Maliev.CustomerService.Tests.Controllers;

[CollectionDefinition("Customer scaffold runtime", DisableParallelization = true)]
public sealed class CustomerScaffoldRuntimeCollection;

internal static class CustomerScaffoldRuntimeProof
{
    public static async Task RunAsync(CustomerDetailAuthorityFixture fixture)
    {
        Assert.True(OperatingSystem.IsLinux(), "Actual EF tooling proof requires its admitted Linux hosted lane; setup failure, not behavioral RED.");
        var directory = new DirectoryInfo(AppContext.BaseDirectory);
        while (directory is not null && !File.Exists(Path.Combine(directory.FullName, "Legacy.Maliev.CustomerService.slnx")))
            directory = directory.Parent;
        var repository = directory?.FullName ?? throw new DirectoryNotFoundException("Customer repository was not found.");
        var lease = Guid.NewGuid().ToString("N");
        var temporary = Path.Combine(Path.GetTempPath(), "customer-actual-scaffold-" + lease);
        var output = Path.Combine(temporary, "preview");
        var tools = Path.Combine(temporary, "tools");
        var phases = new List<PhaseEvidence>();
        var proofPassed = false;
        var runtimeSourcesUnchanged = false;
        var generatedFiles = new List<object>();
        await using var host = fixture.Start();
        var configuration = host.Services.GetRequiredService<IConfiguration>();
        await using var context = fixture.Context();
        var connection = context.Database.GetConnectionString()!;
        var before = await SnapshotAsync(context);
        var protectedPaths = new[] { "Legacy.Maliev.CustomerService.Api/Program.cs", "Legacy.Maliev.CustomerService.Data/CustomerDbContext.cs" };
        var protectedHashes = protectedPaths.Select(path => SHA256.HashData(File.ReadAllBytes(Path.Combine(repository, path)))).ToArray();
        var environment = new Dictionary<string, string?>
        {
            ["ConnectionStrings__CustomerDbContext"] = connection,
            ["ConnectionStrings__redis"] = configuration["ConnectionStrings:redis"],
            ["Cache__RedisEnabled"] = configuration["Cache:RedisEnabled"],
            ["Jwt__PublicKey"] = configuration["Jwt:PublicKey"],
            ["Jwt__Issuer"] = configuration["Jwt:Issuer"],
            ["Jwt__Audience"] = configuration["Jwt:Audience"],
            ["Features__ResourceScopedAuthEnabled"] = configuration["Features:ResourceScopedAuthEnabled"],
            ["IAM__LivePermissionChecks__Credential"] = configuration["IAM:LivePermissionChecks:Credential"],
            ["ASPNETCORE_ENVIRONMENT"] = "Production",
            ["Logging__LogLevel__Default"] = "Warning",
            ["DOTNET_CLI_USE_MSBUILD_SERVER"] = "0",
            ["MSBUILDDISABLENODEREUSE"] = "1",
            ["CODEX_CUSTOMER_SCAFFOLD_LEASE"] = lease,
            ["EnableCustomerScaffoldDesignTime"] = "true",
            ["UseLocalMalievDependencies"] = "true",
            ["ArtifactsPath"] = Path.Combine(temporary, "artifacts"),
            ["VSTestResultsDirectory"] = "",
            ["CORS__AllowedOrigins__0"] = configuration["CORS:AllowedOrigins:0"],
            ["OTEL_EXPORTER_OTLP_ENDPOINT"] = "",
        };
        Directory.CreateDirectory(temporary);
        try
        {
            await ExecuteAsync("dotnet", ["tool", "install", "dotnet-ef", "--version", "10.0.12", "--tool-path", tools], "exact-tool-install", repository, environment, phases);
            environment["PATH"] = tools + Path.PathSeparator + Environment.GetEnvironmentVariable("PATH");
            var build = await ExecuteAsync("dotnet", ["build", Path.Combine(repository, "Legacy.Maliev.CustomerService.Api/Legacy.Maliev.CustomerService.Api.csproj"), "-c", "Release", "--disable-build-servers", "-p:UseSharedCompilation=false", "-nodeReuse:false"], "isolated-design-startup-build", repository, environment, phases);
            RequireStrictBuild(build);
            await ExecuteAsync("pwsh", ["-NoProfile", "-File", Path.Combine(repository, "Legacy.Maliev.CustomerService.Data/ScaffoldCustomerContext.ps1"), "-OutputDirectory", output], "actual-named-connection-scaffold", repository, environment, phases);
            var generated = Directory.GetFiles(output, "*.cs");
            Assert.Equal(4, generated.Length);
            foreach (var path in generated)
            {
                var text = await File.ReadAllTextAsync(path);
                Assert.False(text.Contains(connection, StringComparison.Ordinal), "Generated preview contains a connection value; private details withheld.");
                Assert.False(text.Contains("OnConfiguring", StringComparison.Ordinal), "Generated preview contains prohibited connection configuration; private details withheld.");
                Assert.False(text.Contains("Properties.Resources", StringComparison.Ordinal), "Generated preview contains prohibited resource configuration; private details withheld.");
                generatedFiles.Add(new { name = Path.GetFileName(path), sha256 = Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(path))) });
            }
            await File.WriteAllTextAsync(Path.Combine(output, "Preview.csproj"), PreviewProject);
            await File.WriteAllTextAsync(Path.Combine(output, "Program.cs"), PreviewProgram);
            var previewEnvironment = new Dictionary<string, string?>(environment);
            previewEnvironment.Remove("ArtifactsPath");
            previewEnvironment.Remove("EnableCustomerScaffoldDesignTime");
            var previewBuild = await ExecuteAsync("dotnet", ["build", Path.Combine(output, "Preview.csproj"), "-c", "Release", "--disable-build-servers", "-p:UseSharedCompilation=false", "-nodeReuse:false"], "generated-preview-build", repository, previewEnvironment, phases);
            RequireStrictBuild(previewBuild);
            var query = await ExecuteAsync("dotnet", [Path.Combine(output, "bin/Release/net10.0/Preview.dll")], "generated-model-and-query-proof", repository, previewEnvironment, phases);
            Assert.Contains("GENERATED_CUSTOMER_PROOF_OK", query, StringComparison.Ordinal);
            Assert.Equal(before, await SnapshotAsync(context));
            for (var index = 0; index < protectedPaths.Length; index++)
                Assert.Equal(protectedHashes[index], SHA256.HashData(File.ReadAllBytes(Path.Combine(repository, protectedPaths[index]))));
            runtimeSourcesUnchanged = true;
            proofPassed = true;
        }
        finally
        {
            var full = Path.GetFullPath(temporary);
            Assert.StartsWith(Path.GetFullPath(Path.GetTempPath()) + "customer-actual-scaffold-", full, StringComparison.Ordinal);
            Assert.Equal("customer-actual-scaffold-" + lease, Path.GetFileName(full));
            var cleanupVerified = false;
            try
            {
                if (phases.All(phase => phase.CleanupVerified))
                {
                    if (Directory.Exists(full)) Directory.Delete(full, recursive: true);
                    Assert.False(Directory.Exists(full));
                    cleanupVerified = true;
                }
            }
            finally
            {
                var evidence = Environment.GetEnvironmentVariable("VSTestResultsDirectory");
                if (!string.IsNullOrWhiteSpace(evidence))
                {
                    Directory.CreateDirectory(evidence);
                    await File.WriteAllTextAsync(Path.Combine(evidence, "customer-actual-scaffold.json"), JsonSerializer.Serialize(new
                    {
                        lease,
                        phases,
                        generatedFiles,
                        runtimeSourcesUnchanged,
                        cleanupVerified,
                        accepted = proofPassed && cleanupVerified,
                        remainingTemporaryDirectory = cleanupVerified ? null : full,
                        leaseExpiryUtc = DateTime.UtcNow.AddMinutes(20),
                        scope = "Actual EF named startup configuration, generated preview build/model/queries; disposable fixture only.",
                    }, new JsonSerializerOptions { WriteIndented = true }));
                }
            }
        }
    }

    private static async Task<string> ExecuteAsync(string executable, string[] arguments, string phase, string workingDirectory,
        Dictionary<string, string?> environment, List<PhaseEvidence> phases)
    {
        var memory = File.ReadLines("/proc/meminfo").Single(line => line.StartsWith("MemAvailable:", StringComparison.Ordinal));
        var availableKiB = long.Parse(memory.Split(' ', StringSplitOptions.RemoveEmptyEntries)[1], CultureInfo.InvariantCulture);
        Assert.True(availableKiB >= 4194304, "Actual EF phase memory admission failed; setup failure, not behavioral RED.");
        // A new Linux session and unique inherited lease contain these task-owned
        // children. Build servers are disabled; no shared process is selected by name.
        using var process = new Process { StartInfo = new ProcessStartInfo("setsid") { WorkingDirectory = workingDirectory, RedirectStandardOutput = true, RedirectStandardError = true, UseShellExecute = false } };
        process.StartInfo.ArgumentList.Add(executable);
        foreach (var argument in arguments) process.StartInfo.ArgumentList.Add(argument);
        foreach (var pair in environment) process.StartInfo.Environment[pair.Key] = pair.Value;
        var processLease = environment["CODEX_CUSTOMER_SCAFFOLD_LEASE"] + "-" + phase;
        process.StartInfo.Environment["CODEX_CUSTOMER_SCAFFOLD_LEASE"] = processLease;
        Assert.True(process.Start(), "Actual EF child could not start; setup failure, not behavioral RED.");
        var start = process.StartTime.ToUniversalTime();
        var pid = process.Id;
        var phaseIndex = phases.Count;
        phases.Add(new PhaseEvidence(phase, pid, start, executable, availableKiB, false, -1,
            false, "setup-phase-incomplete", processLease, [], [], false));
        var stdout = process.StandardOutput.ReadToEndAsync();
        var stderr = process.StandardError.ReadToEndAsync();
        var owned = new Dictionary<int, OwnedProcess>();
        var timedOut = false;
        var cleanupFailed = false;
        var deadline = DateTime.UtcNow.AddSeconds(120);
        try
        {
            while (!process.HasExited)
            {
                foreach (var child in ObserveLease(processLease)) owned[child.Pid] = child;
                if (DateTime.UtcNow >= deadline) { timedOut = true; break; }
                await Task.Delay(200);
            }
        }
        finally
        {
            using var cleanupDeadline = new CancellationTokenSource(TimeSpan.FromSeconds(10));
            try
            {
                // Re-observe each exact PID/start/executable/session/lease immediately
                // before signaling it. Never use a process-name or recursive-tree kill.
                for (var sweep = 0; sweep < 3; sweep++)
                {
                    cleanupDeadline.Token.ThrowIfCancellationRequested();
                    var active = ObserveLease(processLease);
                    foreach (var child in active) owned[child.Pid] = child;
                    if (active.Count == 0) break;
                    foreach (var child in active)
                    {
                        if (StillOwned(child, processLease)) _ = SendSignal(child.Pid, 15);
                    }
                    await Task.Delay(250);
                    foreach (var child in active)
                    {
                        cleanupDeadline.Token.ThrowIfCancellationRequested();
                        if (!StillOwned(child, processLease)) continue;
                        using var exact = Process.GetProcessById(child.Pid);
                        exact.Kill();
                        await exact.WaitForExitAsync().WaitAsync(TimeSpan.FromSeconds(3), cleanupDeadline.Token);
                    }
                }
                if (!process.HasExited && process.StartTime.ToUniversalTime() == start)
                {
                    // The direct child retains its original creation identity even if
                    // setsid exec changed the executable during an observation race.
                    _ = SendSignal(pid, 15);
                    await Task.Delay(250);
                    if (!process.HasExited && process.StartTime.ToUniversalTime() == start) process.Kill();
                    await process.WaitForExitAsync().WaitAsync(TimeSpan.FromSeconds(3), cleanupDeadline.Token);
                }
            }
            catch
            {
                cleanupFailed = true;
            }
            finally
            {
                var remainingOwned = ObserveLease(processLease).ToArray();
                var terminal = process.HasExited;
                var exitCode = terminal ? process.ExitCode : -1;
                phases[phaseIndex] = phases[phaseIndex] with
                {
                    TimedOut = timedOut,
                    ExitCode = exitCode,
                    Terminal = terminal,
                    FailureCategory = cleanupFailed || !terminal || remainingOwned.Length != 0 ? "setup-cleanup-incomplete"
                        : timedOut ? "setup-timeout" : exitCode == 0 ? null
                        : phase == "generated-model-and-query-proof" && exitCode == 2 ? "runtime-contract-mismatch" : "setup-tool-or-compiler",
                    OwnedProcesses = owned.Values.ToArray(),
                    RemainingOwnedProcesses = remainingOwned,
                    CleanupVerified = !cleanupFailed && terminal && remainingOwned.Length == 0,
                };
            }
        }
        Assert.True(phases[phaseIndex].CleanupVerified, "Actual EF process cleanup was not confirmed; sanitized ownership evidence retained.");
        var output = await stdout.WaitAsync(TimeSpan.FromSeconds(5));
        _ = await stderr.WaitAsync(TimeSpan.FromSeconds(5)); // Private tool/provider details never enter public evidence.
        phases[phaseIndex] = phases[phaseIndex] with
        {
            WarningCounts = Regex.Matches(output, @"\b(\d+) Warning\(s\)").Select(match => int.Parse(match.Groups[1].Value, CultureInfo.InvariantCulture)).ToArray(),
            ErrorCounts = Regex.Matches(output, @"\b(\d+) Error\(s\)").Select(match => int.Parse(match.Groups[1].Value, CultureInfo.InvariantCulture)).ToArray(),
        };
        Assert.False(timedOut, "Actual EF tool timed out; setup failure, not behavioral RED.");
        Assert.True(process.ExitCode == 0, phase == "generated-model-and-query-proof" && process.ExitCode == 2
            ? "Actual generated model/query contract mismatch after successful generation/build; private details withheld."
            : $"Actual EF phase {phase} failed in tooling/configuration/compiler setup; private details withheld.");
        return output;
    }

    [DllImport("libc", EntryPoint = "kill", SetLastError = true)]
    private static extern int SendSignal(int pid, int signal);

    private sealed record OwnedProcess(int Pid, DateTime ActualStartUtc, string ExecutableIdentity, int SessionId);

    private sealed record PhaseEvidence(string Phase, int Pid, DateTime ActualStartUtc, string Executable,
        long AvailableKiB, bool TimedOut, int ExitCode, bool Terminal, string? FailureCategory,
        string ProcessLease, OwnedProcess[] OwnedProcesses, OwnedProcess[] RemainingOwnedProcesses, bool CleanupVerified)
    {
        public int[] WarningCounts { get; init; } = [];
        public int[] ErrorCounts { get; init; } = [];
    }

    private static IReadOnlyList<OwnedProcess> ObserveLease(string lease)
    {
        var owned = new List<OwnedProcess>();
        foreach (var directory in Directory.EnumerateDirectories("/proc"))
        {
            if (!int.TryParse(Path.GetFileName(directory), out var pid)) continue;
            try
            {
                var stat = File.ReadAllText(Path.Combine(directory, "stat"));
                var fields = stat[(stat.LastIndexOf(')') + 2)..].Split(' ', StringSplitOptions.RemoveEmptyEntries);
                var environment = System.Text.Encoding.UTF8.GetString(File.ReadAllBytes(Path.Combine(directory, "environ")));
                if (!environment.Split('\0').Contains("CODEX_CUSTOMER_SCAFFOLD_LEASE=" + lease, StringComparer.Ordinal)) continue;
                using var process = Process.GetProcessById(pid);
                if (process.HasExited) continue;
                var executable = process.MainModule?.FileName;
                if (executable is not null) owned.Add(new OwnedProcess(pid, process.StartTime.ToUniversalTime(), executable, int.Parse(fields[3], CultureInfo.InvariantCulture)));
            }
            catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or ArgumentException or InvalidOperationException or System.ComponentModel.Win32Exception) { }
        }
        return owned;
    }

    private static bool StillOwned(OwnedProcess expected, string lease) =>
        ObserveLease(lease).Any(actual => actual == expected);

    private static void RequireStrictBuild(string output)
    {
        Assert.Contains("Build succeeded.", output, StringComparison.Ordinal);
        Assert.NotEmpty(Regex.Matches(output, @"\b(\d+) Warning\(s\)"));
        Assert.NotEmpty(Regex.Matches(output, @"\b(\d+) Error\(s\)"));
        Assert.All(Regex.Matches(output, @"\b(\d+) (Warning|Error)\(s\)"), match => Assert.Equal("0", match.Groups[1].Value));
    }

    private static async Task<string> SnapshotAsync(CustomerDbContext context) => JsonSerializer.Serialize(new
    {
        Customers = await context.Customers.AsNoTracking().OrderBy(row => row.Id).ToArrayAsync(),
        Addresses = await context.Addresses.AsNoTracking().OrderBy(row => row.Id).ToArrayAsync(),
        Companies = await context.Companies.AsNoTracking().OrderBy(row => row.Id).ToArrayAsync(),
        CreateOperations = await context.CustomerCreateOperations.AsNoTracking().OrderBy(row => row.Key).ToArrayAsync(),
        CompletionOperations = await context.QuotationProfileCompletionOperations.AsNoTracking().OrderBy(row => row.CustomerId).ThenBy(row => row.Key).ToArrayAsync(),
    });

    private const string PreviewProject = """
        <Project Sdk="Microsoft.NET.Sdk">
          <PropertyGroup><OutputType>Exe</OutputType><TargetFramework>net10.0</TargetFramework><Nullable>enable</Nullable><ImplicitUsings>enable</ImplicitUsings><TreatWarningsAsErrors>true</TreatWarningsAsErrors></PropertyGroup>
          <ItemGroup>
            <PackageReference Include="Npgsql.EntityFrameworkCore.PostgreSQL" Version="10.0.3" />
            <PackageReference Include="Microsoft.EntityFrameworkCore" Version="10.0.12" />
            <PackageReference Include="Microsoft.EntityFrameworkCore.Relational" Version="10.0.12" />
            <PackageReference Include="Microsoft.EntityFrameworkCore.Abstractions" Version="10.0.12" />
          </ItemGroup>
        </Project>
        """;

    private const string PreviewProgram = """
        using Legacy.Maliev.CustomerService.ScaffoldPreview;
        using Microsoft.EntityFrameworkCore;
        using Microsoft.EntityFrameworkCore.Metadata;

        try
        {
            var connection = Environment.GetEnvironmentVariable("ConnectionStrings__CustomerDbContext") ?? throw new InvalidOperationException();
            await using var context = new CustomerScaffoldContext(new DbContextOptionsBuilder<CustomerScaffoldContext>().UseNpgsql(connection).Options);
            Require(context.Model.GetEntityTypes().Select(entity => entity.GetTableName()).Order().SequenceEqual(new[] { "Address", "Company", "Customer" }));
            var customer = context.Model.FindEntityType(typeof(Customer))!;
            var table = StoreObjectIdentifier.Table("Customer", null);
            Require(customer.FindProperty("Id")!.GetColumnName(table) == "ID");
            foreach (var name in new[] { "BillingAddressId", "ShippingAddressId", "CompanyId" })
                Require(customer.FindProperty(name)!.IsNullable && customer.FindProperty(name)!.GetColumnName(table) == name[..^2] + "ID");
            foreach (var name in new[] { "FirstName", "LastName", "Email" })
                Require(customer.FindProperty(name) is { IsNullable: false } property && property.GetMaxLength() == 256);
            Require(customer.FindProperty("FullName")!.GetMaxLength() == 513 && customer.FindProperty("FullName")!.GetComputedColumnSql()!.Contains("btrim", StringComparison.OrdinalIgnoreCase));
            foreach (var name in new[] { "Telephone", "Mobile", "Fax" })
                Require(customer.FindProperty(name) is { IsNullable: true } property && property.GetMaxLength() == 256);
            Require(customer.FindProperty("DateOfBirth")!.IsNullable && customer.FindProperty("DateOfBirth")!.GetColumnType() == "date");
            Require(customer.FindProperty("InternalRemark") is { IsNullable: true } remark && remark.GetMaxLength() == 4000);
            Require(customer.GetForeignKeys().Select(key => key.GetConstraintName()).Order().SequenceEqual(new[] { "FK_Customer_Address", "FK_Customer_Address1", "FK_Customer_Company" }));
            var address = context.Model.FindEntityType(typeof(Address))!;
            Require(address.FindProperty("AddressLine1") is { IsNullable: false } line && line.GetMaxLength() == 256);
            foreach (var name in new[] { "Building", "AddressLine2", "City", "State", "PostalCode" })
                Require(address.FindProperty(name) is { IsNullable: true } property && property.GetMaxLength() == 256);
            Require(!address.FindProperty("CountryId")!.IsNullable);
            var company = context.Model.FindEntityType(typeof(Company))!;
            Require(company.FindProperty("Name") is { IsNullable: false } companyName && companyName.GetMaxLength() == 256);
            foreach (var name in new[] { "TaxNumber", "Registrar" })
                Require(company.FindProperty(name) is { IsNullable: true } property && property.GetMaxLength() == 256);
            foreach (var entity in context.Model.GetEntityTypes())
            {
                Require(entity.FindPrimaryKey()!.Properties.Count == 1);
                foreach (var name in new[] { "CreatedDate", "ModifiedDate" })
                    Require(entity.FindProperty(name)!.IsNullable && entity.FindProperty(name)!.GetColumnType() == "timestamp without time zone" && entity.FindProperty(name)!.GetDefaultValueSql()!.Contains("UTC", StringComparison.Ordinal));
            }
            var row = await context.Set<Customer>().AsNoTracking().Include(item => item.BillingAddress).Include(item => item.ShippingAddress).Include(item => item.Company)
                .SingleOrDefaultAsync(item => item.Id == 1 && EF.Functions.ILike(item.FullName!, "Before %")) ?? throw new GeneratedContractMismatch();
            Require(row.FirstName == "Before" && row.LastName == "Customer" && row.FullName == "Before Customer" && row.Email == "detail@example.test" && row.InternalRemark == "staff-only");
            var billing = row.BillingAddress ?? throw new GeneratedContractMismatch();
            var shipping = row.ShippingAddress ?? throw new GeneratedContractMismatch();
            var linkedCompany = row.Company ?? throw new GeneratedContractMismatch();
            Require(billing.Id == shipping.Id && billing.AddressLine1 == "Before road" && shipping.CountryId == 764 && billing.City == "Bangkok");
            Require(linkedCompany.Name == "Before company" && linkedCompany.TaxNumber == "123");
            Console.WriteLine("GENERATED_CUSTOMER_PROOF_OK");
            return 0;
        }
        catch (GeneratedContractMismatch)
        {
            Console.Error.WriteLine("Generated Customer contract mismatch; private details withheld.");
            return 2;
        }
        catch
        {
            Console.Error.WriteLine("Generated Customer model/query proof failed; private details withheld.");
            return 1;
        }
        static void Require(bool valid) { if (!valid) throw new GeneratedContractMismatch(); }
        sealed class GeneratedContractMismatch : Exception;
        """;
}
