"""Synthetic parser/receipt controls only; never historical or C# runtime proof."""
import datetime as dt
import hashlib
import json
import os
from pathlib import Path
import tempfile
import time
import unittest
from unittest import mock
import uuid
import xml.etree.ElementTree as E

import verify_customer_optional_contact_focus as v


NS = 'http://microsoft.com/schemas/VisualStudio/TeamTest/2010'
CLASS = 'Legacy.Maliev.CustomerService.Tests.Controllers.CustomerNameLiteralHttpTests'
METHOD = 'SourceOptionalContact_OrdinaryCreateAndUpdateComparePinnedReference'
NAMES = [f'{CLASS}.{METHOD}(update: False)', f'{CLASS}.{METHOD}(update: True)']
ZERO = ('failed', 'error', 'timeout', 'aborted', 'inconclusive', 'passedButRunAborted',
        'notRunnable', 'notExecuted', 'disconnected', 'warning', 'completed', 'inProgress', 'pending')


def utc(ns):
    return dt.datetime.fromtimestamp(ns / 1e9, dt.timezone.utc).isoformat()


def fixture(start_ns):
    root = E.Element(f'{{{NS}}}TestRun', id=str(uuid.uuid4()))
    E.SubElement(root, f'{{{NS}}}Times', creation=utc(start_ns + 1_000_000_000),
                 queuing=utc(start_ns + 1_000_000_000), start=utc(start_ns + 2_000_000_000),
                 finish=utc(start_ns + 4_000_000_000))
    results = E.SubElement(root, f'{{{NS}}}Results')
    definitions = E.SubElement(root, f'{{{NS}}}TestDefinitions')
    entries = E.SubElement(root, f'{{{NS}}}TestEntries')
    for name in NAMES:
        test_id, execution_id = str(uuid.uuid4()), str(uuid.uuid4())
        E.SubElement(results, f'{{{NS}}}UnitTestResult', testName=name, testId=test_id,
                     executionId=execution_id, outcome='Passed',
                     startTime=utc(start_ns + 2_000_000_000), endTime=utc(start_ns + 3_000_000_000))
        definition = E.SubElement(definitions, f'{{{NS}}}UnitTest', id=test_id, name=name)
        E.SubElement(definition, f'{{{NS}}}Execution', id=execution_id)
        E.SubElement(definition, f'{{{NS}}}TestMethod', className=CLASS, name=METHOD,
                     codeBase='Legacy.Maliev.CustomerService.Tests.dll')
        E.SubElement(entries, f'{{{NS}}}TestEntry', testId=test_id, executionId=execution_id)
    summary = E.SubElement(root, f'{{{NS}}}ResultSummary', outcome='Completed')
    E.SubElement(summary, f'{{{NS}}}Counters', total='2', executed='2', passed='2',
                 **{key: '0' for key in ZERO})
    return root


