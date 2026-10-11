"""Read-only resource receipts; uncertain container ownership never authorizes kills."""
import datetime
import json
import os
import subprocess
import selectors
import sys
import time
from pathlib import Path
import verify_customer_email_focused as shared

ROOT = Path(__file__).resolve().parents[1]
FOLDER = ROOT / 'runner-default-focused'

def calls_valid(rows):
    return isinstance(rows, list) and bool(rows) and all(isinstance(x, dict) and x.get('terminalVerified') and x.get('identityObserved') and x.get('actualStartTicks') and x.get('actualExecutable') and x.get('exitCode') == 0 and not x.get('outputRefused') and not x.get('timedOut') and not x.get('observationErrorType') and not x.get('cleanupErrorType') for x in rows)

def container_ids(rows):
    if not isinstance(rows, list) or len(rows) > 64:
        return None
    if any(not isinstance(x, dict) or not isinstance(x.get('id'), str) or len(x['id']) != 64 or any(c not in '0123456789abcdef' for c in x['id']) for x in rows):
        return None
    ids = {x['id'] for x in rows}
    return ids if len(ids) == len(rows) else None

def initial_receipt_state(baseline, receipt):
    bound = isinstance(baseline, dict) and baseline.get('stage') == 'initial' and all(receipt.get(k) and baseline.get(k) == receipt[k] for k in ('githubSha', 'runId', 'runAttempt', 'job'))
    ids = container_ids(baseline.get('containers')) if bound else None
    valid = bool(bound) and ids is not None and baseline.get('nativeProcesses') == [] and baseline.get('unknownProcesses') == [] and calls_valid(baseline.get('ownedObservationCommands'))
    return bool(bound), bool(valid), ids if valid else None

def command(args):
    row = {'requestedExecutable': args[0], 'timeoutSeconds': 10, 'outputLimitBytesPerStream': 1048576, 'terminalVerified': False}
    process = None
    selector = selectors.DefaultSelector()
    buffers = {'stdout': bytearray(), 'stderr': bytearray()}
    try:
        process = subprocess.Popen(args, stdout=subprocess.PIPE, stderr=subprocess.PIPE, bufsize=0)
        row['pid'] = process.pid
        directory = Path('/proc') / str(process.pid)
        try:
            row['actualExecutable'] = os.readlink(directory / 'exe')
            row['actualStartTicks'] = (directory / 'stat').read_text().rsplit(')', 1)[1].split()[19]
            boot = next(int(line.split()[1]) for line in Path('/proc/stat').read_text().splitlines() if line.startswith('btime '))
            row['actualStartUtc'] = datetime.datetime.fromtimestamp(boot + int(row['actualStartTicks']) / os.sysconf('SC_CLK_TCK'), datetime.timezone.utc).isoformat()
            row['identityObserved'] = True
        except (OSError, ValueError, IndexError, StopIteration):
            row['identityObserved'] = False
        for name, stream in [('stdout', process.stdout), ('stderr', process.stderr)]:
            os.set_blocking(stream.fileno(), False)
            selector.register(stream, selectors.EVENT_READ, name)
        deadline = time.monotonic() + 10
        while selector.get_map():
            remaining = deadline - time.monotonic()
            if remaining <= 0:
                row['timedOut'] = True
                return None, row
            for key, _ in selector.select(min(remaining, 0.1)):
                block = os.read(key.fileobj.fileno(), 65536)
                if not block:
                    selector.unregister(key.fileobj)
                    continue
                if len(buffers[key.data]) + len(block) > 1048576:
                    row['outputRefused'] = True
                    return None, row
                buffers[key.data].extend(block)
        process.wait(timeout=max(0.01, deadline - time.monotonic()))
        row['exitCode'] = process.returncode
        if process.returncode or not row.get('identityObserved'):
            return None, row
        return buffers['stdout'].decode('utf-8'), row
    except (OSError, subprocess.TimeoutExpired, UnicodeError) as error:
        row['observationErrorType'] = type(error).__name__
        return None, row
    finally:
        selector.close()
        if process is not None:
            if process.poll() is None:
                # Only this exact task-created observation command is signalled.
                try:
                    process.terminate()
                    process.wait(timeout=0.5)
                except subprocess.TimeoutExpired:
                    try:
                        process.kill()
                        process.wait(timeout=1)
                    except subprocess.TimeoutExpired:
                        row['cleanupErrorType'] = 'TerminalWaitExpired'
                    except OSError as error:
                        row['cleanupErrorType'] = type(error).__name__
                except OSError as error:
                    row['cleanupErrorType'] = type(error).__name__
            row['exitCode'] = process.poll()
            row['terminalVerified'] = process.poll() is not None
            row['capturedStdoutBytes'] = len(buffers['stdout'])
            row['capturedStderrBytes'] = len(buffers['stderr'])
            process.stdout.close()
            process.stderr.close()

