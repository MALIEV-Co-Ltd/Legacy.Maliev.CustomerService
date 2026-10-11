"""Synthetic Company10 parser controls; no native, Docker, HTTP or acceptance."""
import copy
import json
import uuid
import xml.etree.ElementTree as E
from pathlib import Path
import verify_customer_company_focus as verifier

selected = json.loads((Path(__file__).parent / 'customer-company-focused-manifest.json').read_bytes())['selected']
q = lambda name: '{' + verifier.NS['t'] + '}' + name
root = E.Element(q('TestRun'))
results = E.SubElement(root, q('Results'))
definitions = E.SubElement(root, q('TestDefinitions'))
entries = E.SubElement(root, q('TestEntries'))
for index, row in enumerate(selected):
    execution = str(uuid.uuid5(uuid.NAMESPACE_URL, 'synthetic-company-parser-' + str(index)))
    E.SubElement(results, q('UnitTestResult'), testName=row['name'], testId=row['testId'], executionId=execution, outcome='Passed')
    definition = E.SubElement(definitions, q('UnitTest'), id=row['testId'], name=row['name'])
    E.SubElement(definition, q('Execution'), id=execution)
    E.SubElement(definition, q('TestMethod'), className='Legacy.Maliev.CustomerService.Tests.Controllers.CustomerCompanyLiteralNameHttpTests', name=row['name'].split('CustomerCompanyLiteralNameHttpTests.', 1)[1].split('(', 1)[0])
    E.SubElement(entries, q('TestEntry'), testId=row['testId'], executionId=execution)
summary = E.SubElement(root, q('ResultSummary'), outcome='Completed')
E.SubElement(summary, q('Counters'), **dict(total='10', executed='10', passed='10', **{key: '0' for key in verifier.shared.NONPASSING}))
count = 0
for label in ('Completed', 'Passed'):
    summary.set('outcome', label)
    assert verifier.validate(E.tostring(root), selected)['selectedCount'] == 10
    count += 1

def check(label, mutate):
    global count
    altered = copy.deepcopy(root)
    mutate(altered)
    try:
        verifier.validate(E.tostring(altered), selected)
    except (ValueError, KeyError, IndexError, AttributeError):
        count += 1
        print('REFUSED ' + label)
    else:
        raise AssertionError('Company parser accepted ' + label)

check('unexecuted result', lambda r: r.find('t:Results/t:UnitTestResult', verifier.NS).set('outcome', 'NotExecuted'))
check('noncanonical result GUID', lambda r: r.find('t:Results/t:UnitTestResult', verifier.NS).set('testId', 'bad'))
check('empty execution GUID', lambda r: r.find('t:Results/t:UnitTestResult', verifier.NS).set('executionId', str(uuid.UUID(int=0))))
check('crossed definition execution', lambda r: r.find('t:TestDefinitions/t:UnitTest/t:Execution', verifier.NS).set('id', str(uuid.uuid4())))
check('unknown name', lambda r: r.find('t:Results/t:UnitTestResult', verifier.NS).set('testName', 'Unknown'))
check('foreign class', lambda r: r.find('t:TestDefinitions/t:UnitTest/t:TestMethod', verifier.NS).set('className', 'Other'))
check('foreign method', lambda r: r.find('t:TestDefinitions/t:UnitTest/t:TestMethod', verifier.NS).set('name', 'Other'))
check('missing entry', lambda r: r.find('t:TestEntries', verifier.NS).remove(r.find('t:TestEntries/t:TestEntry', verifier.NS)))
check('extra counter', lambda r: r.find('t:ResultSummary/t:Counters', verifier.NS).set('extra', '0'))
check('noncanonical pass count', lambda r: r.find('t:ResultSummary/t:Counters', verifier.NS).set('passed', '010'))
check('error info', lambda r: E.SubElement(r, q('ErrorInfo')))
check('missing run-info outcome', lambda r: E.SubElement(r, q('RunInfo')))
check('failed summary', lambda r: r.find('t:ResultSummary', verifier.NS).set('outcome', 'Failed'))
for key in verifier.shared.NONPASSING:
    check('missing ' + key, lambda r, key=key: r.find('t:ResultSummary/t:Counters', verifier.NS).attrib.pop(key))
    check('nonzero ' + key, lambda r, key=key: r.find('t:ResultSummary/t:Counters', verifier.NS).set(key, '1'))
print(str(count) + ' Company parser controls passed; synthetic only, no native execution')

# Exercise the exact build predicate with synthetic logs before hosted execution.
verifier.validate_build('Build succeeded.\n0 Warning(s)\n0 Error(s)\n')
build_count = 1
for label, build in (
    ('warnings', 'Build succeeded.\n0 Warning(s)\n0 Error(s)\n1 Warning(s)'),
    ('errors', 'Build succeeded.\n0 Warning(s)\n0 Error(s)\n1 Error(s)'),
    ('no success', '0 Warning(s)\n0 Error(s)'),
    ('missing warning count', 'Build succeeded.\n0 Error(s)'),
    ('missing error count', 'Build succeeded.\n0 Warning(s)'),
):
    try:
        verifier.validate_build(build)
    except ValueError:
        build_count += 1
        print('REFUSED build ' + label)
    else:
        raise AssertionError('Company build predicate accepted ' + label)
print(str(build_count) + ' build controls passed; synthetic only, no native execution')
