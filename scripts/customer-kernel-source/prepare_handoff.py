"""Seal source successor and existing actual pure receipt; do not execute tests or helpers."""
import ast
import difflib
import hashlib
import json
from pathlib import Path
import re
import kernel_only

root = Path(__file__).absolute().parent
base = root.parents[1]
out = base / 'outputs/customer-native-lifecycle-qualification-20261008-r7'
out.mkdir(exist_ok=True)
original = base / 'work/customer-native-lifecycle-qualification-20261008/kernel_only.py'
assert hashlib.sha256(original.read_bytes()).hexdigest() == '52e69f6ecc92861ba30a186ae81c1d490c205034fe51fcd09aa8c9e814e12abc'
old = base / 'work/customer-native-lifecycle-qualification-20261008-r6/kernel_only.py'
old_raw = old.read_bytes()
assert hashlib.sha256(old_raw).hexdigest() == 'af96ef4ff174bd4ab88f39a03c90414fce97b4dde0263818e258af34d2cb806b'
packet = base / 'outputs/customer-native-build-first-20261008-r2/packet'
captured = kernel_only.sealed(packet, kernel_only.MANIFEST_SHA)
assert len(captured) == 17
close_repro = base/'outputs/customer-kernel-publication-preparation-20261008/r5-close-retry-causal-reproduction.json'
assert hashlib.sha256(close_repro.read_bytes()).hexdigest() == '3f7d27cb560f5c8889127f4f60b0c66c20fa8b4cadf2c37b20a3ee25ee1a16b6'
assert hashlib.sha256((root / 'owned_process.py').read_bytes()).hexdigest() == kernel_only.OWNER_MODULE_SHA
adapter_path=base/'work/customer-kernel-same-allocation-route-20261008-r7-bind/external_unit_owner.py'
assert hashlib.sha256(adapter_path.read_bytes()).hexdigest()==kernel_only.load_custody_module().EXPECTED_EXTERNAL_ADAPTER_SHA
rows = []
for p in sorted(root.iterdir()):
    if not p.is_file():
        continue
    raw = p.read_bytes()
    if p.suffix == '.py':
        ast.parse(raw)
        compile(raw, str(p), 'exec')
    rows.append({'path': str(p), 'sha256': hashlib.sha256(raw).hexdigest().upper(), 'bytes': len(raw)})
receipt_path = out / 'source-control-receipt-final-v2.json'
raw = receipt_path.read_bytes()
receipt = json.loads(raw)
assert receipt['exitCode'] == 0 and receipt['ownedResourceLedger']['cleanup_verified'] is True
assert receipt['ownedResourceLedger']['remaining_job_processes'] == 0
match = re.search(r'Ran (\d+) tests', receipt['stderr'])
assert match and 'FAILED' not in receipt['stderr'] and '\nOK' in receipt['stderr']
assert int(match.group(1)) == 44
patch = ''
for name in ('kernel_only.py', 'owned_process.py'):
    before = (old.parent/name).read_text()
    patch += ''.join(difflib.unified_diff(before.splitlines(True), (root/name).read_text().splitlines(True),
                                         fromfile='immutable-r6/'+name, tofile='r7/'+name))
patch_path = out / 'kernel-source-successor-v2.patch'
patch_path.write_text(patch, encoding='utf-8')
outer = [json.loads(line)['outerRunEvidence'] for line in receipt['stdout'].splitlines()
         if line.startswith('{') and 'outerRunEvidence' in line]
