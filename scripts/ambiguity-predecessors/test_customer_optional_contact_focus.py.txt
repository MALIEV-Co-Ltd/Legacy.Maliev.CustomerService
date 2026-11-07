"""Synthetic parser/receipt controls only; never historical or C# runtime proof."""
from customer_artifact_transport_profile import historical_view
from test_customer_artifact_transport_profile import ArtifactTransportControls
import datetime as dt
import copy
import re
import stat
import verify_customer_email_focused as pin_validator
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


def late_logger_times(doc, start_ns):
    times = doc.find(f'{{{NS}}}Times')
    times.set('creation', utc(start_ns + 3_000_000_000))
    times.set('queuing', utc(start_ns + 3_001_000_000))
    return times


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


    def test_late_logger_creation_uses_earliest_run_time(self):
        times = late_logger_times(self.doc, self.start)
        report = v.validate_trx(self.raw())
        self.assertEqual(v.stamp(times.get('start')), report['startNs'])
        self.assertLess(report['startNs'], v.stamp(times.get('creation')))

    def test_misordered_or_out_of_run_times_rejected(self):
        mutations = [
            ('reversed-creation-queue', lambda doc: doc.find(f'{{{NS}}}Times').set('creation', utc(self.start + 3_000_000_000))),
            ('queue-after-finish', lambda doc: doc.find(f'{{{NS}}}Times').set('queuing', utc(self.start + 5_000_000_000))),
            ('run-start-after-finish', lambda doc: doc.find(f'{{{NS}}}Times').set('start', utc(self.start + 5_000_000_000))),
            ('case-start-before-run', lambda doc: doc.find(f'{{{NS}}}Results/{{{NS}}}UnitTestResult').set('startTime', utc(self.start + 1_000_000_000))),
            ('case-end-after-run', lambda doc: doc.find(f'{{{NS}}}Results/{{{NS}}}UnitTestResult').set('endTime', utc(self.start + 5_000_000_000))),
            ('reversed-case', lambda doc: doc.find(f'{{{NS}}}Results/{{{NS}}}UnitTestResult').set('endTime', utc(self.start + 1_000_000_000))),
        ]
        for label, mutate in mutations:
            with self.subTest(case=label):
                self.doc = fixture(self.start)
                mutate(self.doc)
                self.reject()


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


    def test_fresh_late_logger_creation_receipt_accepted(self):
        self.prepare()
        path = self.write_trx()
        doc = E.fromstring(path.read_bytes())
        times = late_logger_times(doc, self.started)
        path.write_bytes(E.tostring(doc, encoding='utf-8'))
        os.utime(path, ns=(self.started + 5_000_000_000, self.started + 5_000_000_000))
        report = self.verify()
        self.assertEqual(v.stamp(times.get('start')), report['startNs'])
        self.assertLess(report['startNs'], v.stamp(times.get('creation')))

    def test_stale_run_start_rejected_with_recent_logger_times(self):
        self.prepare()
        path = self.write_trx()
        doc = E.fromstring(path.read_bytes())
        doc.find(f'{{{NS}}}Times').set('start', utc(self.started - 1_000_000_000))
        path.write_bytes(E.tostring(doc, encoding='utf-8'))
        os.utime(path, ns=(self.started + 5_000_000_000, self.started + 5_000_000_000))
        with self.assertRaises(v.VerificationError):
            self.verify()

    def test_future_finish_rejected_with_fresh_mtime(self):
        self.prepare()
        path = self.write_trx()
        doc = E.fromstring(path.read_bytes())
        doc.find(f'{{{NS}}}Times').set('finish', utc(self.started + 9_000_000_000))
        path.write_bytes(E.tostring(doc, encoding='utf-8'))
        os.utime(path, ns=(self.started + 5_000_000_000, self.started + 5_000_000_000))
        with self.assertRaises(v.VerificationError):
            self.verify()


