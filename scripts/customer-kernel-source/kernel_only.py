"""TEST-ONLY Linux custody kernel qualification. No Docker, SDK or BUILD authority."""
import argparse
import ast
import datetime as dt
import hashlib
import importlib.util
import json
import os
from pathlib import Path
import socket
import subprocess
import sys
import time
from types import SimpleNamespace

MANIFEST_SHA = 'a647b1593f712317288547f39b1a5367c25d3180af4577aa250f4bca403f38cf'
SYNTHETIC_SHA = '0' * 64
OWNER = '/root/contact_scaffold_source_review/customer-kernel-only-lifecycle-r7'
OWNER_MODULE_SHA = '834cbb4f0380ef4460c9ff71b9632a952ab5a662d79074b6d35ff54484e8ce07'


def need(value, message):
    if not value:
        raise ValueError(message)


def digest(raw):
    return hashlib.sha256(raw).hexdigest()


def regular(path, limit=262144):
    path = Path(path)
    for part in (path, *path.parents):
        need(not part.is_symlink(), 'Links refused')
    need(path.is_file() and path.stat().st_size <= limit, 'Bounded regular file required')
    raw = path.read_bytes()
    need(len(raw) <= limit, 'Input grew')
    return raw


def parse(raw):
    def pairs(items):
        result = {}
        for key, value in items:
            need(key not in result, 'Duplicate JSON key')
            result[key] = value
        return result
    return json.loads(raw, object_pairs_hook=pairs)


def module(name, path, raw):
    value = importlib.util.module_from_spec(importlib.util.spec_from_loader(name, loader=None))
    value.__file__ = str(path)
    exec(compile(raw, str(path), 'exec'), value.__dict__)
    return value


def load_custody_module():
    path = Path(__file__).absolute().parent / 'owned_process.py'
    raw = regular(path)
    need(digest(raw) == OWNER_MODULE_SHA, 'Transitive owner helper source seal differs')
    return module('kernel_captured_owner', path, raw)


def sealed(packet, trusted):
    need(trusted == MANIFEST_SHA, 'Exact independently reviewed manifest required')
    raw = regular(packet / 'driver-manifest.json')
    need(digest(raw) == trusted, 'Manifest seal differs')
    manifest = parse(raw)
    need(manifest.get('nativeExecutionGranted') is False and len(manifest['files']) == 17,
         'Exact source-only17-file packet required')
    captured = {}
    for row in manifest['files']:
        name = row['path']
        need(not name.startswith('/') and '\\' not in name and ':' not in name
             and all(p not in ('', '.', '..') for p in name.split('/')), 'Packet path refused')
        need(name not in captured, 'Duplicate packet path')
        data = regular(packet / name, 8388608)
        need(digest(data) == row['sha256'] and len(data) == row['bytes'], 'Packet source seal differs')
        captured[name] = data
    return captured


def admission():
    need(sys.platform == 'linux', 'Kernel-only qualification requires Linux; Windows must refuse')
    mem = Path('/proc/meminfo').read_text()
    free = [int(line.split()[1]) for line in mem.splitlines() if line.startswith('MemFree:')]
    need(len(free) == 1 and free[0] >= 4194304, 'Fresh actual MemFree >=4194304KiB required')
    native = []
    for directory in Path('/proc').iterdir():
        if not directory.name.isdigit():
            continue
        try:
            exe = os.readlink(directory / 'exe')
        except FileNotFoundError:
            continue
        except PermissionError:
            raise ValueError('Native census unobservable')
        if Path(exe).name.lower() in ('dotnet', 'testhost', 'msbuild', 'vstest.console'):
            native.append({'pid': int(directory.name), 'executable': exe})
    need(not native, 'Competing native process present')
    return {'freePhysicalKiB': free[0], 'nativeProcesses': native,
            'hostBootId': Path('/proc/sys/kernel/random/boot_id').read_text().strip()}


def save(path, row):
    path = Path(path)
    with path.open('x', encoding='utf-8') as stream:
        json.dump(row, stream, indent=2)
        stream.write('\n')