class FocusDocumentControls(unittest.TestCase):
    def setUp(self):
        self.start = time.time_ns() - 10_000_000_000
        self.doc = fixture(self.start)

    def raw(self):
        return E.tostring(self.doc, encoding='utf-8', xml_declaration=True)

    def rows(self, parent, name):
        return self.doc.findall(f'{{{NS}}}{parent}/{{{NS}}}{name}')

    def reject(self):
        with self.assertRaises(v.VerificationError):
            v.validate_trx(self.raw())

    def test_exact_two_actual_ids_are_collected_not_predicted(self):
        report = v.validate_trx(self.raw())
        self.assertEqual(2, report.get('tests'))
        self.assertEqual(NAMES, sorted(report['caseNames']))
        self.assertEqual(2, len(set(report['testIds'])))
        self.assertEqual(2, len(set(report['executionIds'])))
        self.assertEqual([False, True], report['caseParameters'])

    def test_missing_case_rejected(self):
        self.doc.find(f'{{{NS}}}Results').remove(self.rows('Results', 'UnitTestResult')[1])
        self.reject()

    def test_duplicate_false_case_rejected(self):
        self.rows('Results', 'UnitTestResult')[1].set('testName', NAMES[0])
        self.reject()

    def test_other_method_or_parameter_rejected(self):
        for name in (NAMES[0].replace(METHOD, 'Other'), NAMES[0].replace('False', 'null')):
            with self.subTest(name=name):
                self.rows('Results', 'UnitTestResult')[0].set('testName', name)
                self.reject()

    def test_nonpassing_results_rejected(self):
        for outcome in ('Failed', 'NotExecuted', 'Skipped', 'Inconclusive'):
            with self.subTest(outcome=outcome):
                self.rows('Results', 'UnitTestResult')[0].set('outcome', outcome)
                self.reject()

    def test_nonpassing_counters_rejected(self):
        counters = self.doc.find(f'{{{NS}}}ResultSummary/{{{NS}}}Counters')
        for key in ZERO:
            with self.subTest(key=key):
                counters.set(key, '1')
                self.reject()
                counters.set(key, '0')

    def test_missing_counter_and_false_totals_rejected(self):
        for key, value in (('total', '0'), ('executed', '1'), ('passed', '3'), ('failed', None)):
            with self.subTest(key=key):
                counters = self.doc.find(f'{{{NS}}}ResultSummary/{{{NS}}}Counters')
                old = counters.attrib.pop(key)
                if value is not None:
                    counters.set(key, value)
                self.reject()
                counters.set(key, old)

    def test_duplicate_test_or_execution_guid_rejected(self):
        for attr in ('testId', 'executionId'):
            with self.subTest(attr=attr):
                rows = self.rows('Results', 'UnitTestResult')
                old = rows[1].get(attr)
                rows[1].set(attr, rows[0].get(attr))
                self.reject()
                rows[1].set(attr, old)

    def test_bad_guid_rejected(self):
        for value in ('', 'wrong', str(uuid.UUID(int=0)), str(uuid.uuid4()).upper()):
            with self.subTest(value=value):
                self.rows('Results', 'UnitTestResult')[0].set('executionId', value)
                self.reject()

    def test_definition_execution_bijection_rejected(self):
        self.rows('TestDefinitions', 'UnitTest')[0].find(f'{{{NS}}}Execution').set('id', str(uuid.uuid4()))
        self.reject()

    def test_entry_bijection_rejected(self):
        self.rows('TestEntries', 'TestEntry')[0].set('executionId', str(uuid.uuid4()))
        self.reject()

    def test_definition_class_method_and_name_rejected(self):
        definition = self.rows('TestDefinitions', 'UnitTest')[0]
        method = definition.find(f'{{{NS}}}TestMethod')
        for node, attr in ((method, 'className'), (method, 'name'), (definition, 'name')):
            with self.subTest(attr=attr):
                old = node.get(attr)
                node.set(attr, 'Other')
                self.reject()
                node.set(attr, old)

    def test_shadow_and_wrong_namespace_result_rejected(self):
        shadow = E.SubElement(self.doc, f'{{{NS}}}Shadow')
        E.SubElement(shadow, 'UnitTestResult', outcome='Passed')
        self.reject()

    def test_runner_adverse_or_summary_failure_rejected(self):
        self.doc.find(f'{{{NS}}}ResultSummary').set('outcome', 'Failed')
        self.reject()

    def test_error_info_rejected(self):
        E.SubElement(self.doc, f'{{{NS}}}ErrorInfo')
        self.reject()

    def test_zero_results_and_substituted_definition_children_rejected(self):
        results = self.doc.find(f'{{{NS}}}Results')
        rows = list(results)
        for row in rows:
            results.remove(row)
        self.reject()
        results.extend(rows)
        E.SubElement(self.doc, f'{{{NS}}}TestMethod', className=CLASS, name=METHOD)
        self.reject()

    def test_assembly_and_times_rejected(self):
        method = self.rows('TestDefinitions', 'UnitTest')[0].find(f'{{{NS}}}TestMethod')
        method.set('codeBase', 'Other.dll')
        self.reject()
        method.set('codeBase', 'Legacy.Maliev.CustomerService.Tests.dll')
        self.doc.find(f'{{{NS}}}Times').set('finish', '2000-01-01T00:00:00+00:00')
        self.reject()

    def test_strict_build_requires_zero_summary_and_no_diagnostics(self):
        self.assertTrue(v.validate_build(b'Build succeeded.\n    0 Warning(s)\n    0 Error(s)\n'))
        for raw in (b'Build failed.', b'Build succeeded.\n1 Warning(s)\n0 Error(s)',
                    b'Build succeeded.\n0 Warning(s)\n0 Error(s)\na.cs: warning CS123: wrong',
                    b'Build succeeded.\n0 Warning(s)\n0 Error(s)\na.cs: error CS123: wrong'):
            with self.subTest(raw=raw), self.assertRaises(v.VerificationError):
                v.validate_build(raw)

    def test_dtd_and_malformed_xml_rejected(self):
        for raw in (b'<!DOCTYPE TestRun [<!ENTITY x "x">]><TestRun/>', b'<bad>'):
            with self.subTest(raw=raw):
                with self.assertRaises(v.VerificationError):
                    v.validate_trx(raw)


