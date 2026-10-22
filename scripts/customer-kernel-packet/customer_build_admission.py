"""Pure admission for one strict Release build; never creates a grant or executes.

The external grant SHA is a byte binding, not cryptographic issuer proof. Root
must supply that trusted digest independently; a caller computing it from an
untrusted file has no authority. Evidence provenance is supplied by the trusted
IO owner. This module validates its bindings, freshness and stated observations.
"""
from __future__ import annotations
import datetime as dt
import hashlib
import json
import os
from pathlib import Path
import re

STAGE_CANONICAL_SHA256 = '7de8971c4682a33703c1484c3f34e1b575b36d39166d0bc8c9b277461cab8be2'
SHA256 = re.compile(r'[a-f0-9]{64}\Z')
UUID = re.compile(r'[a-f0-9]{8}-[a-f0-9]{4}-[a-f0-9]{4}-[a-f0-9]{4}-[a-f0-9]{12}\Z')
NATIVE_EXECUTABLES = frozenset(('dotnet', 'testhost', 'MSBuild', 'VBCSCompiler', 'postgres', 'pg_ctl',
                              'redis-server', 'mysqld', 'mariadbd', 'mongod'))


class AdmissionError(ValueError):
    pass


def need(condition, message):
    if not condition:
        raise AdmissionError(message)


def utc(value):
    if isinstance(value, str):
        try:
            value = dt.datetime.fromisoformat(value.replace('Z', '+00:00'))
        except ValueError as error:
            raise AdmissionError('Invalid explicit UTC timestamp') from error
    need(isinstance(value, dt.datetime) and value.utcoffset() == dt.timedelta(0), 'Explicit UTC timestamp required')
    return value


def canonical(value):
    return json.dumps(value, sort_keys=True, separators=(',', ':'), allow_nan=False).encode()


def digest(raw):
    return hashlib.sha256(raw).hexdigest()


def unique_pairs(pairs):
    value = {}
    for key, item in pairs:
        need(key not in value, 'Duplicate grant JSON key')
        value[key] = item
    return value


def parse_grant(raw):
    need(isinstance(raw, bytes) and 0 < len(raw) <= 65536, 'Bounded grant raw bytes required')
    try:
        value = json.loads(raw, object_pairs_hook=unique_pairs,
                           parse_constant=lambda _: (_ for _ in ()).throw(AdmissionError('Nonfinite grant JSON')))
    except (UnicodeError, json.JSONDecodeError) as error:
        raise AdmissionError('Grant JSON invalid') from error
    need(isinstance(value, dict), 'Grant object required')
    return value


def parse_handoffs(raw):
    need(isinstance(raw, bytes) and 0 < len(raw) <= 262144, 'Bounded raw predecessor evidence required')
    try:
        value = json.loads(raw, object_pairs_hook=unique_pairs,
                           parse_constant=lambda _: (_ for _ in ()).throw(AdmissionError('Nonfinite predecessor JSON')))
    except (UnicodeError, json.JSONDecodeError) as error:
        raise AdmissionError('Predecessor JSON invalid') from error
    need(isinstance(value, dict), 'Actual predecessor map required')
    return value


def fresh(checked, now, age):
    observed = utc(checked)
    need(dt.timedelta(0) <= now - observed <= dt.timedelta(seconds=age), 'Evidence stale or from future')


