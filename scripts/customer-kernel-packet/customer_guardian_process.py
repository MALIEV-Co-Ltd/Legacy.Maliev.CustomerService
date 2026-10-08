"""Finite independent host custody; no container is created by the relay."""
from __future__ import annotations
import datetime as dt
import hashlib
import json
import os
from pathlib import Path
import signal
import subprocess
import sys
import threading
import time


def need(value, message):
    if not value:
        raise ValueError(message)


def canonical(value):
    return json.dumps(value, sort_keys=True, separators=(',', ':'), allow_nan=False).encode()


def sha(raw):
    return hashlib.sha256(raw).hexdigest()


def identity(pid, proc=Path('/proc')):
    boot = (proc / 'sys/kernel/random/boot_id').read_text().strip()
    path = proc / str(pid)
    first = (path / 'stat').read_text()
    executable = os.readlink(path / 'exe')
    second = (path / 'stat').read_text()
    def ticks(raw):
        end = raw.rfind(')'); fields = raw[end + 2:].split()
        need(end > 0 and raw.split(' ', 1)[0] == str(pid) and len(fields) >= 20, 'Actual process identity required')
        return int(fields[19])
    start = ticks(first)
    need(start == ticks(second), 'Process PID reuse')
    need((proc / 'sys/kernel/random/boot_id').read_text().strip() == boot, 'Host reboot during guardian observation')
    births = [line.split()[1] for line in (proc / 'stat').read_text().splitlines() if line.startswith('btime ')]
    need(len(births) == 1 and births[0].isdigit(), 'Actual host birth time required')
    started = dt.datetime.fromtimestamp(int(births[0]) + start / os.sysconf('SC_CLK_TCK'), dt.timezone.utc)
    return {'pid': pid, 'startTicks': start, 'executable': executable, 'hostBootId': boot,
            'actualStartUtc': started.isoformat()}


def alive(expected, proc=Path('/proc')):
    try:
        return identity(expected['pid'], proc) == expected
    except (FileNotFoundError, ProcessLookupError):
        return False


