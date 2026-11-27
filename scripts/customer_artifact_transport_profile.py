"""Authenticate the reviewed ordinary transport delta; no execution authority is granted."""
import contextlib
import hashlib
import importlib.util
import json
from pathlib import Path
import re
import stat
import tempfile

ARCHIVES = {'scripts/customer-email-focused-manifest.json': {'path': 'scripts/customer-email-focused-manifest.json',
                                                  'archive': 'scripts/artifact-transport-predecessors/customer-email-focused-manifest.json',
                                                  'bytes': 206056,
                                                  'sha256': '98c95c1624468c2623e519cef0bc60671b9b668cc62dbc81611bca707401f755',
                                                  'gitBlob': '474425a94efbce19b8b9841d61e562c2b9b5d257'},
 'scripts/customer-default-page-focused-manifest.json': {'path': 'scripts/customer-default-page-focused-manifest.json',
                                                         'archive': 'scripts/artifact-transport-predecessors/customer-default-page-focused-manifest.json',
                                                         'bytes': 186217,
                                                         'sha256': 'c9aee0f3dd40de3b7c6aa3b1623177dc9b574e26f2955360f5a1507f8ac19d47',
                                                         'gitBlob': '859dad4ff79c50df273242f8c9a9dfb4fd26dd1b'},
 'scripts/customer-company-focused-manifest.json': {'path': 'scripts/customer-company-focused-manifest.json',
                                                    'archive': 'scripts/artifact-transport-predecessors/customer-company-focused-manifest.json',
                                                    'bytes': 217999,
                                                    'sha256': 'ed1f5aa91e86af0533b1b335ec2a63ccacbc23b1f9b1bafc15130e902e290633',
                                                    'gitBlob': '738d4b3cd5c19ff5a01ebe089110cb06e6d258b2'},
 'scripts/customer-address-focused-manifest.json': {'path': 'scripts/customer-address-focused-manifest.json',
                                                    'archive': 'scripts/artifact-transport-predecessors/customer-address-focused-manifest.json',
                                                    'bytes': 220763,
                                                    'sha256': 'ffc4243e594a47320a97200be172d2898e8e1e5a474f004346f7380d590545d5',
                                                    'gitBlob': '28f01f82256d8a6f241f2c776b3c168bcf075e55'},
 'scripts/verify_customer_email_focused.py': {'path': 'scripts/verify_customer_email_focused.py',
                                              'archive': 'scripts/artifact-transport-predecessors/verify_customer_email_focused.py.txt',
                                              'bytes': 9463,
                                              'sha256': '5ea7d087156bd3754b60ac77033ea0ac9e4b32a1bb29c704ba99b91c44153824',
                                              'gitBlob': 'a9bca18d436d8f41b6be0aed2ca100e924e92590'},
 'scripts/test_customer_optional_contact_focus.py': {'path': 'scripts/test_customer_optional_contact_focus.py',
                                                     'archive': 'scripts/artifact-transport-predecessors/test_customer_optional_contact_focus.py.txt',
                                                     'bytes': 51789,
                                                     'sha256': '4262c0b704221a9805485ce7028bc545bcd5c5529633e4c8434fd6ab7cb10268',
                                                     'gitBlob': '17e09fac476f7e730217a72c99c3f4bfdcda2ef0'},
 'Legacy.Maliev.CustomerService.Tests/Workflows/WorkflowContractTests.cs': {'path': 'Legacy.Maliev.CustomerService.Tests/Workflows/WorkflowContractTests.cs',
                                                                            'archive': 'scripts/artifact-transport-predecessors/WorkflowContractTests.cs.txt',
                                                                            'bytes': 47530,
                                                                            'sha256': '32b0beebd58a8d9d640691206c982a079ab68da4c5f3d92637981bb5b51aab84',
                                                                            'gitBlob': 'ec07d2b5ca2169be1acba9045de3a4a0e4a6b103'},
 'scripts/verify_customer_default_page_focus.py': {'path': 'scripts/verify_customer_default_page_focus.py',
                                                   'archive': 'scripts/artifact-transport-predecessors/verify_customer_default_page_focus.py.txt',
                                                   'bytes': 4386,
                                                   'sha256': '7780f8dc6d10a6d4ecbcb3b6b7a7150d9ffca0f888e49a615ab2a11f5e6d3505',
                                                   'gitBlob': '9c8ba521c042bcf2645c8af1648fe80d2d1e3256'},
 'scripts/verify_customer_company_focus.py': {'path': 'scripts/verify_customer_company_focus.py',
                                              'archive': 'scripts/artifact-transport-predecessors/verify_customer_company_focus.py.txt',
                                              'bytes': 8213,
                                              'sha256': 'c88250b597e2a0855e10f885cb50fe5d703eca3c1a1e5aee094995f00de9dffe',
                                              'gitBlob': '1f0febf7968e7f5f662ea7202e41f8e2d1728ac5'},
 'scripts/verify_customer_address_focus.py': {'path': 'scripts/verify_customer_address_focus.py',
                                              'archive': 'scripts/artifact-transport-predecessors/verify_customer_address_focus.py.txt',
                                              'bytes': 7226,
                                              'sha256': '3ae7e0deffdafe4aeb38870dade604067db0394b1e2797d6de29c836557f78f8',
                                              'gitBlob': '5b0c8777db303fb3c6832400b83c9c8dbeac15db'}}