def authority(args):
    need(args.capability and args.capability_sha256, 'External machine-bound kernel capability required')
    raw = regular(args.capability, 65536)
    need(digest(raw) == args.capability_sha256, 'Trusted capability digest differs')
    cap = parse(raw)
    need(set(cap) == {'schemaVersion', 'scope', 'rootIssued', 'harnessSha256', 'packetManifestSha256',
                      'hostBootId', 'issuedUtc', 'expiresUtc'}, 'Exact kernel capability fields required')
    need(type(cap['schemaVersion']) is int and cap['schemaVersion'] == 1
         and cap['scope'] == 'customer-kernel-only-lifecycle' and cap['rootIssued'] is True
         and cap['harnessSha256'] == digest(regular(__file__))
         and cap['packetManifestSha256'] == MANIFEST_SHA, 'Kernel authority/source binding differs')
    issued = dt.datetime.fromisoformat(cap['issuedUtc'].replace('Z', '+00:00'))
    expires = dt.datetime.fromisoformat(cap['expiresUtc'].replace('Z', '+00:00'))
    need(issued.utcoffset() == dt.timedelta(0) and expires.utcoffset() == dt.timedelta(0), 'UTC authority required')
    now = dt.datetime.now(dt.timezone.utc)
    need(issued <= now and (expires-issued).total_seconds() <= 600
         and (expires-now).total_seconds() >= 45, 'Fresh finite kernel slot with45s remaining required')
    need(cap['hostBootId'] == Path('/proc/sys/kernel/random/boot_id').read_text().strip(), 'Machine binding differs')
    return cap


def bind_external(args, capability):
    need(args.external_owner_receipt and args.external_owner_sha256,'Predispatch external owner intent required')
    raw=regular(args.external_owner_receipt,65536)
    need(digest(raw)==args.external_owner_sha256,'External intent trusted raw seal differs')
    intent=parse(raw)
    need(intent.get('harnessSha256')==digest(regular(__file__))
         and intent.get('capabilitySha256')==args.capability_sha256
         and intent.get('packetManifestSha256')==MANIFEST_SHA,'External intent source/authority binding differs')
    route=intent.get('routeIdentity',{})
    need(route.get('hostBootId')==capability['hostBootId']
         and route.get('repository')=='MALIEV-Co-Ltd/Legacy.Maliev.CustomerService', 'Original route machine/owner differs')
    need(intent.get('externalAdapter',{}).get('execStopPost',[])[-1:]==[str(Path(args.external_owner_receipt).absolute().parent)],
         'Outside stop-witness directory differs from exact intent path')
    prefix=[sys.executable,'-B',str(Path(__file__).absolute())]
    required={'--capability':args.capability,'--capability-sha256':args.capability_sha256,
              '--external-owner-receipt':args.external_owner_receipt,'--external-owner-sha256':args.external_owner_sha256,
              '--packet':args.packet,'--manifest-sha256':args.manifest_sha256}
    custody=load_custody_module().ExternalUnitCustody(intent,capability,
        expected_exec=None if getattr(args,'worker',False) else prefix+sys.argv[1:],
        required_exec=(prefix,required),require_main=not getattr(args,'worker',False))
    custody.verify_before_spawn([])  # Actual manager already owns this process.
    return custody


def child_limits():
    import resource
    resource.setrlimit(resource.RLIMIT_AS, (268435456, 268435456))
    resource.setrlimit(resource.RLIMIT_CPU, (30, 30))
    resource.setrlimit(resource.RLIMIT_FSIZE, (65536, 65536))