red_path=out/'prior-r6-causal-red.json'
assert hashlib.sha256(red_path.read_bytes()).hexdigest()=='40584d5079cdb05ec90b322424bbcd6dd2ca5b229524bc1af1876c37dc36b40e'
red=json.loads(red_path.read_bytes())
assert red['exitCode']==0 and red['ownedResourceLedger']['cleanup_verified'] is True
assert red['ownedResourceLedger']['remaining_job_processes']==0
report = {'scope': 'R7 exact prearmed outside systemd/cgroup custody; source-only Root review required',
          'files': rows, 'sourceCompiled': True, 'pureFaultControlsPassed': int(match.group(1)),
          'pureFaultControlsFailed': 0, 'controlReceipt': {'path': str(receipt_path), 'sha256': hashlib.sha256(raw).hexdigest().upper()},
          'ownedResourceLedger': receipt['ownedResourceLedger'],
          'original52ePreserved': True, 'originalR6KernelPreserved': True, 'original17PacketSealsMatch': True,
          'causalSourceRedBeforeFix':{'path':str(red_path),'sha256':hashlib.sha256(red_path.read_bytes()).hexdigest().upper(),
              'result':'ImmutableR6 Linux mock dispatched without required outside proof','nativeRuntimeRed':False,
              'ownedResourceLedger':red['ownedResourceLedger']},
          'outerRunMockEvidence': outer,
          'preimageSHA': hashlib.sha256(old_raw).hexdigest().upper(),
          'patch': {'path': str(patch_path), 'sha256': hashlib.sha256(patch_path.read_bytes()).hexdigest().upper()},
          'capabilityFields': ['schemaVersion','scope','rootIssued','harnessSha256','packetManifestSha256','hostBootId','issuedUtc','expiresUtc'],
          'capabilityCreated': False, 'sourceClearanceGranted': False,
          'actualLinuxRuns': 0, 'actualGuardianStarts': 0, 'SDKStarts': 0, 'DockerLifecycleQualified': False,
          'requiredAction': 'Root independently review exact R7 kernel+owner and separately sealed existing-pattern route adapter, then externally issue exact machine-bound authority. No sourceclear/native/systemd grant is issued here.',
          'externalOwnerContract':{'intentSchemaFields':14,'rootCapabilityFieldsUnchanged':8,'maximumOriginalAuthoritySeconds':600,
              'maximumLocalCleanupSeconds':60,'actualManagerStopStagesBudgetMultiplier':5,'timeoutStartUSec':10000000,
              'requiredActualChecks':['unit','description','InvocationID','boot','MainPID/startTicks/executable','ExecStart',
                 'ExecStopPost/sealedadapter','ExecStop empty','Type exec','NotifyAccess none','all caps',
                 'self+allretainedchild memberships','stable full subtree census','absolute activation+runtime+5stop inside expiry'],
              'handoff':'Fresh actual same manager and membership only; cleanup-only failure, terminalSuccessfalse; outside independent stop/empty census stillrequired'},
          'exceptionCorrection': 'Original exception object and traceback remain primary; acquisition failures are not cleanup failures. Cleanup/durability uncertainties are recorded separately. After cleanup normal signal-scope exit delivers retained interruption and prevents qualified success.',
          'soleCustodyCorrection': 'Signal/wait supervision retained until exact exit/reap. Close invoked only once. Native Linux OSError retires descriptor number with sticky diagnostic; custom/pre-syscall failure keeps retirement unproved with no retry. Bound waits do not establish finite total supervision.',
          'closeAuthoritySource': {'url': 'https://man7.org/linux/man-pages/man2/close.2.html', 'lines': [125,149], 'readThisTurn': True},
          'r5ConfirmedDefectReceipt': {'path': str(close_repro), 'sha256': '3F7D27CB560F5C8889127F4F60B0C66C20FA8B4CADF2C37B20A3EE25EE1A16B6', 'hashReverified': True},
          'acquisitionCorrection': 'Retained Popen child enters custody before identity/fsync; pidfd or exact unreaped-child binding governs cleanup even when metadata fails; signals defer across acquisition; durability/fdclose/cleanup uncertainty blocks qualification.',
          'limitations': ['No actual Linux/process/Docker qualification; all Popen/pidfd/signal operations in controls mocked',
                         'If binding is permanently unobservable and existing child expiry is not provable, safe finite total supervision termination is impossible. Custody remains held; each wait/attempt is bounded; no handoff or release is invented',
                         'Synthetic short timer and worker registration do not qualify production cleanup interval or unmodified parent CLI',
                         'Prior rejected source remains rejected; this source has not been independently cleared'],
          'actualFiniteTotalCleanupQualified': False,'systemdStarts':0,'RootCapabilityCreated':False,
          'explicitExternalAdapterPin':{'sha256':kernel_only.load_custody_module().EXPECTED_EXTERNAL_ADAPTER_SHA,
              'path':str(adapter_path),'actualFileHashVerified':True,
              'callerChosenSelfConsistentSourceRejected':True,'postBindingSourceTamperRejected':True},
          'failedCloseNativeSemanticsQualified': False,
          'r3PrecedenceReproduction': {'toolChunk': '9f936a', 'exitCode': 0,
              'actualException': 'OwnershipError', 'requiredPrimary': 'DeferredInterruption',
              'pendingSignal': 'SIGTERM', 'recordedSecondary': 'durability/OSError',
              'allProcessSignalAPIsMocked': True},
          'preservedFailureEvidence': ['../customer-native-lifecycle-qualification-20261008-r3/source-control-receipt-initial-windows-shim-failure.json','../customer-native-lifecycle-qualification-20261008-r3/source-control-receipt-mock-clock-failure.json'],
          'earlierToolFailures': [{'chunk':'d794f2','exit':1,'reason':'mock clock reset made elapsed evidence zero; corrected monotonically accumulating mock clock; private job verified0remaining'}],
          'resourcesRemaining': 0}
path = out / 'source-handoff-final-v2.json'
path.write_text(json.dumps(report, indent=2)+'\n', encoding='utf-8')
print(json.dumps({'path': str(path), 'sha256': hashlib.sha256(path.read_bytes()).hexdigest().upper(),
                  'passed': int(match.group(1)), 'files': rows, 'resourcesRemaining': 0}))