MANIFESTS = ['scripts/customer-email-focused-manifest.json',
 'scripts/customer-default-page-focused-manifest.json',
 'scripts/customer-company-focused-manifest.json',
 'scripts/customer-address-focused-manifest.json']

PARENTS = {'scripts/customer-email-focused-manifest.json': [],
 'scripts/customer-default-page-focused-manifest.json': [],
 'scripts/customer-company-focused-manifest.json': ['scripts/customer-default-page-focused-manifest.json',
                                                    'scripts/customer-email-focused-manifest.json'],
 'scripts/customer-address-focused-manifest.json': ['scripts/customer-default-page-focused-manifest.json',
                                                    'scripts/customer-email-focused-manifest.json']}

PREDECESSOR_MAIN = '944eac71234ba0f3d0181d97e6ba32d0acfbe8ce'

WORKFLOW = '.github/workflows/_build-and-test.yml'

SELECTED_PIN = 'cf430e030ddbb5b0abf93d22962f4752f3646cd9'

PREVIOUS_PIN = 'ea165f8d65b6e75b540449e92b4886f43607fa02'

SOURCE_EDITS = {'Legacy.Maliev.CustomerService.Tests/Workflows/WorkflowContractTests.cs': [(b'actions/upload-artifact@ea16'
                                                                             b'5f8d65b6e75b540449e92b4886f4'
                                                                             b'3607fa02',
                                                                             b'actions/upload-artifact@cf43'
                                                                             b'0e030ddbb5b0abf93d22962f4752'
                                                                             b'f3646cd9'),
                                                                            (b'    /// <summary>Checks the '
                                                                             b'maintained workflow structur'
                                                                             b'e, admitted consumer phases,'
                                                                             b' and exact address-focused e'
                                                                             b'vidence boundaries.</summary>',
                                                                             b'    private static void Buil'
                                                                             b'dAndTest_RejectsPreviousUplo'
                                                                             b'adArtifactPins()\n    {\n     '
                                                                             b'   foreach (var previousPin '
                                                                             b'in new[] { "ea165f8d65b6e75b'
                                                                             b'540449e92b4886f43607fa02", "'
                                                                             b'043fb46d1a93c77aae656e7c1c64'
                                                                             b'a875d1fc6a0a" })\n        {\n '
                                                                             b'           var previous = Wo'
                                                                             b'rkflow.Replace("actions/uplo'
                                                                             b'ad-artifact@cf430e030ddbb5b0'
                                                                             b'abf93d22962f4752f3646cd9", "'
                                                                             b'actions/upload-artifact@" + '
                                                                             b'previousPin, StringCompariso'
                                                                             b'n.Ordinal);\n            Asse'
                                                                             b'rt.NotEqual(Workflow, previo'
                                                                             b'us);\n            Assert.Thro'
                                                                             b'ws<InvalidOperationException'
                                                                             b'>(() => WorkflowContractVali'
                                                                             b'dator.Validate(previous));\n '
                                                                             b'       }\n    }\n\n    /// '
                                                                             b'<summary>Checks the maintain'
                                                                             b'ed workflow structure, admit'
                                                                             b'ted consumer phases, and exa'
                                                                             b'ct address-focused evidence '
                                                                             b'boundaries.</summary>'),
                                                                            (b'    public void BuildAndTest'
                                                                             b'_SatisfiesStructuralContract'
                                                                             b'()\n    {\n',
                                                                             b'    public void BuildAndTest'
                                                                             b'_SatisfiesStructuralContract'
                                                                             b'()\n    {\n        BuildAndTes'
                                                                             b't_RejectsPreviousUploadArtif'
                                                                             b'actPins();\n')],
 'scripts/verify_customer_email_focused.py': [(b'from pathlib import Path\n',
                                               b'from pathlib import Path\nfrom customer_artifact_transpor'
                                               b't_profile import authenticate_profile\n'),
                                              (b'    manifest=json.loads(MANIFEST.read_bytes())\n',
                                               b'    manifest=json.loads(MANIFEST.read_bytes())\n    authe'
                                               b'nticate_profile(ROOT, MANIFEST.relative_to(ROOT).as_posix(),'
                                               b' manifest)\n'),
                                              (b"    manifest=json.loads(MANIFEST.read_bytes());folder=ROOT/'"
                                               b"runner-focused';",
                                               b'    manifest=json.loads(MANIFEST.read_bytes());authenticate_'
                                               b'profile(ROOT, MANIFEST.relative_to(ROOT).as_posix(), manifes'
                                               b"t);folder=ROOT/'runner-focused';")],
 'scripts/verify_customer_default_page_focus.py': [(b'    manifest = json.loads(MANIFEST.read_bytes())\n',
                                                    b'    manifest = json.loads(MANIFEST.read_bytes())\n   '
                                                    b' shared.authenticate_profile(ROOT, MANIFEST.relative'
                                                    b'_to(ROOT).as_posix(), manifest)\n')],
 'scripts/verify_customer_company_focus.py': [(b'    manifest = json.loads(MANIFEST.read_bytes())\n',
                                               b'    manifest = json.loads(MANIFEST.read_bytes())\n    sha'
                                               b'red.authenticate_profile(ROOT, MANIFEST.relative_to(ROOT).as'
                                               b'_posix(), manifest)\n')],
 'scripts/verify_customer_address_focus.py': [(b"    elif sys.argv[1] == 'verify':\n",
                                               b"    elif sys.argv[1] == 'verify':\n        manifest = jso"
                                               b'n.loads(MANIFEST.read_bytes())\n        shared.authentica'
                                               b'te_profile(ROOT, MANIFEST.relative_to(ROOT).as_posix(), mani'
                                               b'fest)\n')],
 'scripts/test_customer_optional_contact_focus.py': [(b'import datetime as dt\n',
                                                      b'from customer_artifact_transport_profile import hist'
                                                      b'orical_view\nfrom test_customer_artifact_transport_pr'
                                                      b'ofile import ArtifactTransportControls\nimport dateti'
                                                      b'me as dt\n'),
                                                     (b'def validate_documentation_successor(root, manifest_'
                                                      b'path, manifest):\n',
                                                      b'def validate_documentation_successor(root, manifest_'
                                                      b'path, manifest):\n    if "artifactTransportSuccessor"'
                                                      b' in manifest:\n        with historical_view(root, man'
                                                      b'ifest_path, manifest) as (original_root, original_ma'
                                                      b'nifest):\n            return validate_documentation_s'
                                                      b'uccessor(original_root, manifest_path, original_mani'
                                                      b'fest)\n')]}