def worker(args):
    admission()
    capability=authority(args)
    bind_external(args,capability)
    custody_type = load_custody_module().ProcessCustody
    captured = sealed(Path(args.packet), args.manifest_sha256)
    process = module('kernel_sealed_process', Path(args.packet) / 'customer_guardian_process.py', captured['customer_guardian_process.py'])
    owned = module('kernel_sealed_owned', Path(args.packet) / 'customer_owned_build.py', captured['customer_owned_build.py'])
    stage = parse(captured['build-stage.json'])
    # Explicit synthetic short timing; this does NOT qualify the production120s cleanup interval.
    stage['cleanupSeconds'] = 1
    grant = {'expiresUtc': (dt.datetime.now(dt.timezone.utc) + dt.timedelta(seconds=2)).isoformat(),
             'hostBootId': Path('/proc/sys/kernel/random/boot_id').read_text().strip(), 'runId': '0' * 32}
    directory = Path(args.output)
    current = process.identity(os.getpid())
    parent = process.identity(args.parent_pid)
    expected = {'TEST_ONLY': True, 'dockerRequestsPermitted': False}
    owned = SimpleNamespace(reject_links=owned.reject_links, atomic_new=owned.atomic_new,
                            make_create_request=lambda *_: ('TEST_ONLY_NO_CONTAINER', expected))
    registration = {'guardian': current, 'parent': parent, 'owner': stage['owner'],
                    'runId': grant['runId'], 'ports': [], 'persistentData': False,
                    'memoryBytes': stage['hostRecoveryGuardian']['memoryBytes'],
                    'cpuSeconds': stage['hostRecoveryGuardian']['cpuSeconds'],
                    'rootGrantSha256': SYNTHETIC_SHA, 'expiresUtc': grant['expiresUtc'],
                    'expectedConfigDigest': process.sha(process.canonical(expected))}
    if args.scenario == 'malformed':
        registration['runId'] = '1' * 32
    save(directory / 'registration.json', registration)
    save(directory / 'launch-intent.json', {'parent': parent, 'driverPacketSha256': MANIFEST_SHA})
    def forbidden(*_args, **_kwargs):
        raise AssertionError('TEST_ONLY: Docker/network forbidden')
    socket.socket = forbidden
    class StubCustodian:
        def __init__(self, *_values):
            self.parent_alive = _values[-1]
        def create_owned(self, *_values):
            import resource
            save(directory / 'kernel-ready.json', {'TEST_ONLY_STUB_CUSTODY': True,
                 'identity': current, 'sessionId': os.getsid(0), 'actualPid': os.getpid(),
                 'limitsAS': resource.getrlimit(resource.RLIMIT_AS),
                 'limitsCPU': resource.getrlimit(resource.RLIMIT_CPU), 'dockerDispatches': 0})
        def run(self, cancelled):
            deadline = time.monotonic() + (30 if args.scenario == 'timer' else 8)
            while time.monotonic() < deadline:
                if not self.parent_alive():
                    save(directory / 'kernel-terminal.json', {'TEST_ONLY_STUB_CUSTODY': True,
                         'parentDeathObserved': True, 'dockerDispatches': 0})
                    return {'cleanupVerified': True}
                if args.scenario == 'normal':
                    save(directory / 'kernel-terminal.json', {'TEST_ONLY_STUB_CUSTODY': True,
                         'limitsObserved': True, 'dockerDispatches': 0})
                    return {'cleanupVerified': True}
                time.sleep(0.05)
            raise ValueError('Kernel scenario did not observe expected transition')
    context = {'stage': stage, 'owned': owned, 'helper': SimpleNamespace(parse_json=parse),
               'regular': regular, 'grant': grant, 'deadline': time.monotonic(),
               'guardian': SimpleNamespace(Custodian=StubCustodian), 'backend': SimpleNamespace(request=forbidden),
               'admit': forbidden}
    child_args = SimpleNamespace(guardian_directory=str(directory), ledger=str(directory.parent),
                                grant_sha256=SYNTHETIC_SHA, packet_sha256=MANIFEST_SHA)
    # Registration mismatch must fail before StubCustodian and any kernel-ready receipt.
    try:
        return process.run_guardian(context, child_args)
    except ValueError:
        if args.scenario != 'malformed':
            raise
        need(not (directory / 'kernel-ready.json').exists(), 'Malformed registration dispatched custody')
        save(directory / 'kernel-terminal.json', {'registrationRefused': True, 'dockerDispatches': 0})
        return 0


