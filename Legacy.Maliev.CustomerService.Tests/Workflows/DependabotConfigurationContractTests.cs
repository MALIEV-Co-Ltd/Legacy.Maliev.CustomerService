namespace Legacy.Maliev.CustomerService.Tests.Workflows;

/// <summary>Verifies dependency-updater project scope and coordinated EF Core and Npgsql runtime updates.</summary>
public sealed class DependabotConfigurationContractTests
{
    /// <summary>Checks NuGet scanning is limited to independently resolvable Application, Data, and Domain project directories.</summary>
    [Fact]
    public void NuGetUpdater_ScansOnlyIndependentlyResolvableProjectDirectories()
    {
        var source = ReadNuGetBlock();

        Assert.DoesNotContain("    directory: /", source, StringComparison.Ordinal);
        foreach (var directory in new[]
                 {
            "/Legacy.Maliev.CustomerService.Application",
            "/Legacy.Maliev.CustomerService.Data",
            "/Legacy.Maliev.CustomerService.Domain",
                 })
        {
            Assert.Contains($"      - {directory}", source, StringComparison.Ordinal);
        }

        Assert.Equal(3, source.Split("\n      - /Legacy.Maliev.CustomerService.", StringSplitOptions.None).Length - 1);
    }

    /// <summary>Checks EF Core and Npgsql runtime packages remain eligible for coordinated dependency updates.</summary>
    [Fact]
    public void NuGetUpdater_AllowsCoordinatedEfAndNpgsqlRuntimeGraph()
    {
        var source = ReadNuGetBlock();

        foreach (var dependency in new[]
                 {
                     "Microsoft.EntityFrameworkCore",
                     "Microsoft.EntityFrameworkCore.Abstractions",
                     "Microsoft.EntityFrameworkCore.Design",
                     "Microsoft.EntityFrameworkCore.Relational",
                     "Npgsql.EntityFrameworkCore.PostgreSQL",
                 })
        {
            Assert.DoesNotContain($"dependency-name: {dependency}", source, StringComparison.Ordinal);
        }
    }

    private static string ReadNuGetBlock()
    {
        var source = File.ReadAllText(FindRepositoryFile(".github", "dependabot.yml"));
        var start = source.IndexOf("  - package-ecosystem: nuget", StringComparison.Ordinal);
        var end = source.IndexOf("  - package-ecosystem: docker", start, StringComparison.Ordinal);

        Assert.True(start >= 0);
        Assert.True(end > start);
        return source[start..end];
    }

    private static string FindRepositoryFile(params string[] segments)
    {
        DirectoryInfo? directory = new(AppContext.BaseDirectory);

        while (directory is not null)
        {
            var candidate = Path.Combine([directory.FullName, .. segments]);
            if (File.Exists(candidate))
            {
                return candidate;
            }

            directory = directory.Parent;
        }

        throw new FileNotFoundException($"Could not find repository file '{Path.Combine(segments)}'.");
    }
}
