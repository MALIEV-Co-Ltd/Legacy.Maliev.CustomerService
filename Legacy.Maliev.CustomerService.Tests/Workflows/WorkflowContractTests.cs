using System.Text.RegularExpressions;
using System.Xml.Linq;

using YamlDotNet.Core;
using YamlDotNet.RepresentationModel;

namespace Legacy.Maliev.CustomerService.Tests.Workflows;

public sealed class WorkflowContractTests
{
    private static readonly string Workflow = File.ReadAllText(FindRepositoryFile(".github", "workflows", "_build-and-test.yml"));
    private static readonly string ApiProject = File.ReadAllText(
        FindRepositoryFile("Legacy.Maliev.CustomerService.Api", "Legacy.Maliev.CustomerService.Api.csproj"));
    private static readonly string DataProject = File.ReadAllText(
        FindRepositoryFile("Legacy.Maliev.CustomerService.Data", "Legacy.Maliev.CustomerService.Data.csproj"));

    [Fact]
    public void BuildAndTest_SatisfiesStructuralContract()
    {
        WorkflowContractValidator.Validate(Workflow);

        // Exercise the exact opt-in step without changing the original suite identities.
        AssertMutationRejected("if: inputs.customer-consumer-custody != ''", "if: always()");
        AssertMutationRejected("timeout-minutes: 30", "timeout-minutes: 31");
        AssertMutationRejected("type: string", "type: boolean");
        AssertMutationRejected("default: ''", "default: 'UNREVIEWED'");
        AssertMutationRejected("required: false", "required: true");
        AssertMutationRejected("customer-consumer-custody-sha256:", "unreviewed-sha256:");
        AssertMutationRejected("${{ inputs.customer-consumer-custody-sha256 }}", "${{ inputs.customer-consumer-custody }}");
        AssertMutationRejected("--consumer-custody-sha256 \"$CUSTOMER_CONSUMER_CUSTODY_SHA256\"", "--consumer-custody-sha256 \"UNBOUND\"");
        AssertMutationRejected("--source-directory /work/customer-source", "--source-directory /work/foreign-source");
        AssertMutationRejected("1800s python3", "1801s python3");
        AssertMutationRejected("scripts/kernel_root_route.py consumer-phases", "scripts/kernel_root_route.py source-check");
        AssertMutationRejected("            ${{ runner.temp }}/customer-consumer-phases", "            /work/discarded-results");
        AssertMutationRejected("dotnet-validate@e5732037fe94b7ed6e5be2cd4c23dbf6ef5e2617", "dotnet-validate@e3a6093324a24968876782153286f52db8b29fd8");
        AssertMutationRejected("ref: 78e48ffc4ee000df0510cba5e7c7a3c4c4d539d7", "ref: main");

        var consumer = ReadWorkflowStep("Run exact separately admitted Customer consumer phases", "Validate safe scaffold orchestration");
        var scaffold = ReadWorkflowStep("Validate safe scaffold orchestration", "Gate owned production coverage");
        var action = ReadWorkflowStep("Validate .NET solution", "Admitted ordinary Address primitive focused regressions");
        AssertMutationRejected(consumer, string.Empty);
        AssertMutationRejected(consumer, consumer + consumer);
        AssertMutationRejected(consumer + scaffold, scaffold + consumer);
        AssertMutationRejected(action, action + action);
        var address = ReadWorkflowStep("Admitted ordinary Address primitive focused regressions", "Run exact separately admitted Customer consumer phases");
        AssertMutationRejected(address, string.Empty);
        AssertMutationRejected(address, address + address);
        AssertMutationRejected(action + address, address + action);
        AssertMutationRejected("      MSBUILDDISABLENODEREUSE: '1'\n", string.Empty);
        AssertMutationRejected("      MSBUILDDISABLENODEREUSE: '1'\n", "      MSBUILDDISABLENODEREUSE: '0'\n");
        AssertMutationRejected("      DOTNET_CLI_USE_MSBUILD_SERVER: '0'\n", string.Empty);
        AssertMutationRejected("      DOTNET_CLI_USE_MSBUILD_SERVER: '0'\n", "      DOTNET_CLI_USE_MSBUILD_SERVER: '1'\n");
        AssertMutationRejected("      UseSharedCompilation: 'false'\n", string.Empty);
        AssertMutationRejected("      UseSharedCompilation: 'false'\n", "      UseSharedCompilation: 'true'\n");
        AssertMutationRejected("timeout-minutes: 10", "timeout-minutes: 11");
        AssertMutationRejected("GITHUB_ACTIONS: 'false'", "GITHUB_ACTIONS: 'true'");
        AssertMutationRejected("set -euo pipefail", "set -eu");
        AssertMutationRejected("python3 -B scripts/test_customer_address_focus.py\n", "");
        AssertMutationRejected("python3 -B scripts/verify_customer_address_focus.py guard\n", "");
        AssertMutationRejected("python3 -B scripts/verify_customer_address_focus.py verify", "python3 -B scripts/verify_customer_address_focus.py guard");
        AssertMutationRejected("600s dotnet test", "601s dotnet test");
        AssertMutationRejected("--signal=TERM --kill-after=5s 600s", "--signal=KILL --kill-after=5s 600s");
        AssertMutationRejected("--no-build --no-restore", "--no-restore");
        AssertMutationRejected("-c Release --no-build", "-c Debug --no-build");
        AssertMutationRejected("FullyQualifiedName~CustomerAddressLiteralLineHttpTests|FullyQualifiedName~OrdinaryAddressPrimitiveMetadataTests", "FullyQualifiedName~CustomerAddressLiteralLineHttpTests");
        AssertMutationRejected("--logger trx --results-directory runner-address-focused", "--logger trx --results-directory runner-results");
        AssertMutationRejected("            runner-address-focused\n", "");
        AssertMutationRejected(address, address.Replace("        run: |", "        if: always()\n        run: |", StringComparison.Ordinal));
        AssertMutationRejected(address, address.Replace("          GITHUB_ACTIONS: 'false'", "          GITHUB_ACTIONS: 'false'\n          UNREVIEWED: 'true'", StringComparison.Ordinal));
    }

