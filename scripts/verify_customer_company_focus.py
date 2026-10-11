"""Exact separately executed Company16 proof; no full or consumer acceptance."""
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
    need(len(selected) == 16 and hashlib.sha256(json.dumps(selected[:12], sort_keys=True, separators=(',', ':')).encode()).hexdigest() == '50407090793a119dfbf6486f4576b3e3f8b0c023d6f31e3216f4a78ed300275c', 'Frozen original12 name/GUID identities retained')
    authored = ['Legacy.Maliev.CustomerService.Tests.Controllers.CustomerCompanyOrdinaryPrimitiveHttpTests.OrdinaryPrimitiveNames_PersistExactLexemesAndOnlyIntendedGraphAndCacheEffects', 'Legacy.Maliev.CustomerService.Tests.Controllers.CustomerCompanyOrdinaryPrimitiveHttpTests.OrdinaryNumericNames_PreserveOverflowAndSpellingOrRefuseOver256Units', 'Legacy.Maliev.CustomerService.Tests.Controllers.CustomerCompanyOrdinaryPrimitiveHttpTests.OrdinaryPrimitiveNames_InvalidAuthorityCannotChangeGraphOrCache', 'Legacy.Maliev.CustomerService.Tests.Controllers.CompanyOrdinaryPrimitiveReferenceTests.OrdinaryNameConverter_MatchesPinnedReferenceLexemesAcrossCulturesAndSegmentedInput']
    need([row['name'] for row in selected[12:]] == authored and all(row['testId'] is None for row in selected[12:]), 'Four authored compiled Facts; no invented actual GUIDs')
    expected_names = [row['name'] for row in selected]
    expected_known = [(row['name'], shared.guid(row['testId'])) for row in selected[:12]]
    doc = E.fromstring(raw)
    need(doc.tag == '{' + NS['t'] + '}TestRun', 'Exact TRX TestRun root')
    containers = [exact_records(doc, doc, name, 1)[0] for name in ('Results', 'TestDefinitions', 'TestEntries')]
    results, definitions, entries = [exact_records(doc, parent, name, 16) for parent, name in zip(containers, ('UnitTestResult', 'UnitTest', 'TestEntry'))]
    need(all(len(parent) == 16 for parent in containers), 'Only canonical records in join containers')
    all_methods = doc.findall('.//t:TestMethod', NS)
    need(len(all_methods) == 16 and len([r for r in doc.iter() if r.tag.rsplit('}', 1)[-1] == 'TestMethod']) == 16, 'Exactly16 global compiled methods')
    need(len(doc.findall('.//t:Execution', NS)) == 16 and len([r for r in doc.iter() if r.tag.rsplit('}', 1)[-1] == 'Execution']) == 16, 'Exactly16 global definition executions')
    need(len(results) == len(definitions) == len(entries) == 16, 'Exactly16 complete joins required')
    by_id = {shared.guid(row.get('id')): row for row in definitions}
    need(len(by_id) == 16, 'Unique16 definition GUIDs')
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
        pairs.append((test_id, execution_id))
        identities.append((name, test_id))
    entry_pairs = [(shared.guid(row.get('testId')), shared.guid(row.get('executionId'))) for row in entries]
    need(len({row[0] for row in pairs}) == len({row[1] for row in pairs}) == len({row[0] for row in entry_pairs}) == len({row[1] for row in entry_pairs}) == 16, 'Unique tests/executions')
    need(collections.Counter(pairs) == collections.Counter(entry_pairs), 'Entry execution bijection')
    need(collections.Counter(name for name, _ in identities) == collections.Counter(expected_names), 'Exact16 compiled names')
    need(collections.Counter((name, test_id) for name, test_id in identities if name not in authored) == collections.Counter(expected_known), 'Frozen actual12 GUID/name multiset')
    summaries = exact_records(doc, doc, 'ResultSummary', 1)
    need(len(summaries) == 1 and summaries[0].get('outcome') in {'Passed', 'Completed'}, 'Passing summary required')
    counters = exact_records(doc, summaries[0], 'Counters', 1)
    expected_counters = dict(total='16', executed='16', passed='16', **{key: '0' for key in shared.NONPASSING})
    need(len(counters[0]) == 0 and counters[0].attrib == expected_counters, 'Exactly16 canonical clean counters')
    need(doc.find('.//t:ErrorInfo', NS) is None, 'ErrorInfo forbidden')
    need(all(row.get('outcome') in {'Passed', 'Completed', 'Information'} for row in doc.findall('.//t:RunInfo', NS)), 'RunInfo outcome required')
    return {'selectedCount': 16, 'summaryOutcome': summaries[0].get('outcome'), 'executionBijection': True, 'all16CountersCanonical': True, 'original12NameGuidRowsFrozen': True, 'authored4ObservedNameGuidRows': [{'name': name, 'testId': test_id} for name, test_id in identities if name in authored], 'full564IdentityJoinAccepted': False}

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
    result = {'actualFocusedPassed': 16, 'failed': 0, 'skipped': 0, 'trxSha256': hashlib.sha256(raw).hexdigest(), 'strictStructure': structure, 'original12IdentityRun': manifest['identityRun'], 'githubSha': os.environ.get('GITHUB_SHA'), 'runId': os.environ.get('GITHUB_RUN_ID'), 'strictRelease0WE': True, 'full564Accepted': False, 'consumerAccepted': False, 'originalPrimitiveParityAccepted': False, 'authored4ActualGuidsCollectedFromThisTrx': True, 'full564IdentityJoinAccepted': False}
    (FOLDER / 'focused-proof.json').write_text(json.dumps(result, indent=2) + '\n')
    print(json.dumps(result))

if __name__ == '__main__':
    {'guard': guard, 'verify': verify}[sys.argv[1]]()
