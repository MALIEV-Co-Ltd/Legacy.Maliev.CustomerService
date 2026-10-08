"""Pure launcher/custody controls; subprocess and daemon are synthetic."""
import copy
import json
import os
from pathlib import Path
import tempfile
import time
from types import SimpleNamespace
import unittest
from unittest.mock import patch, Mock
import customer_guardian_process as p
import customer_owned_build as owned
import test_customer_owned_build as fixture_source


class LauncherControls(unittest.TestCase):
    def setUp(self):
        self.fixture = fixture_source.BuildControls('test_success_build_gate_and_exact_cleanup')
        self.fixture.setUp(); self.addCleanup(self.fixture.doCleanups)
        self.fixture.ledger.mkdir()
        owned.atomic_new(self.fixture.ledger / 'create-intent.json', {'synthetic': True})
        self.expected = owned.make_create_request(self.fixture.stage, self.fixture.grant)[1]
        self.args = SimpleNamespace(ledger=str(self.fixture.ledger), grant='synthetic-grant.json',
            handoffs='synthetic-handoffs.json', grant_sha256=self.fixture.grant['rootGrantSha256'], packet_sha256='a'*64)
        self.context = {'owned': owned, 'stage': self.fixture.stage, 'grant': self.fixture.grant,
                        'root': Path(__file__).parent, 'deadline': time.monotonic()+60,
                        'regular': lambda path, maximum: Path(path).read_bytes(),
                        'helper': SimpleNamespace(parse_json=json.loads)}
        original_json = self.fixture.backend.json
        def json_read(method, path):
            if path.startswith('/containers/') and path.endswith('/json'):
                return copy.deepcopy(self.fixture.backend.item)
            return original_json(method, path)
        self.fixture.backend.json = json_read
        self.relay = p.GuardedBackend(self.fixture.backend, self.context, self.args)
        self.parent = {'pid': os.getpid(), 'startTicks': 1, 'executable': '/usr/bin/python3', 'hostBootId': 'b', 'actualStartUtc': '2026-10-08T00:00:00+00:00'}
        self.child = dict(self.parent, pid=4321, startTicks=2)
        self.spawn_arguments = None; self.child_live = True

    def spawn(self, command, **kwargs):
        self.spawn_arguments = (command, kwargs)
        receipt = self.fixture.backend.create_owned(self.expected)
        owned.atomic_new(self.relay.directory / 'custody.json', receipt)
        test = self
        class Process:
            pid = 4321
            returncode = None
            def poll(self):
                if (test.relay.directory / 'cancel.json').exists():
                    test.child_live = False; self.returncode = 0
                    target = test.relay.directory / 'terminal.json'
                    if not target.exists():
                        owned.atomic_new(target, {'cleanupVerified': True, 'allocationUnresolved': False})
                return self.returncode
            def wait(self, timeout):
                test.assertEqual(timeout, 0); return self.returncode
            def kill(self): raise AssertionError('No process kill permitted')
            def terminate(self): raise AssertionError('No process termination permitted')
        return Process()

    def launch(self):
        def ident(pid): return copy.deepcopy(self.parent if pid == os.getpid() else self.child)
        with patch.object(p, 'identity', side_effect=ident), patch.object(p, 'alive', side_effect=lambda _: self.child_live), \
             patch.object(p.subprocess, 'Popen', side_effect=self.spawn):
            return self.relay.create_owned(self.expected)

    def test_direct_create_transport_is_forbidden(self):
        with self.assertRaises(ValueError): self.relay.request('POST', '/containers/create?name=x')
        self.assertEqual([], self.fixture.backend.calls)

    def test_tampered_spec_never_spawns_helper(self):
        tampered = copy.deepcopy(self.expected); tampered['HostConfig']['Privileged'] = True
        with patch.object(p.subprocess, 'Popen', side_effect=AssertionError('Must not spawn')):
            with self.assertRaises(ValueError): self.relay.create_owned(tampered)

    def test_registered_finite_independent_session_before_custody(self):
        receipt = self.launch()
        command, args = self.spawn_arguments
        self.assertTrue(args['start_new_session']); self.assertTrue(args['close_fds'])
        self.assertEqual(args['stdin'], p.subprocess.DEVNULL)
        self.assertIn('--guardian-child', command)
        registration = json.loads((self.relay.directory/'registration.json').read_bytes())
        self.assertEqual(registration['guardian'], self.child)
        self.assertEqual(registration['parent'], self.parent)
        self.assertEqual(registration['expiresUtc'], self.fixture.grant['expiresUtc'])
        self.assertEqual(receipt['containerId'], 'c'*64)

    def test_live_custody_checks_exact_sdk_and_daemon(self):
        receipt = self.launch()
        with patch.object(p, 'alive', return_value=True):
            proof = self.relay.ensure_custody(receipt['containerId'], receipt['createdSpec'], self.expected)
        self.assertTrue(proof['verified']); self.assertEqual(len(proof['custodyReceiptSha256']), 64)

    def test_guardian_pid_reuse_refuses_start_custody(self):
        receipt = self.launch()
        with patch.object(p, 'alive', return_value=False):
            with self.assertRaises(ValueError): self.relay.ensure_custody(receipt['containerId'], receipt['createdSpec'], self.expected)

    def test_target_active_exec_refuses_custody(self):
        receipt = self.launch(); self.fixture.backend.item['ExecIDs'] = ['foreign-exec']
        with patch.object(p, 'alive', return_value=True):
            with self.assertRaises(ValueError): self.relay.ensure_custody(receipt['containerId'], receipt['createdSpec'], self.expected)

    def test_target_config_drift_refuses_custody(self):
        receipt = self.launch(); self.fixture.backend.item['Config']['Cmd'] = ['foreign']
        with patch.object(p, 'alive', return_value=True):
            with self.assertRaises(ValueError): self.relay.ensure_custody(receipt['containerId'], receipt['createdSpec'], self.expected)

    def test_cancel_finish_waits_actual_exit_without_kill(self):
        self.launch()
        with patch.object(p, 'alive', side_effect=lambda _: self.child_live):
            terminal = self.relay.finish()
        self.assertTrue(terminal['guardianExited']); self.assertTrue(terminal['cleanupVerified'])
        self.assertEqual(terminal['actualGuardianExitCode'], 0)
        self.assertTrue((self.relay.directory/'cancel.json').exists())

    def test_child_registration_mismatch_never_constructs_custodian(self):
        self.launch()
        self.args.guardian_directory = str(self.relay.directory)
        self.context['grant']['hostBootId'] = self.child['hostBootId']
        self.context['backend'] = self.fixture.backend
        constructor = Mock(side_effect=AssertionError('No SDK custody before registration proof'))
        self.context['guardian'] = SimpleNamespace(Custodian=constructor)
        resource = SimpleNamespace(RLIMIT_AS=0, RLIMIT_CPU=1, setrlimit=Mock())
        foreign = dict(self.child, startTicks=999)
        with patch.dict('sys.modules', {'resource': resource}), patch.object(p, 'identity', return_value=foreign):
            with self.assertRaises(ValueError): p.run_guardian(self.context, self.args)
        constructor.assert_not_called()

    def test_child_caps_and_finite_timer_precede_create(self):
        self.launch(); self.args.guardian_directory = str(self.relay.directory)
        self.context['grant']['hostBootId'] = self.child['hostBootId']
        self.context['backend'] = self.fixture.backend; self.context['admit'] = self.fixture.admission
        resource = SimpleNamespace(RLIMIT_AS=0, RLIMIT_CPU=1, setrlimit=Mock())
        timer = Mock()
        custody = Mock(); custody.run.return_value = {'cleanupVerified': True}
        constructor = Mock(return_value=custody)
        self.context['guardian'] = SimpleNamespace(Custodian=constructor)
        def create(_expected, _admit):
            self.assertEqual(resource.setrlimit.call_count, 2)
            timer.start.assert_called_once()
        custody.create_owned.side_effect = create
        with patch.dict('sys.modules', {'resource': resource}), patch.object(p, 'identity', return_value=self.child), \
             patch.object(p, 'alive', return_value=True), patch.object(p.threading, 'Timer', return_value=timer) as clock, \
             patch.object(p.signal, 'signal'):
            self.assertEqual(p.run_guardian(self.context, self.args), 0)
        resource.setrlimit.assert_any_call(0, (268435456, 268435456))
        resource.setrlimit.assert_any_call(1, (30, 30))
        self.assertLessEqual(clock.call_args.args[0], 1950)
        timer.cancel.assert_called_once()

    def test_no_guardian_started_has_no_owned_helper(self):
        self.assertEqual(self.relay.finish()['remainingOwnedHelpers'], 0)

    def test_changed_custody_receipt_refused(self):
        receipt = self.launch()
        target = self.relay.directory/'custody.json'; value = json.loads(target.read_bytes())
        value['containerId'] = 'd'*64; target.write_text(json.dumps(value))
        with patch.object(p, 'alive', return_value=True):
            with self.assertRaises(ValueError): self.relay.ensure_custody(receipt['containerId'], receipt['createdSpec'], self.expected)