FIELD = 'artifactTransportSuccessor'
SCOPE = 'customer-artifact-transport-v7.0.2-main944'
HELPER = 'scripts/customer_artifact_transport_profile.py'
CONTROLS = 'scripts/test_customer_artifact_transport_profile.py'
CAP = 2 * 1024 * 1024

def need(condition, reason):
    if not condition:
        raise ValueError(reason)

def same(left, right):
    return json.dumps(left, sort_keys=True, separators=(',', ':')) == json.dumps(right, sort_keys=True, separators=(',', ':'))

def parse(data):
    def unique(pairs):
        result = {}
        for key, value in pairs:
            need(key not in result, 'Duplicate JSON field refused')
            result[key] = value
        return result
    return json.loads(data, object_pairs_hook=unique)

def read(root, relative):
    need(isinstance(relative, str) and '\\' not in relative and not Path(relative).is_absolute()
         and all(part not in ('', '.', '..') for part in relative.split('/')), 'Closed relative source path required')
    path = Path(root)
    for part in relative.split('/'):
        path = path / part
        need(not path.is_symlink(), 'Symlink source refused')
    need(stat.S_ISREG(path.stat().st_mode), 'Regular source required')
    with path.open('rb') as stream:
        data = stream.read(CAP + 1)
    need(len(data) <= CAP, 'Source byte bound')
    return data

