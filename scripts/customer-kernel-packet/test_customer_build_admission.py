"""Synthetic admission controls only. No grant creation mode or native execution."""
import copy
import datetime as dt
import json
from pathlib import Path
import tempfile
import unittest
from unittest.mock import patch
import customer_build_admission as admission


class AdmissionControls(unittest.TestCase):
    def setUp(self):
        self.stage = json.loads(Path(__file__).with_name('build-stage.json').read_bytes())
        self.now = dt.datetime(2026, 10, 8, 12, 0, tzinfo=dt.timezone.utc)
        self.driver = 'a' * 64
        self.boot = '11111111-1111-1111-1111-111111111111'
        self.grant = {key: self.stage[key] for key in ('rootThread', 'owner', 'stage', 'transportMain',
                      'sourcePolicySha256', 'providerPlanSha256', 'candidateBase')}
        self.grant.update(schemaVersion=1, rootGranted=True, scope='strict-release-build', runId='b' * 32,
                          driverPacketSha256=self.driver, hostBootId=self.boot, daemonId='synthetic-daemon',
                          stageSha256=admission.STAGE_CANONICAL_SHA256,
                          sourceArchiveSha256=self.stage['sourceArchive']['sha256'], sourceArchiveBytes=self.stage['sourceArchive']['bytes'],
                          issuedUtc=self.now.isoformat(), notBeforeUtc=self.now.isoformat(),
                          expiresUtc=(self.now + dt.timedelta(seconds=900)).isoformat())
        self.evidence = {
            'hostSnapshot': {'checkedUtc': self.now.isoformat(), 'hostBootId': self.boot,
                             'freePhysicalKiB': 4194304, 'nativeProcesses': []},
            'daemonSnapshot': {'checkedUtc': self.now.isoformat(), 'hostBootId': self.boot,
                               'daemonId': 'synthetic-daemon', 'osType': 'linux'},
            'imageSnapshot': {'checkedUtc': self.now.isoformat(), 'hostBootId': self.boot, **self.stage['sdkImage']},
            'handoffs': {lane: {'checkedUtc': self.now.isoformat(), 'threadId': ('%08d' % i) + '-1111-1111-1111-111111111111',
                                'terminal': True, 'activeOwnedNative': False,
                                'evidenceRefs': [{'reference': 'synthetic://direct-control/' + lane, 'sha256': 'c' * 64}]}
                         for i, lane in enumerate(('RootTracking', 'Intranet', 'Workflows'), 1)}}
        self.bind_handoffs()

    def bind_handoffs(self):
        self.evidence['handoffsRaw'] = admission.canonical(self.evidence['handoffs'])
        self.grant['predecessorEvidenceSha256'] = admission.digest(self.evidence['handoffsRaw'])

    def run_validation(self, raw=None, expected_hash=None):
        raw = admission.canonical(self.grant) if raw is None else raw
        return admission.validate_grant(self.stage, self.grant, self.now, self.evidence, self.driver,
                                        admission.digest(raw) if expected_hash is None else expected_hash, raw)

    def reject(self):
        with self.assertRaises(admission.AdmissionError):
            self.run_validation()

    def test_valid_external_byte_binding_returns_independent_build_only_grant(self):
        result = self.run_validation()
        self.assertEqual(result, self.grant)
        self.assertIsNot(result, self.grant)
        self.assertEqual(result['scope'], 'strict-release-build')
        self.assertFalse(self.stage['nativeExecutionGranted'])

    def test_source_metadata_and_mutated_immutable_stage_never_grant(self):
        for key, value in (('rootGranted', False), ('scope', 'full403'), ('runId', 'B' * 32)):
            original = self.grant[key]
            self.grant[key] = value
            self.reject()
            self.grant[key] = original
        self.stage['minimumPhysicalKiB'] -= 1
        self.reject()

    def test_added_permission_fields_cannot_expand_build_scope(self):
        self.grant['providersPermitted'] = True
        self.reject()

    def test_root_owner_stage_source_driver_bindings(self):
        for key in ('rootThread', 'owner', 'stage', 'transportMain', 'sourcePolicySha256',
                    'providerPlanSha256', 'candidateBase', 'driverPacketSha256', 'stageSha256', 'sourceArchiveSha256'):
            original = self.grant[key]
            self.grant[key] = 'wrong'
            self.reject()
            self.grant[key] = original

    def test_untrusted_wrong_hash_and_raw_object_mismatch(self):
        with self.assertRaises(admission.AdmissionError):
            self.run_validation(expected_hash='d' * 64)
        with self.assertRaises(admission.AdmissionError):
            self.run_validation(expected_hash='a' * 32)
        raw = admission.canonical(self.grant)
        self.grant['owner'] = 'other'
        with self.assertRaises(admission.AdmissionError):
            self.run_validation(raw=raw)

    def test_duplicate_json_and_nonfinite_refused(self):
        for raw in (b'{"rootGranted":true,"rootGranted":true}', b'{"value":NaN}'):
            with self.assertRaises(admission.AdmissionError):
                self.run_validation(raw=raw)

    def test_expired_october4_future_notbefore_long_or_short_allocation(self):
        original = copy.deepcopy(self.grant)
        changes = ({'issuedUtc': '2026-10-04T10:00:00Z', 'notBeforeUtc': '2026-10-04T10:00:00Z', 'expiresUtc': '2026-10-04T10:30:00Z'},
                   {'notBeforeUtc': (self.now + dt.timedelta(seconds=1)).isoformat()},
                   {'expiresUtc': (self.now + dt.timedelta(seconds=1801)).isoformat()},
                   {'expiresUtc': (self.now + dt.timedelta(seconds=629)).isoformat()},
                   {'issuedUtc': self.now.replace(tzinfo=None).isoformat()})
        for patch in changes:
            self.grant = dict(original, **patch)
            self.reject()

    def test_remaining630_and_max1800_boundaries(self):
        for seconds in (630, 1800):
            self.grant['expiresUtc'] = (self.now + dt.timedelta(seconds=seconds)).isoformat()
            self.run_validation()

    def test_stale_future_snapshot_and_floor(self):
        host = self.evidence['hostSnapshot']
        for stamp in (self.now - dt.timedelta(seconds=121), self.now + dt.timedelta(seconds=1)):
            host['checkedUtc'] = stamp.isoformat()
            self.reject()
        host['checkedUtc'] = self.now.isoformat()
        host['freePhysicalKiB'] = 4194303
        self.reject()
        host['freePhysicalKiB'] = True
        self.reject()

    def test_competing_native_work_is_preserved_and_blocks(self):
        job = {'pid': 123, 'executable': '/usr/share/dotnet/dotnet', 'startTicks': 12345}
        self.evidence['hostSnapshot']['nativeProcesses'] = [job]
        self.reject()
        self.assertEqual(self.evidence['hostSnapshot']['nativeProcesses'], [job])

    def test_actual_handoffs_not_absence_alone(self):
        handoffs = self.evidence['handoffs']
        original = copy.deepcopy(handoffs)
        for lane in original:
            for field, value in (('terminal', False), ('activeOwnedNative', True), ('evidenceRefs', []),
                                 ('checkedUtc', (self.now - dt.timedelta(seconds=121)).isoformat())):
                self.evidence['handoffs'] = copy.deepcopy(original)
                self.evidence['handoffs'][lane][field] = value
                self.bind_handoffs()
                self.reject()
        self.evidence['handoffs'] = {}
        self.bind_handoffs()
        self.reject()

    def test_unsealed_handoff_substitution_and_raw_digest_drift_refused(self):
        self.evidence['handoffs']['RootTracking']['evidenceRefs'][0]['reference'] = 'synthetic://substituted'
        self.reject()
        self.bind_handoffs()
        self.evidence['handoffsRaw'] += b' '
        self.reject()

    def test_duplicate_raw_handoff_json_refused_even_with_matching_root_hash(self):
        raw = b'{"RootTracking":{},"RootTracking":{}}'
        self.evidence['handoffsRaw'] = raw
        self.grant['predecessorEvidenceSha256'] = admission.digest(raw)
        self.reject()

    def test_archive_bytes_and_extra_native_flag_cannot_change_root_scope(self):
        self.grant['sourceArchiveBytes'] += 1
        self.reject()
        self.grant['sourceArchiveBytes'] = self.stage['sourceArchive']['bytes']
        self.grant['nativeExecutionGranted'] = True
        self.reject()

    def test_host_daemon_image_identity_and_freshness(self):
        original = copy.deepcopy(self.evidence)
        changes = (('hostSnapshot', 'hostBootId', '22222222-2222-2222-2222-222222222222'),
                   ('daemonSnapshot', 'daemonId', 'foreign'), ('daemonSnapshot', 'osType', 'windows'),
                   ('imageSnapshot', 'imageId', 'sha256:' + 'f' * 64), ('imageSnapshot', 'reference', 'sdk:latest'),
                   ('imageSnapshot', 'checkedUtc', (self.now - dt.timedelta(seconds=121)).isoformat()))
        for area, key, value in changes:
            self.evidence = copy.deepcopy(original)
            self.evidence[area][key] = value
            self.reject()

    def test_no_io_is_used_by_pure_validator(self):
        with patch.object(Path, 'read_bytes', side_effect=AssertionError('IO forbidden')), \
             patch.object(Path, 'read_text', side_effect=AssertionError('IO forbidden')):
            self.run_validation()


class SnapshotControls(unittest.TestCase):
    def test_exact_executable_start_ticks_and_memfree_not_available(self):
        with tempfile.TemporaryDirectory() as temp:
            root = Path(temp)
            boot = root / 'sys/kernel/random/boot_id'
            boot.parent.mkdir(parents=True)
            boot.write_text('11111111-1111-1111-1111-111111111111\n')
            (root / 'meminfo').write_text('MemFree: 1234567 kB\nMemAvailable: 9999999 kB\n')
            process = root / '123'
            process.mkdir()
            fields = ['S'] + ['0'] * 18 + ['12345'] + ['0'] * 5
            (process / 'stat').write_text('123 (dotnet worker) ' + ' '.join(fields))
            with patch.object(admission.os, 'readlink', return_value='/usr/share/dotnet/dotnet'):
                snapshot = admission.linux_host_snapshot(root)
            self.assertEqual(snapshot['freePhysicalKiB'], 1234567)
            self.assertEqual(snapshot['nativeProcesses'], [{'pid': 123, 'executable': '/usr/share/dotnet/dotnet', 'startTicks': 12345}])


if __name__ == '__main__':
    unittest.main()
