"""Synthetic parser controls only; these are not CRM runtime evidence."""
import copy
import json
import uuid
import xml.etree.ElementTree as E
from pathlib import Path
import verify_customer_default_page_focus as verifier

names = json.loads((Path(__file__).parent / 'customer-default-page-focused-manifest.json').read_bytes())['expectedNames']
q = lambda name: '{' + verifier.NS['t'] + '}' + name
root = E.Element(q('TestRun'))
results = E.SubElement(root, q('Results'))
definitions = E.SubElement(root, q('TestDefinitions'))
entries = E.SubElement(root, q('TestEntries'))
for index, name in enumerate(names):
    test_id = str(uuid.uuid5(uuid.NAMESPACE_URL, 'parser-control-test-' + str(index)))
    execution_id = str(uuid.uuid5(uuid.NAMESPACE_URL, 'parser-control-execution-' + str(index)))
    E.SubElement(results, q('UnitTestResult'), testName=name, testId=test_id, executionId=execution_id, outcome='Passed')
    definition = E.SubElement(definitions, q('UnitTest'), id=test_id, name=name)
    E.SubElement(definition, q('Execution'), id=execution_id)
    E.SubElement(definition, q('TestMethod'), className='Legacy.Maliev.CustomerService.Tests.Controllers.CustomerDefaultPageHttpTests', name=name.split('CustomerDefaultPageHttpTests.', 1)[1].split('(', 1)[0])
    E.SubElement(entries, q('TestEntry'), testId=test_id, executionId=execution_id)
summary = E.SubElement(root, q('ResultSummary'), outcome='Completed')
E.SubElement(summary, q('Counters'), **dict(total='13', executed='13', passed='13', **{k: '0' for k in verifier.shared.NONPASSING}))
assert len(verifier.validate(E.tostring(root), names)) == 13

def check(label, mutate):
    altered = copy.deepcopy(root)
    mutate(altered)
    try:
        verifier.validate(E.tostring(altered), names)
    except ValueError:
        print('REFUSED ' + label)
    else:
        raise AssertionError('Parser accepted ' + label)

check('unexecuted row', lambda r: r.find('t:Results/t:UnitTestResult', verifier.NS).set('outcome', 'NotExecuted'))
check('noncanonical GUID', lambda r: r.find('t:Results/t:UnitTestResult', verifier.NS).set('testId', 'not-a-guid'))
check('crossed execution', lambda r: r.find('t:TestDefinitions/t:UnitTest/t:Execution', verifier.NS).set('id', str(uuid.uuid4())))
check('unknown source case', lambda r: r.find('t:Results/t:UnitTestResult', verifier.NS).set('testName', 'Unknown'))
check('foreign class', lambda r: r.find('t:TestDefinitions/t:UnitTest/t:TestMethod', verifier.NS).set('className', 'Other'))
check('foreign method', lambda r: r.find('t:TestDefinitions/t:UnitTest/t:TestMethod', verifier.NS).set('name', 'Other'))
check('missing counter', lambda r: r.find('t:ResultSummary/t:Counters', verifier.NS).attrib.pop('aborted'))
check('failed counter', lambda r: r.find('t:ResultSummary/t:Counters', verifier.NS).set('failed', '1'))
check('noncanonical passed counter', lambda r: r.find('t:ResultSummary/t:Counters', verifier.NS).set('passed', '013'))
check('error info', lambda r: E.SubElement(r, q('ErrorInfo')))
check('unknown run info', lambda r: E.SubElement(r, q('RunInfo')))
check('incomplete entry join', lambda r: r.find('t:TestEntries', verifier.NS).remove(r.find('t:TestEntries/t:TestEntry', verifier.NS)))
print('13 parser controls passed: one positive and twelve adversarial controls; no native execution')

# Pure pin-drift controls exercise the same validator used at hosted admission.
# They run no build, HTTP host, container, or consumer.
import hashlib
import tempfile

with tempfile.TemporaryDirectory(prefix='customer-pin-controls-') as temporary:
    pin_root = Path(temporary)
    original = b'CompanyName literal UTF16 source control\n'
    (pin_root / 'source.cs').write_bytes(original)
    row = {'path': 'source.cs', 'bytes': len(original), 'sha256': hashlib.sha256(original).hexdigest()}
    verifier.shared.validate_code_pins(pin_root, [row])
    for label, altered, changed_row in (
        ('same-size source drift', original.replace(b'literal', b'changed'), row),
        ('size drift', original + b'x', row),
        ('incorrect expected size', original, dict(row, bytes=len(original) + 1)),
        ('incorrect expected hash', original, dict(row, sha256='0' * 64)),
    ):
        (pin_root / 'source.cs').write_bytes(altered)
        try:
            verifier.shared.validate_code_pins(pin_root, [changed_row])
        except ValueError:
            print('REFUSED pin ' + label)
        else:
            raise AssertionError('Pin validator accepted ' + label)
    (pin_root / 'source.cs').unlink()
    try:
        verifier.shared.validate_code_pins(pin_root, [row])
    except FileNotFoundError:
        print('REFUSED pin missing source')
    else:
        raise AssertionError('Pin validator accepted missing source')
print('6 pin controls passed: one positive and five negative; no native execution')
