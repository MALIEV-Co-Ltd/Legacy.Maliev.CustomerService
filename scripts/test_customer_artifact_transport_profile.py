"""Real transport profile rejection controls; no SDK or native execution."""
import copy
import hashlib
import json
from pathlib import Path
import tempfile
import unittest
import customer_artifact_transport_profile as profile

REAL_ROOT = Path(__file__).resolve().parents[1]

class ArtifactTransportControls(unittest.TestCase):
    def setUp(self):
        self.temporary = tempfile.TemporaryDirectory(prefix='customer-transport-control-')
        self.addCleanup(self.temporary.cleanup)
        self.root = Path(self.temporary.name)
        paths = set(profile.MANIFESTS) | set(profile.SOURCE_EDITS) | {profile.WORKFLOW, profile.HELPER, profile.CONTROLS}
        paths.update(row['archive'] for row in profile.ARCHIVES.values())
        for path in paths:
            destination = self.root / path
            destination.parent.mkdir(parents=True, exist_ok=True)
            destination.write_bytes(profile.read(REAL_ROOT, path))

    def manifest(self, path=None):
        return json.loads(profile.read(self.root, path or profile.MANIFESTS[0]))

    def write(self, manifest, path=None):
        (self.root / (path or profile.MANIFESTS[0])).write_bytes((json.dumps(manifest, indent=2) + '\n').encode())

    def check(self, path=None):
        path = path or profile.MANIFESTS[0]
        return profile.authenticate_profile(self.root, path, self.manifest(path))

    def test_all_four_actual_current_profiles_and_every_native_pin(self):
        import verify_customer_email_focused as original
        for path in profile.MANIFESTS:
            manifest = json.loads(profile.read(REAL_ROOT, path))
            before = profile.authenticate_profile(REAL_ROOT, path, manifest)
            self.assertEqual(list(profile.rows_by_path(before['codePins'])), list(profile.rows_by_path(manifest['codePins'])))
            original.validate_code_pins(REAL_ROOT, manifest['codePins'])

    def test_actual_historical_documentation_assertions_remain_valid(self):
        import test_customer_optional_contact_focus as historical
        for path in profile.MANIFESTS:
            manifest = json.loads(profile.read(REAL_ROOT, path))
            self.assertEqual(74, len(historical.validate_documentation_successor(REAL_ROOT, path, manifest)))

    def test_historical_view_is_owned_and_removed_after_exit(self):
        path = profile.MANIFESTS[0]
        manifest = json.loads(profile.read(REAL_ROOT, path))
        with profile.historical_view(REAL_ROOT, path, manifest) as (root, original):
            self.assertTrue(root.is_dir())
            self.assertNotIn(profile.FIELD, original)
            self.assertEqual(profile.archived(REAL_ROOT, path), (root / path).read_bytes())
        self.assertFalse(root.exists())

    def test_missing_successor_is_refused(self):
        manifest = self.manifest();manifest.pop(profile.FIELD);self.write(manifest)
        with self.assertRaises(ValueError):self.check()

    def test_historical_policy_change_is_refused(self):
        manifest = self.manifest();manifest['requiredFocusedCount'] -= 1;self.write(manifest)
        with self.assertRaises(ValueError):self.check()

    def test_boolean_schema_cannot_impersonate_integer(self):
        manifest = self.manifest();manifest[profile.FIELD]['schemaVersion'] = True;self.write(manifest)
        with self.assertRaises(ValueError):self.check()

    def test_undeclared_dependency_repin_is_refused(self):
        manifest = self.manifest();next(row for row in manifest['codePins'] if row['path'].startswith('.dependencies/'))['sha256'] = '0' * 64;self.write(manifest)
        with self.assertRaises(ValueError):self.check()

    def test_source_roster_reorder_is_refused(self):
        manifest = self.manifest();manifest['codePins'].reverse();self.write(manifest)
        with self.assertRaises(ValueError):self.check()

    def test_archive_change_cannot_be_resealed(self):
        record = next(iter(profile.ARCHIVES.values()));path = record['archive']
        (self.root / path).write_bytes(profile.read(self.root, path) + b' ')
        manifest = self.manifest()
        row = next(row for row in manifest[profile.FIELD]['additionalInputs'] if row['path'] == path)
        row.update(profile.pin(path, profile.read(self.root, path)));self.write(manifest)
        with self.assertRaises(ValueError):self.check()

    def test_partial_action_upgrade_is_refused_even_with_repin(self):
        path = profile.WORKFLOW
        data = profile.read(self.root, path).replace(profile.SELECTED_PIN.encode(), profile.PREVIOUS_PIN.encode(), 1)
        (self.root / path).write_bytes(data)
        manifest = self.manifest();next(row for row in manifest['codePins'] if row['path'] == path).update(profile.pin(path, data));self.write(manifest)
        with self.assertRaises(ValueError):self.check()

    def test_guard_budget_change_cannot_be_resealed(self):
        path = 'scripts/verify_customer_email_focused.py'
        data = profile.read(self.root, path).replace(b'4194304', b'3145728', 1)
        self.assertNotEqual(profile.read(self.root, path), data)
        (self.root / path).write_bytes(data)
        manifest = self.manifest();next(row for row in manifest['codePins'] if row['path'] == path).update(profile.pin(path, data));self.write(manifest)
        with self.assertRaises(ValueError):self.check()

    def test_parent_manifest_policy_drift_is_refused(self):
        parent = profile.MANIFESTS[0];manifest = self.manifest(parent);manifest['requiredFocusedCount'] -= 1;self.write(manifest, parent)
        company = next(path for path in profile.MANIFESTS if 'company' in path)
        with self.assertRaises(ValueError):self.check(company)

    def test_extra_added_source_cannot_be_omitted(self):
        manifest = self.manifest();manifest[profile.FIELD]['additionalInputs'].pop();self.write(manifest)
        with self.assertRaises(ValueError):self.check()

    def test_new_helper_bytes_must_match_declared_binding(self):
        (self.root / profile.HELPER).write_bytes(profile.read(self.root, profile.HELPER) + b'\n')
        with self.assertRaises(ValueError):self.check()

    def test_duplicate_json_keys_are_refused(self):
        path = self.root / profile.MANIFESTS[0];data = path.read_bytes();path.write_bytes(b'{"schemaVersion":1,' + data.lstrip()[1:])
        with self.assertRaises(ValueError):self.check()

    def test_unapproved_role_field_is_refused(self):
        manifest = self.manifest();manifest['sdkGranted'] = True;self.write(manifest)
        with self.assertRaises(ValueError):self.check()

    def test_unlisted_historical_documentation_source_is_still_required(self):
        path = profile.MANIFESTS[0];old = profile.predecessor(REAL_ROOT, path)
        rows = old['codePins'] + [change['successor'] for change in old['documentationSuccessor']['reviewedChanges']]
        for row in rows:
            target = self.root / row['path']
            if not target.exists():
                target.parent.mkdir(parents=True, exist_ok=True)
                target.write_bytes(profile.read(REAL_ROOT, row['path']))
        names = {row['path'] for row in old['codePins']}
        selected = next(row['path'] for row in rows if row['path'] not in names)
        target = self.root / selected;data = target.read_bytes()
        target.unlink()
        with self.assertRaises(FileNotFoundError):
            with profile.historical_view(self.root, path, self.manifest()):
                self.fail('Missing unlisted historical source was accepted')
        target.write_bytes(data + b' ')
        with self.assertRaises(ValueError):
            with profile.historical_view(self.root, path, self.manifest()):
                self.fail('Changed unlisted historical source was accepted')

if __name__ == '__main__':
    unittest.main()