DOC_TABLE_SHA256 = "5fd7c447d482be22a7e000e98ea93d85328f86cb221b61108a2411bacdd74c37"
FROZEN = {
    "scripts/customer-email-focused-manifest.json": (
        292, "2bc9b3a7b97aecbc0b7e349d75fd2562003fa272d451b11135696f3e3cbf06d2",
        "503f4633a7b3c3c1402b637b679738a116f543682dc0e91ffb18e743198f8f2c",
        "78c9ddaedeca00252ab78ee0f71649540c0b5684"),
    "scripts/customer-default-page-focused-manifest.json": (
        292, "2bc9b3a7b97aecbc0b7e349d75fd2562003fa272d451b11135696f3e3cbf06d2",
        "156edd1bac6be75718d7f9903b895f98bb90a4e5fae1ca44775388ed5f6b3b97",
        "589979cc2c0a3d53619179efcd6dcd78f6514bfc"),
    "scripts/customer-company-focused-manifest.json": (
        304, "1a20fbcf9ed0e2ad199ad79aad296f46ec98dc179775497a52785962053576e3",
        "74547f7ce39cba412a5c28af4c060727072c97f22c011155c08fa88dd25e045d",
        "c8204c4c59e8e4017c1c35658081204fa7d6367a"),
    "scripts/customer-address-focused-manifest.json": (
        314, "10164ed9ab63d827a4bba42f45c2ecbfef2f30597f8aae51d9171407fa8aa513",
        "122d9cbd9f8418db6c27f749f468ebf987f298d1e35b49618d5445566fbbd020",
        "bfad4b519ca3ea3197e00eb8947e83d019a4f90c"),
}
LINEAGE = dict(
    schemaVersion=1,
    sourceCommit="03dc9a1271c16e6535934445e9dd6e3f30e8fffe",
    predecessorHead="cd265781de9fe4dac67571c3f2ddde5456a49cc0",
    documentationHead="71c97b38fbd57a07efc85eb95d942240aa72d079",
    sourceProposalBlob="6bb241d703c507b1c6b09279b5b67bd79368645d",
    independentReviewBlob="78e5073497e28034dc2d9a3ae4dd978150c2a750",
)
REPAIR_PATHS = {
    "scripts/test_customer_optional_contact_focus.py",
    "scripts/verify_customer_optional_contact_focus.py",
    "scripts/verify_customer_optional_contact_boundary_focus.py",
}
PARENT_MANIFEST_PATHS = {
    "scripts/customer-email-focused-manifest.json",
    "scripts/customer-default-page-focused-manifest.json",
}
DOC_ROOT = Path(__file__).resolve().parents[1]

def doc_need(value, message):
    if not value:
        raise ValueError(message)

def doc_digest(value):
    raw = json.dumps(value, sort_keys=True, separators=(",", ":"), ensure_ascii=False).encode("utf-8")
    return hashlib.sha256(raw).hexdigest()

def doc_pin_map(rows):
    doc_need(type(rows) is list, "Pin rows must be a list")
    result = {}
    for row in rows:
        doc_need(type(row) is dict and set(row) == {"path", "bytes", "sha256"}, "Exact pin row keys")
        path = row["path"]
        doc_need(type(path) is str and path and "\\" not in path and
                 not path.startswith("/") and ":" not in path and
                 all(part not in {"", ".", ".."} for part in path.split("/")), "Canonical relative pin path")
        doc_need(path not in result, "Duplicate pin path")
        doc_need(type(row["bytes"]) is int and row["bytes"] >= 0, "Canonical pin byte count")
        doc_need(type(row["sha256"]) is str and re.fullmatch("[0-9a-f]{64}", row["sha256"]), "Canonical pin SHA256")
        result[path] = row
    return result


PRIVATE_CONSOLE_449_PROGRAM = "Legacy.Maliev.CustomerService.Api/Program.cs"
PRIVATE_CONSOLE_449_PREDECESSOR_BLOB = "d59c9e563b1404de2913ec76d6123dae56764259"
PRIVATE_CONSOLE_449_SUCCESSOR_BLOB = "92915b4d4eb431617ef74e9a0008622f7643cb8d"
PRIVATE_CONSOLE_449_BEFORE = {"path":"Legacy.Maliev.CustomerService.Api/Program.cs","bytes":3229,"sha256":"94f908f40a4ded7d3bc9dcff7617cbc2ef30264e611e5fa7cb3b40e2a57ba318"}
PRIVATE_CONSOLE_449_AFTER = {"path":"Legacy.Maliev.CustomerService.Api/Program.cs","bytes":3829,"sha256":"77f9d333ff5805e3518d45ee3a231849318220a8fdc7bc0396e77ea059f940e9"}
PRIVATE_CONSOLE_449_INSERTION = "    // Keep tracing and metrics services while selecting the original private console-only log contract.\n    builder.Logging.ClearProviders();\n    builder.Logging.AddConsoleFormatter<Maliev.Aspire.ServiceDefaults.Logging.PrivateFailureConsoleFormatter,\n        Microsoft.Extensions.Logging.Console.ConsoleFormatterOptions>();\n    builder.Logging.AddConsole(options => options.FormatterName =\n        Maliev.Aspire.ServiceDefaults.Logging.PrivateFailureConsoleFormatter.FormatterName);\n    builder.Logging.AddFilter<Microsoft.Extensions.Logging.Console.ConsoleLoggerProvider>(null, LogLevel.Warning);\n".encode("utf-8")


