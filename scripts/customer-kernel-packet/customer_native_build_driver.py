"""Linux BUILD-only controller. Source preparation never grants native execution."""
from __future__ import annotations
import argparse
import datetime as dt
import hashlib
import http.client
import importlib.util
import io
import json
from pathlib import Path
import re
import signal
import socket
import stat
import sys
import tarfile
import time
from urllib.parse import quote

MAX_BYTES = 8 * 1024 * 1024
SHA = re.compile('[a-f0-9]{64}\\Z')


def need(value, message):
    if not value:
        raise ValueError(message)


def digest(raw):
    return hashlib.sha256(raw).hexdigest()


def regular(path, maximum=MAX_BYTES):
    path = Path(path)
    for part in (path, *path.parents):
        if part.exists() or part.is_symlink():
            metadata = part.lstat()
            need(not stat.S_ISLNK(metadata.st_mode) and not getattr(metadata, 'st_file_attributes', 0) & 0x400,
                 'Source or control link/reparse refused')
    need(path.is_file() and path.stat().st_size <= maximum, 'Bounded regular input required')
    raw = path.read_bytes()
    need(len(raw) <= maximum, 'Input grew beyond bound')
    return raw


def captured_module(name, path, raw):
    module = importlib.util.module_from_spec(importlib.util.spec_from_loader(name, loader=None))
    module.__file__ = str(path)
    exec(compile(raw, str(path), 'exec'), module.__dict__)
    return module


def load_packet(root, expected_packet_sha):
    need(SHA.fullmatch(expected_packet_sha or ''), 'Independently accepted driver packet digest required')
    root = Path(root)
    manifest_raw = regular(root / 'driver-manifest.json', 262144)
    need(digest(manifest_raw) == expected_packet_sha, 'Driver packet digest mismatch')
    manifest = json.loads(manifest_raw)
    need(manifest.get('nativeExecutionGranted') is False and manifest.get('stage') == 'customer-null-build-first',
         'Source-only packet required')
    captured = {}
    aliases = set()
    for row in manifest['files']:
        name = row['path']
        need(isinstance(name, str) and '\\' not in name and ':' not in name and not name.startswith('/')
             and all(p not in ('', '.', '..', '.git') for p in name.split('/'))
             and name.casefold() not in aliases, 'Exact unaliased packet recipe required')
        aliases.add(name.casefold())
        raw = regular(root / name)
        need(type(row['bytes']) is int and len(raw) == row['bytes'] and digest(raw) == row['sha256'], 'Driver file seal mismatch')
        captured[name] = raw
    need(digest(regular(Path(__file__))) == digest(captured['customer_native_build_driver.py']), 'Executing controller differs from sealed file')
    # Execute the captured decoder bytes, not a subsequently reopened pathname.
    helper_raw = captured['input/sealed_source_capsule.py']
    need(digest(helper_raw) == '44a8a5accac9da11422d606be02fe28487642215df511b5f1c4284296a453ee2', 'Accepted File decoder mismatch')
    helper = captured_module('customer_native_accepted_decoder', root / 'input/sealed_source_capsule.py', helper_raw)
    intake = captured_module('customer_native_source_intake', root / 'input/customer_source_intake.py', captured['input/customer_source_intake.py'])
    stage = helper.parse_json(captured['build-stage.json'])
    policy = intake.load_policy(captured['input/customer-candidate-policy.json'], helper)
    need(stage['nativeExecutionGranted'] is False and stage['sourcePolicySha256'] == intake.POLICY_SHA256,
         'Stage cannot confer execution authority')
    need(stage['candidateBase'] == policy['baseCommit'] == 'b50e12d66cf0c3d4211a41febe3b01ca265a0c15'
         and stage['transportMain'] == policy['baseCommit'], 'Current-base driver/policy join differs')
    need(stage['sourceCounts'] == intake.COUNTS == {'source': 162, 'dependencies': 141, 'validation': 21}
         and stage['sourceArchive']['rawFiles'] == len(policy['rawFiles']) == 324,
         'Current-base complete source-count join differs')
    need(stage['providerPlanSha256'] == intake.PROVIDER_PLAN_SHA256 == policy['providerPlanSha256'],
         'Current provider-plan join differs')
    need(stage['associationManifestSha256'] == policy['associationManifestSha256'],
         'Reviewed association join differs')
    need(stage['sourceArchive']['rawGraphSha256'] == digest(json.dumps(policy['rawFiles'], sort_keys=True,
         separators=(',', ':')).encode()), 'Current raw graph seal differs')
    verifier_raw = captured['input/verify_customer_null_association.py']
    need(digest(verifier_raw) == stage['associationVerifierSha256']
         == 'ae35a1ffb0a07cb2cc79236a93cb5ea69a44e4fd21abad659bbdf9bd6ee52d08',
         'Captured association verifier seal differs')
    intake._captured_association_verifier = captured_module('customer_captured_association_verifier',
        root / 'input/verify_customer_null_association.py', verifier_raw)
    admission = captured_module('customer_build_admission', root / 'customer_build_admission.py', captured['customer_build_admission.py'])
    owned = captured_module('customer_owned_build', root / 'customer_owned_build.py', captured['customer_owned_build.py'])
    need(admission.digest(admission.canonical(stage)) == admission.STAGE_CANONICAL_SHA256
         == manifest['stageCanonicalSha256'], 'Reviewed stage differs')
    guardian = captured_module('customer_build_guardian', root / 'customer_build_guardian.py', captured['customer_build_guardian.py'])
    process = captured_module('customer_guardian_process', root / 'customer_guardian_process.py', captured['customer_guardian_process.py'])
    return manifest, stage, policy, helper, intake, admission, owned, guardian, process


