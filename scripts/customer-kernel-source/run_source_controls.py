"""Compile first, then one finite Windows-owned pure mock worker; no Linux execution."""
import ast
import hashlib
import importlib.util
import json
from pathlib import Path
import sys

root = Path(__file__).absolute().parent
base = root.parents[1]
output = base / 'outputs/customer-native-lifecycle-qualification-20261008-r7'
output.mkdir(exist_ok=True)
files = []
for p in sorted(root.glob('*.py')):
    b = p.read_bytes()
    ast.parse(b)
    compile(b, str(p), 'exec')
    files.append({'path': str(p), 'sha256': hashlib.sha256(b).hexdigest().upper(), 'bytes': len(b)})
old = base / 'work/customer-native-lifecycle-qualification-20261008/kernel_only.py'
assert hashlib.sha256(old.read_bytes()).hexdigest() == '52e69f6ecc92861ba30a186ae81c1d490c205034fe51fcd09aa8c9e814e12abc'
prior = base / 'work/customer-native-lifecycle-qualification-20261008-r2/kernel_only.py'
assert hashlib.sha256(prior.read_bytes()).hexdigest() == 'c642f5e91f94767cf1a77912e811862a882382848b457874d4b51cc1d73fa06b'
owned_path = base / 'work/customer_literal_owned_command.py'
spec = importlib.util.spec_from_file_location('source_controls_owned_command', owned_path)
helper = importlib.util.module_from_spec(spec)
sys.modules[spec.name] = helper
spec.loader.exec_module(helper)
completed, ledger = helper.run_owned([sys.executable, '-B', str(root / 'fault_controls.py')],
                                     timeout=20, output_limit=1048576,
                                     job_memory_bytes=268435456, cpu_rate=2500)
receipt = {'scope': 'R7 exact outside-manager binding pure fault controls',
           'files': files, 'compileFirst': True, 'exitCode': completed.returncode,
           'stdout': completed.stdout.decode('utf-8', errors='replace') if isinstance(completed.stdout, bytes) else completed.stdout,
           'stderr': completed.stderr.decode('utf-8', errors='replace') if isinstance(completed.stderr, bytes) else completed.stderr,
           'ownedResourceLedger': ledger,
           'actualLinuxExecution': False, 'nativeSDKStarts': 0, 'containers': [],
           'original52ePreserved': True, 'kernelCapabilityIssued': False,
           'sourceClearanceGranted': False}
path = output / 'source-control-receipt-final-v2.json'
path.write_text(json.dumps(receipt, indent=2) + '\n', encoding='utf-8')
print(json.dumps({'path': str(path), 'sha256': hashlib.sha256(path.read_bytes()).hexdigest().upper(),
                  'exitCode': completed.returncode, 'files': files, 'ledger': ledger}))
raise SystemExit(completed.returncode)