def private_console_449_preimage(current):
    doc_need(type(current) is bytes and len(current) == PRIVATE_CONSOLE_449_AFTER["bytes"]
             and hashlib.sha256(current).hexdigest() == PRIVATE_CONSOLE_449_AFTER["sha256"],
             "Exact reviewed Customer console-only Program required")
    doc_need(current.count(PRIVATE_CONSOLE_449_INSERTION) == 1,
             "Exact Customer console-only insertion required")
    previous = current.replace(PRIVATE_CONSOLE_449_INSERTION, b"", 1)
    doc_need(len(previous) == PRIVATE_CONSOLE_449_BEFORE["bytes"]
             and hashlib.sha256(previous).hexdigest() == PRIVATE_CONSOLE_449_BEFORE["sha256"],
             "Customer console-only inverse must restore exact Program predecessor")
    return previous

HOST_STAGING_65_PATH = "scripts/kernel_root_route.py"
HOST_STAGING_65_BEFORE = {"path":"scripts/kernel_root_route.py","bytes":45503,"sha256":"9e13526b6f020b2580a632e261a50a4278099999dd96af48d76cf9618c726f5c"}
HOST_STAGING_65_AFTER = {"path":"scripts/kernel_root_route.py","bytes":50648,"sha256":"c59501f58a1c2ff61a2dabc148ef045e52c54e42907f0b4cc10c8d7797a0aaa5"}


def host_staging_65_postimage(current):
    doc_need(type(current) is bytes and len(current) == HOST_STAGING_65_AFTER["bytes"]
             and hashlib.sha256(current).hexdigest() == HOST_STAGING_65_AFTER["sha256"],
             "Exact independently reviewed PR65 host-staging postimage required")
    doc_need(hashlib.sha1(b"blob " + str(len(current)).encode() + bytes([0]) + current).hexdigest()
             == "5f12906c3e033c7453e41eefc211e0ccc2f39cfa",
             "Exact PR65 Git blob identity required")
    return True


