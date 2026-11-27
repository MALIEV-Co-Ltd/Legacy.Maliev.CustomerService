using System.Xml.Linq;

namespace Legacy.Maliev.CustomerService.Tests.Documentation;

/// <summary>Checks compiler-produced Customer XML documentation and its isolation from project source roots.</summary>
public sealed class CustomerGeneratedDocumentationIsolationTests
{
    /// <summary>Checks actual assembly XML output, maintained member summaries, and absence of source-root artifacts.</summary>
    /// <param name="assemblyName">The Customer assembly name identifying its compiler-generated XML output and project source directory.</param>
    /// <param name="documentedMember">The XML member identifier whose maintained summary must appear in the compiled assembly documentation.</param>
    [Theory]
    [InlineData("Legacy.Maliev.CustomerService.Api", "T:Program")]
    [InlineData("Legacy.Maliev.CustomerService.Application", "T:Legacy.Maliev.CustomerService.Application.Interfaces.ICustomerService")]
    [InlineData("Legacy.Maliev.CustomerService.Data", "T:Legacy.Maliev.CustomerService.Data.CustomerDbContext")]
    [InlineData("Legacy.Maliev.CustomerService.Domain", "T:Legacy.Maliev.CustomerService.Domain.Customer")]
    [InlineData("Legacy.Maliev.CustomerService.Tests", "T:Legacy.Maliev.CustomerService.Tests.Documentation.CustomerGeneratedDocumentationIsolationTests")]
    [InlineData("Legacy.Maliev.CustomerService.Tests", "M:Legacy.Maliev.CustomerService.Tests.Documentation.CustomerGeneratedDocumentationIsolationTests.CompilerDocumentation_IsAvailableInOutputWithoutSourceRootArtifact(System.String,System.String)")]
    [InlineData("Legacy.Maliev.CustomerService.Tests", "P:Legacy.Maliev.CustomerService.Tests.Controllers.CustomerDetailSaveBarrier.Entered")]
    public void CompilerDocumentation_IsAvailableInOutputWithoutSourceRootArtifact(string assemblyName, string documentedMember)
    {
        var output = Path.Combine(AppContext.BaseDirectory, assemblyName + ".xml");
        Assert.True(File.Exists(output), "Build the actual Customer solution projects before documentation acceptance.");

        var document = XDocument.Load(output);
        Assert.Equal(assemblyName, document.Root?.Element("assembly")?.Element("name")?.Value);
        var member = Assert.Single(document.Descendants("member"), element =>
            string.Equals((string?)element.Attribute("name"), documentedMember, StringComparison.Ordinal));
        Assert.False(string.IsNullOrWhiteSpace(member.Element("summary")?.Value));

        var sourceArtifact = Path.Combine(FindRepositoryRoot(), assemblyName, assemblyName + ".xml");
        Assert.False(File.Exists(sourceArtifact), "Generated assembly documentation must remain outside the project source root.");
    }

    private static string FindRepositoryRoot()
    {
        DirectoryInfo? directory = new(AppContext.BaseDirectory);
        while (directory is not null)
        {
            if (File.Exists(Path.Combine(directory.FullName, "Legacy.Maliev.CustomerService.slnx")))
                return directory.FullName;
            directory = directory.Parent;
        }

        throw new DirectoryNotFoundException("Could not locate the Customer repository root.");
    }
}