def source_archive(source, policy, stage, helper, intake):
    source = Path(source)
    helper.reject_links(source)
    need(source.is_dir(), 'Actual qualified source materialization required')
    expected = policy['rawFiles']
    files = {}
    actual = set()
    total_bytes = 0
    for index, item in enumerate(source.rglob('*')):
        need(index < 1024, 'Materialized source entry cap')
        helper.reject_links(item)
        need(item.is_dir() or item.is_file(), 'Materialized special file refused')
        if item.is_file():
            name = item.relative_to(source).as_posix()
            actual.add(name)
            if name in expected:
                raw = regular(item)
                need(len(raw) == expected[name]['bytes'] and digest(raw) == expected[name]['sha256'], 'Materialized raw source changed')
                total_bytes += len(raw)
                need(total_bytes <= MAX_BYTES, 'Materialized source byte cap')
                files[name] = raw
    need(actual == set(expected) | {'metadata/intake-receipt.json'}, 'Exact materialized raw graph and receipt required')
    receipt = helper.parse_json(regular(source / 'metadata/intake-receipt.json', 65536))
    need(receipt['policySha256'] == stage['sourcePolicySha256'] and receipt['rawFiles'] == len(expected) == 324
         and receipt['baseCommit'] == stage['candidateBase'] == policy['baseCommit']
         and receipt['capsuleFileCounts'] == stage['sourceCounts']
         and receipt['rawGraphSha256'] == stage['sourceArchive']['rawGraphSha256']
         and receipt['forecastCases'] == 400 and receipt['actualTestsRun'] == 0
         and receipt['nativeExecutionGranted'] is False and receipt['providerPlanSha256'] == stage['providerPlanSha256'],
         'Actual current-base source receipt differs')
    intake.nested_graph(policy, files, helper)
    archive = io.BytesIO()
    with tarfile.open(fileobj=archive, mode='w', format=tarfile.PAX_FORMAT) as tar:
        for name, raw in sorted(files.items()):
            item = tarfile.TarInfo(name)
            item.size = len(raw); item.mode = 0o644; item.uid = item.gid = item.mtime = 0
            item.uname = item.gname = ''
            tar.addfile(item, io.BytesIO(raw))
    raw = archive.getvalue()
    need(len(raw) == stage['sourceArchive']['bytes'] and digest(raw) == stage['sourceArchive']['sha256'], 'Frozen SDK transport archive differs')
    return raw


