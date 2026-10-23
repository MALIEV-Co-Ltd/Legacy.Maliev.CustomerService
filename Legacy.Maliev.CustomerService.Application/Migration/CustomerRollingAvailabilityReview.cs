using System.Diagnostics.CodeAnalysis;
using System.Text;
using System.Text.Json;

namespace Legacy.Maliev.CustomerService.Application.Migration;

/// <summary>Frozen caller identities for a controlled, offline Customer rollout receipt review.</summary>
public sealed record CustomerRolloutReviewBinding(
    string CustomerSourceCommit,
    string ProposedImageDigest,
    string Namespace,
    string OriginalDeploymentUid,
    string OriginalImageDigest,
    string ServiceUid,
    string ServiceResourceVersion,
    int[] ServicePorts);

/// <summary>A dormant receipt review; neither a release permit nor observed cluster acceptance.</summary>
public sealed record CustomerRolloutReviewResult(int ExitCode, string Outcome)
{
    /// <summary>The reviewed boundary has no executor.</summary>
    public bool Dormant => true;
    /// <summary>Controlled receipts never establish runtime acceptance.</summary>
    public bool RuntimeAccepted => false;
    /// <summary>Caller review never establishes adoption by a live consumer.</summary>
    public bool ConsumerAdoptionAccepted => false;
}

/// <summary>Reads and validates Customer-owned dormant contracts against controlled handoff receipts.</summary>
public static class CustomerRollingAvailabilityReview
{
    private const string Artifact = "legacy-maliev-customer-service";
    private const string OriginalSource = "c3450c9d9a75f04b32eeed91d05d95b9f7a2c449";

    /// <summary>Consumes explicit review files without starting a process or changing a resource.</summary>
    public static CustomerRolloutReviewResult ReviewFiles(
        string contractPath,
        string receiptPath,
        CustomerRolloutReviewBinding binding)
    {
        return Review(ReadBoundedFile(contractPath), ReadBoundedFile(receiptPath), binding);
    }

    /// <summary>Validates parsed caller inputs; invalid or active requests are refused.</summary>
    public static CustomerRolloutReviewResult Review(
        string contractJson,
        string receiptJson,
        CustomerRolloutReviewBinding binding)
    {
        try
        {
            Require(binding is not null);
            Require(Encoding.UTF8.GetByteCount(contractJson) <= 16384 && Encoding.UTF8.GetByteCount(receiptJson) <= 16384);
            using var contract = JsonDocument.Parse(contractJson, new JsonDocumentOptions { MaxDepth = 16 });
            using var receipt = JsonDocument.Parse(receiptJson, new JsonDocumentOptions { MaxDepth = 16 });
            var policy = contract.RootElement;
            Keys(policy, "schemaVersion", "enabled", "owner", "originalSourceCommit", "rollout");
            Require(Integer(policy, "schemaVersion") == 1 && policy.GetProperty("enabled").ValueKind == JsonValueKind.False);
            Require(Text(policy, "owner") == "Legacy.Maliev.CustomerService" && Text(policy, "originalSourceCommit") == OriginalSource);
            Rollout(policy.GetProperty("rollout"));

            Require(IsHex(binding.CustomerSourceCommit, 40) && IsDigest(binding.ProposedImageDigest)
                && IsDigest(binding.OriginalImageDigest) && !string.IsNullOrWhiteSpace(binding.Namespace)
                && !string.IsNullOrWhiteSpace(binding.OriginalDeploymentUid) && !string.IsNullOrWhiteSpace(binding.ServiceUid)
                && IsRevision(binding.ServiceResourceVersion) && binding.ServicePorts is not null && binding.ServicePorts.Length > 0
                && binding.ServicePorts.Distinct().Count() == binding.ServicePorts.Length
                && binding.ServicePorts.All(port => port is > 0 and <= 65535));
            var input = receipt.RootElement;
            Keys(input, "schemaVersion", "artifact", "namespace", "sourceCommit", "imageDigest", "runtimeAccepted",
                "consumerAdoptionAccepted", "exitCode", "original", "current", "service", "capacity", "observationSeconds");
            Require(Integer(input, "schemaVersion") == 1 && Text(input, "artifact") == Artifact
                && Text(input, "namespace") == binding.Namespace && Text(input, "sourceCommit") == binding.CustomerSourceCommit
                && Text(input, "imageDigest") == binding.ProposedImageDigest
                && input.GetProperty("runtimeAccepted").ValueKind == JsonValueKind.False
                && input.GetProperty("consumerAdoptionAccepted").ValueKind == JsonValueKind.False);
            var original = input.GetProperty("original");
            var replicas = Backend(original, binding);
            Require(Backend(input.GetProperty("current"), binding) == replicas);
            var service = input.GetProperty("service");
            Keys(service, "uid", "resourceVersion", "selectedDeploymentUid", "ports");
            Require(Text(service, "uid") == binding.ServiceUid
                && Text(service, "resourceVersion") == binding.ServiceResourceVersion
                && Text(service, "selectedDeploymentUid") == binding.OriginalDeploymentUid);
            var ports = service.GetProperty("ports");
            Require(ports.ValueKind == JsonValueKind.Array && ports.GetArrayLength() == binding.ServicePorts.Length);
            Require(ports.EnumerateArray().Select(StrictInteger).SequenceEqual(binding.ServicePorts));
            var capacity = input.GetProperty("capacity");
            Keys(capacity, "availableReplicaSlots", "reservedReplicaSlots", "peakReplicas", "reservationId");
            Require(Integer(capacity, "availableReplicaSlots") >= replicas + 1
                && Integer(capacity, "reservedReplicaSlots") == replicas + 1
                && Integer(capacity, "peakReplicas") == replicas * 2 + 1
                && !string.IsNullOrWhiteSpace(Text(capacity, "reservationId"))
                && Integer(input, "observationSeconds") >= 180);
            var exitCode = Integer(input, "exitCode");
            Require(exitCode >= 0);
            return new CustomerRolloutReviewResult(exitCode, exitCode == 0 ? "DormantReviewComplete" : "ControlledFailureOriginalRetained");
        }
        catch (Exception error) when (error is JsonException or InvalidOperationException or FormatException or KeyNotFoundException)
        {
            throw new InvalidOperationException("Customer dormant rollout review refused.");
        }
    }

