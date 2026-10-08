"""Pure driver/archive controls; no SDK, daemon, grant execution or extraction.

CUSTOMER_FROZEN_SDK_ARCHIVE optionally locates the external test fixture in a
relocated packet. Its exact immutable stage SHA/byte count still applies. The
native controller never reads this test-only environment variable.
"""
import ast
import contextlib
import copy
import io
import json
import os
from pathlib import Path
import tarfile
import tempfile
import time
import unittest
from unittest.mock import patch

import customer_native_build_driver as driver


ROOT = Path(__file__).resolve().parent
WORKSPACE = ROOT.parents[1]


class DriverArchiveControls(unittest.TestCase):
    @classmethod
    def setUpClass(cls):
        cls.stage = json.loads((ROOT / 'build-stage.json').read_bytes())
        helper_path = ROOT / 'input/sealed_source_capsule.py'
        helper_raw = helper_path.read_bytes()
        if driver.digest(helper_raw) != '44a8a5accac9da11422d606be02fe28487642215df511b5f1c4284296a453ee2':
            raise AssertionError('Frozen File decoder seal drift')
        cls.helper = driver.captured_module('driver_control_helper', helper_path, helper_raw)
        intake_path = ROOT / 'input/customer_source_intake.py'
        cls.intake = driver.captured_module('driver_control_intake', intake_path, intake_path.read_bytes())
        cls.policy = cls.intake.load_policy((ROOT / 'input/customer-candidate-policy.json').read_bytes(), cls.helper)
        archive_path = Path(os.environ.get('CUSTOMER_FROZEN_SDK_ARCHIVE') or
                            WORKSPACE / 'outputs/customer-native-build-first-20261008/sdk-source.tar')
        cls.frozen_archive = driver.regular(archive_path)
        if driver.digest(cls.frozen_archive) != cls.stage['sourceArchive']['sha256'] or len(cls.frozen_archive) != cls.stage['sourceArchive']['bytes']:
            raise AssertionError('Frozen SDK transport seal drift')
        cls.files = {}
        # Controlled frozen fixture reads only. No extract/extractall operation.
        with tarfile.open(fileobj=io.BytesIO(cls.frozen_archive), mode='r:') as archive:
            for member in archive:
                name = cls.helper.canonical_path(member.name)
                if not member.isfile() or name in cls.files or name not in cls.policy['rawFiles']:
                    raise AssertionError('Frozen controlled fixture graph drift')
                expected = cls.policy['rawFiles'][name]
                if member.size != expected['bytes']:
                    raise AssertionError('Frozen fixture member size drift')
                with archive.extractfile(member) as stream:
                    raw = stream.read(member.size + 1)
                if len(raw) != member.size or driver.digest(raw) != expected['sha256']:
                    raise AssertionError('Frozen fixture bytes drift')
                cls.files[name] = raw
        if set(cls.files) != set(cls.policy['rawFiles']) or len(cls.files) != 275:
            raise AssertionError('Exact275 fixture required')

    def live_archive(self, files=None, additions=()):
        files = self.files if files is None else files
        result = io.BytesIO()
        with tarfile.open(fileobj=result, mode='w', format=tarfile.PAX_FORMAT) as archive:
            root = tarfile.TarInfo('work'); root.type = tarfile.DIRTYPE
            archive.addfile(root)
            for name, raw in sorted(files.items()):
                member = tarfile.TarInfo('work/' + name)
                member.size = len(raw)
                archive.addfile(member, io.BytesIO(raw))
            for member, raw in additions:
                archive.addfile(member, None if raw is None else io.BytesIO(raw))
        return result.getvalue()

    def verify(self, raw):
        return driver.verify_live_archive(raw, self.policy, self.helper)

    def materialized(self, root):
        for name, raw in self.files.items():
            self.helper.write_new(root, name, raw)
        receipt = {'policySha256': self.stage['sourcePolicySha256'], 'rawFiles': 275,
                   'nativeExecutionGranted': False, 'providerPlanSha256': self.stage['providerPlanSha256']}
        self.helper.write_new(root, 'metadata/intake-receipt.json', json.dumps(receipt).encode())

    def test_live_archive_exact275_accepts_only_frozen_bytes(self):
        self.assertTrue(self.verify(self.live_archive()))

    def test_live_archive_tamper_missing_and_extra_files_refused(self):
        name = next(iter(self.files))
        for change in ('tamper', 'missing', 'extra'):
            files = dict(self.files)
            if change == 'tamper': files[name] = b'x' * len(files[name])
            elif change == 'missing': del files[name]
            else: files['unknown.txt'] = b'unknown'
            with self.subTest(change=change), self.assertRaises(ValueError):
                self.verify(self.live_archive(files))

    def test_live_archive_alias_duplicate_link_and_traversal_refused(self):
        name = next(iter(self.files))
        raw = self.files[name]
        for member in (tarfile.TarInfo('work/' + name), tarfile.TarInfo('work/' + name.upper()),
                       tarfile.TarInfo('work/../outside'), tarfile.TarInfo('work/symlink')):
            payload = raw
            if member.name.endswith('symlink'):
                member.type = tarfile.SYMTYPE; member.linkname = '/outside'; payload = None
            else:
                member.size = len(raw)
            with self.subTest(path=member.name), self.assertRaises(ValueError):
                self.verify(self.live_archive(additions=[(member, payload)]))

    def test_live_archive_root_and_directory_aliases_or_unknown_dirs_refused(self):
        for name in ('work', 'work/unknown-empty', 'work/REPO'):
            member = tarfile.TarInfo(name); member.type = tarfile.DIRTYPE
            with self.subTest(path=name), self.assertRaises(ValueError):
                self.verify(self.live_archive(additions=[(member, None)]))

    def test_live_archive_legitimate_gated_control_directory_allowed(self):
        member = tarfile.TarInfo('work/.control'); member.type = tarfile.DIRTYPE
        self.assertTrue(self.verify(self.live_archive(additions=[(member, None)])))

    def test_live_archive_byte_and_member_caps_refused(self):
        with self.assertRaisesRegex(ValueError, 'bound'):
            self.verify(b'x' * (driver.MAX_BYTES + 1))
        # Unique, permitted ancestors isolate count rejection from alias refusal.
        policy = {'rawFiles': {'a%d/b/c/d/file.cs' % i: next(iter(self.policy['rawFiles'].values())) for i in range(275)}}
        entries = []
        for index in range(275):
            for suffix in ('', '/b', '/b/c', '/b/c/d'):
                member = tarfile.TarInfo('work/a%d%s' % (index, suffix)); member.type = tarfile.DIRTYPE
                entries.append((member, None))
        raw = self.live_archive(files={}, additions=entries)
        with self.assertRaisesRegex(ValueError, 'count refused'):
            driver.verify_live_archive(raw, policy, self.helper)

    def test_source_archive_exact275_and_transport_digest(self):
        with tempfile.TemporaryDirectory() as folder:
            root = Path(folder) / 'source'
            self.materialized(root)
            result = driver.source_archive(root, self.policy, self.stage, self.helper, self.intake)
            self.assertEqual(result, self.frozen_archive)

    def test_source_archive_missing_bad_receipt_raw_drift_and_extra(self):
        for change in ('receipt-missing', 'receipt-selfgrant', 'raw-drift', 'extra-file'):
            with self.subTest(change=change), tempfile.TemporaryDirectory() as folder:
                root = Path(folder) / 'source'
                self.materialized(root)
                receipt = root / 'metadata/intake-receipt.json'
                if change == 'receipt-missing': receipt.unlink()
                elif change == 'receipt-selfgrant':
                    value = json.loads(receipt.read_bytes()); value['nativeExecutionGranted'] = True
                    receipt.write_bytes(json.dumps(value).encode())
                elif change == 'raw-drift': (root / next(iter(self.files))).write_bytes(b'drift')
                else: self.helper.write_new(root, 'extra.txt', b'extra')
                with self.assertRaises(ValueError):
                    driver.source_archive(root, self.policy, self.stage, self.helper, self.intake)

    def test_source_archive_refuses_changed_frozen_transport_identity(self):
        with tempfile.TemporaryDirectory() as folder:
            root = Path(folder) / 'source'
            self.materialized(root)
            stage = copy.deepcopy(self.stage)
            stage['sourceArchive']['sha256'] = 'f' * 64
            with self.assertRaises(ValueError):
                driver.source_archive(root, self.policy, stage, self.helper, self.intake)

    def test_source_archive_entry_census_is_finite_before_graph_or_receipt(self):
        with tempfile.TemporaryDirectory() as folder:
            root = Path(folder)
            entry = root / 'owned-directory'; entry.mkdir()
            with patch.object(Path, 'rglob', return_value=iter([entry] * 1025)):
                with self.assertRaisesRegex(ValueError, 'entry cap'):
                    driver.source_archive(root, self.policy, self.stage, self.helper, self.intake)

    def test_actual_driver_image_admission_predicate_rejects_false_mapping(self):
        tree = ast.parse(Path(driver.__file__).read_bytes())
        callback = next(node for node in ast.walk(tree) if isinstance(node, ast.FunctionDef) and node.name == 'admit')
        checks = [node for node in ast.walk(callback) if isinstance(node, ast.Call) and isinstance(node.func, ast.Name)
                  and node.func.id == 'need' and len(node.args) >= 2 and isinstance(node.args[1], ast.Constant)
                  and node.args[1].value == 'Fresh actual SDK reference mapping required']
        self.assertEqual(len(checks), 1)
        code = compile(ast.Expression(checks[0].args[0]), '<actual-driver-image-predicate>', 'eval')
        image = {'Id': self.stage['sdkImage']['imageId'], 'Os': 'linux', 'RepoDigests': [self.stage['sdkImage']['reference']]}
        def accepted(value):
            return eval(code, {'__builtins__': {}, 'isinstance': isinstance, 'list': list}, {'image': value, 'stage': self.stage})
        self.assertTrue(accepted(image))
        for key, value in (('Id', 'sha256:' + 'f' * 64), ('Os', 'windows'), ('RepoDigests', []),
                           ('RepoDigests', self.stage['sdkImage']['reference']), ('RepoDigests', ['sdk:latest'])):
            with self.subTest(field=key, value=value):
                self.assertFalse(accepted(dict(image, **{key: value})))