class IdentityControls(unittest.TestCase):
    def setUp(self):
        self.temp = tempfile.TemporaryDirectory(prefix='guardian-proc-control-'); self.addCleanup(self.temp.cleanup)
        self.root = Path(self.temp.name); (self.root/'42').mkdir()
        (self.root/'sys/kernel/random').mkdir(parents=True)
        (self.root/'sys/kernel/random/boot_id').write_text('boot-id')
        (self.root/'stat').write_text('btime 1000\n')
        self.stat = '42 (python3) ' + ' '.join(['S'] + ['0']*18 + ['123'] + ['0']*3)
        (self.root/'42/stat').write_text(self.stat)

    def test_actual_pid_start_boot_executable_and_utc(self):
        with patch.object(p.os, 'readlink', return_value='/usr/bin/python3'), patch.object(p.os, 'sysconf', return_value=100, create=True):
            result = p.identity(42, self.root)
        self.assertEqual(result['startTicks'], 123); self.assertEqual(result['hostBootId'], 'boot-id')
        self.assertEqual(result['actualStartUtc'], '1970-01-01T00:16:41.230000+00:00')

    def test_pid_reuse_is_refused_without_any_kill(self):
        def changed(_path):
            (self.root/'42/stat').write_text(self.stat.replace('123', '456'))
            return '/usr/bin/python3'
        with patch.object(p.os, 'readlink', side_effect=changed):
            with self.assertRaises(ValueError): p.identity(42, self.root)

    def test_boot_reuse_is_refused(self):
        def changed(_path):
            (self.root/'sys/kernel/random/boot_id').write_text('other-boot')
            return '/usr/bin/python3'
        with patch.object(p.os, 'readlink', side_effect=changed):
            with self.assertRaises(ValueError): p.identity(42, self.root)

    def test_absent_process_not_adopted(self):
        self.assertFalse(p.alive({'pid': 43}, self.root))


if __name__ == '__main__':
    unittest.main()
