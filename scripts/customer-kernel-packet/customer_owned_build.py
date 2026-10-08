"""One explicitly granted Customer BUILD-only SDK; no runtime/provider authority.

Backend request returns (actual status, bounded bytes); backend.json is read-only.
Cleanup may use backend.cleanup_backend_factory(monotonic_deadline).
"""
from __future__ import annotations
import datetime as dt
import hashlib
import io
import json
import os
from pathlib import Path, PurePosixPath
import re
import stat
import tarfile
import time
from urllib.parse import quote

MAX_BYTES = 8 * 1024 * 1024
IDS = re.compile(r"[a-f0-9]{64}\Z")


class BuildError(ValueError):
    pass


def need(value, message):
    if not value:
        raise BuildError(message)


def utc(value):
    result = dt.datetime.fromisoformat(value.replace("Z", "+00:00"))
    need(result.tzinfo is not None, "Timezone required")
    return result.astimezone(dt.timezone.utc)


def now():
    return dt.datetime.now(dt.timezone.utc)


def encode(value):
    return json.dumps(value, sort_keys=True, separators=(",", ":"), allow_nan=False).encode()


def digest(raw):
    return hashlib.sha256(raw).hexdigest()


def atomic_new(path, row):
    raw = encode(row)
    need(len(raw) <= 65536, "Scalar receipt bound")
    target = Path(path)
    temp = target.parent / (".pending-" + os.urandom(16).hex())
    try:
        with temp.open("xb") as stream:
            stream.write(raw); stream.flush(); os.fsync(stream.fileno())
        os.link(temp, target)
        if os.name != "nt":
            fd = os.open(target.parent, os.O_RDONLY | getattr(os, "O_DIRECTORY", 0))
            try: os.fsync(fd)
            finally: os.close(fd)
    finally:
        temp.unlink(missing_ok=True)


def reject_links(path):
    for part in (Path(path), *Path(path).parents):
        if part.exists() or part.is_symlink():
            meta = part.lstat()
            need(not stat.S_ISLNK(meta.st_mode) and not getattr(meta, "st_file_attributes", 0) & 0x400,
                 "Ledger reparse/link forbidden")


def validate_archive(raw):
    need(isinstance(raw, bytes) and 0 < len(raw) <= MAX_BYTES, "Source archive bound")
    seen = set(); expanded = 0
    with tarfile.open(fileobj=io.BytesIO(raw), mode="r:") as archive:
        for index, member in enumerate(archive):
            need(index < 512, "Source archive member bound")
            name = member.name.rstrip("/") if member.isdir() else member.name
            need(name and not name.startswith("/") and "\\" not in name and ":" not in name
                 and str(PurePosixPath(name)) == name
                 and all(x.casefold() not in ("", ".", "..", ".git") for x in name.split("/"))
                 and not name.startswith(".control"), "Archive path rejected")
            need(name not in seen and (member.isfile() or member.isdir()), "Archive duplicate/link rejected")
            seen.add(name); expanded += member.size
            need(0 <= member.size <= MAX_BYTES and expanded <= MAX_BYTES, "Expanded archive bound")
    need(seen, "Empty source archive")


def demux(raw):
    need(len(raw) <= MAX_BYTES, "Docker log byte cap")
    pos = 0; output = []
    while pos < len(raw):
        need(len(raw) - pos >= 8, "Truncated Docker log frame")
        header = raw[pos:pos+8]
        need(header[0] in (1, 2) and header[1:4] == b"\0\0\0", "Invalid Docker log frame")
        size = int.from_bytes(header[4:], "big"); pos += 8
        need(size <= len(raw) - pos, "Truncated Docker log payload")
        output.append(raw[pos:pos+size]); pos += size
    return b"".join(output)


def make_create_request(stage, grant):
    """Return (name, request) with the exact sealed supervisor/config bytes.

    This pure projection grants no execution authority; callers must verify
    Root admission and seals before allocating anything.
    """
    token = digest((grant["rootGrantSha256"] + grant["sourceArchiveSha256"] + grant["runId"]).encode("ascii"))
    cmd = ["/bin/sh", "-c", stage["supervisorScript"], "customer-build-supervisor",
           str(int(utc(grant["expiresUtc"]).timestamp())), str(stage["phaseSeconds"]),
           token, stage["workingDirectory"], *stage["command"]]
    labels = {"codexrun.run": grant["runId"], "codexrun.owner": stage["owner"],
              "codexrun.expiry": grant["expiresUtc"], "codexrun.disposable": "true"}
    request = {"Image": stage["sdkImage"]["reference"], "Cmd": cmd, "Entrypoint": None,
               "WorkingDir": stage["containerWorkingDirectory"], "Env": stage["environment"],
               "Labels": labels, "Tty": False, "HostConfig": stage["hostConfig"]}
    return "customer-build-" + grant["runId"], json.loads(encode(request))