def run(args):
    admission()  # Must precede mkdir and every helper spawn.
    capability=authority(args)
    external=bind_external(args,capability)
    custody_type = load_custody_module().ProcessCustody
    captured = sealed(Path(args.packet), args.manifest_sha256)
    process = module('kernel_identity', Path(args.packet) / 'customer_guardian_process.py', captured['customer_guardian_process.py'])
    root = Path(args.output).absolute()
    for part in (root, *root.parents):
        need(not part.is_symlink(), 'Output link refused')
    need(not root.exists(), 'Fresh private output directory required')
    root.mkdir(mode=0o700)
    results = []
    for scenario in ('normal', 'malformed', 'parent-death', 'timer'):
        snapshot = admission()
        authority(args)
        target = root / scenario / 'guardian'
        target.mkdir(parents=True, mode=0o700)
        parent = None
        child = None
        identities = {}
        sequence = [0]
        def custody_writer(row):
            sequence[0] += 1
            save(target / ('custody-' + str(sequence[0]) + '.json'),
                 dict(row, owner=OWNER, scope='customer-kernel-only-lifecycle', capabilitySha256=args.capability_sha256,
                      finiteParentExpirySeconds=25, finiteChildWaitSeconds=20))
        custody = custody_type(process.identity, process.alive, custody_writer,external_owner=external)
        custody.retention_context = {'authorityExpiresUtc': authority(args).get('expiresUtc'),
            'configuredParentNaturalExpirySeconds': 25, 'childWaitTimeoutSeconds': 20,
            'actualChildTotalExpiryProved': False, 'cleanupDoesNotRenewAuthority': True,
            'state': 'cleanup-only-if-fault; never-terminal-while-retained'}
        with custody.signals(), (target / 'stdout.log').open('xb') as stdout, (target / 'stderr.log').open('xb') as stderr:
            try:
                if scenario == 'parent-death':
                    admission()
                    parent = custody.spawn([sys.executable, '-B', '-c', 'import time; time.sleep(25)'],
                        stdin=subprocess.DEVNULL, stdout=subprocess.DEVNULL, stderr=subprocess.DEVNULL,
                        start_new_session=True, preexec_fn=child_limits)
                    identities['parent'] = custody.records[-1]['identity']
                    actual_limits = Path('/proc') / str(parent.pid) / 'limits'
                    limit_lines = actual_limits.read_text().splitlines()
                    memory = [line.split()[3:5] for line in limit_lines if line.startswith('Max address space')]
                    cpu = [line.split()[3:5] for line in limit_lines if line.startswith('Max cpu time')]
                    need(memory == [['268435456', '268435456']] and cpu == [['30', '30']]
                         and os.getsid(parent.pid) == parent.pid, 'Actual disposable-parent caps/session differ')
                admission()
                authority(args)
                command = [sys.executable, '-B', str(Path(__file__).absolute()), '--kernel-only', '--worker',
                           '--packet', args.packet, '--manifest-sha256', args.manifest_sha256,
                           '--output', str(target), '--scenario', scenario,
                           '--capability', args.capability, '--capability-sha256', args.capability_sha256,
                           '--external-owner-receipt',args.external_owner_receipt,
                           '--external-owner-sha256',args.external_owner_sha256,
                           '--parent-pid', str(parent.pid if parent else os.getpid())]
                started_monotonic = time.monotonic()
                child = custody.spawn(command, stdin=subprocess.DEVNULL, stdout=stdout, stderr=stderr,
                                         start_new_session=True, preexec_fn=child_limits)
                identities['child'] = custody.records[-1]['identity']
                if parent:
                    until = time.monotonic() + 5
                    while not (target / 'kernel-ready.json').exists():
                        custody.checkpoint()
                        need(child.poll() is None and time.monotonic() < until, 'Parent-death child readiness expired')
                        time.sleep(0.05)
                    custody.stop(parent)
                code = child.wait(timeout=20)
                custody.checkpoint()
                elapsed = time.monotonic() - started_monotonic
                need(code == (124 if scenario == 'timer' else 0), 'Unexpected kernel scenario exit')
                need(not process.alive(identities['child']), 'Actual child exit not observed')
                need((target / ('lease-exhausted.json' if scenario == 'timer' else 'kernel-terminal.json')).is_file(), 'Actual kernel terminal receipt missing')
                if scenario != 'malformed':
                    ready = parse(regular(target / 'kernel-ready.json'))
                    need(ready['limitsAS'] == [268435456, 268435456]
                         and ready['limitsCPU'] == [30, 30]
                         and ready['sessionId'] == ready['actualPid'] == child.pid,
                         'Actual caps/session evidence differs')
                    need(ready['identity'] == identities['child'] and ready['dockerDispatches'] == 0,
                         'Readiness identity or no-Docker evidence differs')
                if scenario == 'parent-death':
                    need(parse(regular(target / 'kernel-terminal.json'))['parentDeathObserved'] is True,
                         'Actual parent death not observed')
                if scenario == 'malformed':
                    need(parse(regular(target / 'kernel-terminal.json'))['registrationRefused'] is True,
                         'Malformed registration not refused')
                if scenario == 'timer':
                    expired = parse(regular(target / 'lease-exhausted.json'))
                    need(15 <= elapsed <= 20 and expired['guardian'] == identities['child']
                         and expired['cleanupVerified'] is False
                         and expired['requiredAction'] == 'exact unresolved custody recovery',
                         'Real finite timer identity/elapsed evidence differs')
                results.append({'scenario': scenario, 'exitCode': code, 'identities': identities,
                                'admission': snapshot, 'dockerRequests': 0, 'cleanupVerified': True,
                                'elapsedMonotonicSeconds': elapsed, 'owner': OWNER,
                                'scope': 'customer-kernel-only-lifecycle', 'capabilitySha256': args.capability_sha256,
                                'timerDisposition': 'Expected TEST-only kernel expiry; not production cleanup success' if scenario == 'timer' else None})
            finally:
                primary_failure = sys.exc_info()[1]
                try:
                    custody.cleanup()
                except BaseException as error:
                    custody.failures.append({'phase': 'cleanup', 'failureType': type(error).__name__})
                cleanup_errors = [failure for failure in custody.failures
                                  if failure['phase'] in ('cleanup', 'durability')]
                remaining = [custody.row(r) for r in custody.records if r['process'].poll() is None]
                try:
                    save(target / 'cleanup.json', {'identities': identities, 'remaining': remaining,
                         'cleanupErrors': cleanup_errors, 'acquisitionFailures': [failure for failure in custody.failures if failure['phase'] == 'acquisition'],
                         'cleanupVerified': custody.closed and not remaining and not cleanup_errors,
                         'nativeHandleRows': [custody.row(r) for r in custody.records],
                         'externalHandoff':custody.external_handoff,'actualOutsideOwner':external.witness if external else None,
                         'owner': OWNER, 'scope': 'customer-kernel-only-lifecycle', 'capabilitySha256': args.capability_sha256,
                         'phaseFailureType': type(primary_failure).__name__ if primary_failure else None})
                except BaseException as error:
                    custody.failures.append({'phase': 'durability', 'failureType': type(error).__name__, 'operation': 'cleanup-receipt'})
                    if primary_failure is None:
                        raise
                if primary_failure is None:
                    need(custody.closed and not remaining and not cleanup_errors,
                         'Exact helper cleanup/durability unresolved; durable identity evidence retained')
        need((target / 'stdout.log').stat().st_size <= 65536 and (target / 'stderr.log').stat().st_size <= 65536, 'Output exceeded cap')
    save(root / 'kernel-report.json', {'kernelOnly': True, 'TEST_ONLY_STUB_CUSTODY': True,
         'owner': OWNER, 'scope': 'customer-kernel-only-lifecycle', 'capabilitySha256': args.capability_sha256,
         'manifestSha256': MANIFEST_SHA, 'results': results, 'nativeBuildGranted': False,
         'dockerQualified': False, 'SDKStarted': False, 'remainingHelpers': 0})
    return 0