def verify_live_archive(raw, policy, helper):
    need(isinstance(raw, bytes) and 0 < len(raw) <= MAX_BYTES, 'Actual SDK archive bound')
    expected = policy['rawFiles']; found = {}; seen = set(); aliases = set(); expanded = 0
    directories = {'work', 'work/.control'}
    for path in expected:
        parts = path.split('/')
        directories.update('work/' + '/'.join(parts[:end]) for end in range(1, len(parts)))
    with tarfile.open(fileobj=io.BytesIO(raw), mode='r:') as archive:
        for index, item in enumerate(archive):
            need(index < 1024 and (item.isfile() or item.isdir()), 'Actual SDK archive special file/count refused')
            name = item.name.rstrip('/') if item.isdir() else item.name
            need(name == 'work' or name.startswith('work/'), 'Actual SDK archive root differs')
            need(name not in seen and name.casefold() not in aliases, 'SDK archive duplicate/alias')
            seen.add(name); aliases.add(name.casefold())
            if item.isdir():
                need(name in directories and item.size == 0, 'SDK archive undeclared directory/payload')
                continue
            need(name != 'work', 'SDK archive root must be directory')
            name = name[5:]
            helper.canonical_path(name)
            need(name in expected and item.size == expected[name]['bytes'], 'SDK archive unexpected file/size')
            expanded += item.size
            need(expanded <= MAX_BYTES, 'SDK expanded source cap')
            stream = archive.extractfile(item)
            try:
                value = stream.read(item.size + 1)
            finally:
                stream.close()
            need(len(value) == item.size and digest(value) == expected[name]['sha256'], 'Actual SDK source byte drift')
            found[name] = True
    need(set(found) == set(expected) and len(found) == 324, 'Actual SDK incomplete raw graph')
    return True


class DockerAPI:
    """One fixed local Unix socket; finite requests and separately bounded cleanup."""
    def __init__(self, deadline, socket_path='/var/run/docker.sock'):
        need(socket_path == '/var/run/docker.sock', 'Fixed local daemon socket required')
        self.deadline = deadline
        self.socket_path = socket_path

    def cleanup_backend_factory(self, deadline):
        return DockerAPI(lambda: deadline, self.socket_path)

    def request(self, method, path, body=None, headers=None):
        need(sys.platform == 'linux' and threading_main(), 'Native IO is Linux main-thread only')
        need(method in ('GET', 'POST', 'PUT', 'DELETE') and path.startswith('/') and not path.startswith('//')
             and '#' not in path and '\r' not in path and '\n' not in path, 'Bounded local API route required')
        need(body is None or isinstance(body, bytes) and len(body) <= MAX_BYTES, 'Docker request cap')
        remaining = min(10.0, self.deadline() - time.monotonic())
        need(remaining > 0, 'Docker request deadline expired')
        connection = http.client.HTTPConnection('localhost', timeout=remaining)
        previous = signal.getsignal(signal.SIGALRM)
        def expire(_signum, _frame):
            raise TimeoutError('Docker RPC total deadline')
        signal.signal(signal.SIGALRM, expire)
        signal.setitimer(signal.ITIMER_REAL, remaining)
        response = None
        try:
            sock = socket.socket(socket.AF_UNIX, socket.SOCK_STREAM)
            connection.sock = sock
            sock.settimeout(remaining); sock.connect(self.socket_path)
            connection.request(method, path, body=body, headers=headers or {})
            response = connection.getresponse()
            result = []; size = 0
            while True:
                chunk = response.read1(min(65536, MAX_BYTES + 1 - size))
                if not chunk:
                    break
                size += len(chunk); need(size <= MAX_BYTES, 'Docker response cap')
                result.append(chunk)
            need(response.length in (None, 0), 'Docker response truncated')
            return response.status, b''.join(result)
        finally:
            if response is not None:
                response.close()
            connection.close()
            signal.setitimer(signal.ITIMER_REAL, 0)
            signal.signal(signal.SIGALRM, previous)

    def json(self, method, path):
        need(method == 'GET', 'JSON interface is read-only')
        status, raw = self.request(method, path)
        need(status == 200, 'Actual daemon JSON read failed')
        return json.loads(raw)


