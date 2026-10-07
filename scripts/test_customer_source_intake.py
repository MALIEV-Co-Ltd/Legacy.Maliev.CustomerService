"""Offline raw-intake controls. No online fetch, SDK, providers or child workers."""
import copy
import hashlib
import io
import json
from pathlib import Path
import tempfile
import unittest
from unittest.mock import patch
import zipfile

import customer_source_intake as intake


def raw_json(value):
    return json.dumps(value, sort_keys=True, separators=(',', ':')).encode()


class IntakeControls(unittest.TestCase):
    def setUp(self):
        original_bootstrap = intake.POLICY_SHA256
        self.addCleanup(setattr, intake, 'POLICY_SHA256', original_bootstrap)
        self.helper = intake.accepted_helper()
        self.temp = tempfile.TemporaryDirectory()
        self.addCleanup(self.temp.cleanup)
        self.target = Path(self.temp.name) / 'fresh'
        original_plan_sha = intake.PROVIDER_PLAN_SHA256
        self.addCleanup(setattr, intake, 'PROVIDER_PLAN_SHA256', original_plan_sha)
        source = {'repo/source-%03d.cs' % i: (b'a\r\nb\n' if i == 0 else ('source%d' % i).encode()) for i in range(117)}
        whole = [{'path': p[5:], 'sha256': intake.sha(b)} for p, b in source.items()]
        candidate_rows = [{'path': p[5:], 'candidateSha256': intake.sha(source[p])} for p in list(source)[:5]]
        source['repo/docs/candidate.md'] = b'additional doc\r\n'
        candidate_rows.append({'path': 'docs/candidate.md', 'candidateSha256': intake.sha(source['repo/docs/candidate.md'])})
        candidate = {'base': intake.BASE, 'files': candidate_rows, 'fullCasesForecast': 403}
        dependencies = {}; dep_manifest = []
        for repo, commit in intake.DEPENDENCIES.items():
            count = 122 if repo.endswith('ServiceDefaults') else 19
            rows = []
            for i in range(count):
                path = 'source/%03d.cs' % i
                value = ('dependency-%s-%d' % (repo, i)).encode()
                dependencies['dependencies/' + repo + '/' + path] = value
                rows.append({'path': path, 'sha256': intake.sha(value)})
            dep_manifest.append({'snapshot': 'customer-baseline', 'repository': repo, 'commit': commit,
                                 'extractedFileCount': count, 'files': rows})
        validation = {p: b'validation fixture' for p in intake.ENVELOPE}
        provider_plan = {'executionAdmission': False, 'producerBase': intake.BASE, 'trackedSourceFilesVerified': 117,
                         'candidateFilesVerified': 6, 'dependencyFilesVerified': 141, 'bundleFiles': 271,
                         'normalFixtureDisposal': intake.NORMAL_DISPOSAL, 'sdkRawSocketMount': False,
                         'proxyAndRecoveryRawSocketReadonlyBind': True,
                         'ledgerMountReadOnly': {'sdk': True, 'proxy': False, 'recovery': True},
                         'sdkEnvironment': {'DOCKER_HOST': 'unix:///provider-ledger/proxy.sock', 'TESTCONTAINERS_RYUK_DISABLED': 'true',
                                            'TESTCONTAINERS_HOST_OVERRIDE': '127.0.0.1', 'MalievWorkspaceRoot': '/work/dependencies'}}
        plan_raw = raw_json(provider_plan)
        intake.PROVIDER_PLAN_SHA256 = intake.sha(plan_raw)
        validation.update({'serial.runsettings': b'<RunSettings/>', 'metadata/provider-plan.json': plan_raw,
                           'metadata/candidate-manifest.json': raw_json(candidate),
                           'metadata/all-source-seals.json': raw_json(whole),
                           'metadata/dependency-manifest.json': raw_json(dep_manifest)})
        self.files = {'source': source, 'dependencies': dependencies, 'validation': validation}
        self.seals = {role: (path, intake.sha(validation[path])) for role, (path, _) in intake.SEALS.items()}
        self.addCleanup(patch.stopall)
        patch.object(intake, 'SEALS', self.seals).start()
        self.policy = {'schemaVersion': 1, 'repository': intake.REPOSITORY, 'baseCommit': intake.BASE,
                       'acceptedModuleSha256': intake.MODULE_SHA256, 'forecastCases': 403, 'nativeExecutionGranted': False,
                       'providerPlanSha256': intake.PROVIDER_PLAN_SHA256, 'providerEnvelopeVersion': 'r4', 'actualProviderRuntimeQualification': False,
                       'fileModuleSourceAcceptance': {'protectedFileMainCommit': intake.FILE_MAIN_COMMIT,
                            'manifestSha256': intake.FILE_MANIFEST_SHA256, 'protectedFileMainRefStillPending': False,
                            'immutableV5': True, 'nativeWindowsOrSdkAcceptance': False},
                       'capsules': {}, 'seals': {k: {'path': p, 'sha256': s} for k, (p, s) in self.seals.items()}, 'rawFiles': {}}
        self.capsules = {}
        self.repack()

    def repack(self):
        self.policy['rawFiles'] = {}
        for role, files in self.files.items():
            buffer = io.BytesIO()
            with zipfile.ZipFile(buffer, 'w', compression=zipfile.ZIP_DEFLATED) as archive:
                for path, value in sorted(files.items()):
                    info = zipfile.ZipInfo(path, (2026, 10, 8, 0, 0, 0))
                    info.compress_type = zipfile.ZIP_DEFLATED
                    info.create_system = 3
                    info.external_attr = 0o100600 << 16
                    archive.writestr(info, value)
            raw = buffer.getvalue(); rows = []
            with zipfile.ZipFile(io.BytesIO(raw)) as archive:
                for item in archive.infolist():
                    rows.append({'path': item.filename, 'bytes': item.file_size, 'sha256': intake.sha(files[item.filename]),
                                 'compressedBytes': item.compress_size, 'crc32': item.CRC, 'createSystem': item.create_system,
                                 'externalAttributes': item.external_attr, 'flags': item.flag_bits, 'compression': item.compress_type,
                                 'dateTime': list(item.date_time)})
                    self.policy['rawFiles'][item.filename] = {'capsule': role, 'member': item.filename,
                                                              'sha256': intake.sha(files[item.filename]), 'bytes': item.file_size}
            self.capsules[role] = raw
            self.policy['capsules'][role] = {'oid': hashlib.sha1(b'blob ' + str(len(raw)).encode() + b'\0' + raw).hexdigest(),
                                            'sha256': intake.sha(raw), 'bytes': len(raw), 'rows': rows}

    def sealed_policy(self):
        raw = raw_json(self.policy)
        intake.POLICY_SHA256 = intake.sha(raw)
        return raw

    def reject(self):
        with self.assertRaises((ValueError, KeyError)):
            intake.materialize(self.sealed_policy(), self.capsules, self.target, self.helper)
        self.assertFalse(self.target.exists())

    def test_all275_raw_bytes_crlf_and_403_forecast_materialize(self):
        receipt = intake.materialize(self.sealed_policy(), self.capsules, self.target, self.helper)
        self.assertEqual(receipt['rawFiles'], 275)
        self.assertEqual(receipt['forecastCases'], 403)
        self.assertEqual(receipt['actualTestsRun'], 0)
        self.assertFalse(receipt['nativeExecutionGranted'])
        self.assertEqual((self.target / 'repo/source-000.cs').read_bytes(), b'a\r\nb\n')
        self.assertTrue((self.target / 'metadata/intake-receipt.json').is_file())

    def test_wrong_repository_base_case_forecast_or_execution_flag(self):
        for key, wrong in (('repository', 'foreign/repo'), ('baseCommit', 'a' * 40), ('forecastCases', 402),
                           ('nativeExecutionGranted', True), ('acceptedModuleSha256', 'a' * 64)):
            original = self.policy[key]
            self.policy[key] = wrong
            self.reject()
            self.policy[key] = original

    def test_alias_and_duplicate_cross_capsule_graph(self):
        row = self.policy['capsules']['dependencies']['rows'][0]
        old = row['path']
        row['path'] = 'repo/source-000.cs'
        self.reject()
        row['path'] = 'repo/SOURCE-000.cs'
        self.reject()
        row['path'] = old

    def test_capsule_bytes_oid_and_header_drift_before_writes(self):
        original = self.capsules['source']
        self.capsules['source'] = original + b'drift'
        self.reject()
        self.capsules['source'] = original
        self.policy['capsules']['source']['rows'][0]['crc32'] ^= 1
        self.reject()

    def test_raw_crlf_change_not_normalized(self):
        self.files['source']['repo/source-000.cs'] = b'a\nb\n'
        self.repack()
        self.reject()

    def test_wrong_dependency_or_nested_duplicate(self):
        path = 'metadata/dependency-manifest.json'
        deps = json.loads(self.files['validation'][path])
        deps[0]['commit'] = 'b' * 40
        self.files['validation'][path] = raw_json(deps)
        self.seals['dependencies141'] = (path, intake.sha(self.files['validation'][path]))
        self.policy['seals']['dependencies141']['sha256'] = self.seals['dependencies141'][1]
        self.repack()
        self.reject()

    def test_duplicate_nested117_record_refused_before_writes(self):
        path = 'metadata/all-source-seals.json'
        whole = json.loads(self.files['validation'][path])
        whole[1] = copy.deepcopy(whole[0])
        self.files['validation'][path] = raw_json(whole)
        self.seals['source117'] = (path, intake.sha(self.files['validation'][path]))
        self.policy['seals']['source117']['sha256'] = self.seals['source117'][1]
        self.repack()
        self.reject()

    def test_reentry_forbidden(self):
        raw = self.sealed_policy()
        intake.materialize(raw, self.capsules, self.target, self.helper)
        with self.assertRaises(ValueError):
            intake.materialize(raw, self.capsules, self.target, self.helper)

    def test_receipt_failure_never_reports_success_and_preserves_partial_root(self):
        original = self.helper.write_new
        def fail_receipt(root, relative, raw):
            if relative == 'metadata/intake-receipt.json':
                raise OSError('synthetic receipt failure')
            return original(root, relative, raw)
        with patch.object(self.helper, 'write_new', side_effect=fail_receipt):
            with self.assertRaises(OSError):
                intake.materialize(self.sealed_policy(), self.capsules, self.target, self.helper)
        self.assertTrue(self.target.exists())
        self.assertFalse((self.target / 'metadata/intake-receipt.json').exists())

    def test_unsealed_wrong_policy_hash_and_duplicate_json_refused(self):
        intake.POLICY_SHA256 = 'UNSEALED'
        with self.assertRaises(ValueError):
            intake.load_policy(raw_json(self.policy), self.helper)
        raw = self.sealed_policy()
        with self.assertRaises(ValueError):
            intake.load_policy(raw + b' ', self.helper)
        duplicate = b'{"schemaVersion":1,"schemaVersion":1}'
        intake.POLICY_SHA256 = intake.sha(duplicate)
        with self.assertRaises(ValueError):
            intake.load_policy(duplicate, self.helper)

    def test_no_online_fetch_or_subprocess_in_offline_controls(self):
        with patch.object(self.helper, 'fetch_git_blob', side_effect=AssertionError('online fetch forbidden')), \
             patch.object(self.helper.subprocess, 'Popen', side_effect=AssertionError('child worker forbidden')):
            _, files = intake.validate_inputs(self.sealed_policy(), self.capsules, self.helper)
            self.assertEqual(len(files), 275)

    def test_old13_roster_refused_before_fetch_or_write(self):
        for path in ('envelope/customer_fixture_disposal.py', 'envelope/test_customer_fixture_disposal.py',
                     'envelope/test_customer_disposal_peer_faults.py'):
            del self.files['validation'][path]
        self.repack()
        raw = self.sealed_policy()
        with patch.object(self.helper, 'fetch_git_blob', side_effect=AssertionError('fetch must not occur')) as fetch:
            with self.assertRaises(ValueError):
                intake.fetch_inputs(raw, self.helper)
            fetch.assert_not_called()
        self.reject()

    def test_provider_policy_seal_drift_refused_before_fetch(self):
        self.policy['providerPlanSha256'] = 'a' * 64
        with patch.object(self.helper, 'fetch_git_blob', side_effect=AssertionError('fetch must not occur')) as fetch:
            with self.assertRaises(ValueError):
                intake.fetch_inputs(self.sealed_policy(), self.helper)
            fetch.assert_not_called()
        self.reject()

    def test_provider_raw_plan_drift_and_selfgrant_refused_before_writes(self):
        path = 'metadata/provider-plan.json'
        original = self.files['validation'][path]
        self.files['validation'][path] = original + b' '
        self.repack()
        self.reject()
        for key, value in (('executionAdmission', True), ('sdkRawSocketMount', True),
                           ('ledgerMountReadOnly', {'sdk': False, 'proxy': False, 'recovery': True}),
                           ('normalFixtureDisposal', 'force-delete')):
            plan = json.loads(original)
            plan[key] = value
            self.files['validation'][path] = raw_json(plan)
            # Component-control fixture changes its synthetic expected hash,
            # so the independent authority-field oracle must still reject.
            intake.PROVIDER_PLAN_SHA256 = intake.sha(self.files['validation'][path])
            self.policy['providerPlanSha256'] = intake.PROVIDER_PLAN_SHA256
            self.repack()
            self.reject()