class GuardedBackend:
    """The detached finite custodian owns CREATE, its acknowledgement and retry."""
    def __init__(self, backend, context, args):
        self.backend, self.context, self.args = backend, context, args
        self.directory = Path(args.ledger).absolute() / 'guardian'
        self.process = None; self.registration = None; self.receipt = None

    def request(self, method, path, body=None, headers=None):
        need(not (method == 'POST' and path.startswith('/containers/create')), 'Direct CREATE outside custodian refused')
        return self.backend.request(method, path, body=body, headers=headers)

    def json(self, method, path):
        return self.backend.json(method, path)

    def cleanup_backend_factory(self, deadline):
        return self.backend.cleanup_backend_factory(deadline)

    def read(self, name):
        raw = self.context['regular'](self.directory / name, 65536)
        return self.context['helper'].parse_json(raw), raw

    def create_owned(self, expected):
        ctx = self.context; stage = ctx['stage']; grant = ctx['grant']
        need(self.process is None and stage['hostRecoveryGuardian']['required'], 'Single independent custodian required')
        _, sealed_expected = ctx['owned'].make_create_request(stage, dict(grant, rootGrantSha256=self.args.grant_sha256))
        need(expected == sealed_expected, 'CREATE differs from Root stage')
        ctx['owned'].reject_links(self.directory)
        need(not self.directory.exists(), 'Fresh custody control directory required')
        self.directory.mkdir(mode=0o700)
        parent = identity(os.getpid())
        ctx['owned'].atomic_new(self.directory / 'launch-intent.json', {
            'parent': parent, 'rootGrantSha256': self.args.grant_sha256,
            'driverPacketSha256': self.args.packet_sha256, 'expiresUtc': grant['expiresUtc'],
            'expectedConfigDigest': sha(canonical(expected)), 'hostHelperOnly': True})
        command = [sys.executable, '-B', str(ctx['root'] / 'customer_native_build_driver.py'),
                   'execute-build', '--packet-sha256', self.args.packet_sha256,
                   '--grant', str(Path(self.args.grant).absolute()), '--grant-sha256', self.args.grant_sha256,
                   '--handoffs', str(Path(self.args.handoffs).absolute()), '--ledger', str(Path(self.args.ledger).absolute()),
                   '--guardian-child', '--guardian-directory', str(self.directory)]
        # A finite independent session survives loss of this relay; no shared process is terminated.
        with (self.directory / 'guardian.log').open('xb') as log:
            self.process = subprocess.Popen(command, stdin=subprocess.DEVNULL, stdout=log, stderr=log,
                                            close_fds=True, start_new_session=True)
        started = identity(self.process.pid)
        self.registration = {'schemaVersion': 1, 'parent': parent, 'guardian': started,
                             'owner': stage['owner'], 'runId': grant['runId'],
                             'purpose': 'independent SDK CREATE custody and bounded recovery',
                             'ports': [], 'persistentData': False,
                             'memoryBytes': stage['hostRecoveryGuardian']['memoryBytes'],
                             'cpuSeconds': stage['hostRecoveryGuardian']['cpuSeconds'],
                             'hardExpiryUtc': (dt.datetime.fromisoformat(grant['expiresUtc'].replace('Z', '+00:00'))
                                               + dt.timedelta(seconds=stage['cleanupSeconds'] + 15)).isoformat(),
                             'rootGrantSha256': self.args.grant_sha256, 'expiresUtc': grant['expiresUtc'],
                             'cleanupSeconds': stage['cleanupSeconds'], 'commandSha256': sha(canonical(command)),
                             'expectedConfigDigest': sha(canonical(expected))}
        ctx['owned'].atomic_new(self.directory / 'registration.json', self.registration)
        deadline = min(time.monotonic() + 30, ctx['deadline'])
        while time.monotonic() < deadline:
            if (self.directory / 'custody.json').exists():
                receipt, _ = self.read('custody.json')
                need(alive(started) and receipt['expectedConfigDigest'] == sha(canonical(expected))
                     and receipt['rootGrantSha256'] == self.args.grant_sha256, 'Live sealed custody required')
                self.receipt = receipt
                return receipt
            if self.process.poll() is not None:
                break
            time.sleep(0.05)
        raise ValueError('Custody acknowledgement unavailable; independent recovery retains intent')

    def ensure_custody(self, cid, spec, expected):
        need(self.registration is not None and alive(self.registration['guardian']), 'Independent custody owner absent')
        receipt, raw = self.read('custody.json')
        need(receipt == self.receipt and receipt['containerId'] == cid
             and receipt['actualCreatedRaw'] == spec['Created'] and receipt['createdSpec'] == spec
             and receipt['expectedConfigDigest'] == sha(canonical(expected))
             and receipt['daemonId'] == self.context['grant']['daemonId'], 'Actual custody identity differs')
        info = self.backend.json('GET', '/info')
        item = self.backend.json('GET', '/containers/' + cid + '/json')
        need(info.get('ID') == receipt['daemonId'] and info.get('OSType') == 'linux'
             and item.get('Id') == cid and item.get('Created') == spec['Created']
             and sha(canonical(item['Config'])) == spec['configSha256']
             and sha(canonical(item['HostConfig'])) == spec['hostConfigSha256']
             and item.get('ExecIDs') in (None, []), 'Fresh custody target drift')
        return {'verified': True, 'containerId': cid, 'actualCreatedRaw': spec['Created'],
                'expectedConfigDigest': receipt['expectedConfigDigest'],
                'checkedUtc': dt.datetime.now(dt.timezone.utc).isoformat(), 'custodyReceiptSha256': sha(raw)}

    def finish(self):
        if self.process is None:
            return {'guardianStarted': False, 'cleanupVerified': True, 'remainingOwnedHelpers': 0}
        ctx = self.context
        if not (self.directory / 'cancel.json').exists():
            ctx['owned'].atomic_new(self.directory / 'cancel.json', {
                'guardian': self.registration['guardian'] if self.registration else None,
                'rootGrantSha256': self.args.grant_sha256, 'reason': 'relay terminal; exact SDK recovery required'})
        # No name/tree kill; owner finishes its bounded recovery and publishes actual terminal evidence.
        deadline = time.monotonic() + ctx['stage']['cleanupSeconds'] + 15
        while self.process.poll() is None and time.monotonic() < deadline:
            time.sleep(0.1)
        need(self.process.poll() is not None, 'Independent owner exceeded recorded lease; retain exact identity')
        self.process.wait(timeout=0)
        terminal, _ = self.read('terminal.json')
        need(not alive(self.registration['guardian']), 'Guardian exit not verified')
        terminal['guardianExited'] = True
        terminal['actualGuardianExitCode'] = self.process.returncode
        return terminal


