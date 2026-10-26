"""Strict original evidence for the first separately executed 13-case proof."""
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
MANIFEST = ROOT / 'scripts/customer-default-page-focused-manifest.json'
NS = shared.NS
need = shared.need

def validate(raw, expected_names):
    doc = E.fromstring(raw)
    results = doc.findall('t:Results/t:UnitTestResult', NS)
    definitions = doc.findall('t:TestDefinitions/t:UnitTest', NS)
    entries = doc.findall('t:TestEntries/t:TestEntry', NS)
    need(len(results) == len(definitions) == len(entries) == 13, 'Exactly13 complete joins required')
    need(len(set(expected_names)) == len(expected_names) == 13, 'Independent13 source-case names required')
    need(collections.Counter(x.get('testName') for x in results) == collections.Counter(expected_names), 'Exact source-case name multiset')
    by_id = {shared.guid(x.get('id')): x for x in definitions}
    need(len(by_id) == 13, 'Unique definition identities')
    pairs = []
    for row in results:
        test_id = shared.guid(row.get('testId'))
        execution_id = shared.guid(row.get('executionId'))
        need(test_id in by_id and row.get('outcome') == 'Passed', 'Known passing result required')
        definition = by_id[test_id]
        executions = definition.findall('t:Execution', NS)
        need(len(executions) == 1 and shared.guid(executions[0].get('id')) == execution_id, 'Definition execution join')
        need(definition.get('name') == row.get('testName'), 'Definition/result name join')
        method = definition.find('t:TestMethod', NS)
        need(method is not None and method.get('className') == 'Legacy.Maliev.CustomerService.Tests.Controllers.CustomerDefaultPageHttpTests', 'Exact compiled class')
        need(method.get('name') == row.get('testName').split('CustomerDefaultPageHttpTests.', 1)[1].split('(', 1)[0], 'Exact compiled method')
        pairs.append((test_id, execution_id))
    entry_pairs = [(shared.guid(x.get('testId')), shared.guid(x.get('executionId'))) for x in entries]
    need(len({x[0] for x in pairs}) == len({x[1] for x in pairs}) == 13, 'Unique tests/executions')
    need(collections.Counter(pairs) == collections.Counter(entry_pairs), 'Entry execution bijection')
    summaries = doc.findall('t:ResultSummary', NS)
    need(len(summaries) == 1 and summaries[0].get('outcome') in {'Passed', 'Completed'}, 'Passing summary required')
    counters = summaries[0].findall('t:Counters', NS)
    need(len(counters) == 1, 'One counters record required')
    need(all(counters[0].get(k) == '13' for k in ('total', 'executed', 'passed')), 'Exactly13 executed passed')
    need(all(counters[0].get(k) == '0' for k in shared.NONPASSING), 'All13 nonpassing counters canonical zero')
    need(doc.find('.//t:ErrorInfo', NS) is None, 'ErrorInfo forbidden')
    need(all(x.get('outcome') in {'Passed', 'Completed', 'Information'} for x in doc.findall('.//t:RunInfo', NS)), 'RunInfo outcome required')
    return [{'name': x.get('testName'), 'testId': x.get('testId')} for x in results]

def guard():
    shared.MANIFEST = MANIFEST
    shared.guard()

def verify():
    manifest = json.loads(MANIFEST.read_bytes())
    shared.authenticate_profile(ROOT, MANIFEST.relative_to(ROOT).as_posix(), manifest)
    folder = ROOT / 'runner-default-focused'
    files = list(folder.rglob('*.trx'))
    need(len(files) == 1, 'One original focused TRX required')
    raw = files[0].read_bytes()
    identities = validate(raw, manifest['expectedNames'])
    build = (folder / 'build.log').read_text()
    need('Build succeeded.' in build and re.search(r'0 Warning\(s\)', build) and re.search(r'0 Error\(s\)', build) and not re.search(r'\b[1-9]\d* (?:Warning|Error)\(s\)', build), 'Actual strict Release0WE required')
    proof = {'actualFocusedPassed': 13, 'failed': 0, 'skipped': 0, 'trxSha256': hashlib.sha256(raw).hexdigest(), 'identitiesFromOriginalExecution': identities, 'githubSha': os.environ.get('GITHUB_SHA'), 'runId': os.environ.get('GITHUB_RUN_ID'), 'full548Accepted': False, 'retained535Accepted': False, 'consumerAccepted': False, 'mutantsExecuted': False}
    (folder / 'focused-proof.json').write_text(json.dumps(proof, indent=2) + '\n')
    print(json.dumps(proof))

if __name__ == '__main__':
    {'guard': guard, 'verify': verify}[sys.argv[1]]()