def pin(path, data):
    return dict(path=path, bytes=len(data), sha256=hashlib.sha256(data).hexdigest())

def archived(root, original):
    record = ARCHIVES[original]
    data = read(root, record['archive'])
    need(len(data) == record['bytes'] and hashlib.sha256(data).hexdigest() == record['sha256']
         and hashlib.sha1(b'blob ' + str(len(data)).encode() + b'\0' + data).hexdigest() == record['gitBlob'],
         'Exact accepted predecessor archive required')
    return data

def predecessor(root, manifest_path):
    need(manifest_path in MANIFESTS, 'Known focused profile required')
    return parse(archived(root, manifest_path))

def edited_source(root, path):
    if path == WORKFLOW:
        data = read(root, path)
        selected = ('actions/upload-artifact@' + SELECTED_PIN).encode()
        previous = ('actions/upload-artifact@' + PREVIOUS_PIN).encode()
        need(data.count(selected) == 1, 'One exact current artifact site required')
        old = data.replace(selected, previous)
        expected = next(row for row in predecessor(root, MANIFESTS[0])['codePins'] if row['path'] == path)
        need(same(pin(path, old), expected), 'Workflow inverse must match accepted predecessor')
        return data
    data = archived(root, path)
    for before, after in SOURCE_EDITS[path]:
        need(data.count(before) == 1, 'Unique reviewed source hook required')
        data = data.replace(before, after, 1)
    return data

def rows_by_path(rows):
    need(type(rows) is list, 'Explicit source rows required')
    result = {}
    for row in rows:
        need(type(row) is dict and set(row) == {'path', 'bytes', 'sha256'}
             and type(row['bytes']) is int and row['bytes'] >= 0
             and isinstance(row['sha256'], str) and re.fullmatch('[0-9a-f]{64}', row['sha256']),
             'Canonical source row required')
        need(row['path'] not in result, 'Unique source path required')
        result[row['path']] = row
    return result

