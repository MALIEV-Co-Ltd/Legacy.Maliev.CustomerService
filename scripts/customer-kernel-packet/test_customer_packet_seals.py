"""Pure packet binding controls; no native IO or grant issuance."""
import hashlib
import json
from pathlib import Path
import shutil
import tempfile
import unittest
import customer_native_build_driver as driver

ROOT = Path(__file__).resolve().parent


class PacketSeals(unittest.TestCase):
    def setUp(self):
        self.temp = tempfile.TemporaryDirectory(prefix='customer-packet-control-')
        self.addCleanup(self.temp.cleanup)
        self.root = Path(self.temp.name) / 'packet'
        shutil.copytree(ROOT, self.root)
        self.sha = driver.digest((self.root / 'driver-manifest.json').read_bytes())

    def test_exact_packet_has_no_grant(self):
        manifest, stage, policy, *_ = driver.load_packet(self.root, self.sha)
        self.assertFalse(manifest['nativeExecutionGranted'])
        self.assertFalse(stage['nativeExecutionGranted'])
        self.assertEqual(len(policy['rawFiles']), 275)

    def test_external_digest_required(self):
        with self.assertRaises(ValueError):
            driver.load_packet(self.root, '0' * 64)

    def test_mutated_module_is_refused_before_import(self):
        path = self.root / 'customer_owned_build.py'
        path.write_bytes(path.read_bytes() + b'\nraise RuntimeError("must not execute")\n')
        with self.assertRaisesRegex(ValueError, 'seal mismatch'):
            driver.load_packet(self.root, self.sha)

    def test_changed_stage_is_refused(self):
        path = self.root / 'build-stage.json'
        path.write_bytes(path.read_bytes().replace(b'4194304', b'1'))
        with self.assertRaises(ValueError):
            driver.load_packet(self.root, self.sha)

    def test_decoder_cannot_be_rebound_by_manifest(self):
        path = self.root / 'input/sealed_source_capsule.py'
        path.write_bytes(path.read_bytes() + b'\n# drift\n')
        manifest = json.loads((self.root / 'driver-manifest.json').read_bytes())
        for row in manifest['files']:
            if row['path'] == 'input/sealed_source_capsule.py':
                row.update(bytes=path.stat().st_size, sha256=driver.digest(path.read_bytes()))
        raw = json.dumps(manifest).encode()
        (self.root / 'driver-manifest.json').write_bytes(raw)
        with self.assertRaisesRegex(ValueError, 'decoder mismatch'):
            driver.load_packet(self.root, driver.digest(raw))

    def test_manifest_path_escape_refused(self):
        manifest = json.loads((self.root / 'driver-manifest.json').read_bytes())
        manifest['files'][0]['path'] = '../foreign'
        raw = json.dumps(manifest).encode()
        (self.root / 'driver-manifest.json').write_bytes(raw)
        with self.assertRaisesRegex(ValueError, 'recipe'):
            driver.load_packet(self.root, driver.digest(raw))

    def test_manifest_case_alias_refused(self):
        manifest = json.loads((self.root / 'driver-manifest.json').read_bytes())
        row = dict(manifest['files'][0]); row['path'] = row['path'].upper()
        manifest['files'].append(row)
        raw = json.dumps(manifest).encode()
        (self.root / 'driver-manifest.json').write_bytes(raw)
        with self.assertRaisesRegex(ValueError, 'recipe'):
            driver.load_packet(self.root, driver.digest(raw))


if __name__ == '__main__':
    unittest.main()
