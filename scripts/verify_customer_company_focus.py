"""Exact separately executed Company10 proof; no full or consumer acceptance."""
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

def validate(raw, selected):
    expected = [(row['name'], shared.guid(row['testId'])) for row in selected]
    need(len(expected) == len(set(expected)) == len({row[1] for row in expected}) == 10, 'Exactly10 unique actual source identities')
    doc = E.fromstring(raw)
    results = doc.findall('t:Results/t:UnitTestResult', NS)
    definitions = doc.findall('t:TestDefinitions/t:UnitTest', NS)
    entries = doc.findall('t:TestEntries/t:TestEntry', NS)
    need(len(results) == len(definitions) == len(entries) == 10, 'Exactly10 complete joins required')
    by_id = {shared.guid(row.get('id')): row for row in definitions}
    need(len(by_id) == 10, 'Unique10 definition GUIDs')
    pairs = []
    identities = []
    for row in results:
        test_id = shared.guid(row.get('testId'))
        execution_id = shared.guid(row.get('executionId'))
        need(test_id in by_id and row.get('outcome') == 'Passed', 'Known passing result required')
        definition = by_id[test_id]
        executions = definition.findall('t:Execution', NS)
        need(len(executions) == 1 and shared.guid(executions[0].get('id')) == execution_id and definition.get('name') == row.get('testName'), 'Definition execution/name join')
        method = definition.find('t:TestMethod', NS)
        name = row.get('testName')
        need(method is not None and method.get('className') == 'Legacy.Maliev.CustomerService.Tests.Controllers.CustomerCompanyLiteralNameHttpTests', 'Exact compiled Company class')
        need(method.get('name') == name.split('CustomerCompanyLiteralNameHttpTests.', 1)[1].split('(', 1)[0], 'Exact compiled Company method')
        pairs.append((test_id, execution_id))
        identities.append((name, test_id))
    entry_pairs = [(shared.guid(row.get('testId')), shared.guid(row.get('executionId'))) for row in entries]
    need(len({row[0] for row in pairs}) == len({row[1] for row in pairs}) == len({row[0] for row in entry_pairs}) == len({row[1] for row in entry_pairs}) == 10, 'Unique tests/executions')
    need(collections.Counter(pairs) == collections.Counter(entry_pairs), 'Entry execution bijection')
    need(collections.Counter(identities) == collections.Counter(expected), 'Exact actual Company name/testId multiset')
    summaries = doc.findall('t:ResultSummary', NS)
    need(len(summaries) == 1 and summaries[0].get('outcome') in {'Passed', 'Completed'}, 'Passing summary required')
    counters = summaries[0].findall('t:Counters', NS)
    expected_counters = dict(total='10', executed='10', passed='10', **{key: '0' for key in shared.NONPASSING})
    need(len(counters) == 1 and counters[0].attrib == expected_counters, 'Exactly16 canonical clean counters')
    need(doc.find('.//t:ErrorInfo', NS) is None, 'ErrorInfo forbidden')
    need(all(row.get('outcome') in {'Passed', 'Completed', 'Information'} for row in doc.findall('.//t:RunInfo', NS)), 'RunInfo outcome required')
    return {'selectedCount': 10, 'summaryOutcome': summaries[0].get('outcome'), 'executionBijection': True, 'all16CountersCanonical': True}

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
    result = {'actualFocusedPassed': 10, 'failed': 0, 'skipped': 0, 'trxSha256': hashlib.sha256(raw).hexdigest(), 'strictStructure': structure, 'identitiesBoundToOriginalFullRun': manifest['identityRun'], 'githubSha': os.environ.get('GITHUB_SHA'), 'runId': os.environ.get('GITHUB_RUN_ID'), 'strictRelease0WE': True, 'full558Accepted': False, 'consumerAccepted': False, 'originalPrimitiveParityAccepted': False}
    (FOLDER / 'focused-proof.json').write_text(json.dumps(result, indent=2) + '\n')
    print(json.dumps(result))

if __name__ == '__main__':
    {'guard': guard, 'verify': verify}[sys.argv[1]]()