def authenticate_profile(root, manifest_path, manifest, _stack=()):
    if 'ambiguitySuccessor' in manifest:
        return ambiguity_authenticate(root, manifest_path, manifest)
    need(manifest_path in MANIFESTS and manifest_path not in _stack, 'Closed acyclic focused profile')
    need(type(manifest) is dict and FIELD in manifest, 'Reviewed current transport profile required')
    need(same(manifest, parse(read(root, manifest_path))), 'Actual current manifest bytes required')
    old = predecessor(root, manifest_path)
    need(same({key: value for key, value in manifest.items() if key not in ('codePins', FIELD)},
              {key: value for key, value in old.items() if key != 'codePins'}),
         'All historical identity, policy and grant fields must remain exact')
    before = rows_by_path(old['codePins']);current = rows_by_path(manifest['codePins'])
    need(list(current) == list(before), 'Original source roster and order must remain exact')
    for path in SOURCE_EDITS:
        need(read(root, path) == edited_source(root, path), 'Every reviewed current hook must remain exact')
    changes = []
    for path, previous in before.items():
        expected = previous
        if path == WORKFLOW or path in SOURCE_EDITS:
            data = edited_source(root, path)
            need(read(root, path) == data, 'Current source exceeds the exact reviewed transport hooks')
            expected = pin(path, data)
        elif path in MANIFESTS:
            need(path in PARENTS[manifest_path], 'Declared parent profile required')
            data = read(root, path)
            authenticate_profile(root, path, parse(data), _stack + (manifest_path,))
            expected = pin(path, data)
        need(same(current[path], expected), 'Unapproved source, dependency or workflow repinning')
        if not same(expected, previous):
            changes.append(dict(path=path, predecessor=previous, current=expected))
    extra_paths = {HELPER, CONTROLS} | {r['archive'] for r in ARCHIVES.values()} | (set(SOURCE_EDITS) - set(before))
    extras = [pin(path, read(root, path)) for path in sorted(extra_paths)]
    for original in ARCHIVES:
        archived(root, original)
    expected = dict(schemaVersion=1, scope=SCOPE, predecessorMain=PREDECESSOR_MAIN,
                    predecessorManifestBlob=ARCHIVES[manifest_path]['gitBlob'],
                    predecessorManifestSha256=ARCHIVES[manifest_path]['sha256'],
                    reviewedChanges=changes, additionalInputs=extras)
    need(same(manifest[FIELD], expected), 'Exact current transport metadata and all added inputs required')
    return old

@contextlib.contextmanager
def historical_view(root, manifest_path, manifest):
    """Provide authenticated old bytes to unchanged pure historical assertions only."""
    if 'ambiguitySuccessor' in manifest:
        with ambiguity_historical_view(root, manifest_path, manifest) as result:
            yield result
        return
    old = authenticate_profile(root, manifest_path, manifest)
    originals = {}
    for row in old['codePins']:
        path = row['path']
        if path in ARCHIVES:
            data = archived(root, path)
        elif path == WORKFLOW:
            data = edited_source(root, path).replace(('actions/upload-artifact@' + SELECTED_PIN).encode(),
                                                   ('actions/upload-artifact@' + PREVIOUS_PIN).encode())
        else:
            data = read(root, path)
        need(same(pin(path, data), row), 'Historical view must retain every exact original source pin')
        originals[path] = data
    for change in old['documentationSuccessor']['reviewedChanges']:
        row = change['successor'];path = row['path']
        if path not in originals:
            data = read(root, path)
            need(same(pin(path, data), row), 'Unlisted historical documentation postimage must remain exact')
            originals[path] = data
    for original in ARCHIVES:
        originals[original] = archived(root, original)
    parent = Path(root).resolve().parent
    with tempfile.TemporaryDirectory(prefix='.customer-transport-history-', dir=parent) as temporary:
        target = Path(temporary)
        need(target.resolve().parent == parent, 'Task-owned historical view directory required')
        for path, data in originals.items():
            destination = target / path
            destination.parent.mkdir(parents=True, exist_ok=True)
            destination.write_bytes(data)
        yield target, old