# Only the reviewed Company three-field capacity transition is enrolled here.
COMPANY_CAPACITY_101 = {'Legacy.Maliev.CustomerService.Application/Models/CompanyNameLengthAttribute.cs': {'before': {'path': 'Legacy.Maliev.CustomerService.Application/Models/CompanyNameLengthAttribute.cs',
                                                                                               'bytes': 1449,
                                                                                               'sha256': 'bf219f1c3f4b1179444b48f4a048553d5ab95535eaec8dbd281a6cb6c5675761'},
                                                                                    'after': {'path': 'Legacy.Maliev.CustomerService.Application/Models/CompanyNameLengthAttribute.cs',
                                                                                              'bytes': 1536,
                                                                                              'sha256': '4f59cbd62fbe7918532049197110943ee236ac9ec6adf273de9737abdf4ef212'}},
 'Legacy.Maliev.CustomerService.Application/Models/CustomerRelationEdits.cs': {'before': {'path': 'Legacy.Maliev.CustomerService.Application/Models/CustomerRelationEdits.cs',
                                                                                          'bytes': 5187,
                                                                                          'sha256': '8ed639fc57b54f9607ef5f2468cf810727996f738c493474aa8a181c2e5964e0'},
                                                                               'after': {'path': 'Legacy.Maliev.CustomerService.Application/Models/CustomerRelationEdits.cs',
                                                                                         'bytes': 5193,
                                                                                         'sha256': 'a98f2e32b38ff3461acb25576eb33aab265549208769ec9a7467159f92c78876'}},
 'Legacy.Maliev.CustomerService.Data/CustomerDbContext.cs': {'before': {'path': 'Legacy.Maliev.CustomerService.Data/CustomerDbContext.cs',
                                                                        'bytes': 7144,
                                                                        'sha256': '6e5d93ba8d385099b0d06d782db17344fe9656fc056ddeed1c00c2f07710b970'},
                                                             'after': {'path': 'Legacy.Maliev.CustomerService.Data/CustomerDbContext.cs',
                                                                       'bytes': 7590,
                                                                       'sha256': '4f33a8c15cc5b1241ee42327a4945260c775e3c3d34939ab85f29afa8963c82f'}},
 'Legacy.Maliev.CustomerService.Data/Migrations/CustomerDbContextModelSnapshot.cs': {'before': {'path': 'Legacy.Maliev.CustomerService.Data/Migrations/CustomerDbContextModelSnapshot.cs',
                                                                                                'bytes': 12273,
                                                                                                'sha256': '795475768f136faad32445b7eafa56d87ad6b98a58394292329b4534e450a7be'},
                                                                                     'after': {'path': 'Legacy.Maliev.CustomerService.Data/Migrations/CustomerDbContextModelSnapshot.cs',
                                                                                               'bytes': 12917,
                                                                                               'sha256': '208f45fa79c738fed5e9b5151b92e7185342141ad96415d5e3b4213232f21750'}},
 'Legacy.Maliev.CustomerService.Tests/Controllers/CustomerCompanyLiteralNameHttpTests.cs': {'before': {'path': 'Legacy.Maliev.CustomerService.Tests/Controllers/CustomerCompanyLiteralNameHttpTests.cs',
                                                                                                       'bytes': 19056,
                                                                                                       'sha256': '8fae5c6c10715873e6d1d4a91fbe6fb5f5fee1c17a2870ec511f574f2ae379cb'},
                                                                                            'after': {'path': 'Legacy.Maliev.CustomerService.Tests/Controllers/CustomerCompanyLiteralNameHttpTests.cs',
                                                                                                      'bytes': 19137,
                                                                                                      'sha256': 'b11040ef4290dcaf8b6717ce24f4074c2fc2e73209f2b157a5076fb069f2435a'}},
 'Legacy.Maliev.CustomerService.Tests/Controllers/CompanyOrdinaryDuplicateNullReferenceTests.cs': {'before': {'path': 'Legacy.Maliev.CustomerService.Tests/Controllers/CompanyOrdinaryDuplicateNullReferenceTests.cs',
                                                                                                              'bytes': 12344,
                                                                                                              'sha256': '332fc52c0024f91a336aad47f7e78aab750a8eba23a1bf70d223dd91015d10c8'},
                                                                                                   'after': {'path': 'Legacy.Maliev.CustomerService.Tests/Controllers/CompanyOrdinaryDuplicateNullReferenceTests.cs',
                                                                                                             'bytes': 12333,
                                                                                                             'sha256': 'f020897170fc0169217d56e0bf8f96d3153ec90b8b0f809cba664c975119e517'}},
 'Legacy.Maliev.CustomerService.Tests/Controllers/CompanyTaxRegistrarPrimitiveStorageTests.cs': {'before': {'path': 'Legacy.Maliev.CustomerService.Tests/Controllers/CompanyTaxRegistrarPrimitiveStorageTests.cs',
                                                                                                            'bytes': 9638,
                                                                                                            'sha256': 'adc0161bdc0bfc9a409058f5644396405b061145fb6a406479417a80587fe770'},
                                                                                                 'after': {'path': 'Legacy.Maliev.CustomerService.Tests/Controllers/CompanyTaxRegistrarPrimitiveStorageTests.cs',
                                                                                                           'bytes': 9686,
                                                                                                           'sha256': 'fc00b482736d07170ea32120c9d9b568126d20ad82fa47a819b712b32b5083f4'}},
 'Legacy.Maliev.CustomerService.Tests/Controllers/CustomerCompanyFieldPrimitiveHttpTests.cs': {'before': {'path': 'Legacy.Maliev.CustomerService.Tests/Controllers/CustomerCompanyFieldPrimitiveHttpTests.cs',
                                                                                                          'bytes': 33342,
                                                                                                          'sha256': '64c88de4e5f7de4f724651e1a4eecc4635459e7a2e4eb86784797b33acba75f6'},
                                                                                               'after': {'path': 'Legacy.Maliev.CustomerService.Tests/Controllers/CustomerCompanyFieldPrimitiveHttpTests.cs',
                                                                                                         'bytes': 34219,
                                                                                                         'sha256': '8cd10f262e1d3a87b1438760b7495aae54cc6c09764614001fffb8b0d23fa6a6'}},
 'Legacy.Maliev.CustomerService.Tests/Controllers/CustomerScaffoldRuntimeProof.cs': {'before': {'path': 'Legacy.Maliev.CustomerService.Tests/Controllers/CustomerScaffoldRuntimeProof.cs',
                                                                                                'bytes': 24777,
                                                                                                'sha256': '314b8cd1ad989bba8bd2ad971aba6ca8e6e9d0dd4e7ba13ff052b18d4fd92b2a'},
                                                                                     'after': {'path': 'Legacy.Maliev.CustomerService.Tests/Controllers/CustomerScaffoldRuntimeProof.cs',
                                                                                               'bytes': 27895,
                                                                                               'sha256': 'f19449c162600ebb3fccf2d3d5d0f778c6a6297fc1c1f4e05951631009a729f7'}},
 'Legacy.Maliev.CustomerService.Tests/Data/CustomerPostgresMigrationTests.cs': {'before': {'path': 'Legacy.Maliev.CustomerService.Tests/Data/CustomerPostgresMigrationTests.cs',
                                                                                           'bytes': 20514,
                                                                                           'sha256': '67bc3b667fff19c59f4f1a7f0afe1239ff927160615a684c7f503342d1249285'},
                                                                                'after': {'path': 'Legacy.Maliev.CustomerService.Tests/Data/CustomerPostgresMigrationTests.cs',
                                                                                          'bytes': 22057,
                                                                                          'sha256': 'e1e11de82bb805212726a58443f5026be17f05991b29866728004798596bef9b'}}}


