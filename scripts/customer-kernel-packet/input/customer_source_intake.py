"""Customer-only sealed raw intake. Materialization grants no native execution."""
from __future__ import annotations
import argparse
import hashlib
import importlib.util
import json
from pathlib import Path
import re

POLICY_SHA256 = 'ad4d2c21a3ad7c2dff819d04bac6913874d61db0bc2b410454169a583527655a'
MODULE_SHA256 = '44a8a5accac9da11422d606be02fe28487642215df511b5f1c4284296a453ee2'
FILE_MAIN_COMMIT = '53899c56ea3165e53ebbbcd60bff69a090037d9b'
FILE_MANIFEST_SHA256 = '643085d1542f392f2b0d494aacd1c92afa2b0e01112df2a22d10c310824dbdbd'
REPOSITORY = 'MALIEV-Co-Ltd/Legacy.Maliev.CustomerService'
BASE = 'dd36b868f538d5d8f0d97cc20b3a676fc6ef3d01'
COUNTS = {'source': 118, 'dependencies': 141, 'validation': 16}
RAW_COUNT = 275
PROVIDER_PLAN_SHA256 = '388882ef0c625783eb05731fec8608979420effa41520272011c8cd393ac7cdb'
NORMAL_DISPOSAL = 'Explicit live SDK request; sealed owner/topology and receipt-bound target; durable disposal intent; stop then immediate refreshed target validation before nonforced no-volume DELETE; actual status preserved'
SEALS = {
    'frozen6r2': ('metadata/candidate-manifest.json', 'b7c1f7ec9ee34d607566365b13fc6a68d429fe12a200b44ae9e8c7a4d7b6ed11'),
    'source117': ('metadata/all-source-seals.json', 'd63c594972fd61f260979da93a93f84caba9dfb6229834928eba927da59c54e7'),
    'dependencies141': ('metadata/dependency-manifest.json', '1451fc01d863366d14287ae9cce5603fbf5cf7583cc4c74af414fc3e313ea2e9')}
DEPENDENCIES = {'Legacy.Maliev.ServiceDefaults': '086760fa0aae976a799dbcda1960d5c0981248cb',
                'Legacy.Maliev.CompatibilityContracts': '78e48ffc4ee000df0510cba5e7c7a3c4c4d539d7'}
ENVELOPE = {'envelope/' + n for n in ('customer_fixture_guard.py', 'customer_provider_proxy.py',
    'customer_provider_recovery.py', 'prepare_customer_provider_bundle.py', 'test_customer_fixture_guard.py',
    'test_customer_provider_proxy.py', 'test_customer_provider_recovery.py', 'provider-image-metadata.json',
    'customer_fixture_disposal.py', 'test_customer_fixture_disposal.py', 'test_customer_disposal_peer_faults.py')}
TARGET_ROOT = Path('/work/customer-source')


def need(condition, message):
    if not condition:
        raise ValueError(message)


def sha(raw):
    return hashlib.sha256(raw).hexdigest()


def accepted_helper():
    path = Path(__file__).with_name('sealed_source_capsule.py')
    need(path.is_file() and not path.is_symlink(), 'Regular accepted module required')
    raw = path.read_bytes()
    need(sha(raw) == MODULE_SHA256, 'Accepted module seal mismatch')
    # Execute exactly the bytes which were hashed; never reopen a changed path.
    spec = importlib.util.spec_from_loader('customer_accepted_capsule', loader=None)
    module = importlib.util.module_from_spec(spec)
    module.__file__ = str(path)
    exec(compile(raw, str(path), 'exec'), module.__dict__)
    return module


def load_policy(raw, helper=None):
    need(re.fullmatch('[a-f0-9]{64}', POLICY_SHA256) is not None, 'Policy bootstrap is UNSEALED')
    need(sha(raw) == POLICY_SHA256, 'Policy bootstrap seal mismatch')
    helper = helper or accepted_helper()
    policy = helper.parse_json(raw)
    validate_policy(policy, helper)
    return policy


