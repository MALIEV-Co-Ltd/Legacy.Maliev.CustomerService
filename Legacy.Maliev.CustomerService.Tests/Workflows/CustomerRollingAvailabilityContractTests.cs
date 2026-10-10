using System.Text;
using System.Text.Json.Nodes;

using Legacy.Maliev.CustomerService.Application.Migration;

using YamlDotNet.RepresentationModel;

namespace Legacy.Maliev.CustomerService.Tests.Workflows;

public sealed class CustomerRollingAvailabilityContractTests
{
    private const string Source = "94808c01016acde41a7f264f95b8ee6b95573691";
    private static readonly string ProposedImage = "sha256:" + new string('2', 64);
    private static readonly string OriginalImage = "sha256:" + new string('1', 64);
    private static readonly CustomerRolloutReviewBinding Binding = new(
        Source, ProposedImage, "controlled-customer-fixture", "original-uid", OriginalImage, "service-uid", "123", [80]);

    [Fact]
    public void ExactDormantFiles_AreConsumedWithoutRuntimeOrConsumerAcceptance()
    {
        var directory = Path.Combine(Path.GetTempPath(), "customer-rollout-review-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);
        try
        {
            var contract = Path.Combine(directory, "contract.json");
            var receipt = Path.Combine(directory, "receipt.json");
            File.WriteAllText(contract, Contract().ToJsonString());
            File.WriteAllText(receipt, Receipt().ToJsonString());
            var contractBefore = File.ReadAllBytes(contract);
            var receiptBefore = File.ReadAllBytes(receipt);
            var result = CustomerRollingAvailabilityReview.ReviewFiles(contract, receipt, Binding);
            Assert.Equal(0, result.ExitCode);
            Assert.Equal("DormantReviewComplete", result.Outcome);
            Assert.True(result.Dormant);
            Assert.False(result.RuntimeAccepted);
            Assert.False(result.ConsumerAdoptionAccepted);
            Assert.Equal(2, Directory.GetFiles(directory).Length);
            Assert.Equal(contractBefore, File.ReadAllBytes(contract));
            Assert.Equal(receiptBefore, File.ReadAllBytes(receipt));
        }
        finally
        {
            Directory.Delete(directory, recursive: true);
        }
    }

    [Fact]
    public void UnavailableBudget_RejectsPositiveOrMissing()
    {
        var contract = Contract();
        contract["rollout"]!["maxUnavailable"] = 1;
        Refused(contract, Receipt());
        contract["rollout"]!.AsObject().Remove("maxUnavailable");
        Refused(contract, Receipt());
        var input = Receipt();
        input["current"]!["rollout"]!["maxUnavailable"] = 1;
        Refused(Contract(), input);
    }

    [Fact]
    public void SurgeAndReservation_RejectAlteredOrUnprovedCapacity()
    {
        var contract = Contract();
        contract["rollout"]!["maxSurge"] = 0;
        Refused(contract, Receipt());
        foreach (var field in new[] { "availableReplicaSlots", "reservedReplicaSlots", "peakReplicas" })
        {
            var input = Receipt();
            input["capacity"]![field] = 0;
            Refused(Contract(), input);
        }

        var absent = Receipt();
        absent["capacity"]!["reservationId"] = "";
        Refused(Contract(), absent);
    }

    [Fact]
    public void ReadyAndObservation_RejectInsufficientOrMissing()
    {
        var contract = Contract();
        contract["rollout"]!["minReadySeconds"] = 89;
        Refused(contract, Receipt());
        contract["rollout"]!.AsObject().Remove("minReadySeconds");
        Refused(contract, Receipt());
        var input = Receipt();
        input["observationSeconds"] = 179;
        Refused(Contract(), input);
    }

    [Fact]
    public void DrainAndGrace_RejectAnyChangedSourceTiming()
    {
        foreach (var field in new[] { "preStopDrainSeconds", "terminationGracePeriodSeconds" })
        {
            foreach (var value in new[] { 0, 59, 89, 91 })
            {
                var contract = Contract();
                contract["rollout"]![field] = value;
                Refused(contract, Receipt());
            }
        }
    }

    [Fact]
    public void ParsedSchema_RejectsCoercionDuplicatesUnknownFieldsAndOversizedInputs()
    {
        foreach (var value in new JsonNode?[] { JsonValue.Create(true), JsonValue.Create("90"), null, JsonValue.Create(90.5), JsonValue.Create(-1) })
        {
            var contract = Contract();
            contract["rollout"]!["minReadySeconds"] = value?.DeepClone();
            Refused(contract, Receipt());
        }

        var text = Contract().ToJsonString().Replace("\"enabled\":false", "\"enabled\":false,\"enabled\":false", StringComparison.Ordinal);
        Assert.Equal("Customer dormant rollout review refused.", Assert.Throws<InvalidOperationException>(
            () => CustomerRollingAvailabilityReview.Review(text, Receipt().ToJsonString(), Binding)).Message);
        var unknown = Receipt();
        unknown["executor"] = "unreviewed";
        Refused(Contract(), unknown);
        Assert.Throws<InvalidOperationException>(() => CustomerRollingAvailabilityReview.Review(new string('x', 16385), "{}", Binding));
    }

    [Fact]
    public void ActiveRequestsOrAcceptanceClaims_AreAlwaysRefused()
    {
        var contract = Contract();
        contract["enabled"] = true;
        Refused(contract, Receipt());
        foreach (var field in new[] { "runtimeAccepted", "consumerAdoptionAccepted" })
        {
            foreach (var value in new JsonNode[] { JsonValue.Create(true)!, JsonValue.Create("false")! })
            {
                var input = Receipt();
                input[field] = value.DeepClone();
                Refused(Contract(), input);
            }
        }
    }

    [Fact]
    public void Topology_RejectsForeignArtifactNamespaceUidSelectorOrPorts()
    {
        foreach (var field in new[] { "artifact", "namespace" })
        {
            var input = Receipt();
            input[field] = "foreign";
            Refused(Contract(), input);
        }

        foreach (var field in new[] { "uid", "resourceVersion", "selectedDeploymentUid" })
        {
            var input = Receipt();
            input["service"]![field] = "foreign";
            Refused(Contract(), input);
        }

        var ports = Receipt();
        ports["service"]!["ports"] = new JsonArray(443);
        Refused(Contract(), ports);
        Refused(Contract(), Receipt(), Binding with { Namespace = "" });
    }

    [Fact]
    public void SourceAndImage_FenceExactCallerBindings()
    {
        foreach (var field in new[] { "sourceCommit", "imageDigest" })
        {
            var input = Receipt();
            input[field] = "main";
            Refused(Contract(), input);
        }

        Refused(Contract(), Receipt(), Binding with { CustomerSourceCommit = "main" });
        Refused(Contract(), Receipt(), Binding with { ProposedImageDigest = "latest" });
        Refused(Contract(), Receipt(), Binding with { CustomerSourceCommit = new string('a', 40) });
    }

    [Fact]
    public void HealthyOriginalAndCurrent_RequireExactUnchangedBackendAndReplicaCounts()
    {
        foreach (var backend in new[] { "original", "current" })
        {
            foreach (var field in new[] { "uid", "imageDigest" })
            {
                var input = Receipt();
                input[backend]![field] = "foreign";
                Refused(Contract(), input);
            }

            var unhealthy = Receipt();
            unhealthy[backend]!["healthy"] = false;
            Refused(Contract(), unhealthy);
            var partial = Receipt();
            partial[backend]!["readyReplicas"] = 1;
            Refused(Contract(), partial);
        }

        var mismatch = Receipt();
        mismatch["current"]!["desiredReplicas"] = 3;
        mismatch["current"]!["readyReplicas"] = 3;
        Refused(Contract(), mismatch);
    }

    [Fact]
    public void ControlledFailure_PreservesExitOnlyWhenOriginalIsStillRetained()
    {
        var input = Receipt();
        input["exitCode"] = 17;
        var result = CustomerRollingAvailabilityReview.Review(Contract().ToJsonString(), input.ToJsonString(), Binding);
        Assert.Equal(17, result.ExitCode);
        Assert.Equal("ControlledFailureOriginalRetained", result.Outcome);
        Assert.False(result.RuntimeAccepted);
        Assert.False(result.ConsumerAdoptionAccepted);
        input["service"]!["selectedDeploymentUid"] = "foreign";
        Refused(Contract(), input);
        input = Receipt();
        input["exitCode"] = -1;
        Refused(Contract(), input);
    }

    [Fact]
    public void CurrentParsedPublisherGate_DoesNotActivateDormantReview()
    {
        var repository = FindRepository();
        using var reader = File.OpenText(Path.Combine(repository, ".github", "workflows", "publish-image.yml"));
        var yaml = new YamlStream();
        yaml.Load(reader);
        var jobs = (YamlMappingNode)((YamlMappingNode)yaml.Documents.Single().RootNode).Children[new YamlScalarNode("jobs")];
        Assert.Equal("vars.LEGACY_DEPLOY_ENABLED == 'true'", Scalar((YamlMappingNode)jobs.Children[new YamlScalarNode("publish")], "if"));
        Assert.Equal("vars.LEGACY_DEPLOY_ENABLED != 'true'", Scalar((YamlMappingNode)jobs.Children[new YamlScalarNode("deployment-gate")], "if"));
        var contract = JsonNode.Parse(File.ReadAllText(Path.Combine(repository, "review", "customer-rolling-availability.json")))!;
        var result = CustomerRollingAvailabilityReview.Review(contract.ToJsonString(), Receipt().ToJsonString(), Binding);
        Assert.True(result.Dormant);
        Assert.False(result.RuntimeAccepted);
        contract["enabled"] = true;
        Refused(contract, Receipt());
    }

    [Fact]
    public void ReviewFiles_RejectsOversizedValidContractAndReceiptWithoutDisclosureOrMutation()
    {
        AssertFileBoundaryRefused(path =>
        {
            var json = File.ReadAllText(path);
            File.WriteAllText(path, json + new string(' ', 16385));
            Assert.True(new FileInfo(path).Length > 16384);
        });
    }

    [Fact]
    public void ReviewFiles_RejectsInvalidUtf8ContractAndReceiptWithoutDecoderDisclosure()
    {
        AssertFileBoundaryRefused(path =>
        {
            var json = File.ReadAllText(path);
            var marker = Path.GetFileName(path) == "receipt.json" ? "controlled-reservation" : "Legacy.Maliev.CustomerService";
            var offset = json.IndexOf(marker, StringComparison.Ordinal);
            Assert.True(offset >= 0);
            var prefix = Encoding.UTF8.GetBytes(json[..offset]);
            var suffix = Encoding.UTF8.GetBytes(json[(offset + marker.Length)..]);
            File.WriteAllBytes(path, [.. prefix, 0xc3, 0x28, .. suffix]);
        });
    }

    [Fact]
    public void ReviewFiles_RejectsMissingContractAndReceiptWithoutPathDisclosureOrRecreation()
    {
        AssertFileBoundaryRefused(path =>
        {
            File.Delete(path);
            Assert.False(File.Exists(path));
        });
    }

    private static void AssertFileBoundaryRefused(Action<string> alterFile)
    {
        foreach (var selectedName in new[] { "contract.json", "receipt.json" })
        {
            var directory = Path.Combine(Path.GetTempPath(), "customer-rollout-negative-" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(directory);
            try
            {
                var contract = Path.Combine(directory, "contract.json");
                var receipt = Path.Combine(directory, "receipt.json");
                File.WriteAllText(contract, Contract().ToJsonString());
                File.WriteAllText(receipt, Receipt().ToJsonString());
                alterFile(Path.Combine(directory, selectedName));
                var before = new Dictionary<string, byte[]?>
                {
                    [contract] = File.Exists(contract) ? File.ReadAllBytes(contract) : null,
                    [receipt] = File.Exists(receipt) ? File.ReadAllBytes(receipt) : null
                };

                var failure = Assert.Throws<InvalidOperationException>(
                    () => CustomerRollingAvailabilityReview.ReviewFiles(contract, receipt, Binding));
                Assert.Equal("Customer dormant rollout review refused.", failure.Message);
                Assert.Null(failure.InnerException);
                foreach (var (path, bytes) in before)
                {
                    if (bytes is null)
                    {
                        Assert.False(File.Exists(path));
                    }
                    else
                    {
                        Assert.True(File.Exists(path));
                        Assert.Equal(bytes, File.ReadAllBytes(path));
                    }
                }

                Assert.Equal(
                    before.Where(pair => pair.Value is not null).Select(pair => pair.Key).OrderBy(path => path, StringComparer.Ordinal),
                    Directory.GetFiles(directory).OrderBy(path => path, StringComparer.Ordinal));
            }
            finally
            {
                Directory.Delete(directory, recursive: true);
            }
        }
    }

    private static string Scalar(YamlMappingNode mapping, string name) => ((YamlScalarNode)mapping.Children[new YamlScalarNode(name)]).Value!;

    private static string FindRepository()
    {
        for (DirectoryInfo? directory = new(AppContext.BaseDirectory); directory is not null; directory = directory.Parent)
        {
            if (File.Exists(Path.Combine(directory.FullName, "Legacy.Maliev.CustomerService.slnx")))
            {
                return directory.FullName;
            }
        }

        throw new InvalidOperationException("Repository review files unavailable.");
    }

    private static void Refused(JsonNode contract, JsonNode input, CustomerRolloutReviewBinding? binding = null)
    {
        var contractBefore = contract.ToJsonString();
        var inputBefore = input.ToJsonString();
        var failure = Assert.Throws<InvalidOperationException>(
            () => CustomerRollingAvailabilityReview.Review(contractBefore, inputBefore, binding ?? Binding));
        Assert.Equal("Customer dormant rollout review refused.", failure.Message);
        Assert.Equal(contractBefore, contract.ToJsonString());
        Assert.Equal(inputBefore, input.ToJsonString());
    }

    private static JsonObject Rollout() => new()
    {
        ["minReadySeconds"] = 90,
        ["preStopDrainSeconds"] = 60,
        ["terminationGracePeriodSeconds"] = 90,
        ["maxSurge"] = 1,
        ["maxUnavailable"] = 0
    };

    private static JsonObject Contract() => new()
    {
        ["schemaVersion"] = 1,
        ["enabled"] = false,
        ["owner"] = "Legacy.Maliev.CustomerService",
        ["originalSourceCommit"] = "c3450c9d9a75f04b32eeed91d05d95b9f7a2c449",
        ["rollout"] = Rollout()
    };

    private static JsonObject Backend() => new()
    {
        ["uid"] = "original-uid",
        ["imageDigest"] = OriginalImage,
        ["desiredReplicas"] = 2,
        ["readyReplicas"] = 2,
        ["healthy"] = true,
        ["rollout"] = Rollout()
    };

    private static JsonObject Receipt() => new()
    {
        ["schemaVersion"] = 1,
        ["artifact"] = "legacy-maliev-customer-service",
        ["namespace"] = Binding.Namespace,
        ["sourceCommit"] = Source,
        ["imageDigest"] = ProposedImage,
        ["runtimeAccepted"] = false,
        ["consumerAdoptionAccepted"] = false,
        ["exitCode"] = 0,
        ["original"] = Backend(),
        ["current"] = Backend(),
        ["service"] = new JsonObject
        {
            ["uid"] = "service-uid",
            ["resourceVersion"] = "123",
            ["selectedDeploymentUid"] = "original-uid",
            ["ports"] = new JsonArray(80)
        },
        ["capacity"] = new JsonObject
        {
            ["availableReplicaSlots"] = 3,
            ["reservedReplicaSlots"] = 3,
            ["peakReplicas"] = 5,
            ["reservationId"] = "controlled-reservation"
        },
        ["observationSeconds"] = 180
    };
}