    [Fact]
    public void BuildAndTest_RejectsAnyAlteredOptionalContactPrelude()
    {
        var guard = ReadWorkflowStep("Guard optional contact focused SDK setup", "Set up optional contact focused SDK");
        var sdk = ReadWorkflowStep("Set up optional contact focused SDK", "Restore optional contact focused baseline");
        var restore = ReadWorkflowStep("Restore optional contact focused baseline", "Build optional contact focused Release baseline");
        var build = ReadWorkflowStep("Build optional contact focused Release baseline", "Run exact two optional contact comparison cases");
        var focus = ReadWorkflowStep("Run exact two optional contact comparison cases", "Validate .NET solution");
        foreach (var stage in new[] { guard, sdk, restore, build, focus })
        {
            AssertMutationRejected(stage, string.Empty);
            AssertMutationRejected(stage, stage + stage);
            AssertMutationRejected(stage, stage.Replace("      - name: ", "      - name: Lookalike ", StringComparison.Ordinal));
            AssertMutationRejected(stage, stage.Replace("        with:", "        if: always()\n        with:", StringComparison.Ordinal)
                .Replace("        shell: bash", "        shell: pwsh", StringComparison.Ordinal));
        }

        AssertMutationRejected(guard + sdk, sdk + guard);
        foreach (var stage in new[] { guard, restore, build, focus })
        {
            AssertMutationRejected(stage, stage.Replace("set -euo pipefail", "set -euo pipefail\n          dotnet build Foreign.slnx", StringComparison.Ordinal));
        }

        AssertMutationRejected(restore + build, build + restore);
        AssertMutationRejected(build + focus, focus + build);
        AssertMutationRejected("dotnet-version: 10.0.x", "dotnet-version: 11.0.x");
        AssertMutationRejected("setup-dotnet@a98b56852c35b8e3190ac28c8c2271da59106c68", "setup-dotnet@main");
        AssertMutationRejected("timeout-minutes: 4", "timeout-minutes: 5");
        AssertMutationRejected("timeout-minutes: 2", "timeout-minutes: 3");
        AssertMutationRejected("scripts/test_customer_optional_contact_focus.py", "scripts/test_customer_address_focus.py");
        AssertMutationRejected("scripts/verify_customer_optional_contact_focus.py guard", "scripts/verify_customer_optional_contact_focus.py verify");
        AssertMutationRejected("180s dotnet restore", "181s dotnet restore");
        AssertMutationRejected("180s dotnet build", "181s dotnet build");
        AssertMutationRejected("-warnaserror -p:UseSharedCompilation=false", "-p:UseSharedCompilation=true");
        AssertMutationRejected("540s dotnet test", "541s dotnet test");
        AssertMutationRejected("FullyQualifiedName=Legacy.Maliev.CustomerService.Tests.Controllers.CustomerNameLiteralHttpTests.SourceOptionalContact_OrdinaryCreateAndUpdateComparePinnedReference", "FullyQualifiedName~CustomerNameLiteralHttpTests");
        AssertMutationRejected("scripts/verify_customer_optional_contact_focus.py begin", "scripts/verify_customer_optional_contact_focus.py guard");
        AssertMutationRejected("status=(\"${PIPESTATUS[@]}\")", "status=(0 0)");
        AssertMutationRejected("--test-exit \"${status[0]}\"", "--test-exit 0");
        AssertMutationRejected("--tee-exit \"${status[1]}\"", "--tee-exit 0");
        AssertMutationRejected("            runner-optional-contact-focused\n", string.Empty);
        AssertMutationRejected(focus, focus.Replace("          VSTestCollect: ''", "          VSTestCollect: 'Other'", StringComparison.Ordinal));
        AssertMutationRejected(focus, focus.Replace("        env:", "        env:\n          EXTRA: 'true'", StringComparison.Ordinal));
        AssertMutationRejected("          CUSTOMER_FOCUSED_SOURCE_HEAD: '${{ github.event.pull_request.head.sha || github.sha }}'", "          CUSTOMER_FOCUSED_SOURCE_HEAD: 'main'");
        AssertMutationRejected(restore, restore + "      - run: dotnet build Foreign.slnx\n");
        AssertMutationRejected(focus, focus + "      - uses: actions/cache@main\n");
        AssertMutationRejected("  validate:\n", "  foreign:\n    runs-on: ubuntu-latest\n    steps:\n      - run: dotnet test Foreign.slnx\n  validate:\n");
    }

