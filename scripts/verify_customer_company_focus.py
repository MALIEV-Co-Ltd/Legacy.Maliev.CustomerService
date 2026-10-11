"""Exact separately executed Company21 proof; no full or consumer acceptance."""
import collections
import hashlib
import json
import os
import re
import sys
import xml.etree.ElementTree as E
from pathlib import Path

import verify_customer_email_focused as shared

ROOT = Path(__file__).resolve().parents[1]
MANIFEST = ROOT / 'scripts/customer-company-focused-manifest.json'
FOLDER = ROOT / 'runner-default-focused'
NS = shared.NS
need = shared.need

def exact_records(doc, parent, name, count):
    tag = '{' + NS['t'] + '}' + name
    direct = parent.findall('t:' + name, NS)
    global_rows = [row for row in doc.iter() if row.tag.rsplit('}', 1)[-1] == name]
    need(len(direct) == count and global_rows == direct and all(row.tag == tag for row in global_rows), 'Exact global/direct ' + name)
    return direct

def validate(raw, selected):
    need(len(selected) == 21 and hashlib.sha256(json.dumps(selected[:16], sort_keys=True, separators=(',', ':')).encode()).hexdigest() == '46983af8d84e34b5233e4cfb70a781b48801e311520d48816608c8ae8e9a2b6c', 'Frozen accepted16 actual name/GUID identities retained')
    authored = ['Legacy.Maliev.CustomerService.Tests.Controllers.CompanyOrdinaryDuplicateNullReferenceTests.OrdinaryDuplicateNames_MatchPinnedReferenceForOrderedNullAndEscapedCaseVariants', 'Legacy.Maliev.CustomerService.Tests.Controllers.CustomerCompanyOrdinaryDuplicateNullHttpTests.OrdinaryDuplicateNames_PersistLastNonNullNameAndOnlyIntendedGraphAndCacheEffects', 'Legacy.Maliev.CustomerService.Tests.Controllers.CustomerCompanyOrdinaryDuplicateNullHttpTests.OrdinaryDuplicateNames_InvalidOccurrenceCannotHideBehindLaterValuesOrMutateGraph', 'Legacy.Maliev.CustomerService.Tests.Controllers.CustomerCompanyOrdinaryDuplicateNullHttpTests.OrdinaryDuplicateNames_InvalidAuthorityAndScopedLiteralPolicyCannotChangeGraphOrCache', 'Legacy.Maliev.CustomerService.Tests.Controllers.CompanyOrdinaryMvcMetadataControls.OrdinaryDuplicateNames_PreserveOtherFieldsAndPublicRecordAndWireContracts']
    need([row['name'] for row in selected[16:]] == authored and all(row['testId'] is None for row in selected[16:]), 'Five authored compiled Facts; no invented actual GUIDs')
    expected_names = [row['name'] for row in selected]
    expected_known = [(row['name'], shared.guid(row['testId'])) for row in selected[:16]]
    doc = E.fromstring(raw)
    need(doc.tag == '{' + NS['t'] + '}TestRun', 'Exact TRX TestRun root')
    containers = [exact_records(doc, doc, name, 1)[0] for name in ('Results', 'TestDefinitions', 'TestEntries')]
    results, definitions, entries = [exact_records(doc, parent, name, 21) for parent, name in zip(containers, ('UnitTestResult', 'UnitTest', 'TestEntry'))]
    need(all(len(parent) == 21 for parent in containers), 'Only canonical records in join containers')
    all_methods = doc.findall('.//t:TestMethod', NS)
    need(len(all_methods) == 21 and len([r for r in doc.iter() if r.tag.rsplit('}', 1)[-1] == 'TestMethod']) == 21, 'Exactly21 global compiled methods')
    need(len(doc.findall('.//t:Execution', NS)) == 21 and len([r for r in doc.iter() if r.tag.rsplit('}', 1)[-1] == 'Execution']) == 21, 'Exactly21 global definition executions')
    need(len(results) == len(definitions) == len(entries) == 21, 'Exactly21 complete joins required')
    by_id = {shared.guid(row.get('id')): row for row in definitions}
    need(len(by_id) == 21, 'Unique21 definition GUIDs')
    pairs = []
    identities = []
    for row in results:
        test_id = shared.guid(row.get('testId'))
        execution_id = shared.guid(row.get('executionId'))
        need(test_id in by_id and row.get('outcome') == 'Passed', 'Known passing result required')
        definition = by_id[test_id]
        executions = definition.findall('t:Execution', NS)
        need(len(executions) == 1 and shared.guid(executions[0].get('id')) == execution_id and definition.get('name') == row.get('testName'), 'Definition execution/name join')
        methods = definition.findall('t:TestMethod', NS)
        need(len(methods) == 1 and len(executions[0]) == 0 and len(methods[0]) == 0, 'Singleton direct compiled method and leaf execution')
        method = methods[0]
        name = row.get('testName')
        qualified = name.split('(', 1)[0]
        expected_class, expected_method = qualified.rsplit('.', 1)
        need(method is not None and method.get('className') == expected_class, 'Exact compiled Company class')
        need(method.get('name') == expected_method, 'Exact compiled Company method')
        code_base = method.get('codeBase', '').replace('\\', '/')
        need(code_base.endswith('/bin/Release/net10.0/Legacy.Maliev.CustomerService.Tests.dll'), 'Exact Release/net10.0 Customer test DLL')
        pairs.append((test_id, execution_id))
        identities.append((name, test_id))
    entry_pairs = [(shared.guid(row.get('testId')), shared.guid(row.get('executionId'))) for row in entries]
    need(len({row[0] for row in pairs}) == len({row[1] for row in pairs}) == len({row[0] for row in entry_pairs}) == len({row[1] for row in entry_pairs}) == 21, 'Unique tests/executions')
    need(collections.Counter(pairs) == collections.Counter(entry_pairs), 'Entry execution bijection')
    need(collections.Counter(name for name, _ in identities) == collections.Counter(expected_names), 'Exact21 compiled names')
    need(collections.Counter((name, test_id) for name, test_id in identities if name not in authored) == collections.Counter(expected_known), 'Frozen actual16 GUID/name multiset')
    summaries = exact_records(doc, doc, 'ResultSummary', 1)
    need(len(summaries) == 1 and summaries[0].get('outcome') in {'Passed', 'Completed'}, 'Passing summary required')
    counters = exact_records(doc, summaries[0], 'Counters', 1)
    expected_counters = dict(total='21', executed='21', passed='21', **{key: '0' for key in shared.NONPASSING})
    need(len(counters[0]) == 0 and counters[0].attrib == expected_counters, 'Exactly21 canonical clean counters')
    need(doc.find('.//t:ErrorInfo', NS) is None, 'ErrorInfo forbidden')
    need(all(row.get('outcome') in {'Passed', 'Completed', 'Information'} for row in doc.findall('.//t:RunInfo', NS)), 'RunInfo outcome required')
    return {'selectedCount': 21, 'summaryOutcome': summaries[0].get('outcome'), 'executionBijection': True, 'all21CountersCanonical': True, 'accepted16NameGuidRowsFrozen': True, 'authored5ObservedNameGuidRows': [{'name': name, 'testId': test_id} for name, test_id in identities if name in authored], 'full569IdentityJoinAccepted': False}

