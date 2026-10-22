"""Customer-only sealed raw intake. Materialization grants no native execution."""
from __future__ import annotations
import argparse
import hashlib
import importlib.util
import json
from pathlib import Path
import re

POLICY_SHA256 = 'e0af0d56bc14f48ec7e7e80d445283faa942d2a80c6ab8caee39d45b6937a840'
MODULE_SHA256 = '44a8a5accac9da11422d606be02fe28487642215df511b5f1c4284296a453ee2'
FILE_MAIN_COMMIT = '53899c56ea3165e53ebbbcd60bff69a090037d9b'
FILE_MANIFEST_SHA256 = '643085d1542f392f2b0d494aacd1c92afa2b0e01112df2a22d10c310824dbdbd'
REPOSITORY = 'MALIEV-Co-Ltd/Legacy.Maliev.CustomerService'
BASE = 'b50e12d66cf0c3d4211a41febe3b01ca265a0c15'
COUNTS = {'source': 162, 'dependencies': 141, 'validation': 21}
RAW_COUNT = 324
PROVIDER_PLAN_SHA256 = '6319c8efd12e37ba62bc64c39b2fc2370280231c67b1951214234e64ac328cda'
NORMAL_DISPOSAL = 'Explicit live SDK request; sealed owner/topology and receipt-bound target; durable disposal intent; stop then immediate refreshed target validation before nonforced no-volume DELETE; actual status preserved'
SEALS = {
    'candidate4': ('metadata/candidate-manifest.json', 'ecbde2aaa14fba294a0858def8696e381539e483b1663f5e3ebf1771840e6691'),
    'source160': ('metadata/all-source-seals.json', '511ba044803c5f7b0e693858f3c380e4d66a5595b64c7b34124068045b379552'),
    'dependencies141': ('metadata/dependency-manifest.json', '1451fc01d863366d14287ae9cce5603fbf5cf7583cc4c74af414fc3e313ea2e9')}
DEPENDENCIES = {'Legacy.Maliev.ServiceDefaults': '086760fa0aae976a799dbcda1960d5c0981248cb',
                'Legacy.Maliev.CompatibilityContracts': '78e48ffc4ee000df0510cba5e7c7a3c4c4d539d7'}
ENVELOPE = {'envelope/' + n for n in ('customer_fixture_guard.py', 'customer_provider_proxy.py',
    'customer_provider_recovery.py', 'prepare_customer_provider_bundle.py', 'test_customer_fixture_guard.py',
    'test_customer_provider_proxy.py', 'test_customer_provider_recovery.py', 'provider-image-metadata.json',
    'customer_fixture_disposal.py', 'test_customer_fixture_disposal.py', 'test_customer_disposal_peer_faults.py')}
ASSOCIATION_METADATA = {
    'metadata/association-actual-main-baseline-roster.json',
    'metadata/association-baseline-source-inventory.json',
    'metadata/association-manifest.json',
    'metadata/association-original-full-sha-obligations.json',
    'metadata/association-thirteen-authored-case-contract.json'}
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
    need(type(policy.get('forecastCases')) is int and policy['forecastCases'] == 400
         and policy.get('nativeExecutionGranted') is False, 'Source-only b50/full400 forecast required')
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
    need(set(policy['seals']) == set(SEALS), 'Exactly three current-base nested seals required')
    for role, (path, digest) in SEALS.items():
        need(policy['seals'][role] == {'path': path, 'sha256': digest}, 'Current-base nested manifest seal mismatch')
        need(path in graph and graph[path]['sha256'] == digest, 'Nested manifest missing from graph')
    expected_validation = ENVELOPE | {'serial.runsettings', 'metadata/provider-plan.json'} | {v[0] for v in SEALS.values()} | ASSOCIATION_METADATA
    need({p for p, row in graph.items() if row['capsule'] == 'validation'} == expected_validation,
         'Exact validation21 recipe required')


