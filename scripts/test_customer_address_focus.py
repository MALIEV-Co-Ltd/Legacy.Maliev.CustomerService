"""Synthetic parser controls only; fixture GUIDs are not compiled test identities."""
import copy
import json
import uuid
import xml.etree.ElementTree as E
from pathlib import Path

import verify_customer_address_focus as validator

Q = '{' + validator.URI + '}'
MANIFEST = json.loads((Path(__file__).resolve().parent / 'customer-address-focused-manifest.json').read_bytes())


def fixture():
    doc = E.Element(Q + 'TestRun')
    results = E.SubElement(doc, Q + 'Results')
    definitions = E.SubElement(doc, Q + 'TestDefinitions')
    entries = E.SubElement(doc, Q + 'TestEntries')
    for i, row in enumerate(MANIFEST['acceptedSelected'] + MANIFEST['authoredSelected']):
        # Source parser fixtures intentionally use deterministic synthetic GUIDs.
        test_id = row['testId'] or str(uuid.uuid5(uuid.NAMESPACE_URL, 'synthetic-address-case-' + str(i)))
        execution_id = str(uuid.uuid5(uuid.NAMESPACE_URL, 'synthetic-address-execution-' + str(i)))
        E.SubElement(results, Q + 'UnitTestResult', testName=row['name'], testId=test_id,
                     executionId=execution_id, outcome='Passed')
        definition = E.SubElement(definitions, Q + 'UnitTest', id=test_id, name=row['name'])
        E.SubElement(definition, Q + 'Execution', id=execution_id)
        full_method = row['name'].split('(')[0]
        class_name, method = full_method.rsplit('.', 1)
        E.SubElement(definition, Q + 'TestMethod', className=class_name, name=method)
        E.SubElement(entries, Q + 'TestEntry', testId=test_id, executionId=execution_id)
    summary = E.SubElement(doc, Q + 'ResultSummary', outcome='Completed')
    counts = {'total': '37', 'executed': '37', 'passed': '37'}
    counts.update({key: '0' for key in validator.shared.NONPASSING})
    E.SubElement(summary, Q + 'Counters', **counts)
    E.SubElement(E.SubElement(summary, Q + 'RunInfos'), Q + 'RunInfo', outcome='Information')
    return doc


def reject(mutator):
    doc = fixture()
    mutator(doc)
    try:
        validator.validate_trx(E.tostring(doc), MANIFEST)
    except (ValueError, E.ParseError):
        return
    raise AssertionError('Malformed focused evidence admitted')


good = validator.validate_trx(E.tostring(fixture()), MANIFEST)
assert good['passed'] == 37 and len(good['authoredActualIdentities']) == 5
controls = [
    lambda d: d.find(Q + 'TestDefinitions')[32].find(Q + 'TestMethod').set('name', d.find(Q + 'TestDefinitions')[32].find(Q + 'TestMethod').get('name')[:-1]),
    lambda d: d.find(Q + 'ResultSummary').find(Q + 'RunInfos').find(Q + 'RunInfo').set('outcome', 'Failed'),
    lambda d: d.find(Q + 'ResultSummary').find(Q + 'RunInfos').find(Q + 'RunInfo').attrib.pop('outcome'),
    lambda d: d.find(Q + 'Results')[32].set('testName', MANIFEST['authoredSelected'][1]['name']),
    lambda d: d.find(Q + 'Results')[32].set('testId', d.find(Q + 'Results')[33].get('testId')),
    lambda d: d.find(Q + 'Results')[32].set('executionId', d.find(Q + 'Results')[33].get('executionId')),
    lambda d: d.find(Q + 'Results').remove(d.find(Q + 'Results')[32]),
    lambda d: d.find(Q + 'Results')[32].set('testName', MANIFEST['newMethods'][0] + 'PrefixSubstitute'),
    lambda d: d.find(Q + 'Results')[32].set('outcome', 'NotExecuted'),
    lambda d: d.find(Q + 'TestDefinitions')[32].set('id', str(uuid.uuid4())),
    lambda d: d.find(Q + 'TestDefinitions')[32].find(Q + 'Execution').set('id', str(uuid.uuid4())),
    lambda d: d.find(Q + 'TestDefinitions')[32].find(Q + 'TestMethod').set('name', 'Substitute'),
    lambda d: d.find(Q + 'TestEntries')[32].set('testId', str(uuid.uuid4())),
    lambda d: d.find(Q + 'TestEntries')[32].set('executionId', d.find(Q + 'TestEntries')[33].get('executionId')),
    lambda d: d.find(Q + 'ResultSummary').find(Q + 'Counters').set('notExecuted', '1'),
    lambda d: d.find(Q + 'ResultSummary').find(Q + 'Counters').set('passed', '037'),
    lambda d: E.SubElement(d.find(Q + 'Results')[32], Q + 'ErrorInfo'),
    lambda d: E.SubElement(d, Q + 'Results'),
    lambda d: setattr(d.find(Q + 'Results')[32], 'tag', 'UnitTestResult'),
]
for mutate in controls:
    reject(mutate)
print(json.dumps({'sourceParserControls': len(controls) + 1, 'failures': 0,
                  'syntheticOnly': True, 'nativeStarts': 0, 'compiledGuidDiscovery': 'NOT RUN'}))