# Versioned ambiguity successor; this reviewed verifier remains the trust root.
ambiguity_SCOPE = 'customer-ambiguity-successor-v1'
ambiguity_FIELD = 'ambiguitySuccessor'
ambiguity_ROOT = 'scripts/ambiguity-predecessors/'
ambiguity_MANIFESTS = MANIFESTS
ambiguity_POST = {
    'scripts/test_customer_artifact_transport_profile.py': (12113, '2bd8c6c5d833099473b5532d40a38617cda92ad9e4adba5cd006b2799fdb650f'),
    'Legacy.Maliev.CustomerService.Data/CustomerRepository.cs': (22861, '64a9fc86d4525e6f52fd5ab925dd7b8dee71d70cc67601e7242543a01c08367b'),
    'Legacy.Maliev.CustomerService.Tests/Controllers/CustomerAdministrativeEmailComparisonHttpTests.cs': (21867, 'c8863374d5013b5d0ce83c1af693ac2717e64bed6cfa3060f72f85cb735c80af'),
    'Legacy.Maliev.CustomerService.Tests/Controllers/CustomerEmailCanonicalCaseHttpTests.cs': (19738, '7990448bba4134e3f32754fab4f8c0736a49db819e152a66f46d1f6ea3c447f9'),
    'Legacy.Maliev.CustomerService.Tests/Data/CustomerPostgresMigrationTests.cs': (22019, 'ecd30822655e1a767100969e2aac7f9505f863c38d9ca1ee1e80223fc275ba0a'),
    'scripts/test_customer_optional_contact_focus.py': (52429, '2e7e3ac5bd677a430ac1cdfb67d01d3a87717f244f74c04c78c3052e18852649'),
}
ambiguity_PRE = {
    'Legacy.Maliev.CustomerService.Data/CustomerRepository.cs': (22853, 'e2ded23908eaf7143806d8e551375d5f45f764d17765f2657daa17f43f0f2c8c'),
    'Legacy.Maliev.CustomerService.Tests/Controllers/CustomerAdministrativeEmailComparisonHttpTests.cs': (21935, '7bd2fe8e1008ab8da76c0783d3c552e49e846e0bb3cceb4efffe7f202a9ad318'),
    'Legacy.Maliev.CustomerService.Tests/Controllers/CustomerEmailCanonicalCaseHttpTests.cs': (19962, '23989479e178b0a0b5628f00578ec3ca163f6568e69ab360bb8a807be39d0483'),
    'Legacy.Maliev.CustomerService.Tests/Data/CustomerPostgresMigrationTests.cs': (22057, 'e1e11de82bb805212726a58443f5026be17f05991b29866728004798596bef9b'),
    'scripts/test_customer_optional_contact_focus.py': (52181, 'b731c2ef009121207ff5cdd5069a415298f4b96655a20ed00039e1855d40cbbf'),
    'scripts/customer_artifact_transport_profile.py': (23120, 'f4ab5bcccedffcbc56697eee6fe45f420c4be4a7cd8bc39e2b3312455666c449'),
    'scripts/test_customer_artifact_transport_profile.py': (7759, '4f7ec70aef84cb8892cb0c896e8ffab9692ff75b6eca90f79246a2e2eb286dc0'),
    'scripts/customer-email-focused-manifest.json': (212225, '800fbb059ddb6e0a9020d82c99488ccafc247263c9439d014bd620e20747f79d'),
    'scripts/customer-default-page-focused-manifest.json': (192203, '1bcf782c03ddc6503be86c7aaa7fa55c89ad9c011f8284b596cfaab2b0b0fbeb'),
    'scripts/customer-company-focused-manifest.json': (225651, '6677650da29c41f1b7260b3523a13bcf39417249f1217e549788e05b3a4ebf1d'),
    'scripts/customer-address-focused-manifest.json': (228415, 'c4a3a38969826f5b112b522b7ec9481c33495cab794e8162eefb695ead828818'),
}
ambiguity_EXTRAS = ['scripts/customer_artifact_transport_profile.py', 'scripts/test_customer_artifact_transport_profile.py', 'scripts/test_customer_optional_contact_focus.py']


def ambiguity_archived(root, path):
    size, digest = ambiguity_PRE[path]
    name = ambiguity_ROOT + Path(path).name + '.txt'
    data = read(root, name)
    need(len(data) == size and hashlib.sha256(data).hexdigest() == digest, 'Exact ambiguity predecessor required')
    return data


def ambiguity_old_module(root):
    data = ambiguity_archived(root, 'scripts/customer_artifact_transport_profile.py')
    spec = importlib.util.spec_from_loader('frozen_customer_transport', loader=None)
    module = importlib.util.module_from_spec(spec)
    exec(compile(data, '<frozen-customer-transport>', 'exec'), module.__dict__)
    return module


def ambiguity_old_manifests(root, paths=None):
    return {path: parse(ambiguity_archived(root, path)) for path in (ambiguity_MANIFESTS if paths is None else paths)}