def validate_build(build):
    need('Build succeeded.' in build and re.search(r'0 Warning\(s\)', build) and re.search(r'0 Error\(s\)', build) and not re.search(r'\b[1-9]\d* (?:Warning|Error)\(s\)', build), 'Actual strict Release0WE')

def guard():
    shared.MANIFEST = MANIFEST
    shared.guard()

def verify():
    manifest = json.loads(MANIFEST.read_bytes())
    files = list(FOLDER.rglob('*.trx'))
    need(len(files) == 1, 'One original separately executed focused TRX')
    raw = files[0].read_bytes()
    structure = validate(raw, manifest['selected'])
    build = (FOLDER / 'build.log').read_text()
    validate_build(build)
    result = {'actualFocusedPassed': 21, 'failed': 0, 'skipped': 0, 'trxSha256': hashlib.sha256(raw).hexdigest(), 'strictStructure': structure, 'accepted16IdentityRun': manifest['identityRun'], 'githubSha': os.environ.get('GITHUB_SHA'), 'runId': os.environ.get('GITHUB_RUN_ID'), 'strictRelease0WE': True, 'full569Accepted': False, 'consumerAccepted': False, 'originalPrimitiveParityAccepted': False, 'authored5ActualGuidsCollectedFromThisTrx': True, 'full569IdentityJoinAccepted': False}
    (FOLDER / 'focused-proof.json').write_text(json.dumps(result, indent=2) + '\n')
    print(json.dumps(result))

if __name__ == '__main__':
    {'guard': guard, 'verify': verify}[sys.argv[1]]()