def threading_main():
    import threading
    return threading.current_thread() is threading.main_thread()


def native_context(args, root, packet):
    manifest, stage, policy, helper, intake, admission, owned, guardian, process = packet
    need(manifest.get('runtimeQualificationAccepted') is True and stage.get('runtimeQualificationAccepted') is True,
         'Current-base source successor is not runtime qualified; native IO forbidden')
    need(sys.platform == 'linux' and args.grant and args.handoffs and args.ledger
         and SHA.fullmatch(args.grant_sha256 or ''), 'External Root inputs and Linux host required before native IO')
    grant_raw = regular(args.grant, 65536)
    need(digest(grant_raw) == args.grant_sha256, 'Externally pinned Root grant bytes differ')
    grant = admission.parse_grant(grant_raw)
    need(grant.get('rootGranted') is True and grant.get('rootThread') == stage['rootThread']
         and grant.get('scope') == 'strict-release-build', 'Actual Root build-only authority required')
    handoff_raw = regular(args.handoffs, 262144)
    handoffs = admission.parse_handoffs(handoff_raw)
    archive = source_archive(stage['sourceDirectory'], policy, stage, helper, intake)
    expiry = admission.utc(grant['expiresUtc'])
    deadline = time.monotonic() + (expiry - dt.datetime.now(dt.timezone.utc)).total_seconds()
    backend = DockerAPI(lambda: deadline)
    def admit(phase, actual_stage, actual_grant):
        need(actual_stage == stage, 'Native stage substitution refused')
        host = admission.linux_host_snapshot()
        info = backend.json('GET', '/info')
        image = backend.json('GET', '/images/' + quote(stage['sdkImage']['imageId'], safe='') + '/json')
        need(image.get('Id') == stage['sdkImage']['imageId'] and image.get('Os') == 'linux'
             and isinstance(image.get('RepoDigests'), list)
             and stage['sdkImage']['reference'] in image['RepoDigests'],
             'Fresh actual SDK reference mapping required')
        stamp = dt.datetime.now(dt.timezone.utc).isoformat()
        evidence = {'hostSnapshot': host,
                    'daemonSnapshot': {'checkedUtc': stamp, 'hostBootId': host['hostBootId'], 'daemonId': info.get('ID'), 'osType': info.get('OSType')},
                    'imageSnapshot': {'checkedUtc': stamp, 'hostBootId': host['hostBootId'], 'reference': stage['sdkImage']['reference'], 'imageId': image.get('Id')},
                    'handoffs': handoffs, 'handoffsRaw': handoff_raw}
        admission.validate_grant(stage, grant, dt.datetime.now(dt.timezone.utc), evidence,
                                 args.packet_sha256, args.grant_sha256, grant_raw)
        return {'admitted': True, 'trustedRootGrantVerified': True, 'checkedUtc': host['checkedUtc'],
                'freePhysicalKiB': host['freePhysicalKiB'], 'nativeProcesses': host['nativeProcesses'],
                'rootGrantSha256': args.grant_sha256}
    admit('initial', stage, grant)
    return {'root': root, 'manifest': manifest, 'stage': stage, 'policy': policy,
            'helper': helper, 'intake': intake, 'admission': admission, 'owned': owned,
            'guardian': guardian, 'process': process, 'regular': regular,
            'grant': grant, 'backend': backend, 'deadline': deadline, 'admit': admit, 'archive': archive}