def company_capacity_101_postimage(root, path, predecessor, successor):
    change = COMPANY_CAPACITY_101[path]
    doc_need(predecessor == change['before'] and successor == change['after'],
             'Exact reviewed Company capacity transition required')
    location = root / path
    try:
        mode = location.lstat().st_mode
    except OSError as error:
        raise ValueError('Missing Company capacity postimage') from error
    doc_need(stat.S_ISREG(mode) and not mode & 0o111,
             'Company capacity postimage must be regular and nonexecutable')
    pin_validator.validate_code_pins(root, [successor])


def validate_documentation_successor(root, manifest_path, manifest):
    if "artifactTransportSuccessor" in manifest:
        with historical_view(root, manifest_path, manifest) as (original_root, original_manifest):
            return validate_documentation_successor(original_root, manifest_path, original_manifest)
    doc_need(manifest_path in FROZEN and type(manifest) is dict, "Known focused manifest")
    count, pins_sha, fields_sha, predecessor_blob = FROZEN[manifest_path]
    old_rows = manifest.get("preDocumentationCodePins")
    old = doc_pin_map(old_rows)
    current_rows = manifest.get("codePins")
    current = doc_pin_map(current_rows)
    doc_need(len(old_rows) == count and doc_digest(old_rows) == pins_sha, "Exact historical pin evidence")
    doc_need([row["path"] for row in current_rows] == [row["path"] for row in old_rows],
             "Exact unchanged active pin roster and order")
    preserved_fields = {key: value for key, value in manifest.items()
                        if key not in {"codePins", "preDocumentationCodePins", "documentationSuccessor"}}
    doc_need(doc_digest(preserved_fields) == fields_sha, "Unchanged focused identity and policy fields")
    successor = manifest.get("documentationSuccessor")
    expected_lineage = dict(LINEAGE, predecessorManifestBlob=predecessor_blob)
    doc_need(type(successor) is dict and set(successor) == set(expected_lineage) | {"reviewedChanges"},
             "Exact documentation successor metadata shape")
    doc_need(type(successor["schemaVersion"]) is int, "Canonical successor schema version")
    doc_need({key: value for key, value in successor.items() if key != "reviewedChanges"} == expected_lineage,
             "Exact documentation successor lineage")
    changes = successor["reviewedChanges"]
    doc_need(type(changes) is list and len(changes) == 74 and doc_digest(changes) == DOC_TABLE_SHA256,
             "Exact independently reviewed 74 documentation transitions")
    # The fixed digest above covers each kind, Git blob, old tuple and new tuple.
    new_doc_rows = [change["successor"] for change in changes]
    reviewed = doc_pin_map(new_doc_rows)
    doc_need(len(reviewed) == 74 and
             all(change["path"] == change["successor"]["path"] for change in changes),
             "Unique reviewed paths")
    for change in changes:
        path = change["path"]
        if path in old:
            doc_need(change["predecessor"] == old[path], "Historical row matches reviewed preimage")
    for path, row in current.items():
        if path in COMPANY_CAPACITY_101:
            company_capacity_101_postimage(root, path, reviewed.get(path, old[path]), row)
        elif path in reviewed:
            doc_need(row == reviewed[path], "Exact reviewed documentation postimage")
        elif path == PRIVATE_CONSOLE_449_PROGRAM:
            doc_need(old[path] == PRIVATE_CONSOLE_449_BEFORE and row == PRIVATE_CONSOLE_449_AFTER,
                     "Only the reviewed Customer console-only Program transition is admitted")
            pin_validator.validate_code_pins(root, [row])
            private_console_449_preimage((root / path).read_bytes())
        elif path == HOST_STAGING_65_PATH:
            doc_need(old[path] == HOST_STAGING_65_BEFORE and row == HOST_STAGING_65_AFTER,
                     "Only the reviewed PR65 host-staging transition is admitted")
            pin_validator.validate_code_pins(root, [row])
            host_staging_65_postimage((root / path).read_bytes())
        elif path in REPAIR_PATHS | PARENT_MANIFEST_PATHS:
            # This path must already have existed in the historical roster.
            # Read exact current bytes using the unchanged original primitive.
            pin_validator.validate_code_pins(root, [row])
        else:
            doc_need(row == old[path], "Unapproved source/dependency/workflow/control repinning")
    # Includes the new seven-case file and unlisted PrivateRequestObservation.
    # It adds no codePins rows and grants no focused test-roster change.
    pin_validator.validate_code_pins(root, [current[row['path']] if row['path'] in COMPANY_CAPACITY_101 else row
                                           for row in new_doc_rows])
    return reviewed