class OwnedBuild:
    def __init__(self, backend, stage, grant, ledger, admission_callback):
        self.backend, self.stage, self.grant = backend, stage, grant
        self.admission_callback = admission_callback
        self.cleanup_deadline = None
        self.ledger = Path(ledger)
        reject_links(self.ledger)
        need(not self.ledger.exists(), "Fresh build ledger required")
        self.ledger.mkdir(parents=True, mode=0o700)
        self.cid = None; self.spec = None; self.expected = None; self.name = None; self.image_env = None
        self.report = {"schemaVersion": 1, "stage": stage.get("stage"), "sdkStarted": False,
                       "buildAccepted": False, "cleanupVerified": False, "createIntentUnresolved": False,
                       "behavioralRed": False, "scope": "BUILD-only", "providersStarted": 0,
                       "errors": [], "ownedResources": [], "actualExitCode": None}

    def save(self, name, row):
        atomic_new(self.ledger / (name + ".json"), row)

    def request(self, backend, method, path, body=None, headers=None):
        need(body is None or isinstance(body, bytes) and len(body) <= MAX_BYTES, "Backend request body cap")
        if self.cleanup_deadline is not None:
            need(time.monotonic() < self.cleanup_deadline, "Cleanup deadline expired")
        status, raw = backend.request(method, path, body=body, headers=headers)
        need(type(status) is int and 100 <= status <= 599 and isinstance(raw, bytes)
             and len(raw) <= MAX_BYTES, "Bounded actual backend response required")
        return status, raw

    def daemon(self, backend):
        if self.cleanup_deadline is not None:
            need(time.monotonic() < self.cleanup_deadline, "Cleanup deadline expired")
        info = backend.json("GET", "/info")
        need(info.get("ID") == self.grant["daemonId"] and info.get("OSType") == "linux", "Exact healthy Linux daemon required")

    def validate_grant(self):
        s, g = self.stage, self.grant
        need(s.get("schemaVersion") == 1 and g.get("schemaVersion") == 1
             and g.get("rootGranted") is True, "External native grant required")
        for key in ("rootThread", "owner", "stage"):
            need(g.get(key) == s.get(key), "Grant authority/stage drift")
        need(re.fullmatch(r"[a-f0-9]{32}", g.get("runId", ""))
             and g.get("stageSha256") == digest(encode(s)), "Run/stage seal invalid")
        issued, expiry = utc(g["issuedUtc"]), utc(g["expiresUtc"])
        need(issued <= now() < expiry and expiry <= issued + dt.timedelta(seconds=s["maxGrantSeconds"]), "Finite fresh grant required")
        need((expiry - now()).total_seconds() >= s["minimumRemainingSeconds"], "Insufficient build lease")
        need(s["phaseSeconds"] == 600 and s["cleanupSeconds"] == 120 and s["sdkRawSocketPermitted"] is False
             and s["persistentDataPermitted"] is False and all(s[k] == [] for k in
             ("providersPermitted", "portsPermitted", "volumesPermitted", "bindsPermitted")), "BUILD-only scope required")
        need(re.fullmatch(r"[a-f0-9]{64}", g.get("rootGrantSha256", "")), "Trusted Root grant digest required")
        need(g["sourceArchiveSha256"] == s["sourceArchive"]["sha256"]
             and g["sourceArchiveBytes"] == s["sourceArchive"]["bytes"], "Stage/grant archive binding differs")
        need(s["containerWorkingDirectory"] == "/" and s["supervisorReadinessPath"] == "/work/.control/build.ready"
             and digest(s["supervisorScript"].encode()) == s["supervisorScriptSha256"], "Supervisor seal/path differs")
        need(s["command"][:2] == ["dotnet", "build"] and "/warnaserror" in s["command"], "Strict build command required")
        host = s["hostConfig"]
        need(host["Memory"] == 2147483648 and host["MemorySwap"] == host["Memory"]
             and host["NanoCpus"] == 1000000000 and host["PidsLimit"] == 256
             and host["NetworkMode"] == "bridge" and host["PortBindings"] == {}
             and host["Privileged"] is False and host["ReadonlyRootfs"] is True
             and host["AutoRemove"] is False and host["CapDrop"] == ["ALL"]
             and host["SecurityOpt"] == ["no-new-privileges:true"] and host["PidMode"] == ""
             and host["IpcMode"] == "private" and host["PublishAllPorts"] is False
             and host["Tmpfs"] == {"/work": "rw,nosuid,nodev,size=1610612736", "/tmp": "rw,noexec,nosuid,nodev,size=67108864"}
             and host["LogConfig"] == {"Type": "json-file", "Config": {"max-size": "1m", "max-file": "2"}}, "Exact build caps required before allocation")
        for key in ("Binds", "Mounts", "Devices", "DeviceRequests", "CapAdd", "Links", "VolumesFrom"):
            need(host.get(key) in (None, []), "Stage elevated/mounted access forbidden")

    def admit(self, phase):
        evidence = self.admission_callback(phase, self.stage, self.grant)
        need(isinstance(evidence, dict) and evidence.get("admitted") is True
             and evidence.get("trustedRootGrantVerified") is True
             and evidence.get("rootGrantSha256") == self.grant["rootGrantSha256"], "Trusted Root admission refused")
        age = (now() - utc(evidence["checkedUtc"])).total_seconds()
        need(0 <= age <= self.stage["maxEvidenceAgeSeconds"]
             and type(evidence.get("freePhysicalKiB")) is int
             and evidence["freePhysicalKiB"] >= self.stage["minimumPhysicalKiB"]
             and evidence.get("nativeProcesses") == [], "Fresh resource admission required")
        self.save("admission-" + phase, {"phase": phase, "checkedUtc": evidence["checkedUtc"],
                  "freePhysicalKiB": evidence["freePhysicalKiB"], "nativeProcessCount": 0,
                  "trustedRootGrantVerified": True})

    def image(self):
        image = self.stage["sdkImage"]
        need(re.fullmatch(r"sha256:[a-f0-9]{64}", image["imageId"])
             and "@sha256:" in image["reference"], "Immutable SDK image required")
        item = self.backend.json("GET", "/images/" + quote(image["imageId"], safe="") + "/json")
        need(isinstance(item.get("RepoDigests"), list) and image["reference"] in item["RepoDigests"],
             "Actual SDK pinned reference mapping required")
        need(item.get("Id") == image["imageId"] and item.get("Os") == "linux"
             and item.get("Config", {}).get("Entrypoint") is None
             and not item.get("Config", {}).get("Volumes"), "Actual SDK image/default entrypoint/volumes drift")

        defaults = item.get("Config", {}).get("Env") or []
        need(isinstance(defaults, list) and len(defaults) <= 128
             and all(isinstance(v, str) and "=" in v and len(v) <= 4096 for v in defaults), "Bounded SDK image environment required")
        self.image_env = dict(v.split("=", 1) for v in defaults)
        need(len(self.image_env) == len(defaults), "Duplicate SDK image environment")

    def validate_identity(self, item, exact_created=False):
        need(isinstance(item, dict) and item.get("Id") == self.cid and IDS.fullmatch(self.cid or ""), "Exact SDK ID required")
        need(item.get("Image") == self.stage["sdkImage"]["imageId"] and item.get("Name") == "/" + self.name,
             "SDK image/name drift")
        created = item.get("Created")
        need(isinstance(created, str) and utc(self.grant["issuedUtc"]) <= utc(created) <= utc(self.grant["expiresUtc"]), "SDK creation window invalid")
        if exact_created:
            need(self.spec is not None and created == self.spec["Created"], "Raw SDK Created drift")
        config, host = item.get("Config") or {}, item.get("HostConfig") or {}
        for key in ("Image", "Cmd", "Entrypoint", "WorkingDir", "Labels", "Tty"):
            need(key in config and config[key] == self.expected[key], "SDK config drift: " + key)
        env = config.get("Env")
        need(isinstance(env, list) and len(env) <= 128 and all(isinstance(v, str) and "=" in v and len(v) <= 4096 for v in env), "Bounded SDK environment required")
        actual_env = dict(v.split("=", 1) for v in env)
        expected_env = dict(self.image_env)
        expected_env.update(v.split("=", 1) for v in self.expected["Env"])
        need(len(actual_env) == len(env) and actual_env == expected_env, "SDK environment drift")
        for key, value in self.expected["HostConfig"].items():
            need(key in host and host[key] == value, "SDK host cap drift: " + key)
        need(host["Memory"] == 2147483648 and host["MemorySwap"] == host["Memory"]
             and host["NanoCpus"] > 0 and host["PidsLimit"] > 0
             and host["Privileged"] is False and host["ReadonlyRootfs"] is True
             and host["AutoRemove"] is False, "SDK finite caps invalid")
        for key in ("Binds", "Mounts", "Devices", "DeviceRequests", "CapAdd", "Links", "VolumesFrom"):
            need(host.get(key) in (None, []), "Unexpected SDK host access: " + key)
        need(not config.get("Volumes") and not config.get("ExposedPorts") and host["PortBindings"] == {}, "SDK ports/volumes forbidden")
        mounts = item.get("Mounts")
        need(isinstance(mounts, list) and all(m.get("Type") == "tmpfs" and m.get("Destination") in host["Tmpfs"] for m in mounts), "Unexpected SDK mount")
        need(len({m.get("Destination") for m in mounts}) == len(mounts), "Duplicate SDK mount")
        ports = item.get("NetworkSettings", {}).get("Ports")
        need(ports in (None, {}), "SDK published ports forbidden")
        return item

    def inspect(self, backend):
        self.daemon(backend)
        status, raw = self.request(backend, "GET", "/containers/" + self.cid + "/json")
        if status == 404:
            filters = quote(json.dumps({"id": [self.cid]}, separators=(",", ":")), safe="")
            self.daemon(backend)
            rows = backend.json("GET", "/containers/json?all=true&filters=" + filters)
            need(isinstance(rows, list) and len(rows) <= 64
                 and all(isinstance(x, dict) and IDS.fullmatch(x.get("Id", "")) for x in rows)
                 and all(x["Id"] != self.cid for x in rows), "Exact SDK absence unproved")
            return None
        need(status == 200, "SDK inspect refused")
        item = json.loads(raw)
        return self.validate_identity(item, exact_created=self.spec is not None)

    def spec_for(self, item):
        return {"Id": item["Id"], "Created": item["Created"], "Image": item["Image"], "Name": item["Name"],
                "configSha256": digest(encode(item["Config"])), "hostConfigSha256": digest(encode(item["HostConfig"]))}

    def validate_create_receipt(self, row):
        need(isinstance(row, dict) and len(encode(row)) <= 65536
             and row.get("containerId") == self.cid and IDS.fullmatch(self.cid or ""), "Exact bounded CREATE receipt required")
        need(row.get("daemonId") == self.grant["daemonId"]
             and row.get("expectedConfigDigest") == digest(encode(self.expected)), "CREATE receipt domain/config drift")
        if self.stage.get("hostRecoveryGuardian", {}).get("required") is True:
            need(row.get("rootGrantSha256") == self.grant["rootGrantSha256"], "Guardian CREATE Root seal drift")
        kind = row.get("receiptKind")
        if kind == "actual-http-create":
            need(row.get("httpStatus") == 201, "Actual201 CREATE receipt required")
        else:
            need(kind == "actual-daemon-create-event" and row.get("httpStatus") is None
                 and self.stage.get("hostRecoveryGuardian", {}).get("required") is True,
                 "No synthetic201 or inventory CREATE adoption")
            need(isinstance(row.get("createdSpec"), dict) and isinstance(row.get("actualCreatedRaw"), str)
                 and re.fullmatch(r"[a-f0-9]{64}", row.get("intentSha256", ""))
                 and isinstance(row.get("guardianDirectory"), str) and row["guardianDirectory"]
                 and type(row.get("eventTimeNano")) is int and row["eventTimeNano"] > 0
                 and re.fullmatch(r"[a-f0-9]{64}", row.get("eventSha256", "")),
                 "Verified daemon CREATE event custody receipt required")
        return row

    def recover_identity(self, item):
        # Only a durable exact CREATE response/event may recover the missing
        # identity receipt. Never search inventory or names for a candidate ID.
        path = self.ledger / "create-response.json"
        reject_links(path)
        need(path.is_file() and path.stat().st_size <= 65536, "Durable CREATE receipt required for recovery")
        with path.open("rb") as stream:
            raw = stream.read(65537)
        need(len(raw) <= 65536, "CREATE receipt read bound")
        row = self.validate_create_receipt(json.loads(raw))
        self.validate_identity(item)
        if row.get("actualCreatedRaw") is not None:
            need(row["actualCreatedRaw"] == item["Created"], "Recovered raw Created continuity")
        spec = self.spec_for(item)
        if row.get("createdSpec") is not None:
            need(row["createdSpec"] == spec, "Guardian CREATE spec continuity")
        self.save("recovered-created-identity", dict(spec, createReceiptSha256=digest(raw)))
        self.spec = spec
        self.report["createIntentUnresolved"] = False
        self.report["ownedResources"] = [dict(spec, identityVerified=True)]
        self.report["identityRecoveredFromDurableCreateReceipt"] = True

    def ensure_custody(self, phase="start"):
        if self.stage.get("hostRecoveryGuardian", {}).get("required") is not True:
            return
        callback = getattr(self.backend, "ensure_custody", None)
        need(callable(callback), "Independent guardian custody hook required")
        proof = callback(self.cid, json.loads(encode(self.spec)), json.loads(encode(self.expected)))
        need(isinstance(proof, dict) and proof.get("verified") is True
             and proof.get("containerId") == self.cid and proof.get("actualCreatedRaw") == self.spec["Created"]
             and proof.get("expectedConfigDigest") == digest(encode(self.expected))
             and re.fullmatch(r"[a-f0-9]{64}", proof.get("custodyReceiptSha256", "")), "Exact independent custody proof required")
        age = (now() - utc(proof["checkedUtc"])).total_seconds()
        need(0 <= age <= self.stage["maxEvidenceAgeSeconds"], "Fresh guardian custody required")
        self.save("guardian-custody" if phase == "start" else "guardian-custody-ready", dict(proof, phase=phase))
        self.report["guardianCustodyVerified"] = True

    def cleanup(self):
        deadline = time.monotonic() + self.stage["cleanupSeconds"]
        self.cleanup_deadline = deadline
        factory = getattr(self.backend, "cleanup_backend_factory", None)
        backend = factory(deadline) if callable(factory) else self.backend
        item = self.inspect(backend)
        if item is None:
            need(self.spec is not None, "Cannot certify lost-create acknowledgement")
            self.report["cleanupVerified"] = True; self.report["ownedResources"] = []
            return
        if self.spec is None:
            self.recover_identity(item)
        if item["State"].get("Running") is True:
            need(time.monotonic() < deadline, "Cleanup deadline expired")
            item = self.inspect(backend)
            need(item is not None and item.get("ExecIDs") in (None, []), "SDK missing or active exec before stop")
            status, raw = self.request(backend, "POST", "/containers/" + self.cid + "/stop?t=10")
            self.save("cleanup-stop", {"status": status})
            need(status in (204, 304) and raw == b"", "Actual SDK stop refused")
        item = self.inspect(backend)
        need(item is not None and item["State"].get("Running") is False and item.get("ExecIDs") in (None, []), "SDK exit/exec activity unproved")
        status, raw = self.request(backend, "POST", "/containers/" + self.cid + "/wait?condition=not-running")
        result = json.loads(raw)
        self.save("cleanup-wait", {"status": status, "exitCode": result.get("StatusCode")})
        need(status == 200 and type(result.get("StatusCode")) is int and not result.get("Error")
             and type(item["State"].get("ExitCode")) is int
             and item["State"]["ExitCode"] == result["StatusCode"], "SDK wait refused")
        item = self.inspect(backend)
        need(item is not None and item["State"].get("Running") is False and item.get("ExecIDs") in (None, []), "SDK active before delete")
        need(time.monotonic() < deadline, "Cleanup deadline expired")
        status, raw = self.request(backend, "DELETE", "/containers/" + self.cid + "?force=false&v=false")
        self.save("cleanup-delete", {"status": status})
        need(status in (204, 404) and (status != 204 or raw == b""), "SDK delete refused")
        need(self.inspect(backend) is None, "SDK removal unproved")
        self.report["cleanupVerified"] = True; self.report["ownedResources"] = []

    def run(self, source_archive: bytes, verify_stage_callback):
        try:
            self.validate_grant(); validate_archive(source_archive)
            need(digest(source_archive) == self.grant["sourceArchiveSha256"]
                 and len(source_archive) == self.grant["sourceArchiveBytes"], "Granted raw source archive differs")
            self.daemon(self.backend); self.image(); self.admit("create")
            self.name, self.expected = make_create_request(self.stage, self.grant)
            supervisor_cmd = self.expected["Cmd"]
            ready_token = supervisor_cmd[6]
            self.save("create-intent", {"schemaVersion": 1, "runId": self.grant["runId"], "daemonId": self.grant["daemonId"],
                      "issuedUtc": now().isoformat(), "name": self.name, "requestSha256": digest(encode(self.expected)),
                      "stageSha256": self.grant["stageSha256"], "sourceArchiveSha256": digest(source_archive)})
            self.report["createIntentUnresolved"] = True
            need(now() < utc(self.grant["expiresUtc"]), "Create lease expired")
            if self.stage.get("hostRecoveryGuardian", {}).get("required") is True:
                creator = getattr(self.backend, "create_owned", None)
                need(callable(creator), "Independent guardian CREATE transport required")
                receipt = creator(json.loads(encode(self.expected)))
                need(isinstance(receipt, dict) and IDS.fullmatch(receipt.get("containerId", "")), "Exact guardian CREATE response required")
                self.cid = receipt["containerId"]
                self.validate_create_receipt(receipt)
            else:
                status, raw = self.request(self.backend, "POST", "/containers/create?name=" + self.name, encode(self.expected), {"Content-Type": "application/json"})
                result = json.loads(raw)
                need(status == 201 and IDS.fullmatch(result.get("Id", "")), "Exact actual create response required")
                self.cid = result["Id"]
                receipt = {"receiptKind": "actual-http-create", "httpStatus": status, "containerId": self.cid,
                           "daemonId": self.grant["daemonId"], "expectedConfigDigest": digest(encode(self.expected)),
                           "actualResponseSha256": digest(raw), "receivedUtc": now().isoformat()}
            self.save("create-response", receipt)
            self.report["ownedResources"] = [{"containerId": self.cid, "identityVerified": False}]
            item = self.inspect(self.backend)
            need(item is not None and item["State"].get("Running") is False and item.get("ExecIDs") in (None, []), "New SDK must be stopped")
            spec = self.spec_for(item)
            if receipt.get("actualCreatedRaw") is not None:
                need(receipt["actualCreatedRaw"] == item["Created"], "Guardian raw Created differs")
            if receipt.get("createdSpec") is not None:
                need(receipt["createdSpec"] == spec, "Guardian created spec differs")
            self.save("created-identity", spec)
            self.spec = spec
            self.report["createIntentUnresolved"] = False
            self.report["ownedResources"] = [dict(self.spec, identityVerified=True)]
            self.validate_grant(); self.daemon(self.backend); self.image()
            item = self.inspect(self.backend)
            need(item is not None and item["State"].get("Running") is False and item.get("ExecIDs") in (None, []), "SDK pre-start drift")
            self.save("start-intent", {"containerId": self.cid, "actualCreatedRaw": self.spec["Created"], "issuedUtc": now().isoformat()})
            self.admit("start")
            self.ensure_custody()
            item = self.inspect(self.backend)
            need(item is not None and item["State"].get("Running") is False and item.get("ExecIDs") in (None, []), "SDK post-admission start drift")
            need(now() < utc(self.grant["expiresUtc"]), "Start lease expired")
            self.report["sdkStartAttempted"] = True
            status, raw = self.request(self.backend, "POST", "/containers/" + self.cid + "/start")
            self.save("start-response", {"status": status})
            need(status == 204 and raw == b"", "Actual SDK start refused")
            self.report["sdkStarted"] = True
            item = self.inspect(self.backend)
            need(item is not None and item["State"].get("Running") is True and item.get("ExecIDs") in (None, []), "Live gated supervisor required")
            pid, started = item["State"].get("Pid"), item["State"].get("StartedAt")
            need(type(pid) is int and pid > 0 and isinstance(started, str)
                 and utc(self.spec["Created"]) <= utc(started) <= now(), "Actual live SDK PID/start time required")
            self.save("started-observation", {"containerId": self.cid, "actualCreatedRaw": self.spec["Created"],
                      "imageId": item["Image"], "pid": pid, "startedAtRaw": started,
                      "configuredExecutable": "/bin/sh", "configuredCommandSha256": digest(encode(supervisor_cmd)),
                      "actualExecutable": None, "executableObservation": "Docker inspect does not report executable path"})
            status, raw = self.request(self.backend, "PUT", "/containers/" + self.cid + "/archive?path=/work", source_archive, {"Content-Type": "application/x-tar"})
            self.save("archive-upload", {"status": status, "sha256": digest(source_archive), "bytes": len(source_archive)})
            need(status == 200 and raw == b"", "Actual live source upload refused")
            need(verify_stage_callback(self.backend, self.cid, self.stage, self.grant) is True, "Actual live staged raw source verification refused")
            self.save("source-verified", {"verified": True, "sourceArchiveSha256": digest(source_archive)})
            self.validate_grant(); self.daemon(self.backend); self.image()
            item = self.inspect(self.backend)
            need(item is not None and item["State"].get("Running") is True and item.get("ExecIDs") in (None, []), "Supervisor pre-ready drift")
            marker = io.BytesIO()
            with tarfile.open(fileobj=marker, mode="w") as archive:
                row = tarfile.TarInfo(".control/build.ready"); row.mode = 0o600
                payload = (ready_token + "\n").encode("ascii"); row.size = len(payload)
                archive.addfile(row, io.BytesIO(payload))
            self.save("ready-intent", {"containerId": self.cid, "actualCreatedRaw": self.spec["Created"], "readyTokenSha256": digest(payload)})
            self.admit("ready")
            need(verify_stage_callback(self.backend, self.cid, self.stage, self.grant) is True, "Post-admission live raw graph verification refused")
            self.ensure_custody("ready")
            item = self.inspect(self.backend)
            need(item is not None and item["State"].get("Running") is True and item.get("ExecIDs") in (None, []), "SDK post-admission ready drift")
            need(now() < utc(self.grant["expiresUtc"]), "Ready lease expired")
            self.report["buildReleaseAttempted"] = True
            status, raw = self.request(self.backend, "PUT", "/containers/" + self.cid + "/archive?path=/work", marker.getvalue(), {"Content-Type": "application/x-tar"})
            self.save("ready-response", {"status": status})
            need(status == 200 and raw == b"", "Actual ready publication refused")
            self.report["buildReleased"] = True
            deadline = min(time.monotonic() + self.stage["phaseSeconds"], time.monotonic() + (utc(self.grant["expiresUtc"]) - now()).total_seconds())
            while True:
                need(time.monotonic() < deadline and now() < utc(self.grant["expiresUtc"]), "Build phase deadline expired")
                item = self.inspect(self.backend)
                need(item is not None, "SDK disappeared during build")
                if item["State"].get("Running") is False:
                    break
                time.sleep(min(0.2, max(0, deadline - time.monotonic())))
            exit_code = item["State"].get("ExitCode")
            need(type(exit_code) is int and type(item["State"].get("OOMKilled")) is bool, "Actual SDK exit/OOM state required")
            self.report["oomKilled"] = item["State"]["OOMKilled"]
            self.report["actualExitCode"] = exit_code
            status, raw = self.request(self.backend, "GET", "/containers/" + self.cid + "/logs?stdout=true&stderr=true&timestamps=false")
            need(status == 200, "Actual SDK logs refused")
            logs = demux(raw)
            with (self.ledger / "build.log").open("xb") as stream:
                stream.write(logs); stream.flush(); os.fsync(stream.fileno())
            text = logs.decode("utf-8", errors="strict").replace("\r\n", "\n")
            summary = re.search(r"Build succeeded\.\s*0 Warning\(s\)\s*0 Error\(s\)\s*Time Elapsed [^\n]+\s*\Z", text)
            need(exit_code == 0 and self.report["oomKilled"] is False and summary is not None, "Strict final zero-warning/error build gate failed")
            self.report["buildGatePassed"] = True
        except BaseException as caught:
            self.report["errors"].append({"phase": "build", "type": type(caught).__name__})
        finally:
            if self.cid is not None:
                try: self.cleanup()
                except BaseException as caught:
                    self.report["errors"].append({"phase": "cleanup", "type": type(caught).__name__})
            elif not self.report["createIntentUnresolved"]:
                self.report["cleanupVerified"] = True
            self.report["buildAccepted"] = bool(self.report.get("buildGatePassed") and self.report["cleanupVerified"] and not self.report["errors"])
            self.save("result", self.report)
        return self.report