    [Theory]
    [InlineData("shell: pwsh", "shell: bash")]
    [InlineData("./tooling/Test-CustomerScaffoldContract.ps1", "./tooling/UnreviewedScaffold.ps1")]
    [InlineData("./runner-results/customer-scaffold-orchestration.json", "./discarded-scaffold-evidence.json")]
    public void BuildAndTest_RejectsAlteredScaffoldGuardBoundary(string original, string replacement)
    {
        AssertMutationRejected(original, replacement);
    }

    [Fact]
    public void BuildAndTest_RejectsSharedActionMainWithPinnedShaComment()
    {
        AssertMutationRejected(
            "MALIEV-Co-Ltd/Legacy.Maliev.Workflows/actions/dotnet-validate@e5732037fe94b7ed6e5be2cd4c23dbf6ef5e2617",
            "MALIEV-Co-Ltd/Legacy.Maliev.Workflows/actions/dotnet-validate@main # e5732037fe94b7ed6e5be2cd4c23dbf6ef5e2617");
    }

    [Fact]
    public void BuildAndTest_RejectsCommentedDependencySha()
    {
        AssertMutationRejected(
            "ref: 086760fa0aae976a799dbcda1960d5c0981248cb",
            "ref: main # 086760fa0aae976a799dbcda1960d5c0981248cb");
    }

    [Fact]
    public void ApiProject_UsesOnlyLegacyServiceDefaults()
    {
        Assert.Contains("Legacy.Maliev.ServiceDefaults", ApiProject, StringComparison.Ordinal);
        Assert.DoesNotContain("Maliev.Aspire\\Maliev.Aspire.ServiceDefaults", ApiProject, StringComparison.Ordinal);
        Assert.DoesNotContain("Include=\"Maliev.Aspire.ServiceDefaults\"", ApiProject, StringComparison.Ordinal);
    }

    [Fact]
    public void EfDesignDependency_IsOwnedByDataProjectOnly()
    {
        var project = XDocument.Parse(ApiProject);
        var enabled = Assert.Single(project.Descendants("EnableCustomerScaffoldDesignTime"));
        Assert.Equal("false", enabled.Value);
        Assert.Equal("'$(EnableCustomerScaffoldDesignTime)' == ''", enabled.Attribute("Condition")?.Value);
        var reference = Assert.Single(project.Descendants("PackageReference"),
            element => (string?)element.Attribute("Include") == "Microsoft.EntityFrameworkCore.Design");
        Assert.Equal("'$(EnableCustomerScaffoldDesignTime)' == 'true'", reference.Attribute("Condition")?.Value);
        Assert.Equal("10.0.12", reference.Attribute("Version")?.Value);
        Assert.Equal("all", reference.Element("PrivateAssets")?.Value);
        Assert.Contains("Microsoft.EntityFrameworkCore.Design", DataProject, StringComparison.Ordinal);
    }

    [Fact]
    public void BuildAndTest_RejectsJobPermissionEscalation()
    {
        AssertMutationRejected(
            "  validate:\n    name: validate",
            "  validate:\n    permissions:\n      contents: write\n    name: validate");
    }

    [Fact]
    public void BuildAndTest_RejectsSecretReferenceAnywhere()
    {
        var mutated = $"{Workflow}\n# ${{{{ secrets.X }}}}\n";

        Assert.Throws<InvalidOperationException>(() => WorkflowContractValidator.Validate(mutated));
    }

    [Theory]
    [InlineData("${{secrets.X}}")]
    [InlineData("${{ secrets['X'] }}")]
    public void BuildAndTest_RejectsSecretExpressionInJobEnvironment(string expression)
    {
        AssertMutationRejected(
            "    env:\n      MalievWorkspaceRoot: ${{ github.workspace }}/.dependencies",
            $"    env:\n      MalievWorkspaceRoot: ${{{{ github.workspace }}}}/.dependencies\n      REVIEW_TOKEN: {expression}");
    }

    [Fact]
    public void BuildAndTest_RejectsWhitespaceObfuscatedRestoreCommand()
    {
        AssertMutationRejected(
            "          solution: Legacy.Maliev.CustomerService.slnx",
            "          solution: Legacy.Maliev.CustomerService.slnx\n      - run: dotnet  restore Legacy.Maliev.CustomerService.slnx");
    }

    [Fact]
    public void BuildAndTest_RejectsMissingLocalDependencyOptIn()
    {
        AssertMutationRejected(
            "          use-local-maliev-dependencies: 'true'\n",
            string.Empty);
    }

