"""Exact ten authored boundary Fact evidence; synthetic controls never prove historical parity."""
import argparse
import datetime as dt
import hashlib
import json
import os
from pathlib import Path
import re
import time
import uuid
import xml.etree.ElementTree as E
SOURCE_PATH = 'Legacy.Maliev.CustomerService.Tests/Controllers/CustomerOptionalContactMvcBoundaryTests.cs'
MANIFEST_PATH = 'scripts/customer-address-focused-manifest.json'
REVIEWED_SOURCE_SHA256 = 'a736a1bc843acedcd98710053127e5dff87f430545d8a4e26aaba8391752521d'
NS = 'http://microsoft.com/schemas/VisualStudio/TeamTest/2010'
CLASS = 'Legacy.Maliev.CustomerService.Tests.Controllers.CustomerOptionalContactMvcBoundaryTests'
METHODS = ['DescriptorAndHeaderSelection_PreserveStrictRoutesAndUnknownEndpoints', 'OrdinaryFields_MatchReferenceAndIgnoreOnlyContactDuplicateNulls', 'CustomPoliciesConvertersResolversFactories_FallThroughWithoutMutation', 'DifferentialBodies_KeepEncodingEmptyMalformedAndPrivacyBehavior', 'DifferentialExceptionalConverters_KeepFormatOverflowAndCancellation', 'ConcurrentReads_IsolateOrdinaryVersionedReplayAndDefaultSerialization', 'RealHttp_OrdinaryCreateAndUpdatePreserveLiteralContactsAndState', 'RealHttp_ReplayVersionedAndDeniedWritesStayStrictAndUnchanged', 'FormattersAndGlobalOptions_RemainUnmodifiedAcrossReads', 'StreamAndPipeSwitch_DifferentialReaderLeavesRequestBodyOpen']
NAMES = sorted(f'{CLASS}.{method}' for method in METHODS)
ZERO = ('failed', 'error', 'timeout', 'aborted', 'inconclusive', 'passedButRunAborted',
        'notRunnable', 'notExecuted', 'disconnected', 'warning', 'completed', 'inProgress', 'pending')


class VerificationError(ValueError):
    """Evidence does not satisfy the focused contract."""


def need(condition, message):
    if not condition:
        raise VerificationError(message)


def exact(doc, parent, name, count):
    direct = parent.findall(f'{{{NS}}}{name}')
    rows = [row for row in doc.iter() if row.tag.rsplit('}', 1)[-1] == name]
    need(len(direct) == count and rows == direct, 'Exact direct/global ' + name)
    return direct


def guid(value):
    try:
        parsed = uuid.UUID(value)
    except (ValueError, TypeError, AttributeError) as error:
        raise VerificationError('Invalid actual GUID') from error
    need(parsed.int != 0 and str(parsed) == value, 'Canonical nonzero actual GUID')
    return value


def stamp(value):
    try:
        parsed = dt.datetime.fromisoformat(value.replace('Z', '+00:00'))
        need(parsed.utcoffset() is not None, 'Timezone required')
        return round(parsed.timestamp() * 1_000_000_000)
    except (ValueError, TypeError, AttributeError, OverflowError) as error:
        raise VerificationError('Invalid TRX timestamp') from error