class ActualPacketControls(unittest.TestCase):
    def test_exact_frozen_policy_module_and_capsules_offline_materialize(self):
        helper = intake.accepted_helper()
        scripts = Path(__file__).resolve().parent
        packet = Path(__file__).resolve().parents[3] / 'outputs/customer-hosted-transport-20261008-r4'
        policy_raw = (scripts / 'customer-candidate-policy.json').read_bytes()
        capsules = {role: (packet / (role + '.zip')).read_bytes() for role in intake.COUNTS}
        with tempfile.TemporaryDirectory() as temp, \
             patch.object(helper, 'fetch_git_blob', side_effect=AssertionError('online fetch forbidden')), \
             patch.object(helper.subprocess, 'Popen', side_effect=AssertionError('child worker forbidden')):
            root = Path(temp) / 'fresh'
            receipt = intake.materialize(policy_raw, capsules, root, helper)
            self.assertEqual(receipt['rawFiles'], 275)
            self.assertEqual(receipt['forecastCases'], 403)
            self.assertFalse(receipt['nativeExecutionGranted'])
            policy = intake.load_policy(policy_raw, helper)
            for path, row in policy['rawFiles'].items():
                raw = (root / path).read_bytes()
                self.assertEqual(intake.sha(raw), row['sha256'])
                self.assertEqual(len(raw), row['bytes'])
            with self.assertRaises(ValueError):
                intake.materialize(policy_raw, capsules, root, helper)

    def test_actual_old_r2_policy_refused_before_fetch_or_write(self):
        helper = intake.accepted_helper()
        old = Path(__file__).resolve().parents[3] / 'work/customer-hosted-transport-20261008/scripts/customer-candidate-policy.json'
        with patch.object(helper, 'fetch_git_blob', side_effect=AssertionError('fetch must not occur')) as fetch:
            with self.assertRaises(ValueError):
                intake.fetch_inputs(old.read_bytes(), helper)
            fetch.assert_not_called()


if __name__ == '__main__':
    unittest.main()
