"""Pure synthetic backend controls; no Docker/socket/Popen/SDK/provider."""
import copy
import datetime as dt
import io
import json
from pathlib import Path
import tarfile
import tempfile
import unittest
from unittest.mock import patch
import customer_owned_build as b


def frame(raw, stream=1):
    return bytes([stream, 0, 0, 0]) + len(raw).to_bytes(4, "big") + raw


class Backend:
    def __init__(self, test):
        self.test = test; self.calls = []; self.item = None; self.absent = False
        self.fail_create_ack = False; self.exit_code = 0; self.force_running = False
        self.logs = frame(b"Build succeeded.\n    0 Warning(s)\n    0 Error(s)\n\nTime Elapsed 00:00:01.00\n")
        self.bad_daemon = False; self.contradict_absence = False
        self.inspect_hook = None; self.before_delete = None
        self.image_entry = None; self.image_volumes = None
        self.ready = False; self.marker = None
        self.image_digests = [test.stage["sdkImage"]["reference"]]
        self.cleanup_factories = []
        self.create_owned_calls = 0; self.custody_calls = 0
        self.custody_verified = True; self.create_kind = "actual-http-create"
        self.delayed_create_seconds = 0
        self.fail_first_inspect = False

    def create_owned(self, expected):
        self.create_owned_calls += 1
        name, projected = b.make_create_request(self.test.stage, self.test.grant)
        self.test.assertEqual(projected, expected)
        status, raw = self.request("POST", "/containers/create?name=" + name, body=b.encode(expected), headers={"Content-Type": "application/json"})
        self.test.clock += dt.timedelta(seconds=self.delayed_create_seconds)
        spec = {"Id": self.item["Id"], "Created": self.item["Created"], "Image": self.item["Image"], "Name": self.item["Name"],
                "configSha256": b.digest(b.encode(self.item["Config"])), "hostConfigSha256": b.digest(b.encode(self.item["HostConfig"]))}
        return {"receiptKind": self.create_kind, "httpStatus": status if self.create_kind == "actual-http-create" else None,
                "containerId": json.loads(raw)["Id"], "daemonId": self.test.grant["daemonId"],
                "intentSha256": b.digest((self.test.ledger / "create-intent.json").read_bytes()),
                "actualCreatedRaw": self.item["Created"], "createdSpec": spec,
                "expectedConfigDigest": b.digest(b.encode(expected)), "guardianDirectory": "/synthetic-guardian",
                "rootGrantSha256": self.test.grant["rootGrantSha256"], "eventTimeNano": 123456789, "eventSha256": "e" * 64}

    def ensure_custody(self, cid, spec, expected):
        self.custody_calls += 1
        self.test.assertEqual(self.item["Id"], cid)
        return {"verified": self.custody_verified, "containerId": cid, "actualCreatedRaw": spec["Created"],
                "expectedConfigDigest": b.digest(b.encode(expected)), "checkedUtc": self.test.clock.isoformat(),
                "custodyReceiptSha256": "f" * 64}

    def cleanup_backend_factory(self, deadline):
        self.cleanup_factories.append(deadline)
        return self

    def json(self, method, path, *args, **kwargs):
        self.calls.append((method, path))
        t = self.test
        if path == "/info":
            return {"ID": "foreign" if self.bad_daemon else t.grant["daemonId"], "OSType": "linux"}
        if path.startswith("/images/"):
            return {"Id": t.stage["sdkImage"]["imageId"], "Os": "linux", "RepoDigests": self.image_digests,
                    "Config": {"Entrypoint": self.image_entry, "Volumes": self.image_volumes, "Env": ["PATH=/synthetic-sdk"]}}
        if path.startswith("/containers/json?"):
            return [{"Id": "c" * 64}] if self.contradict_absence else []
        raise AssertionError("Unexpected synthetic JSON read " + path)

    def request(self, method, path, body=None, headers=None):
        self.calls.append((method, path))
        t = self.test
        if path.startswith("/containers/create?"):
            t.assertTrue((t.ledger / "create-intent.json").is_file())
            request = json.loads(body)
            config = {key: copy.deepcopy(value) for key, value in request.items() if key != "HostConfig"}
            config["Env"] = ["PATH=/synthetic-sdk", *config["Env"]]
            self.item = {"Id": "c" * 64, "Created": t.clock.isoformat(), "Image": t.stage["sdkImage"]["imageId"],
                         "Name": "/customer-build-" + t.grant["runId"], "Config": config,
                         "HostConfig": copy.deepcopy(request["HostConfig"]), "Mounts": [],
                         "NetworkSettings": {"Ports": None}, "State": {"Running": False, "ExitCode": 0, "OOMKilled": False, "Pid": 0, "StartedAt": "0001-01-01T00:00:00Z"}, "ExecIDs": None}
            if self.fail_create_ack:
                raise TimeoutError("synthetic sensitive provider body")
            return 201, json.dumps({"Id": self.item["Id"]}).encode()
        if path.endswith("/json"):
            if self.fail_first_inspect:
                self.fail_first_inspect = False
                raise TimeoutError("synthetic inspection fault")
            if self.absent: return 404, b'{"message":"not found"}'
            if self.inspect_hook: self.inspect_hook()
            if self.item["State"]["Running"] and self.ready and not self.force_running:
                self.item["State"].update(Running=False, ExitCode=self.exit_code)
            return 200, json.dumps(self.item).encode()
        if "/archive?" in path:
            t.assertTrue(self.item["State"]["Running"])
            t.assertTrue((t.ledger / "created-identity.json").is_file())
            with tarfile.open(fileobj=io.BytesIO(body), mode="r:") as archive:
                rows = archive.getmembers()
                if len(rows) == 1 and rows[0].name == ".control/build.ready":
                    self.marker = archive.extractfile(rows[0]).read()
                    t.assertTrue((t.ledger / "source-verified.json").is_file())
                    t.assertTrue((t.ledger / "admission-ready.json").is_file())
                    self.ready = True
            return 200, b""
        if path.endswith("/start"):
            t.assertTrue((t.ledger / "start-intent.json").is_file())
            t.assertTrue((t.ledger / "admission-start.json").is_file())
            self.item["State"].update(Running=True, Pid=12345, StartedAt=t.clock.isoformat())
            return 204, b""
        if "/logs?" in path: return 200, self.logs
        if "/stop?" in path:
            self.item["State"]["Running"] = False
            return 204, b""
        if "/wait?" in path:
            if self.before_delete: self.before_delete()
            return 200, json.dumps({"StatusCode": self.item["State"]["ExitCode"]}).encode()
        if method == "DELETE":
            t.assertTrue(path.endswith("?force=false&v=false"))
            self.absent = True
            return 204, b""
        raise AssertionError("Unexpected synthetic request " + path)