def semantic_joins(value):
    """Pure source preparation; joining inputs never grants native acceptance."""
    need(value.get('sourceOnly') is True, 'Semantic diagnostic must stay source-only')
    historical = value['historicalBaseline']; authored = value['authoredContract']
    expected_base = 'b50e12d66cf0c3d4211a41febe3b01ca265a0c15'
    need(historical['baseSha'] == expected_base and historical['caseCount'] == len(historical['cases']) == 387,
         'Exact actual historical baseline387 required')
    def identity(case):
        return (case['definition']['className'], case['definition']['method'], case['testName'])
    def roster(phase, count):
        row = value[phase]
        need(row['baseSha'] == expected_base and row['configuration'] == 'Release'
             and row['buildExitCode'] == row['buildWarnings'] == row['buildErrors'] == 0
             and row['buildCompletedBeforeDiscovery'] is True, 'Build-first strict Release evidence required')
        need(SHA.fullmatch(row.get('compiledAssemblySha256') or '')
             and SHA.fullmatch(row.get('discoverySha256') or '')
             and SHA.fullmatch(row.get('rawTrxSha256') or ''), 'Actual compiled/discovery/TRX byte bindings required')
        cases = row['cases']; need(len(cases) == count and row['caseCount'] == count,
                                   'Exact compiled roster count differs: ' + phase)
        need(all(c['outcome'] == 'Passed' for c in cases), 'Skipped/failed/nonterminal cases refused')
        keys = [identity(c) for c in cases]
        need(len(set(keys)) == len(keys) and len({c['testId'] for c in cases}) == len(cases),
             'Duplicate compiled display or test ID refused')
        return {identity(c): c for c in cases}
    old = {identity(c) for c in historical['cases']}
    baseline = roster('baseline', 387); candidate = roster('candidate', 400)
    need(value['baseline']['compiledAssemblySha256'] != value['candidate']['compiledAssemblySha256'],
         'Baseline and changed candidate binary identities cannot be aliased')
    need(value['baseline']['unfiltered'] is True and value['candidate']['unfiltered'] is True
         and value['full']['unfiltered'] is True and value['focused']['filter'] == authored['filter'],
         'Exact unfiltered discovery/full suite and focused filter required')
    need(set(baseline) == old and old <= set(candidate), 'Actual baseline identities were dropped or changed')
    need(all(candidate[key]['testId'] == baseline[key]['testId'] for key in baseline),
         'Candidate changed fresh baseline actual test ID')
    new = {key: candidate[key] for key in set(candidate) - old}
    need(len(new) == 13 and authored['caseCount'] == 13 and len(authored['methods']) == 6,
         'Thirteen new cases across six authored methods required')
    expected = sorted((m['fullyQualifiedMethod'], json.dumps(row, sort_keys=True))
                      for m in authored['methods'] for row in m['rows'])
    observed = sorted((c['definition']['className'] + '.' + c['definition']['method'],
                       json.dumps(c['sourceArguments'], sort_keys=True)) for c in new.values())
    need(observed == expected, 'Exact native discovered argument binding differs from authored rows')
    focused = roster('focused', 13); full = roster('full', 400)
    need(set(focused) == set(new) and set(full) == set(candidate), 'Focused/full semantic roster differs')
    for phase, rows in [('focused', focused), ('full', full)]:
        need(value[phase]['compiledAssemblySha256'] == value['candidate']['compiledAssemblySha256'],
             'Focused/full must use the exact discovered candidate assembly')
        need(all(rows[key]['testId'] == candidate[key]['testId'] for key in rows),
             'Focused/full case IDs differ from actual candidate discovery')
    return {'semanticInputsJoined': True, 'historicalCases': 387, 'newSourceRows': 13,
            'forecastCandidateCases': 400, 'nativeTestsAccepted': False, 'fullSuiteAccepted': False,
            'sourceOnly': True, 'actualNativeEvidenceStillRequired': True}