def capture(stage):
    shared.need(sys.platform == 'linux' and os.environ.get('GITHUB_REPOSITORY') == 'MALIEV-Co-Ltd/Legacy.Maliev.CustomerService', 'Exact hosted repository required')
    shared.need(stage in {'initial', 'terminal'}, 'Exact observation stage required')
    FOLDER.mkdir(exist_ok=True)
    census = []
    for directory in Path('/proc').iterdir():
        if not directory.name.isdigit():
            continue
        def start_ticks():
            try:
                return (directory / 'stat').read_text().rsplit(')', 1)[1].split()[19]
            except (OSError, IndexError):
                return None
        before = start_ticks()
        identity = shared.read_process_identity(directory)
        if identity is None:
            continue
        middle = start_ticks()
        identity_after = shared.read_process_identity(directory)
        after = start_ticks()
        executable, source = identity
        row = {'pid': int(directory.name), 'identity': executable, 'identitySource': source}
        if before is not None and before == middle == after and identity == identity_after:
            row['actualStartTicks'] = before
            row['stableIdentityObserved'] = True
        else:
            row['actualStartTicks'] = None
            row['stableIdentityObserved'] = False
            row['identity'] = None
            row['identitySource'] = 'unknown-unstable-process-observation'
        census.append(row)
    raw_ids, first = command(['docker', 'ps', '--all', '--quiet', '--no-trunc'])
    calls = [first]
    containers = None
    if raw_ids is not None:
        ids = raw_ids.split()
        containers = []
        if len(ids) > 64 or len(ids) != len(set(ids)) or any(len(x) != 64 or any(c not in '0123456789abcdef' for c in x) for x in ids):
            containers = None
        elif ids:
            template = '{"id":{{json .Id}},"created":{{json .Created}},"running":{{json .State.Running}},"pid":{{json .State.Pid}},"started":{{json .State.StartedAt}},"sessionLabel":{{json (index .Config.Labels "org.testcontainers.session-id")}},"testcontainersLabel":{{json (index .Config.Labels "org.testcontainers")}},"memoryBytes":{{json .HostConfig.Memory}},"nanoCpus":{{json .HostConfig.NanoCpus}},"portBindings":{{json .HostConfig.PortBindings}}}'
            inspected, row = command(['docker', 'inspect', '--format', template, *ids])
            calls.append(row)
            try:
                containers = None if inspected is None else [json.loads(line) for line in inspected.splitlines()]
                if containers is not None and container_ids(containers) != set(ids):
                    containers = None
            except (ValueError, KeyError, TypeError):
                containers = None
    native = [r for r in census if r['identity'] is not None and r['identity'].lower() in {'dotnet', 'testhost', 'msbuild', 'vbcscompiler'}]
    unknown = [r for r in census if r['identity'] is None or r['actualStartTicks'] is None]
    receipt = {'stage': stage, 'observedUtc': datetime.datetime.now(datetime.timezone.utc).isoformat(), 'githubSha': os.environ.get('GITHUB_SHA'), 'runId': os.environ.get('GITHUB_RUN_ID'), 'runAttempt': os.environ.get('GITHUB_RUN_ATTEMPT'), 'job': os.environ.get('GITHUB_JOB'), 'observerPid': os.getpid(), 'observerExecutable': sys.executable, 'nativeProcesses': native, 'unknownProcesses': unknown, 'containers': containers, 'ownedObservationCommands': calls, 'killsOfNativeOrContainers': False, 'persistentDataChanged': False}
    if stage == 'terminal':
        initial = FOLDER / 'resources-initial.json'
        try:
            baseline = json.loads(initial.read_bytes()) if initial.exists() else None
        except (OSError, ValueError):
            baseline = None
        bound, valid, baseline_ids = initial_receipt_state(baseline, receipt)
        leftovers = [r for r in containers if r['id'] not in baseline_ids] if containers is not None and baseline_ids is not None else None
        receipt.update(initialReceiptBound=bool(bound), initialBaselineValid=bool(valid), newContainerCandidatesRemaining=leftovers, containerOwnershipQualified=False, cleanupAbsenceObserved=bool(valid) and calls_valid(calls) and not native and not unknown and leftovers == [], vmExpiryIsOnlyContainment=True)
    (FOLDER / ('resources-' + stage + '.json')).write_text(json.dumps(receipt, indent=2) + '\n')
    print(json.dumps(receipt))
    if stage == 'terminal':
        shared.need(receipt['cleanupAbsenceObserved'], 'Actual final absence unqualified; retain resource receipt, do not kill uncertain resources')

if __name__ == '__main__':
    stage = sys.argv[1]
    try:
        capture(stage)
    except Exception as error:
        # Ordinary observation failures retain an explicitly unqualified receipt.
        # Forced process/job cancellation can still prevent any terminal receipt;
        # an absent receipt never establishes release and VM expiry is containment.
        if stage in {'initial', 'terminal'}:
            FOLDER.mkdir(exist_ok=True)
            path = FOLDER / ('resources-' + stage + '.json')
            try:
                receipt = json.loads(path.read_bytes()) if path.exists() else {}
            except (OSError, ValueError):
                receipt = {}
            receipt.update(stage=stage, observedUtc=datetime.datetime.now(datetime.timezone.utc).isoformat(), githubSha=os.environ.get('GITHUB_SHA'), runId=os.environ.get('GITHUB_RUN_ID'), runAttempt=os.environ.get('GITHUB_RUN_ATTEMPT'), job=os.environ.get('GITHUB_JOB'), observationErrorType=type(error).__name__, cleanupAbsenceObserved=False, vmExpiryIsOnlyContainment=True)
            path.write_text(json.dumps(receipt, indent=2) + '\n')
            print(json.dumps(receipt))
        raise SystemExit(1) from None
