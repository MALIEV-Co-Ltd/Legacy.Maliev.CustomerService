"""Synthetic Company27 parser controls; no native, Docker, HTTP or acceptance."""
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
    test_id = row['testId'] or str(uuid.uuid5(uuid.NAMESPACE_URL, 'synthetic-authored-company-' + row['name']))
    execution = str(uuid.uuid5(uuid.NAMESPACE_URL, 'synthetic-company-parser-' + str(index)))
    E.SubElement(results, q('UnitTestResult'), testName=row['name'], testId=test_id, executionId=execution, outcome='Passed')
    definition = E.SubElement(definitions, q('UnitTest'), id=test_id, name=row['name'])
    E.SubElement(definition, q('Execution'), id=execution)
    E.SubElement(definition, q('TestMethod'), codeBase='/source/Legacy.Maliev.CustomerService.Tests/bin/Release/net10.0/Legacy.Maliev.CustomerService.Tests.dll', className=row['name'].split('(', 1)[0].rsplit('.', 1)[0], name=row['name'].split('(', 1)[0].rsplit('.', 1)[1])
    E.SubElement(entries, q('TestEntry'), testId=test_id, executionId=execution)
summary = E.SubElement(root, q('ResultSummary'), outcome='Completed')
E.SubElement(summary, q('Counters'), **dict(total='27', executed='27', passed='27', **{key: '0' for key in verifier.shared.NONPASSING}))
count = 0
for label in ('Completed', 'Passed'):
    summary.set('outcome', label)
    assert verifier.validate(E.tostring(root), selected)['selectedCount'] == 27
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

# New authored rows must retain their exact compiled class/method and cannot invent actual GUIDs.
check('new Fact foreign compiled class', lambda r: r.findall('t:TestDefinitions/t:UnitTest/t:TestMethod', verifier.NS)[-1].set('className', 'Other'))
check('new Fact foreign compiled method', lambda r: r.findall('t:TestDefinitions/t:UnitTest/t:TestMethod', verifier.NS)[-1].set('name', 'Other'))
for label, index in [('invented authored actual GUID', 25), ('replaced frozen actual GUID', 0), ('replaced accepted prior4 GUID', 12)]:
    altered = copy.deepcopy(selected)
    altered[index]['testId'] = str(uuid.uuid4())
    try:
        verifier.validate(E.tostring(root), altered)
    except ValueError:
        count += 1
        print('REFUSED manifest ' + label)
    else:
        raise AssertionError('Company parser accepted ' + label)
print(str(count) + ' total Company parser controls passed; synthetic GUIDs are not runtime identities')

# Contradictory records must never hide outside the direct joins.
check('wrong root', lambda r: setattr(r, 'tag', q('Other')))
for name in ('Results', 'TestDefinitions', 'TestEntries', 'ResultSummary'):
    check('duplicate container ' + name, lambda r, name=name: r.append(copy.deepcopy(r.find('t:' + name, verifier.NS))))
    check('extra nested container ' + name, lambda r, name=name: E.SubElement(r, q('Metadata')).append(copy.deepcopy(r.find('t:' + name, verifier.NS))))
    def relocate(r, name=name):
        row = r.find('t:' + name, verifier.NS)
        r.remove(row)
        E.SubElement(r, q('Metadata')).append(row)
    check('relocated container ' + name, relocate)
for path, name in (
    ('Results/UnitTestResult', 'UnitTestResult'),
    ('TestDefinitions/UnitTest', 'UnitTest'),
    ('TestEntries/TestEntry', 'TestEntry'),
    ('TestDefinitions/UnitTest/TestMethod', 'TestMethod'),
    ('TestDefinitions/UnitTest/Execution', 'Execution'),
    ('ResultSummary/Counters', 'Counters'),
):
    qualified_path = '/'.join('t:' + part for part in path.split('/'))
    parent_path = qualified_path.rsplit('/', 1)[0]
    check('duplicate record ' + name, lambda r, path=qualified_path, parent=parent_path: r.find(parent, verifier.NS).append(copy.deepcopy(r.find(path, verifier.NS))))
    check('extra nested record ' + name, lambda r, path=qualified_path: E.SubElement(r, q('Metadata')).append(copy.deepcopy(r.find(path, verifier.NS))))
    def relocate_record(r, path=qualified_path, parent=parent_path):
        row = r.find(path, verifier.NS)
        r.find(parent, verifier.NS).remove(row)
        E.SubElement(r, q('Metadata')).append(row)
    check('relocated record ' + name, relocate_record)
    check('foreign namespace record ' + name, lambda r, path=qualified_path, name=name: setattr(r.find(path, verifier.NS), 'tag', '{urn:foreign}' + name))
check('unknown results child', lambda r: E.SubElement(r.find('t:Results', verifier.NS), q('OtherResult')))
check('nested method leaf', lambda r: E.SubElement(r.find('t:TestDefinitions/t:UnitTest/t:TestMethod', verifier.NS), q('Metadata')))
check('nested execution leaf', lambda r: E.SubElement(r.find('t:TestDefinitions/t:UnitTest/t:Execution', verifier.NS), q('Metadata')))
check('nested counters leaf', lambda r: E.SubElement(r.find('t:ResultSummary/t:Counters', verifier.NS), q('Metadata')))
print(str(count) + ' total Company parser controls passed; strict global/direct structural joins')

# Preserve the new draft's exact authored rows and all accepted16; synthetic only.
for index in range(16, 27):
    check('wrong new DLL ' + str(index), lambda r, index=index: r.findall('t:TestDefinitions/t:UnitTest/t:TestMethod', verifier.NS)[index].set('codeBase', '/source/bin/Debug/net10.0/Legacy.Maliev.CustomerService.Tests.dll'))
    check('wrong new compiled class ' + str(index), lambda r, index=index: r.findall('t:TestDefinitions/t:UnitTest/t:TestMethod', verifier.NS)[index].set('className', 'Other'))
    check('missing authored result ' + str(index), lambda r, index=index: r.find('t:Results', verifier.NS).remove(r.findall('t:Results/t:UnitTestResult', verifier.NS)[index]))
check('missing accepted prior16', lambda r: r.find('t:Results', verifier.NS).remove(r.findall('t:Results/t:UnitTestResult', verifier.NS)[15]))
check('wrong old Release DLL', lambda r: r.find('t:TestDefinitions/t:UnitTest/t:TestMethod', verifier.NS).set('codeBase', '/source/bin/Release/net9.0/Legacy.Maliev.CustomerService.Tests.dll'))
check('missing codeBase', lambda r: r.find('t:TestDefinitions/t:UnitTest/t:TestMethod', verifier.NS).attrib.pop('codeBase'))
print(str(count) + ' total Company27 parser controls passed; no native runtime acceptance')