def main():
    parser = argparse.ArgumentParser()
    parser.add_argument('--kernel-only', action='store_true')
    parser.add_argument('--source-controls', action='store_true')
    parser.add_argument('--worker', action='store_true')
    parser.add_argument('--packet')
    parser.add_argument('--manifest-sha256')
    parser.add_argument('--output')
    parser.add_argument('--scenario')
    parser.add_argument('--parent-pid', type=int)
    parser.add_argument('--capability')
    parser.add_argument('--capability-sha256')
    parser.add_argument('--external-owner-receipt')
    parser.add_argument('--external-owner-sha256')
    args = parser.parse_args()
    if args.source_controls:
        ast.parse(Path(__file__).read_bytes())
        need(MANIFEST_SHA == 'a647b1593f712317288547f39b1a5367c25d3180af4577aa250f4bca403f38cf', 'Seal constant changed')
        if sys.platform != 'linux':
            try:
                admission()
            except ValueError:
                print(json.dumps({'sourceControlsPassed': 3, 'actualLinuxRun': False, 'resourcesRemaining': 0}))
                return 0
            raise AssertionError('Windows did not refuse')
        print(json.dumps({'sourceControlsPassed': 2, 'actualLinuxRun': False}))
        return 0
    need(args.kernel_only, 'Explicit --kernel-only required')
    need(args.packet and args.output and args.manifest_sha256 == MANIFEST_SHA, 'Exact sealed packet and private output required')
    return worker(args) if args.worker else run(args)


if __name__ == '__main__':
    try:
        raise SystemExit(main())
    except (ValueError, OSError, MemoryError, subprocess.TimeoutExpired) as error:
        print(json.dumps({'kernelQualified': False, 'failureType': type(error).__name__,
                          'nativeBuildGranted': False}), file=sys.stderr)
        raise SystemExit(1)