class DocumentationSuccessorControls(unittest.TestCase):
    def setUp(self):
        self.profiles = {path: json.loads((DOC_ROOT / path).read_bytes()) for path in FROZEN}

    def reject(self, path, manifest):
        with self.assertRaises(ValueError):
            validate_documentation_successor(DOC_ROOT, path, manifest)

    def test_all_four_actual_manifests_have_exact_reviewed_successor(self):
        for path, manifest in self.profiles.items():
            with self.subTest(path=path):
                self.assertEqual(74, len(validate_documentation_successor(DOC_ROOT, path, manifest)))

    def test_active_pin_empty_drop_add_duplicate_rejected(self):
        for path, original in self.profiles.items():
            for mode in ("empty", "drop-first", "drop-last", "duplicate", "conflicting-duplicate", "add"):
                with self.subTest(path=path, mode=mode):
                    manifest = copy.deepcopy(original)
                    rows = manifest["codePins"]
                    if mode == "empty":
                        rows.clear()
                    elif mode == "drop-first":
                        rows.pop(0)
                    elif mode == "drop-last":
                        rows.pop()
                    elif mode in {"duplicate", "conflicting-duplicate"}:
                        duplicate = dict(rows[0])
                        if mode == "conflicting-duplicate":
                            duplicate["sha256"] = "0" * 64
                        rows.append(duplicate)
                    else:
                        rows.append(dict(path="unapproved.cs", bytes=0, sha256="0" * 64))
                    self.reject(path, manifest)

    def test_historical_pin_and_untouched_policy_mutations_rejected(self):
        for path, original in self.profiles.items():
            for mode in ("history-hash", "history-drop", "history-duplicate", "policy"):
                with self.subTest(path=path, mode=mode):
                    manifest = copy.deepcopy(original)
                    if mode == "history-hash":
                        manifest["preDocumentationCodePins"][0]["sha256"] = "0" * 64
                    elif mode == "history-drop":
                        manifest["preDocumentationCodePins"].pop()
                    elif mode == "history-duplicate":
                        manifest["preDocumentationCodePins"].append(copy.deepcopy(manifest["preDocumentationCodePins"][0]))
                    else:
                        manifest["unapprovedPolicyWaiver"] = True
                    self.reject(path, manifest)

    def test_review_table_lineage_and_documented_pin_mutations_rejected(self):
        for path, original in self.profiles.items():
            for mode in ("missing-table-row", "duplicate-table-row", "changed-table-row", "lineage", "active-doc-pin"):
                with self.subTest(path=path, mode=mode):
                    manifest = copy.deepcopy(original)
                    successor = manifest["documentationSuccessor"]
                    if mode == "missing-table-row":
                        successor["reviewedChanges"].pop()
                    elif mode == "duplicate-table-row":
                        successor["reviewedChanges"].append(copy.deepcopy(successor["reviewedChanges"][0]))
                    elif mode == "changed-table-row":
                        successor["reviewedChanges"][0]["successor"]["sha256"] = "0" * 64
                    elif mode == "lineage":
                        successor["documentationHead"] = "0" * 40
                    else:
                        documented = {row["path"] for row in successor["reviewedChanges"]}
                        next(row for row in manifest["codePins"] if row["path"] in documented)["sha256"] = "0" * 64
                    self.reject(path, manifest)

    def test_unapproved_dependency_workflow_and_negative_guard_repin_rejected(self):
        categories = {
            "dependency": lambda p: p.startswith(".dependencies/"),
            "workflow": lambda p: p.startswith(".github/workflows/"),
            "negative-control": lambda p: p == "scripts/test_customer_email_focused_validator.py",
        }
        for path, original in self.profiles.items():
            for label, select in categories.items():
                with self.subTest(path=path, category=label):
                    manifest = copy.deepcopy(original)
                    row = next(row for row in manifest["codePins"] if select(row["path"]))
                    row["sha256"] = "0" * 64
                    self.reject(path, manifest)

    def test_exact_byte_helper_keeps_old_current_mismatch_and_content_refusals(self):
        before = b"public void Check() { Assert.True(true); }\n"
        current = b"/// <summary>Checks admission.</summary>\n" + before
        pin = lambda data: dict(path="source.cs", bytes=len(data), sha256=hashlib.sha256(data).hexdigest())
        with tempfile.TemporaryDirectory(prefix="customer-doc-pin-control-") as temporary:
            root = Path(temporary)
            source = root / "source.cs"
            for actual, expected in ((before, before), (current, current)):
                source.write_bytes(actual)
                pin_validator.validate_code_pins(root, [pin(expected)])
            for actual, expected in ((before, current), (current, before)):
                source.write_bytes(actual)
                with self.assertRaises(ValueError):
                    pin_validator.validate_code_pins(root, [pin(expected)])
            # Any byte change is refused, even a different XML comment.
            for changed in (
                current.replace(b"Assert.True(true)", b"Assert.True(false)"),
                current.replace(b"public void", b"[Fact] public void"),
                current.replace(b"Checks admission", b"Bypass admission"),
                current + b"// extra",
            ):
                source.write_bytes(changed)
                with self.assertRaises(ValueError):
                    pin_validator.validate_code_pins(root, [pin(current)])
            source.unlink()
            with self.assertRaises(FileNotFoundError):
                pin_validator.validate_code_pins(root, [pin(current)])