def validate_grant(stage, grant, now, evidence, expected_driver_digest, expected_grant_digest, grant_raw):
    """Validate an externally authorized grant and fresh evidence; no IO or writes.

    Returns an independent JSON copy. Neither source metadata nor a process
    absence creates authority; actual terminal predecessor evidence is required.
    The caller revalidates before CREATE, supervisor START, and READY publication
    while no .NET work is active. Cleanup after expiry uses a separate exact
    identity guard; admission memory floors never block owned-resource cleanup.
    """
    need(isinstance(stage, dict) and digest(canonical(stage)) == STAGE_CANONICAL_SHA256, 'Immutable build-stage mismatch')
    need(SHA256.fullmatch(expected_driver_digest or '') is not None
         and SHA256.fullmatch(expected_grant_digest or '') is not None, 'Externally trusted full digests required')
    parsed = parse_grant(grant_raw)
    need(digest(grant_raw) == expected_grant_digest, 'External Root grant byte seal mismatch')
    need(parsed == grant, 'Grant object differs from sealed raw bytes')
    fields = {'schemaVersion', 'rootThread', 'owner', 'stage', 'transportMain', 'sourcePolicySha256',
              'providerPlanSha256', 'candidateBase', 'scope', 'rootGranted', 'runId', 'driverPacketSha256',
              'hostBootId', 'daemonId', 'issuedUtc', 'notBeforeUtc', 'expiresUtc', 'stageSha256',
              'sourceArchiveSha256', 'sourceArchiveBytes', 'predecessorEvidenceSha256'}
    need(set(grant) == fields, 'Exact build-only grant fields required')
    need(type(grant.get('schemaVersion')) is int and grant['schemaVersion'] == 1 and grant.get('rootGranted') is True,
         'Actual external Root grant required')
    for key in ('rootThread', 'owner', 'stage', 'transportMain', 'sourcePolicySha256', 'providerPlanSha256', 'candidateBase'):
        need(grant.get(key) == stage[key], 'Grant binding mismatch: ' + key)
    need(grant.get('scope') == 'strict-release-build' and grant.get('driverPacketSha256') == expected_driver_digest,
         'Only exact driver strict Release build is authorized')
    need(grant.get('stageSha256') == STAGE_CANONICAL_SHA256
         and grant.get('sourceArchiveSha256') == stage['sourceArchive']['sha256']
         and type(grant.get('sourceArchiveBytes')) is int and grant['sourceArchiveBytes'] == stage['sourceArchive']['bytes'],
         'Exact externally granted stage/source archive binding required')
    need(re.fullmatch('[a-f0-9]{32}', grant.get('runId', '')) is not None, 'Exact 32-hex run required')
    need(UUID.fullmatch(grant.get('hostBootId', '')) is not None and isinstance(grant.get('daemonId'), str)
         and 0 < len(grant['daemonId']) <= 256 and not any(c.isspace() for c in grant['daemonId']), 'Bound host/daemon identities required')
    now = utc(now)
    issued, not_before, expiry = (utc(grant[k]) for k in ('issuedUtc', 'notBeforeUtc', 'expiresUtc'))
    need(issued <= not_before <= now < expiry and 0 < (expiry - issued).total_seconds() <= stage['maxGrantSeconds'],
         'Fresh finite externally allocated grant required; old October 4 slot cannot be reused')
    need((expiry - now).total_seconds() >= stage['minimumRemainingSeconds'], 'Insufficient admitted build/cleanup remainder')
    need(isinstance(evidence, dict) and set(evidence) == {'hostSnapshot', 'daemonSnapshot', 'imageSnapshot', 'handoffs', 'handoffsRaw'},
         'Exact host, daemon, image and actual handoff evidence required')
    host = evidence['hostSnapshot']
    fresh(host['checkedUtc'], now, stage['maxEvidenceAgeSeconds'])
    need(host.get('hostBootId') == grant['hostBootId'] and type(host.get('freePhysicalKiB')) is int
         and host['freePhysicalKiB'] >= stage['minimumPhysicalKiB'], 'Host identity/physical-memory admission failed')
    need(isinstance(host.get('nativeProcesses'), list) and not host['nativeProcesses'], 'Competing native work remains')
    daemon, image = evidence['daemonSnapshot'], evidence['imageSnapshot']
    for observation in (daemon, image):
        fresh(observation['checkedUtc'], now, stage['maxEvidenceAgeSeconds'])
        need(observation.get('hostBootId') == grant['hostBootId'], 'Host evidence domain mismatch')
    need(daemon.get('daemonId') == grant['daemonId'] and daemon.get('osType') == 'linux', 'Actual Linux daemon binding required')
    need(image.get('imageId') == stage['sdkImage']['imageId'] and image.get('reference') == stage['sdkImage']['reference'],
         'Actual immutable SDK image binding required')
    handoffs = evidence['handoffs']
    handoffs_raw = evidence['handoffsRaw']
    need(SHA256.fullmatch(grant.get('predecessorEvidenceSha256', '')) is not None
         and isinstance(handoffs_raw, bytes) and digest(handoffs_raw) == grant['predecessorEvidenceSha256'],
         'Root-bound raw predecessor evidence seal mismatch')
    need(parse_handoffs(handoffs_raw) == handoffs, 'Predecessor map substituted after Root byte binding')
    need(isinstance(handoffs, dict) and set(handoffs) == set(stage['admissionContract']['actualFinitePredecessorHandoffs']),
         'All three actual predecessor handoffs required; absence alone insufficient')
    threads = set()
    for lane, item in handoffs.items():
        fresh(item['checkedUtc'], now, stage['maxEvidenceAgeSeconds'])
        need(item.get('terminal') is True and item.get('activeOwnedNative') is False,
             'Predecessor not terminal/released: ' + lane)
        need(UUID.fullmatch(item.get('threadId', '')) is not None and item['threadId'] not in threads,
             'Distinct actual predecessor threads required')
        threads.add(item['threadId'])
        refs = item.get('evidenceRefs')
        need(isinstance(refs, list) and 0 < len(refs) <= 16, 'Actual predecessor evidence references required')
        for ref in refs:
            need(isinstance(ref, dict) and set(ref) == {'reference', 'sha256'}
                 and isinstance(ref['reference'], str) and 0 < len(ref['reference']) <= 2048
                 and not any(ord(c) < 32 for c in ref['reference']) and SHA256.fullmatch(ref['sha256'] or '') is not None,
                 'Actual bounded evidence reference/hash required')
    return parse_grant(canonical(grant))