    [Fact]
    public void BuildAndTest_RejectsReservedGitHubActionsOverride()
    {
        AssertMutationRejected(
            "          use-local-maliev-dependencies: 'true'\n",
            "          use-local-maliev-dependencies: 'true'\n        env:\n          GITHUB_ACTIONS: 'false'\n");
    }

    private static string ReadWorkflowStep(string name, string nextName)
    {
        var start = Workflow.IndexOf($"      - name: {name}\n", StringComparison.Ordinal);
        Assert.True(start >= 0, $"Missing workflow step '{name}'.");
        var end = Workflow.IndexOf($"      - name: {nextName}\n", start + 1, StringComparison.Ordinal);
        Assert.True(end > start, $"Missing following workflow step '{nextName}'.");
        return Workflow[start..end];
    }

    private static void AssertMutationRejected(string original, string replacement)
    {
        Assert.Contains(original, Workflow, StringComparison.Ordinal);
        var mutated = Workflow.Replace(original, replacement, StringComparison.Ordinal);

        Assert.Throws<InvalidOperationException>(() => WorkflowContractValidator.Validate(mutated));
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

internal static partial class WorkflowContractValidator
{
    private const string CheckoutAction = "actions/checkout@3d3c42e5aac5ba805825da76410c181273ba90b1";
    private const string SharedValidationAction = "MALIEV-Co-Ltd/Legacy.Maliev.Workflows/actions/dotnet-validate@e5732037fe94b7ed6e5be2cd4c23dbf6ef5e2617";

    public static void Validate(string workflow)
    {
        if (SecretExpression().IsMatch(workflow))
        {
            throw new InvalidOperationException("Workflow must not reference secrets.");
        }

        var yaml = new YamlStream();
        try
        {
            yaml.Load(new StringReader(workflow));
        }
        catch (Exception exception)
        {
            throw new InvalidOperationException("Workflow must be valid YAML.", exception);
        }

        if (yaml.Documents.Count != 1 || yaml.Documents[0].RootNode is not YamlMappingNode root)
        {
            throw new InvalidOperationException("Workflow must contain exactly one mapping document.");
        }

        var workflowPermissions = RequireExactReadOnlyPermissions(RequireMapping(root, "permissions"), "workflow");
        var jobs = RequireMapping(root, "jobs");
        if (jobs.Children.Count != 1)
        {
            throw new InvalidOperationException("Workflow must define only the validate job.");
        }

        var validateJob = RequireMapping(jobs, "validate");
        var jobPermissionsNode = GetOptional(validateJob, "permissions");
        var effectiveJobPermissions = jobPermissionsNode is null
            ? workflowPermissions
            : RequireExactReadOnlyPermissions(RequireMapping(jobPermissionsNode, "jobs.validate.permissions"), "validate job");
        if (!effectiveJobPermissions.SequenceEqual(workflowPermissions, StringComparer.Ordinal))
        {
            throw new InvalidOperationException("Validate job permissions must not differ from workflow permissions.");
        }

        RequireScalarValue(validateJob, "name", "validate");

        var steps = RequireSequence(validateJob, "steps");
        if (steps.Children.Count != 14)
        {
            throw new InvalidOperationException("Validate job must contain four shared validation steps, five exact optional contact prelude steps, one exact admitted Address focus, one optional custody caller, one scaffold guard, and two evidence steps.");
        }

        ValidateConsumerInputs(root);
        ValidateOptionalContactPrelude(steps);
        ValidateAddressFocusedCaller(steps.Children[9]);
        RejectDuplicatedValidationActionsAndCommands(jobs, steps.Children[9], steps.Children[4], steps.Children[5], steps.Children[6], steps.Children[7]);
        ValidateConsumerCaller(steps.Children[10]);

        var environment = RequireMapping(validateJob, "env");
        if (environment.Children.Count != 7)
        {
            throw new InvalidOperationException("Validate environment must contain only dependency root, evidence properties, and exact finite build-server lifecycle controls.");
        }

        RequireScalarValue(environment, "MalievWorkspaceRoot", "${{ github.workspace }}/.dependencies");
        RequireScalarValue(environment, "VSTestCollect", "XPlat Code Coverage");
        RequireScalarValue(environment, "VSTestLogger", "trx");
        RequireScalarValue(environment, "VSTestResultsDirectory", "${{ github.workspace }}/runner-results");
        RequireScalarValue(environment, "MSBUILDDISABLENODEREUSE", "1");
        RequireScalarValue(environment, "DOTNET_CLI_USE_MSBUILD_SERVER", "0");
        RequireScalarValue(environment, "UseSharedCompilation", "false");

        var scaffold = RequireMapping(steps.Children[11], "scaffold guard");
        if (scaffold.Children.Count != 3)
        {
            throw new InvalidOperationException("Scaffold guard must contain only name, shell, and run.");
        }

        RequireScalarValue(scaffold, "name", "Validate safe scaffold orchestration");
        RequireScalarValue(scaffold, "shell", "pwsh");
        RequireScalarValue(scaffold, "run", "./tooling/Test-CustomerScaffoldContract.ps1 -EvidencePath ./runner-results/customer-scaffold-orchestration.json");
        var gate = RequireMapping(steps.Children[12], "coverage gate");
        if (gate.Children.Count != 2)
        {
            throw new InvalidOperationException("Coverage gate must contain only name and run.");
        }

        RequireScalarValue(gate, "name", "Gate owned production coverage");
        RequireScalarValue(gate, "run", "python3 scripts/verify-runner-coverage.py runner-results");
        var evidence = RequireMapping(steps.Children[13], "evidence upload");
        if (evidence.Children.Count != 4)
        {
            throw new InvalidOperationException("Evidence upload must contain exactly name, if, uses and with.");
        }

        RequireScalarValue(evidence, "name", "Preserve validation evidence");
        RequireScalarValue(evidence, "if", "always()");
        RequireScalarValue(evidence, "uses", "actions/upload-artifact@ea165f8d65b6e75b540449e92b4886f43607fa02");
        var evidenceInputs = RequireMapping(evidence, "with");
        if (evidenceInputs.Children.Count != 4)
        {
            throw new InvalidOperationException("Evidence upload must have exactly four bounded inputs.");
        }

        RequireScalarValue(evidenceInputs, "name", "customer-validation-${{ github.sha }}");
        var evidencePaths = RequireScalar(GetRequired(evidenceInputs, "path")).TrimEnd('\r', '\n');
        if (!string.Equals(evidencePaths, "runner-results\nrunner-address-focused\nrunner-optional-contact-focused\n${{ runner.temp }}/customer-consumer-phases", StringComparison.Ordinal))
        {
            throw new InvalidOperationException("Evidence upload must retain exactly full validation, Address focus, and consumer phase results.");
        }
        RequireScalarValue(evidenceInputs, "if-no-files-found", "warn");
        RequireScalarValue(evidenceInputs, "retention-days", "7");

        ValidateStep(
            steps.Children[0],
            CheckoutAction,
            new Dictionary<string, string>(StringComparer.Ordinal)
            {
                ["persist-credentials"] = "false",
            });
        ValidateStep(
            steps.Children[1],
            CheckoutAction,
            new Dictionary<string, string>(StringComparer.Ordinal)
            {
                ["repository"] = "MALIEV-Co-Ltd/Legacy.Maliev.ServiceDefaults",
                ["ref"] = "086760fa0aae976a799dbcda1960d5c0981248cb",
                ["path"] = ".dependencies/Legacy.Maliev.ServiceDefaults",
                ["persist-credentials"] = "false",
            });
        ValidateStep(
            steps.Children[2],
            CheckoutAction,
            new Dictionary<string, string>(StringComparer.Ordinal)
            {
                ["repository"] = "MALIEV-Co-Ltd/Legacy.Maliev.CompatibilityContracts",
                ["ref"] = "78e48ffc4ee000df0510cba5e7c7a3c4c4d539d7",
                ["path"] = ".dependencies/Legacy.Maliev.CompatibilityContracts",
                ["persist-credentials"] = "false",
            });
        ValidateStep(
            steps.Children[8],
            SharedValidationAction,
            new Dictionary<string, string>(StringComparer.Ordinal)
            {
                ["solution"] = "Legacy.Maliev.CustomerService.slnx",
                ["use-local-maliev-dependencies"] = "true",
            });
    }

    private static void ValidateOptionalContactPrelude(YamlSequenceNode steps)
    {
        var sdk = RequireMapping(steps.Children[4], "focused SDK");
        var sdkKeys = new HashSet<string>(["name", "timeout-minutes", "uses", "with"], StringComparer.Ordinal);
        if (!sdk.Children.Keys.Select(RequireScalar).ToHashSet(StringComparer.Ordinal).SetEquals(sdkKeys))
        {
            throw new InvalidOperationException("Focused SDK setup must have exact bounded keys.");
        }

        RequireScalarValue(sdk, "name", "Set up optional contact focused SDK");
        RequireScalarValue(sdk, "timeout-minutes", "4");
        RequireScalarValue(sdk, "uses", "actions/setup-dotnet@a98b56852c35b8e3190ac28c8c2271da59106c68");
        var sdkInputs = RequireMapping(sdk, "with");
        if (sdkInputs.Children.Count != 1)
        {
            throw new InvalidOperationException("Focused SDK setup must contain one exact version input.");
        }

        RequireScalarValue(sdkInputs, "dotnet-version", "10.0.x");
        ValidateOptionalContactRun(steps.Children[3], "Guard optional contact focused SDK setup", "2",
            "set -euo pipefail\ntest ! -e runner-optional-contact-focused\nmkdir runner-optional-contact-focused\npython3 -B scripts/verify_customer_optional_contact_focus.py guard 2>&1 | tee runner-optional-contact-focused/controls-admission.log\n(ulimit -v 262144; timeout --signal=TERM --kill-after=5s 60s python3 -B scripts/test_customer_optional_contact_focus.py) 2>&1 | tee runner-optional-contact-focused/controls.log\npython3 -B scripts/verify_customer_optional_contact_focus.py guard 2>&1 | tee runner-optional-contact-focused/sdk-admission.log",
            new Dictionary<string, string>(StringComparer.Ordinal));
        ValidateOptionalContactRun(steps.Children[5], "Restore optional contact focused baseline", "4",
            "set -euo pipefail\npython3 -B scripts/verify_customer_optional_contact_focus.py guard 2>&1 | tee runner-optional-contact-focused/restore-admission.log\ntimeout --signal=TERM --kill-after=5s 180s dotnet restore Legacy.Maliev.CustomerService.slnx 2>&1 | tee runner-optional-contact-focused/restore.log",
            new Dictionary<string, string>(StringComparer.Ordinal)
            {
                ["GITHUB_ACTIONS"] = "false",
            });
        ValidateOptionalContactRun(steps.Children[6], "Build optional contact focused Release baseline", "4",
            "set -euo pipefail\npython3 -B scripts/verify_customer_optional_contact_focus.py guard 2>&1 | tee runner-optional-contact-focused/build-admission.log\ntimeout --signal=TERM --kill-after=5s 180s dotnet build Legacy.Maliev.CustomerService.slnx -c Release --no-restore -warnaserror -p:UseSharedCompilation=false 2>&1 | tee runner-optional-contact-focused/build.log",
            new Dictionary<string, string>(StringComparer.Ordinal)
            {
                ["GITHUB_ACTIONS"] = "false",
            });
        ValidateOptionalContactRun(steps.Children[7], "Run exact two optional contact comparison cases", "10",
            "set -euo pipefail\npython3 -B scripts/verify_customer_optional_contact_focus.py guard 2>&1 | tee runner-optional-contact-focused/test-admission.log\npython3 -B scripts/verify_customer_optional_contact_focus.py begin\nset +e\ntimeout --signal=TERM --kill-after=5s 540s dotnet test Legacy.Maliev.CustomerService.slnx -c Release --no-build --no-restore --filter \"FullyQualifiedName=Legacy.Maliev.CustomerService.Tests.Controllers.CustomerNameLiteralHttpTests.SourceOptionalContact_OrdinaryCreateAndUpdateComparePinnedReference\" --logger trx --results-directory runner-optional-contact-focused/results 2>&1 | tee runner-optional-contact-focused/test.log\nstatus=(\"${PIPESTATUS[@]}\")\nset -e\npython3 -B scripts/verify_customer_optional_contact_focus.py verify --test-exit \"${status[0]}\" --tee-exit \"${status[1]}\"\npython3 -B scripts/verify_customer_optional_contact_focus.py guard 2>&1 | tee runner-optional-contact-focused/full-admission.log",
            new Dictionary<string, string>(StringComparer.Ordinal)
            {
                ["GITHUB_ACTIONS"] = "false",
                ["VSTestCollect"] = "",
                ["VSTestResultsDirectory"] = "${{ github.workspace }}/runner-optional-contact-focused/results",
                ["CUSTOMER_FOCUSED_SOURCE_HEAD"] = "${{ github.event.pull_request.head.sha || github.sha }}",
            });
    }

    private static void ValidateOptionalContactRun(
        YamlNode node,
        string name,
        string minutes,
        string command,
        IReadOnlyDictionary<string, string> expectedEnvironment)
    {
        var step = RequireMapping(node, "optional contact prelude");
        var expectedKeys = new HashSet<string>(["name", "timeout-minutes", "shell", "run"], StringComparer.Ordinal);
        if (expectedEnvironment.Count != 0)
        {
            expectedKeys.Add("env");
        }

        if (!step.Children.Keys.Select(RequireScalar).ToHashSet(StringComparer.Ordinal).SetEquals(expectedKeys))
        {
            throw new InvalidOperationException("Optional contact prelude has unreviewed keys.");
        }

        RequireScalarValue(step, "name", name);
        RequireScalarValue(step, "timeout-minutes", minutes);
        RequireScalarValue(step, "shell", "bash");
        if (!string.Equals(RequireScalar(GetRequired(step, "run")).TrimEnd('\r', '\n'), command, StringComparison.Ordinal))
        {
            throw new InvalidOperationException("Optional contact prelude must retain its complete exact guarded command.");
        }

        if (expectedEnvironment.Count != 0)
        {
            var environment = RequireMapping(step, "env");
            if (environment.Children.Count != expectedEnvironment.Count)
            {
                throw new InvalidOperationException("Optional contact prelude environment is not exact.");
            }

            foreach (var expected in expectedEnvironment)
            {
                RequireScalarValue(environment, expected.Key, expected.Value);
            }
        }
    }

    private static void ValidateAddressFocusedCaller(YamlNode node)
    {
        var caller = RequireMapping(node, "Address focused caller");
        var expectedKeys = new HashSet<string>(["name", "timeout-minutes", "env", "run"], StringComparer.Ordinal);
        if (!caller.Children.Keys.Select(RequireScalar).ToHashSet(StringComparer.Ordinal).SetEquals(expectedKeys))
        {
            throw new InvalidOperationException("Address focus must contain exactly name, timeout-minutes, env, and run.");
        }

        RequireScalarValue(caller, "name", "Admitted ordinary Address primitive focused regressions");
        RequireScalarValue(caller, "timeout-minutes", "10");
        var environment = RequireMapping(caller, "env");
        if (environment.Children.Count != 1)
        {
            throw new InvalidOperationException("Only the guarded Address focus may override GITHUB_ACTIONS.");
        }

        RequireScalarValue(environment, "GITHUB_ACTIONS", "false");
        var command = RequireScalar(GetRequired(caller, "run")).TrimEnd('\r', '\n');
        const string expectedCommand = "set -euo pipefail\npython3 -B scripts/test_customer_address_focus.py\npython3 -B scripts/verify_customer_address_focus.py guard\ntimeout --signal=TERM --kill-after=5s 600s dotnet test Legacy.Maliev.CustomerService.slnx -c Release --no-build --no-restore --filter \"FullyQualifiedName~CustomerAddressLiteralLineHttpTests|FullyQualifiedName~OrdinaryAddressPrimitiveMetadataTests\" --logger trx --results-directory runner-address-focused\npython3 -B scripts/verify_customer_address_focus.py verify";
        if (!string.Equals(command, expectedCommand, StringComparison.Ordinal))
        {
            throw new InvalidOperationException("Address focus must use the exact guarded, bounded, filtered no-build command and strict evidence verifier.");
        }
    }

    private static void ValidateConsumerInputs(YamlMappingNode root)
    {
        var triggers = RequireMapping(root, "on");
        var call = RequireMapping(triggers, "workflow_call");
        var inputs = RequireMapping(call, "inputs");
        if (triggers.Children.Count != 1 || call.Children.Count != 1 || inputs.Children.Count != 2)
        {
            throw new InvalidOperationException("Only the two default-empty consumer custody inputs are permitted.");
        }

        foreach (var key in new[] { "customer-consumer-custody", "customer-consumer-custody-sha256" })
        {
            var input = RequireMapping(inputs, key);
            if (input.Children.Count != 4)
            {
                throw new InvalidOperationException("Consumer custody input must contain exactly description, type, required, and default.");
            }

            _ = RequireScalar(GetRequired(input, "description"));
            RequireScalarValue(input, "type", "string");
            RequireScalarValue(input, "required", "false");
            RequireScalarValue(input, "default", string.Empty);
        }
    }

    private static void ValidateConsumerCaller(YamlNode node)
    {
        var caller = RequireMapping(node, "consumer custody caller");
        if (caller.Children.Count != 5)
        {
            throw new InvalidOperationException("Consumer caller must contain exactly name, if, timeout-minutes, env, and run.");
        }

        RequireScalarValue(caller, "name", "Run exact separately admitted Customer consumer phases");
        RequireScalarValue(caller, "if", "inputs.customer-consumer-custody != ''");
        RequireScalarValue(caller, "timeout-minutes", "30");
        var environment = RequireMapping(caller, "env");
        if (environment.Children.Count != 2)
        {
            throw new InvalidOperationException("Consumer caller must bind exactly the original custody path and independent SHA.");
        }

        RequireScalarValue(environment, "CUSTOMER_CONSUMER_CUSTODY", "${{ inputs.customer-consumer-custody }}");
        RequireScalarValue(environment, "CUSTOMER_CONSUMER_CUSTODY_SHA256", "${{ inputs.customer-consumer-custody-sha256 }}");
        var command = RequireScalar(GetRequired(caller, "run")).TrimEnd('\r', '\n');
        const string expectedCommand = "timeout --signal=TERM --kill-after=5s 1800s python3 -B scripts/kernel_root_route.py consumer-phases --packet scripts/customer-kernel-packet --source-directory /work/customer-source --consumer-custody \"$CUSTOMER_CONSUMER_CUSTODY\" --consumer-custody-sha256 \"$CUSTOMER_CONSUMER_CUSTODY_SHA256\" --output \"$RUNNER_TEMP/customer-consumer-phases\"";
        if (!string.Equals(command, expectedCommand, StringComparison.Ordinal))
        {
            throw new InvalidOperationException("Consumer caller must use the exact bounded original dispatcher command.");
        }
    }

    private static IReadOnlyList<string> RequireExactReadOnlyPermissions(YamlMappingNode permissions, string scope)
    {
        if (permissions.Children.Count != 1)
        {
            throw new InvalidOperationException($"{scope} permissions must contain only contents: read.");
        }

        RequireScalarValue(permissions, "contents", "read");
        return ["contents:read"];
    }

    private static void ValidateStep(
        YamlNode node,
        string expectedAction,
        IReadOnlyDictionary<string, string> expectedInputs)
    {
        var step = RequireMapping(node, "workflow step");
        var allowedKeys = new HashSet<string>(["name", "uses", "with"], StringComparer.Ordinal);

        var actualKeys = step.Children.Keys.Select(RequireScalar).ToHashSet(StringComparer.Ordinal);
        if (!actualKeys.SetEquals(allowedKeys))
        {
            throw new InvalidOperationException("Each workflow step must contain exactly name, uses, and with.");
        }

        var name = RequireScalar(GetRequired(step, "name"));
        if (string.IsNullOrWhiteSpace(name))
        {
            throw new InvalidOperationException("Workflow step name must not be empty.");
        }

        RequireScalarValue(step, "uses", expectedAction);
        var inputs = RequireMapping(step, "with");
        if (inputs.Children.Count != expectedInputs.Count)
        {
            throw new InvalidOperationException($"Action {expectedAction} has an unexpected input count.");
        }

        foreach (var expectedInput in expectedInputs)
        {
            RequireScalarValue(inputs, expectedInput.Key, expectedInput.Value);
        }

        var environment = GetOptional(step, "env");
        if (environment is not null)
        {
            throw new InvalidOperationException("Workflow action steps must not override reserved environment variables.");
        }

        if (expectedInputs.ContainsKey("use-local-maliev-dependencies"))
        {
            var useLocalDependencies = GetRequired(inputs, "use-local-maliev-dependencies") as YamlScalarNode;
            if (useLocalDependencies?.Style != ScalarStyle.SingleQuoted)
            {
                throw new InvalidOperationException("use-local-maliev-dependencies must use the single-quoted string value 'true'.");
            }
        }
    }

    private static void RejectDuplicatedValidationActionsAndCommands(YamlMappingNode jobs, YamlNode validatedAddressFocus, YamlNode validatedSdk, params YamlNode[] validatedPrelude)
    {
        foreach (var jobNode in jobs.Children.Values.OfType<YamlMappingNode>())
        {
            var stepsNode = GetOptional(jobNode, "steps");
            if (stepsNode is not YamlSequenceNode steps)
            {
                continue;
            }

            foreach (var stepNode in steps.Children.OfType<YamlMappingNode>())
            {
                if (GetOptional(stepNode, "uses") is YamlScalarNode usesNode)
                {
                    var action = usesNode.Value ?? string.Empty;
                    if ((!ReferenceEquals(stepNode, validatedSdk) && action.StartsWith("actions/setup-dotnet@", StringComparison.OrdinalIgnoreCase))
                        || action.StartsWith("actions/cache@", StringComparison.OrdinalIgnoreCase))
                    {
                        throw new InvalidOperationException($"Caller duplicates shared action {action}.");
                    }
                }

                if (!ReferenceEquals(stepNode, validatedAddressFocus)
                    && !validatedPrelude.Any(validated => ReferenceEquals(stepNode, validated))
                    && GetOptional(stepNode, "run") is YamlScalarNode runNode)
                {
                    RejectDuplicatedDotNetCommand(runNode.Value ?? string.Empty);
                }
            }
        }
    }

    private static void RejectDuplicatedDotNetCommand(string command)
    {
        var tokens = command
            .Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Select(token => token.Trim('"', '\'', ';', '&', '|').ToLowerInvariant())
            .Where(token => token.Length > 0)
            .ToArray();

        for (var index = 0; index < tokens.Length - 1; index++)
        {
            if (!string.Equals(tokens[index], "dotnet", StringComparison.Ordinal))
            {
                continue;
            }

            var verb = tokens[index + 1];
            if (verb is "restore" or "build" or "test" or "format" or "list"
                || tokens[(index + 1)..].Any(token => token is "audit" or "--vulnerable"))
            {
                throw new InvalidOperationException($"Caller duplicates shared dotnet validation command: {verb}.");
            }
        }
    }

    private static YamlMappingNode RequireMapping(YamlMappingNode parent, string key) =>
        RequireMapping(GetRequired(parent, key), key);

    private static YamlMappingNode RequireMapping(YamlNode node, string description)
    {
        return node as YamlMappingNode
            ?? throw new InvalidOperationException($"{description} must be a mapping.");
    }

    private static YamlSequenceNode RequireSequence(YamlMappingNode parent, string key)
    {
        return GetRequired(parent, key) as YamlSequenceNode
            ?? throw new InvalidOperationException($"{key} must be a sequence.");
    }

    private static void RequireScalarValue(YamlMappingNode parent, string key, string expected)
    {
        var actual = RequireScalar(GetRequired(parent, key));
        if (!string.Equals(actual, expected, StringComparison.Ordinal))
        {
            throw new InvalidOperationException($"{key} must equal '{expected}', but was '{actual}'.");
        }
    }

    private static string RequireScalar(YamlNode node)
    {
        return (node as YamlScalarNode)?.Value
            ?? throw new InvalidOperationException("Expected a scalar YAML value.");
    }

    private static YamlNode GetRequired(YamlMappingNode parent, string key)
    {
        return GetOptional(parent, key)
            ?? throw new InvalidOperationException($"Missing required YAML key '{key}'.");
    }

    private static YamlNode? GetOptional(YamlMappingNode parent, string key)
    {
        foreach (var child in parent.Children)
        {
            if (child.Key is YamlScalarNode scalar && string.Equals(scalar.Value, key, StringComparison.Ordinal))
            {
                return child.Value;
            }
        }

        return null;
    }

    [GeneratedRegex(@"\$\{\{\s*secrets\s*(?:\.|\[)", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex SecretExpression();
}