class PrivateConsole449SuccessorControls(unittest.TestCase):
    def test_actual_program_has_exact_console_only_inverse(self):
        current = (DOC_ROOT / PRIVATE_CONSOLE_449_PROGRAM).read_bytes()
        previous = private_console_449_preimage(current)
        self.assertEqual(PRIVATE_CONSOLE_449_BEFORE["bytes"], len(previous))
        self.assertEqual(PRIVATE_CONSOLE_449_BEFORE["sha256"], hashlib.sha256(previous).hexdigest())
        self.assertEqual(current, previous.replace(
            b'    builder.AddServiceDefaults();\n',
            b'    builder.AddServiceDefaults();\n' + PRIVATE_CONSOLE_449_INSERTION, 1))
        for changed in (
            previous,
            current + b" ",
            current.replace(b"LogLevel.Warning", b"LogLevel.Information"),
            current.replace(b"builder.Logging.ClearProviders();", b"// provider selection removed"),
            current.replace(b'AddPrivateRequestObservation("customer")', b'AddPrivateRequestObservation("other")'),
            current.replace(b"PrivateFailureConsoleFormatter", b"UnreviewedConsoleFormatter", 1),
        ):
            with self.subTest(sourceSha256=hashlib.sha256(changed).hexdigest()), self.assertRaises(ValueError):
                private_console_449_preimage(changed)

    def test_all_profiles_admit_only_exact_program_postimage_without_roster_changes(self):
        for manifest_path in FROZEN:
            manifest = json.loads((DOC_ROOT / manifest_path).read_bytes())
            with self.subTest(manifest=manifest_path):
                before = doc_pin_map(manifest["preDocumentationCodePins"])
                after = doc_pin_map(manifest["codePins"])
                self.assertEqual(list(before), list(after))
                self.assertEqual(PRIVATE_CONSOLE_449_BEFORE, before[PRIVATE_CONSOLE_449_PROGRAM])
                self.assertEqual(PRIVATE_CONSOLE_449_AFTER, after[PRIVATE_CONSOLE_449_PROGRAM])
                self.assertEqual(74, len(validate_documentation_successor(DOC_ROOT, manifest_path, manifest)))
            for mutation in ("rollback", "wrong-hash", "wrong-size", "historical", "unreviewed-source"):
                changed = copy.deepcopy(manifest)
                if mutation == "historical":
                    row = next(row for row in changed["preDocumentationCodePins"]
                               if row["path"] == PRIVATE_CONSOLE_449_PROGRAM)
                    row["sha256"] = PRIVATE_CONSOLE_449_AFTER["sha256"]
                else:
                    row = next(row for row in changed["codePins"] if row["path"] ==
                               (PRIVATE_CONSOLE_449_PROGRAM if mutation != "unreviewed-source"
                                else "scripts/verify_customer_email_focused.py"))
                    if mutation == "rollback":
                        row.update(PRIVATE_CONSOLE_449_BEFORE)
                    elif mutation == "wrong-size":
                        row["bytes"] += 1
                    else:
                        row["sha256"] = "0" * 64
                with self.subTest(manifest=manifest_path, mutation=mutation), self.assertRaises(ValueError):
                    validate_documentation_successor(DOC_ROOT, manifest_path, changed)