def linux_host_snapshot(proc_root=Path('/proc'), now=None):
    """Read actual host /proc only. No commands, Docker, process kills or adoption.

    MemFree is the physical-memory floor; reclaimable/available memory is not
    substituted. Executable census is exact-basename based; command lines and
    environments are never read. IO permission/identity faults fail closed.
    """
    checked = utc(now) if now is not None else dt.datetime.now(dt.timezone.utc)
    root = Path(proc_root)
    boot = (root / 'sys/kernel/random/boot_id').read_text().strip()
    need(UUID.fullmatch(boot) is not None, 'Actual Linux boot ID required')
    mem = (root / 'meminfo').read_text().splitlines()
    matches = [re.fullmatch(r'MemFree:\s+(\d+)\s+kB', line) for line in mem]
    values = [int(match[1]) for match in matches if match]
    need(len(values) == 1, 'Actual MemFree KiB required')
    jobs = []
    entries = list(root.iterdir())
    need(len(entries) <= 65536, 'Host process census bound')
    for path in entries:
        if not path.name.isdigit():
            continue
        try:
            first = (path / 'stat').read_text()
            executable = os.readlink(path / 'exe')
            second = (path / 'stat').read_text()
        except (FileNotFoundError, ProcessLookupError):
            continue  # Exited during observation; terminal handoff still required.
        def ticks(raw):
            close = raw.rfind(')')
            need(close > 0 and raw[:raw.find(' ')].isdigit(), 'Actual proc stat malformed')
            fields = raw[close + 2:].split()
            need(len(fields) >= 20 and fields[19].isdigit(), 'Actual process start ticks missing')
            return int(fields[19])
        need(first.split(' ', 1)[0] == path.name and second.split(' ', 1)[0] == path.name, 'Actual proc PID mismatch')
        start = ticks(first)
        need(start == ticks(second), 'PID reuse during native census')
        base = Path(executable.removesuffix(' (deleted)')).name
        if base in NATIVE_EXECUTABLES or base.startswith(('Legacy.Maliev.', 'Maliev.')):
            jobs.append({'pid': int(path.name), 'executable': executable, 'startTicks': start})
    need((root / 'sys/kernel/random/boot_id').read_text().strip() == boot, 'Host reboot during snapshot')
    return {'checkedUtc': checked.isoformat(), 'hostBootId': boot, 'freePhysicalKiB': values[0],
            'nativeProcesses': sorted(jobs, key=lambda row: row['pid'])}