def main():
    parser = argparse.ArgumentParser()
    parser.add_argument('mode', choices=('plan', 'source-check', 'semantic-check', 'execute-build'))
    parser.add_argument('--packet-sha256', required=True)
    parser.add_argument('--source-directory', type=Path)
    parser.add_argument('--semantic-input', type=Path)
    parser.add_argument('--grant'); parser.add_argument('--grant-sha256')
    parser.add_argument('--handoffs'); parser.add_argument('--ledger')
    parser.add_argument('--guardian-child', action='store_true', help=argparse.SUPPRESS)
    parser.add_argument('--guardian-directory', help=argparse.SUPPRESS)
    args = parser.parse_args()
    root = Path(__file__).resolve().parent
    packet = load_packet(root, args.packet_sha256)
    manifest, stage, policy, helper, intake, admission, owned, guardian, process = packet
    if args.mode == 'semantic-check':
        need(args.semantic_input is not None and args.source_directory is None and not args.grant
             and not args.grant_sha256 and not args.handoffs and not args.ledger
             and not args.guardian_child and args.guardian_directory is None,
             'Semantic-check cannot request native authority')
        print(json.dumps(semantic_joins(json.loads(regular(args.semantic_input)))))
        return 0
    need(args.semantic_input is None, 'Semantic inputs are diagnostic only')
    if args.mode == 'source-check':
        need(args.source_directory is not None and not args.grant and not args.grant_sha256
             and not args.handoffs and not args.ledger and not args.guardian_child
             and args.guardian_directory is None, 'Source-check inputs cannot request native authority')
        raw = source_archive(args.source_directory, policy, stage, helper, intake)
        print(json.dumps({'sourceValidated': True, 'rawFiles': 324, 'archiveSha256': digest(raw),
                          'archiveBytes': len(raw), 'nativeExecutionGranted': False,
                          'sdkStarts': 0, 'providerStarts': 0}))
        return 0
    need(args.source_directory is None, 'Source-directory override is source-check only')
    if args.mode == 'plan':
        need(not args.guardian_child, 'Guardian requires actual Root execution inputs')
        print(json.dumps({'stage': stage['stage'], 'transportMain': stage['transportMain'],
                          'sourceArchive': stage['sourceArchive'], 'nativeExecutionGranted': False,
                          'sdkStarts': 0, 'providerStarts': 0, 'rootGrantRequired': True,
                          'independentCreateCustodianRequired': True}))
        return 0
    ctx = native_context(args, root, packet)
    if args.guardian_child:
        need(args.guardian_directory is not None, 'Registered custody directory required')
        return process.run_guardian(ctx, args)
    need(args.guardian_directory is None, 'No direct custody directory override')
    backend = process.GuardedBackend(ctx['backend'], ctx, args)
    actual_grant = dict(ctx['grant'], rootGrantSha256=args.grant_sha256)
    def verify(daemon, cid, actual_stage, actual_grant):
        status, raw = daemon.request('GET', '/containers/' + cid + '/archive?path=/work')
        need(status == 200, 'Actual live SDK source archive unavailable')
        return verify_live_archive(raw, policy, helper)
    previous = signal.getsignal(signal.SIGTERM)
    def cancelled(_signum, _frame):
        raise KeyboardInterrupt('Controller cancellation; independent exact custody retained')
    signal.signal(signal.SIGTERM, cancelled)
    guardian_terminal = None
    try:
        runner = owned.OwnedBuild(backend, stage, actual_grant, args.ledger, ctx['admit'])
        try:
            report = runner.run(ctx['archive'], verify)
        finally:
            guardian_terminal = backend.finish()
    finally:
        signal.signal(signal.SIGTERM, previous)
    report['guardianTerminal'] = guardian_terminal
    report['guardianCleanupVerified'] = guardian_terminal.get('cleanupVerified') is True
    report['buildAccepted'] = bool(report['buildAccepted'] and report['guardianCleanupVerified'])
    report['full403Accepted'] = False
    report['nativeApplicationTestCases'] = 0
    report['providersStarted'] = 0
    report['failureIsBehavioralRed'] = False
    runner.save('controller-terminal', report)
    print(json.dumps(report))
    return 0 if report['buildAccepted'] else 1


if __name__ == '__main__':
    try:
        raise SystemExit(main())
    except (ValueError, OSError, TimeoutError, KeyboardInterrupt) as error:
        # Never echo a provider response, environment, grant body or credentials.
        print(json.dumps({'status': 'native-build-not-accepted', 'failureType': type(error).__name__,
                          'failureIsBehavioralRed': False, 'full403Accepted': False}), file=sys.stderr)
        raise SystemExit(1)