def ambiguity_expected_metadata(root, path, current, old):
    before = rows_by_path(old['codePins'])
    after = rows_by_path(current['codePins'])
    need(list(before) == list(after), 'Original source pin roster and order required')
    changes = []
    for name, previous in before.items():
        expected = previous
        if name in ambiguity_POST:
            data = read(root, name)
            size, digest = ambiguity_POST[name]
            need(len(data) == size and hashlib.sha256(data).hexdigest() == digest, 'Exact ambiguity postimage required')
            need(same(pin(name, ambiguity_archived(root, name)), previous), 'Exact previous source pin required')
            expected = pin(name, data)
        elif name in ambiguity_MANIFESTS:
            need(name in PARENTS[path], 'Declared focused parent required')
            data = read(root, name)
            ambiguity_validate_current(root, name, parse(data))
            expected = pin(name, data)
        need(same(after[name], expected), 'Unapproved current source or dependency pin')
        if not same(previous, expected):
            changes.append(dict(path=name, predecessor=previous, current=expected))
    extras = set(ambiguity_EXTRAS) | set(ambiguity_POST) | {ambiguity_ROOT + Path(name).name + '.txt' for name in ambiguity_PRE}
    return dict(schemaVersion=1, scope=ambiguity_SCOPE, predecessorMain='b12f18aa3c07f9839473f22b7eeb5a48221dd5f4',
                predecessorManifest=pin(path, ambiguity_archived(root, path)), reviewedChanges=changes,
                additionalInputs=[pin(name, read(root, name)) for name in sorted(extras)])


@contextlib.contextmanager
def ambiguity_predecessor_view(root, path):
    files = set(ambiguity_PRE)
    required = {path}
    for selected in tuple(required):
        required.update(PARENTS[selected])
    old = ambiguity_old_manifests(root, required)
    for selected in required:
        manifest = old[selected]
        files.update(row['path'] for row in manifest['codePins'])
        files.update(row['path'] for row in manifest[FIELD]['additionalInputs'])
        files.update(row['successor']['path'] for row in manifest['documentationSuccessor']['reviewedChanges'])
    parent = Path(root).resolve().parent
    with tempfile.TemporaryDirectory(prefix='.customer-ambiguity-history-', dir=parent) as temp:
        target = Path(temp)
        need(target.resolve().parent == parent, 'Owned predecessor view required')
        for path in files:
            data = ambiguity_archived(root, path) if path in ambiguity_PRE else read(root, path)
            destination = target / path
            destination.parent.mkdir(parents=True, exist_ok=True)
            destination.write_bytes(data)
        yield target, old


def ambiguity_validate_current(root, path, manifest):
    need(path in ambiguity_MANIFESTS, 'Known current manifest required')
    actual = parse(read(root, path))
    need(manifest == actual and same(manifest, actual), 'Exact current manifest required')
    old = ambiguity_old_manifests(root, [path])[path]
    need(same({k: v for k, v in manifest.items() if k not in ('codePins', ambiguity_FIELD)},
                                  {k: v for k, v in old.items() if k != 'codePins'}), 'Prior profile and policy fields must remain exact')
    for name in ambiguity_PRE:
        ambiguity_archived(root, name)
    for name, (size, digest) in ambiguity_POST.items():
        data = read(root, name)
        need(len(data) == size and hashlib.sha256(data).hexdigest() == digest, 'Exact reviewed current postimage required')
    need(same(manifest.get(ambiguity_FIELD), ambiguity_expected_metadata(root, path, manifest, old)), 'Exact ambiguity successor metadata required')
    for row in manifest['codePins']:
        data = read(root, row['path'])
        need(same(pin(row['path'], data), row), 'Actual current source and dependency pin required')


def ambiguity_authenticate(root, path, manifest):
    ambiguity_validate_current(root, path, manifest)
    module = ambiguity_old_module(root)
    with ambiguity_predecessor_view(root, path) as (previous_root, old):
        return module.authenticate_profile(previous_root, path, old[path])


@contextlib.contextmanager
def ambiguity_historical_view(root, path, manifest):
    ambiguity_validate_current(root, path, manifest)
    module = ambiguity_old_module(root)
    with ambiguity_predecessor_view(root, path) as (previous_root, old):
        with module.historical_view(previous_root, path, old[path]) as result:
            yield result