class EntrypointControls(unittest.TestCase):
    def test_windows_docker_request_refused_before_any_io(self):
        backend = driver.DockerAPI(lambda: time.monotonic() + 20)
        with patch.object(driver.sys, 'platform', 'win32'), \
             patch.object(driver.socket, 'socket', side_effect=AssertionError('Socket IO forbidden')) as socket, \
             patch.object(driver.http.client, 'HTTPConnection', side_effect=AssertionError('HTTP IO forbidden')) as http:
            with self.assertRaises(ValueError): backend.request('GET', '/info')
            socket.assert_not_called(); http.assert_not_called()

    def test_foreign_socket_refused_before_io(self):
        with patch.object(driver.socket, 'socket', side_effect=AssertionError('Socket IO forbidden')) as socket:
            for path in ('/tmp/foreign.sock', 'npipe:////./pipe/docker_engine', 'tcp://localhost:2375'):
                with self.subTest(path=path), self.assertRaises(ValueError):
                    driver.DockerAPI(lambda: time.monotonic() + 20, path)
            socket.assert_not_called()

    def test_no_grant_creation_entrypoint_or_implicit_execution_mode(self):
        for mode in ('create-grant', 'permit', 'execute-tests'):
            with patch.object(driver.sys, 'argv', ['driver', mode, '--packet-sha256', 'a' * 64]), \
                 patch.object(driver, 'load_packet', side_effect=AssertionError('Packet IO forbidden')) as packet, \
                 contextlib.redirect_stderr(io.StringIO()):
                with self.assertRaises(SystemExit) as exited:
                    driver.main()
                self.assertEqual(exited.exception.code, 2)
                packet.assert_not_called()


if __name__ == '__main__':
    unittest.main()