def validate_trx(raw):
    need(isinstance(raw, bytes) and 0 < len(raw) <= 8 * 1024 * 1024, 'Bounded raw TRX required')
    try:
        text = raw.decode('utf-16') if raw.startswith((b'\xff\xfe', b'\xfe\xff')) else raw.decode('utf-8-sig')
        need('<!DOCTYPE' not in text.upper() and '<!ENTITY' not in text.upper(), 'No DTD/entities')
        doc = E.fromstring(text)
    except (UnicodeError, E.ParseError) as error:
        raise VerificationError('Malformed TRX') from error
    need(doc.tag == f'{{{NS}}}TestRun', 'Exact TRX namespace')
    guid(doc.get('id'))
    containers = [exact(doc, doc, name, 1)[0] for name in ('Results', 'TestDefinitions', 'TestEntries')]
    results, definitions, entries = [exact(doc, parent, name, 10) for parent, name in
                                    zip(containers, ('UnitTestResult', 'UnitTest', 'TestEntry'))]
    for name in ('Execution', 'TestMethod'):
        children = [child for definition in definitions for child in definition.findall(f'{{{NS}}}{name}')]
        need(len(children) == 10 and
             [row for row in doc.iter() if row.tag.rsplit('}', 1)[-1] == name] == children,
             'No shadow definition children')
    need(all(len(parent) == 10 for parent in containers), 'No substituted container children')
    need(sorted(row.get('testName', '') for row in results) == NAMES, 'Exact ten authored boundary Facts')
    identities, executions, result_times = {}, {}, []
    for row in results:
        test_id, execution_id = guid(row.get('testId')), guid(row.get('executionId'))
        need(test_id not in identities and execution_id not in executions, 'Unique actual identities')
        need(row.get('outcome') == 'Passed', 'All cases passed')
        identities[test_id] = row.get('testName')
        executions[execution_id] = test_id
        start, end = stamp(row.get('startTime')), stamp(row.get('endTime'))
        need(start <= end, 'Ordered case times')
        result_times.extend((start, end))
    defined, defined_executions = {}, {}
    for row in definitions:
        test_id = guid(row.get('id'))
        execution, method = row.findall(f'{{{NS}}}Execution'), row.findall(f'{{{NS}}}TestMethod')
        need(len(execution) == len(method) == 1 and len(row) == 2, 'Exact definition children')
        execution_id = guid(execution[0].get('id'))
        need(test_id not in defined and execution_id not in defined_executions, 'Unique definitions')
        need(identities.get(test_id) == row.get('name') and executions.get(execution_id) == test_id,
             'Definition/result identity and execution join')
        need(method[0].get('className') == CLASS and row.get('name') == CLASS + '.' + method[0].get('name', '') and method[0].get('name') in METHODS,
             'Exact compiled class and method')
        need(method[0].get('codeBase', '').replace('\\', '/').rsplit('/', 1)[-1]
             == 'Legacy.Maliev.CustomerService.Tests.dll', 'Exact test assembly')
        defined[test_id] = row.get('name')
        defined_executions[execution_id] = test_id
    joined = {}
    for row in entries:
        test_id, execution_id = guid(row.get('testId')), guid(row.get('executionId'))
        need(execution_id not in joined and executions.get(execution_id) == test_id, 'Exact entry join')
        joined[execution_id] = test_id
    need(identities == defined and executions == defined_executions == joined, 'Complete bijection')
    summary = exact(doc, doc, 'ResultSummary', 1)[0]
    need(summary.get('outcome') == 'Completed', 'Completed summary')
    counters = exact(doc, summary, 'Counters', 1)[0]
    need(counters.attrib == dict(total='10', executed='10', passed='10', **{key: '0' for key in ZERO}),
         'Exact counters, zero adverse outcomes')
    for row in doc.iter():
        local = row.tag.rsplit('}', 1)[-1]
        need(local != 'ErrorInfo', 'No ErrorInfo')
        if local == 'RunInfo':
            need(row.tag == f'{{{NS}}}RunInfo' and row.get('outcome') in
                 ('Passed', 'Completed', 'Information'), 'No adverse runner information')
    times = exact(doc, doc, 'Times', 1)[0]
    creation, queuing, start, finish = [stamp(times.get(key)) for key in ('creation', 'queuing', 'start', 'finish')]
    need(creation <= queuing <= start <= finish and all(start <= t <= finish for t in result_times),
         'Ordered complete run times')
    return {'tests': 10, 'caseNames': NAMES,
            'testIds': sorted(identities), 'executionIds': sorted(executions),
            'actualIdentities': [{'name': name, 'testId': key} for key, name in identities.items()],
            'startNs': creation, 'finishNs': finish, 'executionBijection': True}


def digest(path):
    need(path.is_file() and not path.is_symlink(), 'Regular pinned file required')
    raw = path.read_bytes()
    return raw, hashlib.sha256(raw).hexdigest()


def validate_build(raw):
    try:
        text = raw.decode('utf-8-sig')
    except UnicodeError as error:
        raise VerificationError('UTF-8 build log required') from error
    need('Build succeeded.' in text and re.search(r'^\s*0 Warning\(s\)\s*$', text, re.M)
         and re.search(r'^\s*0 Error\(s\)\s*$', text, re.M)
         and not re.search(r'\b[1-9][0-9]* (?:Warning|Error)\(s\)|\b(?:warning|error) [A-Za-z]+[0-9]+:', text),
         'Strict successful Release build with zero warnings and errors')
    return True


def build_binding(output):
    raw, sha = digest(output.parent / 'runner-optional-contact-focused' / 'build.log')
    validate_build(raw)
    return sha