def nested_graph(policy, files, helper):
    for path, digest in SEALS.values():
        need(path in files and sha(files[path]) == digest, 'Frozen nested raw manifest drift')
    candidate = helper.parse_json(files[SEALS['candidate4'][0]])
    whole = helper.parse_json(files[SEALS['source160'][0]])
    deps = helper.parse_json(files[SEALS['dependencies141'][0]].decode('utf-8-sig'))
    need(sha(files['metadata/provider-plan.json']) == PROVIDER_PLAN_SHA256, 'Nested r4 provider raw plan drift')
    provider = helper.parse_json(files['metadata/provider-plan.json'].decode('utf-8-sig'))
    need(provider.get('qualifiedFullCountForecastOnly') == 400 and provider.get('newCaseForecast') == 13
         and provider.get('compiledDiscoveryRequired') is True and provider.get('validatedFreshBaselineIdsRequired') is True,
         'Executable provider400/discovery/baseline-ID obligations required')
    need(provider.get('executionAdmission') is False and provider.get('producerBase') == BASE
         and provider.get('trackedSourceFilesVerified') == 160 and provider.get('candidateFilesVerified') == 4
         and provider.get('dependencyFilesVerified') == 141 and provider.get('bundleFiles') == 324,
         'Exact source-only r4 provider inventory required')
    need(provider.get('normalFixtureDisposal') == NORMAL_DISPOSAL and provider.get('sdkRawSocketMount') is False
         and provider.get('proxyAndRecoveryRawSocketReadonlyBind') is True
         and provider.get('ledgerMountReadOnly') == {'sdk': True, 'proxy': False, 'recovery': True},
         'Exact owner-directed STOP/DELETE and live SDK readonly policy required')
    need(provider.get('sdkEnvironment') == {'DOCKER_HOST': 'unix:///provider-ledger/proxy.sock',
         'TESTCONTAINERS_RYUK_DISABLED': 'true', 'TESTCONTAINERS_HOST_OVERRIDE': '127.0.0.1',
         'MalievWorkspaceRoot': '/work/dependencies'}, 'Exact isolated SDK transport policy required')
    need(candidate['base'] == BASE and candidate['fullCasesForecast'] == 400 and len(candidate['files']) == 4,
         'Current-base candidate4/full400 mismatch')
    need(isinstance(whole, list) and len(whole) == 160, 'Current-base160 source inventory required')
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
    need(len(expected) == 162, 'Exact source162 union required')
    association = helper.parse_json(files['metadata/association-manifest.json'])
    baseline = helper.parse_json(files['metadata/association-baseline-source-inventory.json'])
    authored = helper.parse_json(files['metadata/association-thirteen-authored-case-contract.json'])
    historical = helper.parse_json(files['metadata/association-actual-main-baseline-roster.json'])
    originals = helper.parse_json(files['metadata/association-original-full-sha-obligations.json'])
    need(sha(files['metadata/association-manifest.json']) == candidate['associationManifestSha256']
         == policy['associationManifestSha256'], 'Exact reviewed association seal required')
    for key, name in [('baselineSourceInventorySha256', 'baseline-source-inventory.json'),
                      ('originalSourceMappingSha256', 'original-full-sha-obligations.json'),
                      ('authoredCaseContractSha256', 'thirteen-authored-case-contract.json'),
                      ('actualHistoricalBaselineRosterSha256', 'actual-main-baseline-roster.json')]:
        need(sha(files['metadata/association-' + name]) == association[key],
             'Association nested proof seal mismatch')
    need(association['baseSha'] == BASE and association['nativeExecutionGranted'] is False
         and association['forecastNewCases'] == 13 and len(association['files']) == 4,
         'Source-only four-postimage association required')
    need(authored['caseCount'] == 13 and sum(len(r['rows']) for r in authored['methods']) == 13
         and authored['compiledCases'] is None and authored['forecastOnly'] is True,
         'Thirteen source rows without compiled acceptance required')
    need(historical['baseSha'] == BASE and historical['caseCount'] == 387
         and len(historical['cases']) == 387 and all(r['outcome'] == 'Passed' for r in historical['cases'])
         and historical['compiledAssemblySha256'] is None,
         'Exact historical387 roster with fresh compiled proof still pending required')
    need(len(originals) == 6 and association['wholeSourceClosure'] is False,
         'Six original obligations with source closure pending required')
    baseline_hashes = {r['path']: r['sha256'] for r in baseline['files']}
    candidate_hashes = {r['path']: r['sha256'] for r in association['files']}
    need(baseline['baseSha'] == BASE and len(baseline_hashes) == 160,
         'Exact b50 tracked160 baseline required')
    for row in association['files']:
        need(row['preimageSha256'] == baseline_hashes.get(row['path'])
             and expected['repo/' + row['path']] == row['sha256'], 'Exact four pre/postimage binding required')
    for path, digest in baseline_hashes.items():
        need(expected['repo/' + path] == candidate_hashes.get(path, digest), 'Unrelated current-base source drift')
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
               'rawFiles': len(files), 'forecastCases': 400, 'actualTestsRun': 0,
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
    parser.add_argument('mode', choices=('validate', 'materialize', 'fetch-materialize', 'association-validate'))
    parser.add_argument('--capsule-directory', type=Path)
    parser.add_argument('--target', type=Path)
    parser.add_argument('--association-packet')
    parser.add_argument('--association-manifest-sha256')
    args = parser.parse_args()
    scripts = Path(__file__).resolve().parent
    if args.mode == 'association-validate' or args.association_packet or args.association_manifest_sha256:
        need(args.association_packet and args.association_manifest_sha256, 'Exact association path and trusted manifest SHA required')
        verifier = globals().get('_captured_association_verifier')
        if verifier is None:
            verifier_path = scripts / 'verify_customer_null_association.py'
            verifier_raw = verifier_path.read_bytes()
            need(sha(verifier_raw) == 'ae35a1ffb0a07cb2cc79236a93cb5ea69a44e4fd21abad659bbdf9bd6ee52d08', 'Association verifier seal mismatch')
            spec = importlib.util.spec_from_loader('customer_null_association', loader=None)
            verifier = importlib.util.module_from_spec(spec)
            exec(compile(verifier_raw, str(verifier_path), 'exec'), verifier.__dict__)
        result = verifier.check(args.association_packet, args.association_manifest_sha256)
        if args.mode == 'association-validate':
            print(json.dumps(result))
            return
        need(BASE == 'b50e12d66cf0c3d4211a41febe3b01ca265a0c15',
             'Existing sealed dd36/full403 policy cannot materialize the b50/400 association; separately reviewed successor required')
    helper = accepted_helper()
    policy_raw = (scripts / 'customer-candidate-policy.json').read_bytes()
    policy = load_policy(policy_raw, helper)
    if args.mode == 'fetch-materialize':
        capsules = fetch_inputs(policy_raw, helper)
    else:
        packet = args.capsule_directory or scripts.parent / 'capsules'
        capsules = {role: (packet / (role + '.zip')).read_bytes() for role in COUNTS}
    if args.mode == 'validate':
        _, files = validate_inputs(policy_raw, capsules, helper)
        print(json.dumps({'validatedRawFiles': len(files), 'forecastCases': policy['forecastCases'], 'nativeExecutionGranted': False}))
    else:
        print(json.dumps(materialize(policy_raw, capsules, args.target or TARGET_ROOT, helper)))


if __name__ == '__main__':
    main()