class FocusReceiptControls(unittest.TestCase):
    def setUp(self):
        parent = os.environ.get('CUSTOMER_OPTIONAL_PURE_TMP')
        self.temp = tempfile.TemporaryDirectory(dir=parent)
        self.addCleanup(self.temp.cleanup)
        self.root = Path(self.temp.name)
        self.source = self.root / v.SOURCE_PATH
        self.source.parent.mkdir(parents=True)
        self.source.write_bytes(b'synthetic source only, not C# proof')
        self.sha = hashlib.sha256(self.source.read_bytes()).hexdigest()
        self.patch = mock.patch.object(v, 'REVIEWED_SOURCE_SHA256', self.sha)
        self.patch.start()
        self.addCleanup(self.patch.stop)
        manifest = self.root / v.MANIFEST_PATH
        manifest.parent.mkdir(parents=True, exist_ok=True)
        manifest.write_text(json.dumps({'codePins': [
            {'path': v.SOURCE_PATH, 'bytes': self.source.stat().st_size, 'sha256': self.sha}
        ]}), encoding='utf-8')
        self.out = self.root / 'focused'
        self.context = {'githubSha': 'a' * 40, 'sourceHead': 'b' * 40, 'runId': '123', 'runAttempt': '1'}
        self.started = time.time_ns() - 10_000_000_000

    def prepare(self):
        self.out.mkdir(exist_ok=True)
        (self.out / 'build.log').write_bytes(b'Build succeeded.\n    0 Warning(s)\n    0 Error(s)\n')
        return v.begin(self.root, self.out, self.context, now_ns=self.started)

    def write_trx(self, *, mtime=None):
        results = self.out / 'results'
        results.mkdir(exist_ok=True)
        path = results / 'customer-optional-contact.trx'
        path.write_bytes(E.tostring(fixture(self.started), encoding='utf-8'))
        stamp = self.started + 5_000_000_000 if mtime is None else mtime
        os.utime(path, ns=(stamp, stamp))
        return path

    def verify(self, **kw):
        return v.verify(self.root, self.out, self.context, now_ns=self.started + 8_000_000_000, **kw)

    def test_fresh_source_bound_results_accepted_without_parity_claim(self):
        self.prepare()
        self.write_trx()
        report = self.verify()
        self.assertEqual(2, report.get('tests'))
        self.assertEqual(self.context, report['context'])
        self.assertEqual(102, report['comparisonAttemptsForecast'])
        self.assertFalse(report['historicalRuntimeProof'])

    def test_begin_refuses_reuse_or_existing_results(self):
        self.out.mkdir()
        (self.out / 'results').mkdir()
        with self.assertRaises(v.VerificationError):
            self.prepare()

    def test_begin_refuses_wrong_source(self):
        self.source.write_bytes(b'changed')
        with self.assertRaises(v.VerificationError):
            self.prepare()

    def test_missing_or_multiple_trx_rejected(self):
        self.prepare()
        with self.assertRaises(v.VerificationError):
            self.verify()
        first = self.write_trx()
        (first.parent / 'second.trx').write_bytes(first.read_bytes())
        with self.assertRaises(v.VerificationError):
            self.verify()

    def test_stale_file_rejected(self):
        self.prepare()
        self.write_trx(mtime=self.started - 1_000_000_000)
        with self.assertRaises(v.VerificationError):
            self.verify()

    def test_old_trx_times_rejected_even_with_fresh_mtime(self):
        self.prepare()
        path = self.write_trx()
        path.write_bytes(E.tostring(fixture(self.started - 100_000_000_000), encoding='utf-8'))
        os.utime(path, ns=(self.started + 5_000_000_000, self.started + 5_000_000_000))
        with self.assertRaises(v.VerificationError):
            self.verify()

    def test_context_source_and_manifest_changes_rejected(self):
        self.prepare()
        self.write_trx()
        changed = dict(self.context, runId='124')
        with self.assertRaises(v.VerificationError):
            v.verify(self.root, self.out, changed, now_ns=self.started + 8_000_000_000)
        self.source.write_bytes(b'changed')
        with self.assertRaises(v.VerificationError):
            self.verify()

    def test_manifest_change_rejected(self):
        self.prepare()
        self.write_trx()
        with (self.root / v.MANIFEST_PATH).open('a', encoding='utf-8') as stream:
            stream.write(' ')
        with self.assertRaises(v.VerificationError):
            self.verify()

    def test_failed_test_or_capture_exit_rejected(self):
        self.prepare()
        self.write_trx()
        for field in ('test_exit', 'tee_exit'):
            with self.subTest(field=field), self.assertRaises(v.VerificationError):
                self.verify(**{field: 1})

    def test_expired_or_future_receipt_rejected(self):
        self.prepare()
        self.write_trx()
        for now in (self.started - 1, self.started + 601_000_000_000):
            with self.subTest(now=now), self.assertRaises(v.VerificationError):
                v.verify(self.root, self.out, self.context, now_ns=now)

    def test_changed_build_rejected(self):
        self.prepare()
        self.write_trx()
        (self.out / 'build.log').write_bytes(b'Build succeeded.\n0 Warning(s)\n0 Error(s)\nchanged')
        with self.assertRaises(v.VerificationError):
            self.verify()

    def test_missing_build_and_invalid_context_rejected(self):
        with self.assertRaises(v.VerificationError):
            v.begin(self.root, self.out, self.context, now_ns=self.started)
        for patch in ({'sourceHead': 'main'}, {'runId': '0'}, {'runAttempt': True}, {'extra': 'x'}):
            with self.subTest(patch=patch), self.assertRaises(v.VerificationError):
                v.begin(self.root, self.out, dict(self.context, **patch), now_ns=self.started)


if __name__ == '__main__':
    unittest.main(verbosity=2)