class BuildControls(unittest.TestCase):
    def setUp(self):
        self.temp = tempfile.TemporaryDirectory(); self.addCleanup(self.temp.cleanup)
        self.ledger = Path(self.temp.name) / "ledger"
        self.clock = dt.datetime(2026, 10, 8, tzinfo=dt.timezone.utc)
        self.mono = 0.0
        self.stage = json.loads((Path(__file__).parent / "build-stage.json").read_text(encoding="utf-8"))
        archive = io.BytesIO()
        with tarfile.open(fileobj=archive, mode="w") as tar:
            row = tarfile.TarInfo("repo/source.txt"); row.size = 5
            tar.addfile(row, io.BytesIO(b"bytes"))
        self.archive = archive.getvalue()
        self.stage["sourceArchive"].update(sha256=b.digest(self.archive), bytes=len(self.archive), rawFiles=1)
        self.grant = {"schemaVersion": 1, "rootGranted": True,
            "rootThread": self.stage["rootThread"], "owner": self.stage["owner"], "stage": self.stage["stage"],
            "runId": "a" * 32, "rootGrantSha256": "d" * 64, "daemonId": "synthetic-build-daemon", "issuedUtc": self.clock.isoformat(),
            "expiresUtc": (self.clock + dt.timedelta(minutes=20)).isoformat(),
            "stageSha256": b.digest(b.encode(self.stage)), "sourceArchiveSha256": b.digest(self.archive), "sourceArchiveBytes": len(self.archive)}
        self.admissions = []
        self.refuse = None; self.verify_result = True
        self.backend = Backend(self)
        self.patches = [patch.object(b, "now", lambda: self.clock), patch.object(b.time, "monotonic", lambda: self.mono),
                        patch.object(b.time, "sleep", self.sleep)]
        for item in self.patches: item.start(); self.addCleanup(item.stop)

    def sleep(self, seconds):
        self.mono += seconds; self.clock += dt.timedelta(seconds=seconds)

    def admission(self, phase, stage, grant):
        self.admissions.append(phase)
        return {"admitted": phase != self.refuse, "trustedRootGrantVerified": True,
                "checkedUtc": self.clock.isoformat(), "freePhysicalKiB": 4194304, "nativeProcesses": [], "rootGrantSha256": self.grant["rootGrantSha256"]}

    def verify(self, backend, cid, stage, grant):
        self.assertTrue(backend.item["State"]["Running"])
        self.assertEqual("c" * 64, cid)
        return self.verify_result

    def run_build(self):
        owner = b.OwnedBuild(self.backend, self.stage, self.grant, self.ledger, self.admission)
        return owner.run(self.archive, self.verify)

    def mutations(self):
        return [x for x in self.backend.calls if x[0] != "GET"]

    def test_success_build_gate_and_exact_cleanup(self):
        result = self.run_build()
        self.assertTrue(result["buildAccepted"])
        self.assertTrue(result["cleanupVerified"])
        self.assertEqual([], result["ownedResources"])
        self.assertEqual(["create", "start", "ready"], self.admissions)
        self.assertEqual(0, result["actualExitCode"])
        self.assertTrue((self.ledger / "build.log").exists())

    def test_no_root_grant_no_container_create(self):
        self.grant["rootGranted"] = False
        result = self.run_build()
        self.assertFalse(result["buildAccepted"])
        self.assertEqual([], self.mutations())

    def test_create_admission_refusal_no_allocation(self):
        self.refuse = "create"
        result = self.run_build()
        self.assertEqual([], self.mutations())
        self.assertTrue(result["cleanupVerified"])

    def test_start_admission_refusal_cleans_stopped_sdk(self):
        self.refuse = "start"
        result = self.run_build()
        self.assertFalse(result["sdkStarted"])
        self.assertTrue(result["cleanupVerified"])
        self.assertFalse(any(path.endswith("/start") for _, path in self.mutations()))

    def test_source_verification_refusal_prevents_build_release(self):
        self.verify_result = False
        result = self.run_build()
        self.assertTrue(result["sdkStarted"])
        self.assertFalse(result.get("buildReleased", False))
        self.assertTrue(result["cleanupVerified"])

    def test_lost_create_ack_retains_unresolved_intent_no_inventory_adoption(self):
        self.backend.fail_create_ack = True
        result = self.run_build()
        self.assertTrue(result["createIntentUnresolved"])
        self.assertFalse(result["cleanupVerified"])
        self.assertEqual(1, len(self.mutations()))
        self.assertFalse(any('/containers/json?' in p for _, p in self.backend.calls))
        self.assertNotIn("synthetic sensitive provider body", (self.ledger / "result.json").read_text())

    def test_nonzero_exit_rejected_but_sdk_removed(self):
        self.backend.exit_code = 1
        result = self.run_build()
        self.assertFalse(result["buildAccepted"])
        self.assertEqual(1, result["actualExitCode"])
        self.assertTrue(result["cleanupVerified"])

    def test_warning_final_summary_rejected(self):
        self.backend.logs = frame(b"Build succeeded.\n1 Warning(s)\n0 Error(s)\nTime Elapsed 00:01:00\n")
        result = self.run_build()
        self.assertFalse(result["buildAccepted"])
        self.assertTrue(result["cleanupVerified"])

    def test_nonfinal_zero_summary_rejected(self):
        self.backend.logs += frame(b"unexpected trailing failure\n", 2)
        self.assertFalse(self.run_build()["buildAccepted"])

    def test_truncated_mux_log_rejected(self):
        self.backend.logs = self.backend.logs[:-1]
        self.assertFalse(self.run_build()["buildAccepted"])

    def test_wrong_image_default_entrypoint_prevents_create(self):
        self.backend.image_entry = ["foreign"]
        self.assertEqual(False, self.run_build()["buildAccepted"])
        self.assertEqual([], self.mutations())

    def test_wrong_image_volumes_prevents_create(self):
        self.backend.image_volumes = {"/persistent": {}}
        self.run_build(); self.assertEqual([], self.mutations())

    def test_dangerous_stage_host_caps_refused_before_create(self):
        self.stage["hostConfig"]["Privileged"] = True
        self.grant["stageSha256"] = b.digest(b.encode(self.stage))
        self.run_build(); self.assertEqual([], self.mutations())

    def test_archive_digest_drift_refused_before_create(self):
        self.grant["sourceArchiveSha256"] = "0" * 64
        self.run_build(); self.assertEqual([], self.mutations())

    def test_caps_drift_before_delete_retains_exact_owned_sdk(self):
        self.backend.before_delete = lambda: self.backend.item["HostConfig"].update(Memory=1)
        result = self.run_build()
        self.assertFalse(result["cleanupVerified"])
        self.assertFalse(self.backend.absent)
        self.assertEqual("c" * 64, result["ownedResources"][0]["Id"])

    def test_running_restart_before_delete_is_not_deleted(self):
        self.backend.force_running = True
        # Refuse source staging to enter cleanup before actual SDK start.
        self.verify_result = False
        self.backend.before_delete = lambda: self.backend.item["State"].update(Running=True)
        result = self.run_build()
        self.assertFalse(result["cleanupVerified"])
        self.assertFalse(self.backend.absent)

    def test_inspect404_inventory_contradiction_is_not_cleanup_success(self):
        self.backend.contradict_absence = True
        result = self.run_build()
        self.assertFalse(result["cleanupVerified"])

    def test_finite_build_timeout_stops_only_owned_sdk(self):
        self.backend.force_running = True
        def jump():
            if self.backend.item["State"]["Running"] and self.backend.ready:
                self.mono += 601
                self.backend.inspect_hook = None
        self.backend.inspect_hook = jump
        result = self.run_build()
        self.assertFalse(result["buildAccepted"])
        self.assertTrue(result["cleanupVerified"])
        self.assertTrue(any('/stop?t=10' in p for _, p in self.mutations()))

    def test_ready_admission_refusal_does_not_publish_marker(self):
        self.refuse = "ready"
        result = self.run_build()
        self.assertTrue(result["sdkStarted"])
        self.assertFalse(result.get("buildReleased", False))
        self.assertIsNone(self.backend.marker)
        self.assertTrue(result["cleanupVerified"])

    def test_ready_marker_binds_exact_root_digest_source_and_run(self):
        result = self.run_build()
        token = b.digest((self.grant["rootGrantSha256"] + self.grant["sourceArchiveSha256"] + self.grant["runId"]).encode())
        self.assertEqual((token + "\n").encode(), self.backend.marker)
        cmd = self.backend.item["Config"]["Cmd"]
        self.assertEqual(["/bin/sh", "-c", self.stage["supervisorScript"], "customer-build-supervisor"], cmd[:4])
        self.assertEqual("/", self.backend.item["Config"]["WorkingDir"])
        self.assertEqual([token, "/work/repo", *self.stage["command"]], cmd[6:])
        self.assertTrue(result["buildAccepted"])

    def test_oom_killed_exit_zero_is_rejected(self):
        def oom():
            if self.backend.ready: self.backend.item["State"]["OOMKilled"] = True
        self.backend.inspect_hook = oom
        result = self.run_build()
        self.assertFalse(result["buildAccepted"])
        self.assertTrue(result["oomKilled"])
        self.assertTrue(result["cleanupVerified"])

    def test_create_intent_write_failure_never_allocates(self):
        original = b.atomic_new
        def write(path, row):
            if Path(path).name == "create-intent.json": raise OSError("synthetic durability fault")
            original(path, row)
        with patch.object(b, "atomic_new", write): result = self.run_build()
        self.assertEqual([], self.mutations())
        self.assertTrue(result["cleanupVerified"])

    def test_created_identity_write_failure_recovers_only_durable_response(self):
        original = b.atomic_new
        def write(path, row):
            if Path(path).name == "created-identity.json": raise OSError("synthetic durability fault")
            original(path, row)
        with patch.object(b, "atomic_new", write): result = self.run_build()
        self.assertTrue(result["cleanupVerified"])
        self.assertFalse(result["sdkStarted"])
        self.assertEqual(3, len(self.mutations()))
        self.assertFalse(result["createIntentUnresolved"])
        self.assertTrue(result["identityRecoveredFromDurableCreateReceipt"])
        self.assertTrue((self.ledger / "recovered-created-identity.json").is_file())

    def test_root_digest_admission_drift_refuses_create(self):
        original = self.admission
        def admission(phase, stage, grant):
            evidence = original(phase, stage, grant)
            evidence["rootGrantSha256"] = "e" * 64
            return evidence
        self.admission = admission
        self.run_build(); self.assertEqual([], self.mutations())

    def test_memory_floor_not_lowered_by_callback(self):
        original = self.admission
        def admission(phase, stage, grant):
            evidence = original(phase, stage, grant)
            evidence["freePhysicalKiB"] = 4194303
            return evidence
        self.admission = admission
        self.run_build(); self.assertEqual([], self.mutations())

    def test_caps_drift_during_start_admission_prevents_start(self):
        original = self.admission
        def admission(phase, stage, grant):
            evidence = original(phase, stage, grant)
            if phase == "start": self.backend.item["HostConfig"]["Memory"] = 1
            return evidence
        self.admission = admission
        result = self.run_build()
        self.assertFalse(result["sdkStarted"])
        self.assertFalse(any(p.endswith("/start") for _, p in self.mutations()))

    def test_caps_drift_during_ready_admission_prevents_marker(self):
        original = self.admission
        def admission(phase, stage, grant):
            evidence = original(phase, stage, grant)
            if phase == "ready": self.backend.item["HostConfig"]["Memory"] = 1
            return evidence
        self.admission = admission
        result = self.run_build()
        self.assertTrue(result["sdkStarted"])
        self.assertFalse(result.get("buildReleased", False))
        self.assertIsNone(self.backend.marker)

    def test_raw_graph_drift_during_ready_admission_prevents_marker(self):
        original = self.admission
        def admission(phase, stage, grant):
            evidence = original(phase, stage, grant)
            if phase == "ready": self.verify_result = False
            return evidence
        self.admission = admission
        result = self.run_build()
        self.assertTrue(result["cleanupVerified"])
        self.assertIsNone(self.backend.marker)

    def test_live_sdk_pid_and_raw_started_timestamp_are_retained(self):
        self.run_build()
        observation = json.loads((self.ledger / "started-observation.json").read_text())
        self.assertEqual(12345, observation["pid"])
        self.assertEqual(self.clock.isoformat(), observation["startedAtRaw"])
        self.assertEqual("c" * 64, observation["containerId"])
        self.assertEqual("/bin/sh", observation["configuredExecutable"])
        self.assertIsNone(observation["actualExecutable"])

    def test_active_unknown_exec_prevents_cleanup_stop_and_delete(self):
        original = self.verify
        def verify(backend, cid, stage, grant):
            original(backend, cid, stage, grant)
            backend.item["ExecIDs"] = ["e" * 64]
            return False
        self.verify = verify
        result = self.run_build()
        self.assertFalse(result["cleanupVerified"])
        self.assertFalse(any('/stop?' in p or m == "DELETE" for m, p in self.mutations()))
        self.assertTrue(self.backend.item["State"]["Running"])

    def test_missing_repo_digest_mapping_prevents_create(self):
        self.backend.image_digests = []
        self.run_build()
        self.assertEqual([], self.mutations())

    def test_foreign_repo_digest_mapping_prevents_create(self):
        self.backend.image_digests = ["foreign/sdk@sha256:" + "e" * 64]
        self.run_build()
        self.assertEqual([], self.mutations())

    def test_required_custody_refusal_prevents_start_and_cleans_known_sdk(self):
        self.backend.custody_verified = False
        result = self.run_build()
        self.assertFalse(result["sdkStarted"])
        self.assertTrue(result["cleanupVerified"])
        self.assertFalse(any(p.endswith("/start") for _, p in self.mutations()))

    def test_required_guardian_has_no_direct_create_fallback(self):
        self.backend.create_owned = None
        result = self.run_build()
        self.assertFalse(result["buildAccepted"])
        self.assertEqual([], self.mutations())

    def test_required_guardian_missing_custody_hook_refuses_start(self):
        self.backend.ensure_custody = None
        result = self.run_build()
        self.assertFalse(result["sdkStarted"])
        self.assertTrue(result["cleanupVerified"])

    def test_delayed_actual201_after_root_expiry_is_cleanup_only(self):
        self.backend.delayed_create_seconds = 1201
        result = self.run_build()
        self.assertFalse(result["sdkStarted"])
        self.assertTrue(result["cleanupVerified"])
        receipt = json.loads((self.ledger / "create-response.json").read_text())
        self.assertEqual(201, receipt["httpStatus"])
        self.assertEqual("actual-http-create", receipt["receiptKind"])

    def test_event_receipt_never_synthesizes_http201(self):
        self.backend.create_kind = "actual-daemon-create-event"
        result = self.run_build()
        self.assertTrue(result["buildAccepted"])
        receipt = json.loads((self.ledger / "create-response.json").read_text())
        self.assertIsNone(receipt["httpStatus"])
        self.assertEqual("actual-daemon-create-event", receipt["receiptKind"])

    def test_create_projection_is_independent_and_same_request_bytes(self):
        name, expected = b.make_create_request(self.stage, self.grant)
        self.assertEqual("customer-build-" + self.grant["runId"], name)
        before = b.encode(expected)
        self.run_build()
        self.assertEqual(before, b.encode({key: value for key, value in self.backend.item["Config"].items() if key != "Env"} | {"Env": self.stage["environment"], "HostConfig": self.backend.item["HostConfig"]}))
        expected["HostConfig"]["Memory"] = 1
        self.assertEqual(2147483648, self.stage["hostConfig"]["Memory"])

    def test_transient_first_inspection_failure_recovers_durable_create_for_cleanup(self):
        self.backend.fail_first_inspect = True
        result = self.run_build()
        self.assertFalse(result["sdkStarted"])
        self.assertTrue(result["cleanupVerified"])
        self.assertTrue(result["identityRecoveredFromDurableCreateReceipt"])
        self.assertEqual(3, len(self.mutations()))

    def test_response_receipt_durability_failure_does_not_adopt_target(self):
        original = b.atomic_new
        def write(path, row):
            if Path(path).name == "create-response.json": raise OSError("synthetic receipt durability fault")
            original(path, row)
        with patch.object(b, "atomic_new", write): result = self.run_build()
        self.assertFalse(result["cleanupVerified"])
        self.assertTrue(result["createIntentUnresolved"])
        self.assertEqual(1, len(self.mutations()))
        self.assertFalse(any('/containers/json?' in p for _, p in self.backend.calls))

    def test_event_receipt_cannot_claim_actual_http201(self):
        original = self.backend.create_owned
        def create(expected):
            receipt = original(expected)
            receipt.update(receiptKind="actual-daemon-create-event", httpStatus=201)
            return receipt
        self.backend.create_owned = create
        result = self.run_build()
        self.assertFalse(result["sdkStarted"])
        self.assertFalse(result["cleanupVerified"])
        self.assertEqual(1, len(self.mutations()))

    def test_guardian_loss_after_start_prevents_ready_release_and_primary_cleans(self):
        original = self.admission
        def admission(phase, stage, grant):
            evidence = original(phase, stage, grant)
            if phase == "ready": self.backend.custody_verified = False
            return evidence
        self.admission = admission
        result = self.run_build()
        self.assertTrue(result["sdkStarted"])
        self.assertFalse(result.get("buildReleased", False))
        self.assertIsNone(self.backend.marker)
        self.assertEqual(2, self.backend.custody_calls)
        self.assertTrue(result["cleanupVerified"])
        self.assertEqual([], result["ownedResources"])
        self.assertEqual(["/containers/" + "c" * 64 + "?force=false&v=false"],
                         [p for m, p in self.mutations() if m == "DELETE"])

    def test_stage_seal_drift_refused(self):
        self.stage["workingDirectory"] = "/foreign"
        self.run_build(); self.assertEqual([], self.mutations())

    def test_expired_grant_no_create(self):
        self.clock += dt.timedelta(minutes=21)
        self.run_build(); self.assertEqual([], self.mutations())

    def test_readonly_archive_traversal_rejected(self):
        archive = io.BytesIO()
        with tarfile.open(fileobj=archive, mode="w") as tar:
            row = tarfile.TarInfo("../foreign"); row.size = 1
            tar.addfile(row, io.BytesIO(b"x"))
        with self.assertRaises(b.BuildError): b.validate_archive(archive.getvalue())

    def test_readonly_archive_link_rejected(self):
        archive = io.BytesIO()
        with tarfile.open(fileobj=archive, mode="w") as tar:
            row = tarfile.TarInfo("repo/link"); row.type = tarfile.SYMTYPE; row.linkname = "/foreign"
            tar.addfile(row)
        with self.assertRaises(b.BuildError): b.validate_archive(archive.getvalue())


if __name__ == "__main__": unittest.main()