def run_guardian(context, args):
    """Only invoked after the exact Root grant/packet and fresh host admission."""
    import resource
    ctx = context; stage = ctx['stage']; owned = ctx['owned']
    directory = Path(args.guardian_directory).absolute()
    need(directory == Path(args.ledger).absolute() / 'guardian', 'Custody directory scope differs')
    owned.reject_links(directory)
    limit = stage['hostRecoveryGuardian']['memoryBytes']
    resource.setrlimit(resource.RLIMIT_AS, (limit, limit))
    cpu = stage['hostRecoveryGuardian']['cpuSeconds']
    resource.setrlimit(resource.RLIMIT_CPU, (cpu, cpu))
    current = identity(os.getpid()); until = time.monotonic() + stage['hostRecoveryGuardian']['registrationSeconds']
    while not (directory / 'registration.json').exists():
        need(time.monotonic() < until, 'Custodian registration expired before CREATE')
        time.sleep(0.05)
    registration = ctx['helper'].parse_json(ctx['regular'](directory / 'registration.json', 65536))
    intent = ctx['helper'].parse_json(ctx['regular'](directory / 'launch-intent.json', 65536))
    need(registration['guardian'] == current and registration['parent'] == intent['parent']
         and registration['owner'] == stage['owner'] and registration['runId'] == ctx['grant']['runId']
         and registration['persistentData'] is False and registration['ports'] == []
         and registration['memoryBytes'] == stage['hostRecoveryGuardian']['memoryBytes']
         and registration['cpuSeconds'] == stage['hostRecoveryGuardian']['cpuSeconds']
         and current['hostBootId'] == ctx['grant']['hostBootId']
         and registration['rootGrantSha256'] == args.grant_sha256
         and intent['driverPacketSha256'] == args.packet_sha256
         and registration['expiresUtc'] == ctx['grant']['expiresUtc'], 'Registered custody identity/authority differs')
    _, expected = owned.make_create_request(stage, dict(ctx['grant'], rootGrantSha256=args.grant_sha256))
    need(registration['expectedConfigDigest'] == sha(canonical(expected)), 'Custody CREATE configuration differs')
    stop = {'cancelled': False}
    def cancelled(_signum, _frame):
        stop['cancelled'] = True  # Finish acknowledgement first; never discard a real allocated full ID.
    signal.signal(signal.SIGTERM, cancelled); signal.signal(signal.SIGINT, cancelled)
    hard_seconds = max(0, ctx['deadline'] - time.monotonic()) + stage['cleanupSeconds'] + 15
    def hard_expiry():
        try:
            owned.atomic_new(directory / 'lease-exhausted.json', {
                'guardian': current, 'cleanupVerified': False, 'requiredAction': 'exact unresolved custody recovery',
                'rootGrantSha256': args.grant_sha256})
        finally:
            os._exit(124)  # Exact current helper only; never foreign SDK/process adoption.
    timer = threading.Timer(hard_seconds, hard_expiry); timer.daemon = True; timer.start()
    custodian = ctx['guardian'].Custodian(ctx['backend'], owned, stage, dict(ctx['grant'], rootGrantSha256=args.grant_sha256), args.grant_sha256,
                                        directory, lambda: alive(registration['parent']))
    try:
        custodian.create_owned(expected, ctx['admit'])
    except BaseException:
        pass  # Durable intent/actual acknowledgement remains under independent recovery ownership.
    def request_cancelled():
        if stop['cancelled']:
            return True
        path = directory / 'cancel.json'
        if path.exists():
            value = ctx['helper'].parse_json(ctx['regular'](path, 65536))
            need(value['rootGrantSha256'] == args.grant_sha256
                 and value['guardian'] == current, 'Foreign cancellation refused')
            return True
        return False
    try:
        terminal = custodian.run(cancelled=request_cancelled)
        return 0 if terminal.get('cleanupVerified') is True else 1
    finally:
        timer.cancel()
