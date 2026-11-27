"""Exact separately executed Company30 proof; no full or consumer acceptance."""
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
    need(len(selected) == 30 and hashlib.sha256(json.dumps(selected[:16], sort_keys=True, separators=(',', ':')).encode()).hexdigest() == '46983af8d84e34b5233e4cfb70a781b48801e311520d48816608c8ae8e9a2b6c', 'Frozen accepted16 actual name/GUID identities retained')
    need(hashlib.sha256(json.dumps(selected[:21], sort_keys=True, separators=(',', ':')).encode()).hexdigest() == '5d57191218ade04707f4b01248b0c5561a1265acaecdecdfcb68608700cf3c6b', 'Frozen accepted21 actual main name/GUID identities retained')
    need(hashlib.sha256(json.dumps(selected[:25], sort_keys=True, separators=(',', ':')).encode()).hexdigest() == '243feaf18fcdad1c5d8e103566e9062fa122971e2705d4bf59e3fb79f4c1e5a9', 'Frozen accepted25 actual main names/GUIDs retained')
    need(hashlib.sha256(json.dumps(selected[:27], sort_keys=True, separators=(',', ':')).encode()).hexdigest() == 'd3221929789b5c987ff50d4c5fd75af76408f0db8ef0db409425f8e430a46426', 'Frozen accepted27 actual main names/GUIDs retained')
    authored = ['Legacy.Maliev.CustomerService.Tests.Controllers.CustomerCompanyFieldPrimitiveHttpTests.OrdinaryPairedFields_PersistPrimitiveLexemesAndMatchEquivalentStrings', 'Legacy.Maliev.CustomerService.Tests.Controllers.CustomerCompanyFieldPrimitiveHttpTests.OrdinaryPairedFields_RefusedShapesAndStorageBoundariesMatchEquivalentStringPipeline', 'Legacy.Maliev.CustomerService.Tests.Controllers.CustomerCompanyFieldPrimitiveHttpTests.OrdinaryPairedFields_AuthorityScopedPolicyAndStaleVersionsPreserveGraphCache']
    need([row['name'] for row in selected[27:]] == authored and all(row['testId'] is None for row in selected[27:]), 'Three authored compiled Facts; no invented actual GUIDs')
    expected_names = [row['name'] for row in selected]
    expected_known = [(row['name'], shared.guid(row['testId'])) for row in selected[:27]]
    doc = E.fromstring(raw)
    need(doc.tag == '{' + NS['t'] + '}TestRun', 'Exact TRX TestRun root')
    containers = [exact_records(doc, doc, name, 1)[0] for name in ('Results', 'TestDefinitions', 'TestEntries')]
    results, definitions, entries = [exact_records(doc, parent, name, 30) for parent, name in zip(containers, ('UnitTestResult', 'UnitTest', 'TestEntry'))]
    need(all(len(parent) == 30 for parent in containers), 'Only canonical records in join containers')
    all_methods = doc.findall('.//t:TestMethod', NS)
    need(len(all_methods) == 30 and len([r for r in doc.iter() if r.tag.rsplit('}', 1)[-1] == 'TestMethod']) == 30, 'Exactly30 global compiled methods')
    need(len(doc.findall('.//t:Execution', NS)) == 30 and len([r for r in doc.iter() if r.tag.rsplit('}', 1)[-1] == 'Execution']) == 30, 'Exactly30 global definition executions')
    need(len(results) == len(definitions) == len(entries) == 30, 'Exactly30 complete joins required')
    by_id = {shared.guid(row.get('id')): row for row in definitions}
    need(len(by_id) == 30, 'Unique30 definition GUIDs')
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
    need(len({row[0] for row in pairs}) == len({row[1] for row in pairs}) == len({row[0] for row in entry_pairs}) == len({row[1] for row in entry_pairs}) == 30, 'Unique tests/executions')
    need(collections.Counter(pairs) == collections.Counter(entry_pairs), 'Entry execution bijection')
    need(collections.Counter(name for name, _ in identities) == collections.Counter(expected_names), 'Exact30 compiled names')
    need(collections.Counter((name, test_id) for name, test_id in identities if name not in authored) == collections.Counter(expected_known), 'Frozen actual27 GUID/name multiset')
    summaries = exact_records(doc, doc, 'ResultSummary', 1)
    need(len(summaries) == 1 and summaries[0].get('outcome') in {'Passed', 'Completed'}, 'Passing summary required')
    counters = exact_records(doc, summaries[0], 'Counters', 1)
    expected_counters = dict(total='30', executed='30', passed='30', **{key: '0' for key in shared.NONPASSING})
    need(len(counters[0]) == 0 and counters[0].attrib == expected_counters, 'Exactly30 canonical clean counters')
    need(doc.find('.//t:ErrorInfo', NS) is None, 'ErrorInfo forbidden')
    need(all(row.get('outcome') in {'Passed', 'Completed', 'Information'} for row in doc.findall('.//t:RunInfo', NS)), 'RunInfo outcome required')
    return {'selectedCount': 30, 'summaryOutcome': summaries[0].get('outcome'), 'executionBijection': True, 'all30CountersCanonical': True, 'accepted21NameGuidRowsFrozen': True, 'authored3ObservedNameGuidRows': [{'name': name, 'testId': test_id} for name, test_id in identities if name in authored], 'full578IdentityJoinAccepted': False}

def validate_build(build):
    need('Build succeeded.' in build and re.search(r'0 Warning\(s\)', build) and re.search(r'0 Error\(s\)', build) and not re.search(r'\b[1-9]\d* (?:Warning|Error)\(s\)', build), 'Actual strict Release0WE')

def guard():
    shared.MANIFEST = MANIFEST
    shared.guard()

def verify():
    manifest = json.loads(MANIFEST.read_bytes())
    shared.authenticate_profile(ROOT, MANIFEST.relative_to(ROOT).as_posix(), manifest)
    files = list(FOLDER.rglob('*.trx'))
    need(len(files) == 1, 'One original separately executed focused TRX')
    raw = files[0].read_bytes()
    structure = validate(raw, manifest['selected'])
    build = (FOLDER / 'build.log').read_text()
    validate_build(build)
    result = {'actualFocusedPassed': 30, 'failed': 0, 'skipped': 0, 'trxSha256': hashlib.sha256(raw).hexdigest(), 'strictStructure': structure, 'accepted21MainIdentityRun': manifest['accepted21MainIdentityRun'], 'githubSha': os.environ.get('GITHUB_SHA'), 'runId': os.environ.get('GITHUB_RUN_ID'), 'strictRelease0WE': True, 'full578Accepted': False, 'consumerAccepted': False, 'originalPrimitiveParityAccepted': False, 'authored3ActualGuidsCollectedFromThisTrx': True, 'full578IdentityJoinAccepted': False}
    (FOLDER / 'focused-proof.json').write_text(json.dumps(result, indent=2) + '\n')
    print(json.dumps(result))

if __name__ == '__main__':
    {'guard': guard, 'verify': verify}[sys.argv[1]]()
