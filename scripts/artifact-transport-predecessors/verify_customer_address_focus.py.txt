"""Exact required Address focus evidence; actual compiled GUIDs are collected, never predicted."""
import collections
import hashlib
import json
import sys
import xml.etree.ElementTree as E
from pathlib import Path

import verify_customer_email_focused as shared

ROOT = Path(__file__).resolve().parents[1]
MANIFEST = ROOT / 'scripts/customer-address-focused-manifest.json'
URI = shared.NS['t']
need = shared.need
AUTHORED_NAMES = ['Legacy.Maliev.CustomerService.Tests.Controllers.CustomerAddressLiteralLineHttpTests.OrdinaryOptionalFields_PreservePrimitiveLexemesAndMatchReferenceStrings(route: "create")', 'Legacy.Maliev.CustomerService.Tests.Controllers.CustomerAddressLiteralLineHttpTests.OrdinaryOptionalFields_PreservePrimitiveLexemesAndMatchReferenceStrings(route: "update")', 'Legacy.Maliev.CustomerService.Tests.Controllers.CustomerAddressLiteralLineHttpTests.OrdinaryOptionalFields_RefusalsAndStringBoundariesPreserveGraphAndCaches(route: "create")', 'Legacy.Maliev.CustomerService.Tests.Controllers.CustomerAddressLiteralLineHttpTests.OrdinaryOptionalFields_RefusalsAndStringBoundariesPreserveGraphAndCaches(route: "update")', 'Legacy.Maliev.CustomerService.Tests.Controllers.OrdinaryAddressPrimitiveMetadataTests.OrdinaryDefaultSlots_PreserveOwnedConvertersScopedPolicyNullsAndWrites']
ACCEPTED32_SHA256 = 'cb796e08ec502f9865e7c2819b16b47197f3cdc9ea99fc81992bbec7fe4f44f9'


def exact_rows(doc, parent, name, count):
    qualified = '{' + URI + '}' + name
    direct = parent.findall(qualified)
    global_rows = [row for row in doc.iter() if row.tag.rsplit('}', 1)[-1] == name]
    need(len(direct) == count and global_rows == direct
         and all(row.tag == qualified for row in global_rows), 'Exact direct/global ' + name)
    return direct


def validate_trx(raw, manifest):
    accepted = manifest['acceptedSelected']
    authored = manifest['authoredSelected']
    need(len(accepted) == 32 and len(authored) == 5, 'Frozen32 and exact5 authored cases')
    need(hashlib.sha256(json.dumps(accepted, sort_keys=True, separators=(',', ':')).encode()).hexdigest() == ACCEPTED32_SHA256, 'Exact accepted32 identity bytes')
    need([row['name'] for row in authored] == AUTHORED_NAMES, 'Exact five authored display names and multiplicities')
    need(all(row['testId'] is None for row in authored), 'No predicted compiled GUIDs')
    expected = [row['name'] for row in accepted + authored]
    need(len(expected) == len(set(expected)) == manifest['forecastSelectedCount'] == 37,
         'Exact37 distinct expected case names')
    doc = E.fromstring(raw)
    need(doc.tag == '{' + URI + '}TestRun', 'Exact TRX root namespace')
    containers = [exact_rows(doc, doc, name, 1)[0]
                  for name in ('Results', 'TestDefinitions', 'TestEntries')]
    results, definitions, entries = [exact_rows(doc, parent, name, 37)
                                    for parent, name in zip(containers, ('UnitTestResult', 'UnitTest', 'TestEntry'))]
    need(all(len(parent) == 37 for parent in containers), 'No substituted container children')
    need(collections.Counter(row.get('testName') for row in results) == collections.Counter(expected),
         'Every exact authored and accepted display name occurs once')
    identities = {}
    executions = {}
    for row in results:
        name = row.get('testName')
        test_id = shared.guid(row.get('testId'))
        execution_id = shared.guid(row.get('executionId'))
        need(test_id not in identities and execution_id not in executions and row.get('outcome') == 'Passed',
             'Unique passing actual test/execution identities')
        identities[test_id] = name
        executions[execution_id] = test_id
    for row in accepted:
        need(identities.get(shared.guid(row['testId'])) == row['name'], 'Frozen accepted name/GUID retention')
    defined = {}
    defined_executions = {}
    for row in definitions:
        test_id = shared.guid(row.get('id'))
        execution = row.findall('{' + URI + '}Execution')
        method = row.findall('{' + URI + '}TestMethod')
        need(len(execution) == len(method) == 1 and len(row) == 2,
             'One definition execution and method; no substitute children')
        execution_id = shared.guid(execution[0].get('id'))
        need(test_id not in defined and execution_id not in defined_executions,
             'Unique definition identities')
        name = row.get('name')
        need(identities.get(test_id) == name and executions.get(execution_id) == test_id,
             'Definition/result exact identity and execution join')
        expected_class, expected_method = name.split('(', 1)[0].rsplit('.', 1)
        need(method[0].get('className') == expected_class and method[0].get('name') == expected_method,
             'Exact definition class and method equality')
        defined[test_id] = name
        defined_executions[execution_id] = test_id
    joined_entries = {}
    for row in entries:
        test_id = shared.guid(row.get('testId'))
        execution_id = shared.guid(row.get('executionId'))
        need(execution_id not in joined_entries and executions.get(execution_id) == test_id,
             'Unique exact TestEntry joins')
        joined_entries[execution_id] = test_id
    need(defined == identities and defined_executions == executions == joined_entries,
         'Complete results/definitions/entries bijection')
    summary = exact_rows(doc, doc, 'ResultSummary', 1)[0]
    need(summary.get('outcome') == 'Completed', 'Completed exact summary')
    counters = exact_rows(doc, summary, 'Counters', 1)[0]
    required = {'total': '37', 'executed': '37', 'passed': '37'}
    required.update({key: '0' for key in shared.NONPASSING})
    need(counters.attrib == required, 'All canonical exact counters; no skip/error/extra count')
    need(not any(row.tag.rsplit('}', 1)[-1] == 'ErrorInfo' for row in doc.iter()), 'No ErrorInfo anywhere')
    for info in doc.iter():
        if info.tag.rsplit('}', 1)[-1] == 'RunInfo':
            need(info.tag == '{' + URI + '}RunInfo'
                 and info.get('outcome') in {'Passed', 'Completed', 'Information'},
                 'Bad or missing RunInfo outcome; retained shared policy')
    return {'passed': 37, 'failed': 0, 'skipped': 0,
            'actualIdentities': [{'name': name, 'testId': test_id} for test_id, name in identities.items()],
            'authoredActualIdentities': [{'name': name, 'testId': test_id} for test_id, name in identities.items()
                                        if name in {row['name'] for row in authored}],
            'executionBijection': True, 'consumerAccepted': False}


def main():
    if sys.argv[1] == 'guard':
        shared.MANIFEST = MANIFEST
        shared.guard()
    elif sys.argv[1] == 'verify':
        files = list((ROOT / 'runner-address-focused').rglob('*.trx'))
        need(len(files) == 1, 'One independently executed Address focus TRX')
        result = validate_trx(files[0].read_bytes(), json.loads(MANIFEST.read_bytes()))
        (ROOT / 'runner-address-focused/focused-proof.json').write_bytes(
            (json.dumps(result, indent=2) + '\n').encode())
    else:
        raise ValueError('Unknown phase')


if __name__ == '__main__':
    main()