def validate_policy(policy, helper):
    need(type(policy.get('schemaVersion')) is int and policy['schemaVersion'] == 1 and policy.get('repository') == REPOSITORY,
         'Exact Customer repository policy required')
    need(policy.get('baseCommit') == BASE and policy.get('acceptedModuleSha256') == MODULE_SHA256,
         'Customer base/module mismatch')
    need(type(policy.get('forecastCases')) is int and policy['forecastCases'] == 403
         and policy.get('nativeExecutionGranted') is False, 'Source-only full403 forecast required')
    need(policy.get('providerPlanSha256') == PROVIDER_PLAN_SHA256 and policy.get('providerEnvelopeVersion') == 'r4'
         and policy.get('actualProviderRuntimeQualification') is False, 'Exact unqualified r4 provider policy required')
    file_acceptance = policy.get('fileModuleSourceAcceptance') or {}
    need(file_acceptance.get('protectedFileMainCommit') == FILE_MAIN_COMMIT
         and file_acceptance.get('manifestSha256') == FILE_MANIFEST_SHA256
         and file_acceptance.get('protectedFileMainRefStillPending') is False
         and file_acceptance.get('immutableV5') is True
         and file_acceptance.get('nativeWindowsOrSdkAcceptance') is False, 'Exact protected File source acceptance required')
    need(set(policy['capsules']) == set(COUNTS), 'Exactly three bound capsules required')
    names = set(); aliases = set(); graph = {}
    for role, count in COUNTS.items():
        capsule = policy['capsules'][role]
        need(re.fullmatch('[a-f0-9]{40}', capsule.get('oid', '')) is not None
             and re.fullmatch('[a-f0-9]{64}', capsule.get('sha256', '')) is not None
             and type(capsule.get('bytes')) is int and 0 < capsule['bytes'] <= helper.MAX_ARCHIVE_BYTES,
             'Bound capsule identity required')
        need(isinstance(capsule['rows'], list) and len(capsule['rows']) == count, 'Exact capsule row count required')
        if 'entries' in capsule:
            need(type(capsule['entries']) is int and capsule['entries'] == count, 'Capsule entries mismatch')
        for row in capsule['rows']:
            path = helper.canonical_path(row['path'])
            need(path not in names and path.casefold() not in aliases, 'Duplicate/aliased cross-capsule graph')
            names.add(path); aliases.add(path.casefold())
            need(path == 'serial.runsettings' or path.split('/')[0] in ('repo', 'dependencies', 'envelope', 'metadata'),
                 'Unexpected materialization recipe')
            if role == 'source': need(path.startswith('repo/'), 'Source capsule recipe mismatch')
            if role == 'dependencies': need(path.startswith('dependencies/'), 'Dependency capsule recipe mismatch')
            if role == 'validation': need(not path.startswith(('repo/', 'dependencies/')), 'Validation capsule recipe mismatch')
            graph[path] = {'capsule': role, 'member': path, 'sha256': row['sha256'], 'bytes': row['bytes']}
    need(policy['rawFiles'] == graph and len(graph) == RAW_COUNT, 'Exact complete raw graph required')
    need(graph.get('metadata/provider-plan.json', {}).get('sha256') == PROVIDER_PLAN_SHA256, 'Exact r4 provider plan graph seal required')
    folded = {path.casefold() for path in graph}
    need(not any('/'.join(path.casefold().split('/')[:i]) in folded
                 for path in graph for i in range(1, len(path.split('/')))), 'File/parent graph collision')
    need(set(policy['seals']) == set(SEALS), 'Exactly three nested seals required')
    for role, (path, digest) in SEALS.items():
        need(policy['seals'][role] == {'path': path, 'sha256': digest}, 'Frozen nested manifest seal mismatch')
        need(path in graph and graph[path]['sha256'] == digest, 'Nested manifest missing from graph')
    expected_validation = ENVELOPE | {'serial.runsettings', 'metadata/provider-plan.json'} | {v[0] for v in SEALS.values()}
    need({p for p, row in graph.items() if row['capsule'] == 'validation'} == expected_validation,
         'Exact validation16 recipe required')