class HostStaging65SuccessorControls(unittest.TestCase):
    def test_actual_host_staging_source_is_exact_reviewed_postimage(self):
        current = (DOC_ROOT / HOST_STAGING_65_PATH).read_bytes()
        self.assertTrue(host_staging_65_postimage(current))
        for changed in (
            current + b" ",
            current.replace(b"consumer-stage-source", b"unreviewed-source-mode", 1),
            current.replace(b"4194304", b"3145728", 1),
            current.replace(b"hostSourceReceiptSha256", b"unreviewedReceiptSha256", 1),
        ):
            with self.subTest(sourceSha256=hashlib.sha256(changed).hexdigest()), self.assertRaises(ValueError):
                host_staging_65_postimage(changed)

    def test_all_profiles_admit_only_exact_host_staging_tuple(self):
        for manifest_path in FROZEN:
            manifest = json.loads((DOC_ROOT / manifest_path).read_bytes())
            before = doc_pin_map(manifest["preDocumentationCodePins"])
            after = doc_pin_map(manifest["codePins"])
            with self.subTest(manifest=manifest_path):
                self.assertEqual(list(before), list(after))
                self.assertEqual(HOST_STAGING_65_BEFORE, before[HOST_STAGING_65_PATH])
                self.assertEqual(HOST_STAGING_65_AFTER, after[HOST_STAGING_65_PATH])
                self.assertEqual(74, len(validate_documentation_successor(DOC_ROOT, manifest_path, manifest)))
            for mutation in ("rollback", "wrong-hash", "wrong-size", "historical", "unreviewed-guard"):
                changed = copy.deepcopy(manifest)
                if mutation == "historical":
                    row = next(row for row in changed["preDocumentationCodePins"]
                               if row["path"] == HOST_STAGING_65_PATH)
                    row.update(HOST_STAGING_65_AFTER)
                else:
                    target = ("scripts/verify_customer_email_focused.py"
                              if mutation == "unreviewed-guard" else HOST_STAGING_65_PATH)
                    row = next(row for row in changed["codePins"] if row["path"] == target)
                    if mutation == "rollback":
                        row.update(HOST_STAGING_65_BEFORE)
                    elif mutation == "wrong-size":
                        row["bytes"] += 1
                    else:
                        row["sha256"] = "0" * 64
                with self.subTest(manifest=manifest_path, mutation=mutation), self.assertRaises(ValueError):
                    validate_documentation_successor(DOC_ROOT, manifest_path, changed)


class CompanyCapacity101Controls(unittest.TestCase):
    def test_all_ten_exact_current_postimages(self):
        self.assertEqual(10, len(COMPANY_CAPACITY_101))
        for path, change in COMPANY_CAPACITY_101.items():
            company_capacity_101_postimage(DOC_ROOT, path, change['before'], change['after'])

    def test_changed_old_new_hash_size_or_unrelated_binding_refused(self):
        for path, change in COMPANY_CAPACITY_101.items():
            for side, field in [('before', 'sha256'), ('after', 'sha256'), ('after', 'bytes')]:
                altered = copy.deepcopy(change)
                altered[side][field] = 0 if field == 'bytes' else '0' * 64
                with self.subTest(path=path, side=side, field=field), self.assertRaises(ValueError):
                    company_capacity_101_postimage(DOC_ROOT, path, altered['before'], altered['after'])
        with self.assertRaises(KeyError):
            company_capacity_101_postimage(DOC_ROOT, 'unrelated.cs', {}, {})

    def test_missing_directory_symlink_and_executable_refused(self):
        path, change = next(iter(COMPANY_CAPACITY_101.items()))
        with tempfile.TemporaryDirectory() as temporary:
            with self.assertRaises(ValueError):
                company_capacity_101_postimage(Path(temporary), path, change['before'], change['after'])
        for mode in (stat.S_IFDIR | 0o755, stat.S_IFLNK | 0o777, stat.S_IFREG | 0o755):
            with mock.patch.object(Path, 'lstat', return_value=type('Metadata', (), {'st_mode': mode})()):
                with self.assertRaises(ValueError):
                    company_capacity_101_postimage(DOC_ROOT, path, change['before'], change['after'])

    def test_actual_byte_drift_refused_under_regular_file_metadata(self):
        path, change = next(iter(COMPANY_CAPACITY_101.items()))
        with tempfile.TemporaryDirectory() as temporary:
            location = Path(temporary) / path
            location.parent.mkdir(parents=True)
            location.write_bytes(b'x' * change['after']['bytes'])
            with self.assertRaises(ValueError):
                company_capacity_101_postimage(Path(temporary), path, change['before'], change['after'])

if __name__ == '__main__':
    unittest.main(verbosity=2)
