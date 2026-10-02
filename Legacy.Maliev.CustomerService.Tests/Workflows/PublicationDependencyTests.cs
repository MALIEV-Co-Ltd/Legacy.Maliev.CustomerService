using YamlDotNet.RepresentationModel;

namespace Legacy.Maliev.CustomerService.Tests.Workflows;

public sealed class PublicationDependencyTests
{
    [Fact]
    public void Publisher_ProvidesExactValidationPinAndMinimumPermissions_WithoutOpeningDeploymentGate()
    {
        DirectoryInfo? root = new(AppContext.BaseDirectory);
        while (root is not null && !File.Exists(Path.Combine(root.FullName, "Legacy.Maliev.CustomerService.slnx")))
        {
            root = root.Parent;
        }

        Assert.NotNull(root);
        var yaml = new YamlStream();
        yaml.Load(new StringReader(File.ReadAllText(Path.Combine(root.FullName, ".github", "workflows", "publish-image.yml"))));
        var document = (YamlMappingNode)Assert.Single(yaml.Documents).RootNode;
        var workflowPermissions = Mapping(document, "permissions");
        Assert.Single(workflowPermissions.Children);
        Assert.Equal("read", Value(workflowPermissions, "contents"));
        var jobs = Mapping(document, "jobs");
        Assert.Equal(2, jobs.Children.Count);
        var deploymentGate = Mapping(jobs, "deployment-gate");
        Assert.Equal("vars.LEGACY_DEPLOY_ENABLED != 'true'", Value(deploymentGate, "if"));
        Assert.False(deploymentGate.Children.ContainsKey(new YamlScalarNode("permissions")));
        var publish = Mapping(jobs, "publish");
        Assert.Equal("vars.LEGACY_DEPLOY_ENABLED == 'true'", Value(publish, "if"));
        Assert.Equal("MALIEV-Co-Ltd/Legacy.Maliev.Workflows/.github/workflows/publish-image.yml@503e8846390a597c267d2889b33a9c26863389b3", Value(publish, "uses"));
        var permissions = Mapping(publish, "permissions");
        Assert.Equal(3, permissions.Children.Count);
        Assert.Equal("read", Value(permissions, "contents"));
        Assert.Equal("read", Value(permissions, "actions"));
        Assert.Equal("write", Value(permissions, "id-token"));
        var inputs = Mapping(publish, "with");
        Assert.Equal(8, inputs.Children.Count);
        Assert.Equal("${{ vars.LEGACY_ARTIFACT_REGISTRY }}/legacy-maliev-customer-service", Value(inputs, "image"));
        Assert.Equal("Legacy.Maliev.CustomerService.Api/Dockerfile", Value(inputs, "dockerfile"));
        Assert.Equal("086760fa0aae976a799dbcda1960d5c0981248cb", Value(inputs, "legacy-service-defaults-ref"));
        Assert.Equal("78e48ffc4ee000df0510cba5e7c7a3c4c4d539d7", Value(inputs, "compatibility-contracts-ref"));
        Assert.Equal(".", Value(inputs, "context"));
        Assert.Equal("legacy-production", Value(inputs, "environment"));
        Assert.Equal("${{ vars.LEGACY_WORKLOAD_IDENTITY_PROVIDER }}", Value(inputs, "workload-identity-provider"));
        Assert.Equal("${{ vars.LEGACY_CUSTOMER_PUBLISHER_SERVICE_ACCOUNT }}", Value(inputs, "service-account"));
    }

    private static YamlMappingNode Mapping(YamlMappingNode mapping, string key) =>
        Assert.IsType<YamlMappingNode>(mapping.Children[new YamlScalarNode(key)]);

    private static string? Value(YamlMappingNode mapping, string key) =>
        mapping.Children.TryGetValue(new YamlScalarNode(key), out var value) ? Assert.IsType<YamlScalarNode>(value).Value : null;
}