def nested_graph(policy, files, helper):
    for path, digest in SEALS.values():
        need(path in files and sha(files[path]) == digest, 'Frozen nested raw manifest drift')
    candidate = helper.parse_json(files[SEALS['frozen6r2'][0]])
    whole = helper.parse_json(files[SEALS['source117'][0]])
    deps = helper.parse_json(files[SEALS['dependencies141'][0]].decode('utf-8-sig'))
    need(sha(files['metadata/provider-plan.json']) == PROVIDER_PLAN_SHA256, 'Nested r4 provider raw plan drift')
    provider = helper.parse_json(files['metadata/provider-plan.json'].decode('utf-8-sig'))
    need(provider.get('executionAdmission') is False and provider.get('producerBase') == BASE
         and provider.get('trackedSourceFilesVerified') == 117 and provider.get('candidateFilesVerified') == 6
         and provider.get('dependencyFilesVerified') == 141 and provider.get('bundleFiles') == 271,
         'Exact source-only r4 provider inventory required')
    need(provider.get('normalFixtureDisposal') == NORMAL_DISPOSAL and provider.get('sdkRawSocketMount') is False
         and provider.get('proxyAndRecoveryRawSocketReadonlyBind') is True
         and provider.get('ledgerMountReadOnly') == {'sdk': True, 'proxy': False, 'recovery': True},
         'Exact owner-directed STOP/DELETE and live SDK readonly policy required')
    need(provider.get('sdkEnvironment') == {'DOCKER_HOST': 'unix:///provider-ledger/proxy.sock',
         'TESTCONTAINERS_RYUK_DISABLED': 'true', 'TESTCONTAINERS_HOST_OVERRIDE': '127.0.0.1',
         'MalievWorkspaceRoot': '/work/dependencies'}, 'Exact isolated SDK transport policy required')
    need(candidate['base'] == BASE and candidate['fullCasesForecast'] == 403 and len(candidate['files']) == 6,
         'Frozen candidate6/full403 mismatch')
    need(isinstance(whole, list) and len(whole) == 117, 'Frozen117 source inventory required')
    expected = {}; aliases = set()
    def add(path, digest, allow_identical=False):
        path = helper.canonical_path(path)
        digest = digest.lower()
        need(re.fullmatch('[a-f0-9]{64}', digest) is not None, 'Raw manifest hash required')
        if path in expected:
            need(allow_identical and expected[path] == digest, 'Duplicate/conflicting nested source graph')
            return
        need(path.casefold() not in aliases, 'Aliased nested graph')
        aliases.add(path.casefold()); expected[path] = digest
    for row in whole:
        add('repo/' + row['path'], row['sha256'])
    candidate_paths = set()
    for row in candidate['files']:
        path = helper.canonical_path(row['path'])
        need(path not in candidate_paths, 'Duplicate candidate row')
        candidate_paths.add(path)
        add('repo/' + path, row['candidateSha256'], allow_identical=True)
    need(len(expected) == 118, 'Exact source118 union required')
    selected = [row for row in deps if row['snapshot'] == 'customer-baseline']
    need(len(selected) == 2 and {row['repository']: row['commit'] for row in selected} == DEPENDENCIES,
         'Exact Customer baseline dependency commits required')
    dep_count = 0
    for row in selected:
        need(row['extractedFileCount'] == len(row['files']), 'Dependency manifest count mismatch')
        for item in row['files']:
            add('dependencies/' + row['repository'] + '/' + item['path'].replace('\\', '/'), item['sha256'])
            dep_count += 1
    need(dep_count == 141, 'Exact dependency141 inventory required')
    need(set(expected) == {p for p, row in policy['rawFiles'].items() if row['capsule'] != 'validation'},
         'Nested raw source/dependency graph differs')
    for path, digest in expected.items():
        need(policy['rawFiles'][path]['sha256'] == digest and sha(files[path]) == digest,
             'Raw source/dependency byte drift')