def binding(root, context):
    need(set(context) == {'githubSha', 'sourceHead', 'runId', 'runAttempt'}, 'Exact CI context')
    need(all(isinstance(context[key], str) and re.fullmatch('[0-9a-f]{40}', context[key])
             for key in ('githubSha', 'sourceHead')), 'Exact CI source revisions')
    need(all(isinstance(context[key], str) and re.fullmatch('[1-9][0-9]*', context[key])
             for key in ('runId', 'runAttempt')), 'Exact CI run and attempt')
    source, source_sha = digest(root / SOURCE_PATH)
    need(source_sha == REVIEWED_SOURCE_SHA256, 'Frozen reviewed comparison source')
    raw, manifest_sha = digest(root / MANIFEST_PATH)
    try:
        pins = json.loads(raw)['codePins']
        matches = [row for row in pins if row['path'] == SOURCE_PATH]
        need(len(matches) == 1 and matches[0]['bytes'] == len(source)
             and matches[0]['sha256'] == source_sha, 'Exact manifest source binding')
    except (ValueError, KeyError, TypeError) as error:
        raise VerificationError('Invalid pinned manifest') from error
    return {'sourceSha256': source_sha, 'manifestSha256': manifest_sha, 'context': dict(context)}


def begin(root, output, context, *, now_ns=None):
    observed = binding(root, context)
    observed['buildSha256'] = build_binding(output)
    need(not output.is_symlink(), 'No linked output')
    need(not (output / 'results').exists() and not (output / 'receipt.json').exists()
         and not list(output.rglob('*.trx')), 'Fresh receipt and results required')
    output.mkdir(parents=True, exist_ok=True)
    (output / 'results').mkdir()
    receipt = dict(observed, schemaVersion=1, startedNs=time.time_ns() if now_ns is None else now_ns,
                   nonce=str(uuid.uuid4()))
    with (output / 'receipt.json').open('x', encoding='utf-8') as stream:
        json.dump(receipt, stream, indent=2)
        stream.write('\n')
    return receipt


def verify(root, output, context, *, test_exit=0, tee_exit=0, now_ns=None):
    need(type(test_exit) is int and type(tee_exit) is int and test_exit == tee_exit == 0,
         'Test and capture must both exit zero')
    observed = binding(root, context)
    observed['buildSha256'] = build_binding(output)
    raw_receipt, _ = digest(output / 'receipt.json')
    try:
        receipt = json.loads(raw_receipt)
        need(set(receipt) == set(observed) | {'schemaVersion', 'startedNs', 'nonce'}, 'Exact receipt shape')
        need(receipt['schemaVersion'] == 1 and type(receipt['startedNs']) is int, 'Receipt schema')
        guid(receipt['nonce'])
        need(all(receipt[key] == value for key, value in observed.items()), 'Unchanged source and CI context')
        start = receipt['startedNs']
    except (ValueError, KeyError, TypeError) as error:
        raise VerificationError('Invalid receipt') from error
    now = time.time_ns() if now_ns is None else now_ns
    need(start <= now <= start + 600_000_000_000, 'Finite fresh ten-minute receipt')
    results = output / 'results'
    need(results.is_dir() and not results.is_symlink(), 'Owned results directory')
    paths = list(results.rglob('*.trx'))
    need(len(paths) == 1 and paths[0].parent == results, 'Exactly one direct raw TRX')
    path = paths[0]
    raw, sha = digest(path)
    need(start <= path.stat().st_mtime_ns <= now, 'Fresh raw file time')
    report = validate_trx(raw)
    need(start <= report['startNs'] <= report['finishNs'] <= now, 'Fresh raw execution times')
    return dict(report, context=dict(context), sourceSha256=observed['sourceSha256'], rawSha256=sha,
                buildSha256=observed['buildSha256'], buildSummaryZeroWarningsErrors=True,
                authoredBoundaryFacts=10, historicalRuntimeProof=False, consumerAccepted=False)


def main():
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument('phase', choices=('guard', 'begin', 'verify'))
    parser.add_argument('--output', type=Path, default=Path('runner-optional-contact-boundary'))
    parser.add_argument('--test-exit', type=int, default=0)
    parser.add_argument('--tee-exit', type=int, default=0)
    args = parser.parse_args()
    root = Path(__file__).resolve().parents[1]
    if args.phase == 'guard':
        import verify_customer_address_focus as address
        address.shared.MANIFEST = root / MANIFEST_PATH
        address.shared.guard()
        return
    context = {'githubSha': os.environ.get('GITHUB_SHA', ''),
               'sourceHead': os.environ.get('CUSTOMER_FOCUSED_SOURCE_HEAD', ''),
               'runId': os.environ.get('GITHUB_RUN_ID', ''),
               'runAttempt': os.environ.get('GITHUB_RUN_ATTEMPT', '')}
    report = begin(root, args.output, context) if args.phase == 'begin' else verify(
        root, args.output, context, test_exit=args.test_exit, tee_exit=args.tee_exit)
    if args.phase == 'verify':
        with (args.output / 'verified.json').open('x', encoding='utf-8') as stream:
            json.dump(report, stream, indent=2)
            stream.write('\n')
    print(json.dumps(report, sort_keys=True))


if __name__ == '__main__':
    main()