    private static int Backend(JsonElement backend, CustomerRolloutReviewBinding binding)
    {
        Keys(backend, "uid", "imageDigest", "desiredReplicas", "readyReplicas", "healthy", "rollout");
        var desired = Integer(backend, "desiredReplicas");
        Require(Text(backend, "uid") == binding.OriginalDeploymentUid && Text(backend, "imageDigest") == binding.OriginalImageDigest
            && desired is >= 1 and <= 10 && Integer(backend, "readyReplicas") == desired
            && backend.GetProperty("healthy").ValueKind == JsonValueKind.True);
        Rollout(backend.GetProperty("rollout"));
        return desired;
    }

    private static string ReadBoundedFile(string path)
    {
        try
        {
            using var stream = File.OpenRead(path);
            Require(stream.Length <= 16384);
            var bytes = new byte[checked((int)stream.Length)];
            stream.ReadExactly(bytes);
            Require(stream.ReadByte() == -1);
            return new UTF8Encoding(false, true).GetString(bytes);
        }
        catch (Exception error) when (error is IOException or DecoderFallbackException)
        {
            throw new InvalidOperationException("Customer dormant rollout review refused.");
        }
    }

    private static void Rollout(JsonElement rollout)
    {
        Keys(rollout, "minReadySeconds", "preStopDrainSeconds", "terminationGracePeriodSeconds", "maxSurge", "maxUnavailable");
        Require(Integer(rollout, "minReadySeconds") == 90 && Integer(rollout, "preStopDrainSeconds") == 60
            && Integer(rollout, "terminationGracePeriodSeconds") == 90
            && Integer(rollout, "maxSurge") == 1 && Integer(rollout, "maxUnavailable") == 0);
    }

    private static void Keys(JsonElement value, params string[] required)
    {
        Require(value.ValueKind == JsonValueKind.Object);
        var names = value.EnumerateObject().Select(property => property.Name).ToArray();
        Require(names.Length == required.Length && names.Distinct(StringComparer.Ordinal).Count() == names.Length
            && names.ToHashSet(StringComparer.Ordinal).SetEquals(required));
    }

    private static string Text(JsonElement value, string name)
    {
        var item = value.GetProperty(name);
        Require(item.ValueKind == JsonValueKind.String);
        return item.GetString()!;
    }

    private static int Integer(JsonElement value, string name) => StrictInteger(value.GetProperty(name));

    private static int StrictInteger(JsonElement value)
    {
        Require(value.ValueKind == JsonValueKind.Number);
        Require(value.TryGetInt32(out var result));
        return result;
    }

    private static bool IsHex(string? value, int length) => value is not null && value.Length == length && value.All(character => character is >= '0' and <= '9' or >= 'a' and <= 'f');

    private static bool IsDigest(string? value) => value is not null && value.StartsWith("sha256:", StringComparison.Ordinal) && IsHex(value[7..], 64);

    private static bool IsRevision(string? value) => value is not null && value.Length is > 0 and <= 16384 && value[0] is >= '1' and <= '9' && value.All(character => character is >= '0' and <= '9');

    private static void Require([DoesNotReturnIf(false)] bool condition)
    {
        if (!condition)
        {
            throw new InvalidOperationException("Customer dormant rollout review refused.");
        }
    }
}