def validate_inputs(policy_raw, capsules, helper=None):
    helper = helper or accepted_helper()
    policy = load_policy(policy_raw, helper)
    need(set(capsules) == set(COUNTS), 'Exactly three capsule inputs required')
    files = {}
    for role in COUNTS:
        raw = capsules[role]; bound = policy['capsules'][role]
        oid = hashlib.sha1(b'blob ' + str(len(raw)).encode() + b'\0' + raw).hexdigest()
        need(oid == bound['oid'], 'Exact bound capsule Git OID required')
        decoded = helper.validate_zip(raw, bound['sha256'], bound['bytes'], bound['rows'])
        need(not set(files).intersection(decoded), 'Duplicate decoded graph')
        files.update(decoded)
    nested_graph(policy, files, helper)
    return policy, files


def materialize(policy_raw, capsules, root=TARGET_ROOT, helper=None):
    helper = helper or accepted_helper()
    policy, files = validate_inputs(policy_raw, capsules, helper)
    root = Path(root)
    helper.reject_links(root)
    need(not root.exists(), 'Fresh target required; reentry forbidden')
    # Every capsule, nested manifest and complete graph has passed before mkdir.
    root.mkdir(parents=True, exist_ok=False)
    for path in sorted(files):
        helper.write_new(root, path, files[path])
    receipt = {'schemaVersion': 1, 'repository': REPOSITORY, 'baseCommit': BASE,
               'policySha256': sha(policy_raw), 'acceptedModuleSha256': MODULE_SHA256,
               'providerEnvelopeVersion': 'r4', 'providerPlanSha256': PROVIDER_PLAN_SHA256,
               'capsuleFileCounts': COUNTS, 'rawGraphSha256': sha(json.dumps(policy['rawFiles'], sort_keys=True, separators=(',', ':')).encode()),
               'rawFiles': len(files), 'forecastCases': 403, 'actualTestsRun': 0,
               'nativeExecutionGranted': False, 'status': 'raw-source-materialized-runtime-unqualified'}
    # A failed receipt never yields successful materialization; retain partial root.
    helper.write_new(root, 'metadata/intake-receipt.json', json.dumps(receipt, sort_keys=True).encode() + b'\n')
    return receipt


def fetch_inputs(policy_raw, helper=None):
    helper = helper or accepted_helper()
    policy = load_policy(policy_raw, helper)
    # Fixed repository and exact prebound OIDs; no URLs or caller repositories.
    return {role: helper.fetch_git_blob(REPOSITORY, policy['capsules'][role]['oid']) for role in COUNTS}


def main():
    parser = argparse.ArgumentParser()
    parser.add_argument('mode', choices=('validate', 'materialize', 'fetch-materialize'))
    args = parser.parse_args()
    scripts = Path(__file__).resolve().parent
    helper = accepted_helper()
    policy_raw = (scripts / 'customer-candidate-policy.json').read_bytes()
    policy = load_policy(policy_raw, helper)
    if args.mode == 'fetch-materialize':
        capsules = fetch_inputs(policy_raw, helper)
    else:
        packet = Path(__file__).resolve().parents[3] / 'outputs/customer-hosted-transport-20261008-r4'
        capsules = {role: (packet / (role + '.zip')).read_bytes() for role in COUNTS}
    if args.mode == 'validate':
        _, files = validate_inputs(policy_raw, capsules, helper)
        print(json.dumps({'validatedRawFiles': len(files), 'forecastCases': policy['forecastCases'], 'nativeExecutionGranted': False}))
    else:
        print(json.dumps(materialize(policy_raw, capsules, TARGET_ROOT, helper)))


if __name__ == '__main__':
    main()
