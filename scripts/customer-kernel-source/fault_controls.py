"""Pure regression controls: every Popen, pidfd, identity and signal is mocked."""
import signal
import subprocess
import unittest
import json
from pathlib import Path
import sys
import tempfile
import datetime as dt
from types import SimpleNamespace
from unittest.mock import patch
import owned_process as owned


class Child:
    pid = 123
    def __init__(self, timeout=False):
        self.code = None
        self.timeout = timeout
        self.signals = []
    def poll(self):
        return self.code
    def wait(self, timeout):
        if self.code is None:
            raise subprocess.TimeoutExpired('mock child', timeout)
        return self.code
    def send_signal(self, sig):
        self.signals.append(sig)
        if sig == signal.SIGKILL or not self.timeout:
            self.code = -sig


class Controls(unittest.TestCase):
    def setUp(self):
        # Supply the Linux signal enum in this Windows-only synthetic control run.
        kill = patch.object(signal, 'SIGKILL', 9, create=True)
        kill.start()
        self.addCleanup(kill.stop)

    def setup_owner(self, child=None, identity=None, writer=None, binding=None, popen=None, pidfd=None):
        child = child or Child()
        self.child = child
        self.rows, self.fd_signals, self.closed = [], [], []
        def send(fd, sig, _info, _flags):
            self.fd_signals.append((fd, sig))
            child.send_signal(sig)
        self.owner = owned.ProcessCustody(identity or (lambda pid: {'pid': pid, 'start': 99}),
            lambda _: True, writer or self.rows.append,
            popen=popen or (lambda *_args, **_kw: child),
            pidfd_open=pidfd or (lambda _pid, _flags: 9), pidfd_signal=send,
            close_fd=self.closed.append, child_binding=binding or (lambda _: True))
        return self.owner

    def test_normal_acquires_native_handle_before_identity(self):
        owner = self.setup_owner()
        with patch.object(owned.signal, 'signal'), patch.object(owned.signal, 'getsignal'):
            with owner.signals():
                owner.spawn(['mock'])
                self.assertEqual('acquired-native-handle-before-identity', self.rows[0]['kind'])
                self.assertIsNone(self.rows[0]['resources'][0]['identity'])
        self.assertTrue(owner.closed)
        self.assertEqual([(9, signal.SIGTERM)], self.fd_signals)
        self.assertEqual([9], self.closed)

    def test_popen_failure_creates_no_owned_child(self):
        def failure(*_args, **_kw):
            raise OSError('mock acquisition')
        owner = self.setup_owner(popen=failure)
        with self.assertRaises(OSError):
            owner.spawn(['mock'])
        self.assertEqual([], owner.records)
        self.assertEqual([], self.fd_signals)
        self.assertTrue(owner.closed)

    def test_immediate_identity_fault_still_exact_cleanup(self):
        def failure(_pid):
            raise OSError('mock proc inaccessible')
        owner = self.setup_owner(identity=failure)
        with self.assertRaises(OSError):
            owner.spawn(['mock'])
        self.assertTrue(owner.closed)
        self.assertEqual([(9, signal.SIGTERM)], self.fd_signals)
        self.assertIsNone(owner.records[0]['identity'])

    def test_durable_ledger_fault_still_exact_cleanup(self):
        def failure(_row):
            raise OSError('mock disk unavailable')
        owner = self.setup_owner(writer=failure)
        with self.assertRaises(OSError):
            owner.spawn(['mock'])
        self.assertTrue(owner.closed)
        self.assertEqual([(9, signal.SIGTERM)], self.fd_signals)
        self.assertTrue(any(row['phase'] == 'durability' for row in owner.failures))

    def test_signal_during_popen_is_deferred_until_custody(self):
        handlers = {}
        child = Child()
        def popen(*_args, **_kw):
            handlers[signal.SIGTERM](signal.SIGTERM, None)
            return child
        owner = self.setup_owner(child=child, popen=popen)
        with patch.object(owned.signal, 'signal', side_effect=lambda s, fn: handlers.update({s: fn})), \
             patch.object(owned.signal, 'getsignal', return_value=None):
            with self.assertRaises(owned.DeferredInterruption):
                with owner.signals():
                    owner.spawn(['mock'])
        self.assertTrue(owner.closed)
        self.assertEqual([(9, signal.SIGTERM)], self.fd_signals)
        self.assertEqual(signal.SIGTERM, owner.interrupted)

    def test_sigint_after_spawn_cleans_retained_handle(self):
        owner = self.setup_owner()
        owner.spawn(['mock'])
        owner.interrupted = signal.SIGINT
        with self.assertRaises(owned.DeferredInterruption):
            owner.checkpoint()
        owner.cleanup()
        self.assertTrue(owner.closed)

    def test_graceful_timeout_escalates_only_exact_pidfd(self):
        owner = self.setup_owner(child=Child(timeout=True))
        owner.spawn(['mock'])
        owner.cleanup()
        self.assertEqual([(9, signal.SIGTERM), (9, signal.SIGKILL)], self.fd_signals)
        self.assertTrue(owner.closed)

    def test_pidfd_acquisition_failure_uses_native_child_binding(self):
        def failure(_pid, _flags):
            raise OSError('mock pidfd unavailable')
        owner = self.setup_owner(pidfd=failure)
        with self.assertRaises(OSError):
            owner.spawn(['mock'])
        self.assertTrue(owner.closed)
        self.assertEqual([signal.SIGTERM], self.child.signals)
        self.assertEqual([], self.fd_signals)

    def test_pid_reuse_or_child_binding_uncertainty_retains_failure(self):
        owner = self.setup_owner(binding=lambda _: False)
        owner.spawn(['mock'])
        waits = []
        def natural_expiry(timeout):
            waits.append(timeout)
            if len(waits) < 3:
                raise subprocess.TimeoutExpired('mock finite natural expiry', timeout)
            self.child.code = 124
            return 124
        self.child.wait = natural_expiry
        owner.cleanup()
        self.assertTrue(owner.closed)
        self.assertEqual([], self.child.signals)
        self.assertEqual([], self.fd_signals)
        self.assertTrue(any(row['failureType'] == 'OwnershipError' for row in owner.failures))
        self.assertEqual(124, self.child.poll())
        self.assertGreaterEqual(len(waits), 3)

    def test_identity_reuse_cannot_redirect_exact_pidfd_signal(self):
        owner = self.setup_owner(identity=lambda pid: {'pid': pid, 'start': 'changed'})
        owner.spawn(['mock'])
        owner.cleanup()
        self.assertEqual([(9, signal.SIGTERM)], self.fd_signals)
        self.assertTrue(owner.closed)

    def test_outer_signal_scope_refuses_swallowed_cleanup_failure(self):
        owner = self.setup_owner(binding=lambda _: False)
        def natural_expiry(timeout):
            self.child.code = 124
            return 124
        self.child.wait = natural_expiry
        with patch.object(owned.signal, 'signal'), patch.object(owned.signal, 'getsignal'):
            with self.assertRaises(owned.OwnershipError):
                with owner.signals():
                    owner.spawn(['mock'])
        self.assertTrue(owner.closed)
        self.assertEqual([], self.child.signals)

    def test_transitive_owner_seal_refuses_tampered_import_before_exec(self):
        import kernel_only
        with patch.object(kernel_only, 'regular', return_value=b'raise AssertionError("Must not execute")'), \
             patch.object(kernel_only, 'module', side_effect=AssertionError('Unsealed execution')):
            with self.assertRaises(ValueError):
                kernel_only.load_custody_module()

    def test_pidfd_close_failure_cannot_certify_cleanup(self):
        owner = self.setup_owner()
        owner.spawn(['mock'])
        attempts = []
        def failure(fd):
            attempts.append(fd)
            if len(attempts) < 3:
                raise OSError('mock transient fd close failed')
        owner.close_fd = failure
        self.child.code = 0
        with self.assertRaises(OSError):
            owner._retire_handle(owner.records[0])
        with self.assertRaises(owned.OwnershipError):
            owner._retire_handle(owner.records[0])
        self.assertIsNotNone(self.child.poll())
        self.assertFalse(owner.closed)
        self.assertFalse(owner.records[0]['cleanupVerified'])
        self.assertEqual(9, owner.records[0]['pidfd'])
        self.assertEqual('retirement-unproved', owner.records[0]['pidfdState'])
        self.assertEqual([9], attempts)
        self.assertTrue(any(r.get('operation')=='fd-close' for r in owner.failures))


class OuterRunControls(unittest.TestCase):
    def exercise(self, mode):
        import kernel_only as kernel
        primary = OSError('exact original acquisition object')
        initial_trace = []
        handlers = {}
        children = {}
        owners = []
        observations = []
        clock = SimpleNamespace(value=0)
        count = [0]
        cleanup_saved = []
        foreign_descriptor = {'untouched':True}
        all_closes = []
        signal_faults=[]
        wait_faults=[]
        binding_timeouts=[]
        real_save = kernel.save
        real_read = Path.read_text
        def identity(pid):
            if mode.startswith('identity') or mode == 'primary-plus-durability':
                try:
                    raise primary
                except OSError:
                    initial_trace.append(sys.exc_info()[2])
                    raise
            return {'pid': pid, 'startTicks': 99, 'executable': 'mock-python', 'hostBootId': 'mock-boot'}
        def fake_read(path, *args, **kwargs):
            if str(path).replace('\\', '/').startswith('/proc/'):
                return 'Max address space 268435456 268435456 bytes\nMax cpu time 30 30 seconds\n'
            return real_read(path, *args, **kwargs)
        class Process(Child):
            def __init__(self, pid, scenario=None):
                super().__init__()
                self.pid, self.scenario = pid, scenario
            def wait(self, timeout):
                if self.code is None and mode == 'identity-cleanup-failure' and len(wait_faults)<3:
                    wait_faults.append(timeout)
                    raise OSError('repeated actual outer wait fault')
                if self.code is None and mode == 'identity-external-handoff':
                    clock.value+=16
                    raise OSError('permanent wait fault under mocked outside custody')
                if self.code is None and mode == 'identity-unavailable-binding' and len(binding_timeouts)<3:
                    binding_timeouts.append(timeout)
                    raise subprocess.TimeoutExpired('mock finite natural expiry',timeout)
                if self.code is None and self.scenario is not None:
                    self.code = 124 if self.scenario == 'timer' else 0
                    clock.value += 16
                return super().wait(timeout)
        def popen(command, **_options):
            count[0] += 1
            scenario = command[command.index('--scenario')+1] if '--scenario' in command else None
            value = Process(1000+count[0], scenario)
            children[value.pid] = value
            if scenario is not None:
                target = Path(command[command.index('--output')+1])
                ident = {'pid': value.pid, 'startTicks': 99, 'executable': 'mock-python', 'hostBootId': 'mock-boot'}
                if scenario != 'malformed':
                    real_save(target/'kernel-ready.json', {'identity':ident,'actualPid':value.pid,'sessionId':value.pid,
                        'limitsAS':[268435456,268435456],'limitsCPU':[30,30],'dockerDispatches':0})
                if scenario == 'timer':
                    real_save(target/'lease-exhausted.json', {'guardian':ident,'cleanupVerified':False,
                              'requiredAction':'exact unresolved custody recovery'})
                else:
                    real_save(target/'kernel-terminal.json', {'parentDeathObserved':True,'registrationRefused':True})
            return value
        def factory(identity_arg, alive_arg, writer,external_owner=None):
            failed_signals = []
            close_attempts = []
            def pidfd_send(fd, sig, *_values):
                if mode=='identity-external-handoff':
                    raise OSError('Permanent mock signal uncertainty; actual outside owner required')
                if mode == 'identity-cleanup-failure' and len(failed_signals) < 3:
                    failed_signals.append(sig)
                    signal_faults.append(sig)
                    raise OSError('secondary cleanup uncertainty')
                children[fd].send_signal(sig)
            def close(fd):
                close_attempts.append(fd)
                all_closes.append(fd)
                if mode == 'identity-fdclose':
                    if len(close_attempts) > 1:
                        foreign_descriptor['untouched'] = False
                    raise InterruptedError(4, 'mock native close consumed owned fd; integer now foreign')
                if mode == 'identity-latecancel':
                    handlers[signal.SIGTERM](signal.SIGTERM,None)
            # Mock native Linux close authority explicitly: no real Linux calls.
            with patch.object(owned, 'NATIVE_CLOSE', close), patch.object(owned.sys, 'platform', 'linux'):
                owner = owned.ProcessCustody(identity_arg, alive_arg, writer, popen=popen,external_owner=external_owner,
                    pidfd_open=lambda pid,_flags:pid, pidfd_signal=pidfd_send,
                    close_fd=close, child_binding=lambda _p: mode != 'identity-unavailable-binding')
            owners.append(owner)
            return owner
        def save(path, row):
            if mode == 'primary-plus-durability' and row.get('kind') == 'terminal-cleanup':
                raise OSError('secondary durability uncertainty')
            if Path(path).name == 'cleanup.json':
                cleanup_saved.append(row)
                if mode == 'finally-signal' and Path(path).parent.parent.name == 'timer':
                    handlers[signal.SIGTERM](signal.SIGTERM, None)
            return real_save(path, row)
        def need(value, message):
            if mode == 'validation-signal' and message == 'Real finite timer identity/elapsed evidence differs':
                handlers[signal.SIGINT](signal.SIGINT, None)
            return original_need(value, message)
        original_need = kernel.need
        base = Path(__file__).absolute().parents[2]
        scratch_parent = base/'outputs/customer-native-lifecycle-qualification-20261008-r7'
        scratch_parent.mkdir(exist_ok=True)
        with tempfile.TemporaryDirectory(dir=scratch_parent) as scratch:
            args = SimpleNamespace(packet='mock-packet', manifest_sha256=kernel.MANIFEST_SHA,
                output=str(Path(scratch)/'fresh'),capability='mock-not-written',capability_sha256='a'*64,
                external_owner_receipt='mock-external',external_owner_sha256='b'*64)
            class Outside:
                witness=None
                def verify_before_spawn(self,records): return {'mockOnly':True}
                verify_after_spawn=verify_before_spawn
                def remaining_authority_seconds(self): return 600
                def verify_cleanup_transfer(self,records):
                    self.witness={'cleanupOnlyTransfer':True,'terminalSuccess':False,
                        'invocationId':'c'*32,'ownedLivePids':[r['process'].pid for r in records if r['process'].poll() is None],
                        'freshActualObservation':'MOCK-ONLY-NO-NATIVE'}
                    return self.witness
            outside=Outside() if mode=='identity-external-handoff' else None
            fake_process = SimpleNamespace(identity=identity,alive=lambda ident:children[ident['pid']].poll() is None)
            with patch.object(kernel,'admission',return_value={'freePhysicalKiB':4194304}), \
                 patch.object(kernel,'authority',return_value={'TEST_ONLY':True}), \
                 patch.object(kernel,'bind_external',return_value=outside), \
                 patch.object(kernel,'sealed',return_value={'customer_guardian_process.py':b'#mock'}), \
                 patch.object(kernel,'module',return_value=fake_process), \
                 patch.object(kernel,'load_custody_module',return_value=SimpleNamespace(ProcessCustody=factory)), \
                 patch.object(kernel,'save',side_effect=save), patch.object(kernel,'need',side_effect=need), \
                 patch.object(kernel.time,'monotonic',side_effect=lambda:clock.value), \
                 patch.object(kernel.os,'getsid',side_effect=lambda pid:pid,create=True), \
                 patch.object(Path,'read_text',fake_read), \
                 patch.object(owned.signal,'SIGKILL',9,create=True), \
                 patch.object(owned.signal,'getsignal',return_value=None), \
                 patch.object(owned.signal,'signal',side_effect=lambda sig,fn:handlers.update({sig:fn})):
                actual = None
                try:
                    kernel.run(args)
                except BaseException as error:
                    actual = error
                if mode.startswith('identity') or mode == 'primary-plus-durability':
                    self.assertIs(actual, primary)
                    traces=[];tb=actual.__traceback__
                    while tb:
                        traces.append(tb);tb=tb.tb_next
                    self.assertTrue(any(tb is initial_trace[0] for tb in traces))
                    self.assertTrue(cleanup_saved)
                    self.assertTrue(cleanup_saved[-1]['acquisitionFailures'])
                    if mode == 'identity-external-handoff':
                        self.assertFalse(owners[-1].closed)
                        self.assertTrue(cleanup_saved[-1]['remaining'])
                        self.assertEqual([1001],owners[-1].external_handoff['ownedLivePids'])
                        self.assertFalse(owners[-1].external_handoff['terminalSuccess'])
                    elif mode == 'identity-cleanup-failure':
                        self.assertTrue(owners[-1].closed)
                        self.assertFalse(cleanup_saved[-1]['remaining'])
                        self.assertTrue(all(p.poll() is not None for p in children.values()))
                    else:
                        self.assertTrue(owners[-1].closed)
                    if mode!='identity-external-handoff':
                        self.assertTrue(all(r['cleanupVerified'] and r['pidfd'] is None for r in owners[-1].records))
                        self.assertTrue(all(p.poll() is not None for p in children.values()))
                    if mode=='identity-cleanup-failure':
                        self.assertEqual(3,len(signal_faults))
                        self.assertEqual(3,len(wait_faults))
                    if mode == 'identity-unavailable-binding':
                        self.assertTrue(all(not p.signals for p in children.values()))
                        self.assertEqual(3,len(binding_timeouts))
                    if mode == 'identity-latecancel':
                        self.assertEqual(signal.SIGTERM, owners[-1].interrupted)
                    if mode == 'identity-fdclose':
                        self.assertEqual([1001], all_closes)
                        self.assertTrue(foreign_descriptor['untouched'])
                        self.assertEqual('retired-native-linux-error',owners[-1].records[0]['pidfdState'])
                        self.assertTrue(cleanup_saved[-1]['cleanupErrors'])
                    observations.append({'mode':mode,'originalExceptionObjectPreserved':True,
                        'originalTracebackNodePreserved':True,'cleanupVerified':owners[-1].closed,
                        'nativeCloseCalls':len(all_closes),'foreignDescriptorUntouched':foreign_descriptor['untouched'],
                        'secondaryFailures':[r for r in owners[-1].failures if r['phase']!='acquisition']})
                elif mode in ('finally-signal','validation-signal'):
                    self.assertIsInstance(actual,owned.DeferredInterruption)
                    self.assertFalse((Path(args.output)/'kernel-report.json').exists())
                    self.assertTrue(all(o.closed for o in owners))
                    observations.append({'mode':mode,'deferredInterruptionRaised':True,'noSuccessfulReport':True})
                else:
                    self.assertIsNone(actual)
                    self.assertTrue((Path(args.output)/'kernel-report.json').is_file())
        print(json.dumps({'outerRunEvidence':observations}))

    def test_outer_identity_error_cleanup_success_keeps_original_object_traceback(self):
        self.exercise('identity-cleanup-success')

    def test_outer_identity_error_cleanup_failure_keeps_original_object_traceback(self):
        self.exercise('identity-cleanup-failure')

    def test_outer_primary_exception_plus_durability_failure_is_not_masked(self):
        self.exercise('primary-plus-durability')

    def test_outer_native_close_consumes_fd_then_reuse_never_closed_twice(self):
        self.exercise('identity-fdclose')

    def test_outer_unavailable_binding_waits_natural_expiry_without_raw_signal(self):
        self.exercise('identity-unavailable-binding')

    def test_outer_late_cancel_during_cleanup_preserves_earlier_primary(self):
        self.exercise('identity-latecancel')

    def test_outer_permanent_failure_transfers_only_to_fresh_outside_custody(self):
        self.exercise('identity-external-handoff')

    def test_signal_during_last_validation_cannot_qualify_success(self):
        self.exercise('validation-signal')

    def test_signal_during_last_finally_cannot_qualify_success(self):
        self.exercise('finally-signal')

    def test_outer_normal_success_with_all_kernel_edges_mocked(self):
        self.exercise('normal')

    def test_signal_at_normal_scope_exit_raises_retained_interruption(self):
        owner = owned.ProcessCustody(lambda _:None,lambda _:False,lambda _:None,
                                    popen=lambda *_args,**_kwargs:None)
        with patch.object(owned.signal,'signal'),patch.object(owned.signal,'getsignal'):
            with self.assertRaises(owned.DeferredInterruption):
                with owner.signals():
                    owner.interrupted = signal.SIGTERM

    def test_pending_signal_remains_primary_with_terminal_durability_fault(self):
        def writer(_row):
            raise OSError('mock durability fault')
        owner = owned.ProcessCustody(lambda _:None,lambda _:False,writer)
        with patch.object(owned.signal,'signal'),patch.object(owned.signal,'getsignal'):
            with self.assertRaises(owned.DeferredInterruption):
                with owner.signals():
                    owner.interrupted = signal.SIGTERM
        self.assertTrue(any(r['phase']=='durability' for r in owner.failures))
        self.assertEqual(signal.SIGTERM, owner.interrupted)

    def test_pending_signal_remains_primary_with_cleanup_uncertainty(self):
        owner = owned.ProcessCustody(lambda _:None,lambda _:False,lambda _:None)
        def failure():
            raise OSError('mock cleanup uncertainty')
        owner.cleanup = failure
        with patch.object(owned.signal,'signal'),patch.object(owned.signal,'getsignal'):
            with self.assertRaises(owned.DeferredInterruption):
                with owner.signals():
                    owner.interrupted = signal.SIGTERM
        self.assertFalse(owner.closed)
        self.assertTrue(any(r['phase']=='cleanup' for r in owner.failures))

    def test_signal_installation_exception_remains_primary_with_durability_fault(self):
        original = OSError('original installation fault')
        def writer(_row):
            raise OSError('secondary durability fault')
        owner = owned.ProcessCustody(lambda _:None,lambda _:False,writer)
        calls = []
        def install(sig, handler):
            calls.append(sig)
            if len(calls) == 1:
                raise original
        with patch.object(owned.signal,'signal',side_effect=install),patch.object(owned.signal,'getsignal'):
            try:
                with owner.signals():
                    self.fail('Failed installation must not yield')
            except BaseException as error:
                self.assertIs(error, original)
            else:
                self.fail('Original installation exception was swallowed')
        self.assertTrue(any(r['phase']=='durability' for r in owner.failures))

    def test_custom_close_consumes_or_not_unproved_never_retried(self):
        calls = []
        def custom(fd):
            calls.append(fd)
            raise OSError('custom implementation may fail before syscall')
        owner = owned.ProcessCustody(lambda _:None,lambda _:False,lambda _:None,close_fd=custom)
        record = {'pidfd':55,'process':Child(),'cleanupVerified':False,'identity':None}
        with self.assertRaises(OSError):
            owner._retire_handle(record)
        with self.assertRaises(owned.OwnershipError):
            owner._retire_handle(record)
        self.assertEqual([55], calls)
        self.assertEqual(55, record['pidfd'])
        self.assertEqual('retirement-unproved', record['pidfdState'])


class ExternalOwnerControls(unittest.TestCase):
    def fixture(self):
        now=dt.datetime(2026,10,8,tzinfo=dt.timezone.utc)
        cap={'issuedUtc':now.isoformat(),'expiresUtc':(now+dt.timedelta(seconds=600)).isoformat(),'hostBootId':'boot'}
        props=dict(owned.ExternalUnitCustody.FIXED,RuntimeMaxUSec='560000000',TimeoutStopUSec='5000000')
        intent={'schemaVersion':1,'scope':'customer-kernel-external-custody','originalObserverSha256':'a'*64,
            'capabilitySha256':'b'*64,'harnessSha256':'c'*64,'packetManifestSha256':'d'*64,
            'unit':'customer-kernel-1-1-nonce.service','description':'exact-nonce',
            'controlGroup':'/system.slice/customer-kernel-1-1-nonce.service','issuedUtc':now.isoformat(),
            'expiresUtc':cap['expiresUtc'],'routeIdentity':{},'managerProperties':props}
        intent['externalAdapter']={'path':'/sealed/external_unit_owner.py','sha256':owned.hashlib.sha256(b'#mock-adapter').hexdigest(),
            'execStopPost':['/usr/bin/python3','-B','/sealed/external_unit_owner.py','stop-witness','--directory','/owned/external-owner']}
        state=dict(props,Id=intent['unit'],Description='exact-nonce',InvocationID='f'*32,
            MainPID='77',ControlGroup=intent['controlGroup'],ActiveState='active',SubState='running',LoadState='loaded',
            ActiveEnterTimestampMonotonic='100000000')
        state['ExecStopPost']='{ argv[]='+ ' '.join(intent['externalAdapter']['execStopPost'])+' ; ignore_errors=no ; }'
        state['ExecStop']=''
        with patch.object(owned,'EXPECTED_EXTERNAL_ADAPTER_SHA',intent['externalAdapter']['sha256']):
            verifier=owned.ExternalUnitCustody(intent,cap,observe=lambda:dict(state),membership=lambda _:intent['controlGroup'],
                boot=lambda:'boot',clock=lambda:100,utc=lambda:now,require_main=False,
                process_identity=lambda pid:{'pid':pid,'startTicks':10,'executable':'mock-python'},adapter_reader=lambda _:b'#mock-adapter')
        return verifier,state,intent,cap

    def test_missing_actual_external_owner_refuses_linux_spawn(self):
        dispatch=[]
        owner=owned.ProcessCustody(lambda _:None,lambda _:False,lambda _:None,popen=lambda *a,**k:dispatch.append(a))
        with patch.object(owned.sys,'platform','linux'),self.assertRaises(owned.OwnershipError):
            owner.spawn(['MOCK'])
        self.assertEqual([],dispatch)

    def test_actual_owner_binding_and_child_membership_required(self):
        verifier,state,intent,_=self.fixture()
        child=Child();record={'process':child}
        witness=verifier.verify_before_spawn([record])
        self.assertEqual([123],witness['ownedLivePids'])
        verifier.membership=lambda pid:'/foreign' if pid==123 else intent['controlGroup']
        with self.assertRaises(owned.OwnershipError):verifier.verify_after_spawn([record])

    def test_invocation_replacement_and_missing_manager_refuse_transfer(self):
        verifier,state,_,_=self.fixture();verifier.verify_before_spawn([])
        state['InvocationID']='a'*32
        with self.assertRaises(owned.OwnershipError):verifier.verify_cleanup_transfer([])
        state['LoadState']='not-found'
        with self.assertRaises(owned.OwnershipError):verifier.verify_cleanup_transfer([])

    def test_original600_authority_and_actual_activation_expiry_not_extended(self):
        verifier,state,_,_=self.fixture()
        state['RuntimeMaxUSec']='600s'
        verifier.intent['managerProperties']['RuntimeMaxUSec']='600000000'
        with self.assertRaises(owned.OwnershipError):verifier.verify_before_spawn([])
        verifier,intent_state,intent,cap=self.fixture()
        cap['expiresUtc']=(dt.datetime.fromisoformat(cap['issuedUtc'])+dt.timedelta(seconds=1800)).isoformat()
        intent['expiresUtc']=cap['expiresUtc']
        with patch.object(owned,'EXPECTED_EXTERNAL_ADAPTER_SHA',intent['externalAdapter']['sha256']),self.assertRaises(owned.OwnershipError):
            owned.ExternalUnitCustody(intent,cap,adapter_reader=lambda _:b'#mock-adapter')

    def test_cgroup_security_and_boot_drift_refuse_admission(self):
        verifier,state,_,_=self.fixture()
        state['Delegate']='yes'
        with self.assertRaises(owned.OwnershipError):verifier.verify_before_spawn([])
        state['Delegate']='no';verifier.boot=lambda:'different-boot'
        with self.assertRaises(owned.OwnershipError):verifier.verify_before_spawn([])

    def test_duration_rendering_and_failed_census_never_absence_success(self):
        verifier,state,_,_=self.fixture()
        state.update(RuntimeMaxUSec='9min 20s',TimeoutStopUSec='5s',CPUQuotaPerSecUSec='250ms')
        verifier.verify_before_spawn([])
        def unreadable(_):raise PermissionError('Actual census unobservable')
        verifier.membership=unreadable
        with self.assertRaises(PermissionError):verifier.verify_cleanup_transfer([])

    def test_repeated_cleanup_keeps_deadline_custom_close_uncertain_handoff_not_success(self):
        verifier,_,_,_=self.fixture()
        calls=[]
        def close(fd):calls.append(fd);raise OSError('Custom retirement unproved')
        child=Child();child.code=0
        owner=owned.ProcessCustody(lambda _:None,lambda _:False,lambda _:None,close_fd=close,external_owner=verifier)
        owner.records=[{'process':child,'pidfd':55,'identity':None,'cleanupVerified':False}]
        with self.assertRaises(OSError):owner._retire_handle(owner.records[0])
        owner.cleanup_deadline=60
        with patch.object(owned.time,'monotonic',return_value=61):
            owner.cleanup();first=owner.external_handoff
            owner.cleanup();second=owner.external_handoff
        self.assertEqual(60,owner.cleanup_deadline)
        self.assertFalse(owner.closed)
        self.assertTrue(first['cleanupOnlyTransfer']);self.assertTrue(second['cleanupOnlyTransfer'])
        self.assertFalse(second['terminalSuccess']);self.assertEqual([55],calls)

    def test_main_entry_and_exact_command_binding_not_receipt_alone(self):
        verifier,state,_,_=self.fixture()
        verifier.require_main=True
        with self.assertRaises(owned.OwnershipError):verifier.verify_before_spawn([])
        verifier.require_main=False;verifier.expected_exec=['/usr/bin/python3','-B','/sealed/kernel.py']
        state['ExecStart']='{ argv[]=/usr/bin/python3 -B /foreign/kernel.py ; ignore_errors=no ; }'
        with self.assertRaises(owned.OwnershipError):verifier.verify_before_spawn([])
        state['ExecStart']='{ argv[]=/usr/bin/python3 -B /sealed/kernel.py ; ignore_errors=no ; }'
        verifier.verify_before_spawn([])
        verifier.process_identity=lambda pid:{'pid':pid,'startTicks':11,'executable':'mock-python'}
        with self.assertRaises(owned.OwnershipError):verifier.verify_cleanup_transfer([])

    def test_full_cgroup_unknown_members_or_failed_scan_block_custody(self):
        verifier,state,_,_=self.fixture();verifier.require_main=True;state['MainPID']=str(owned.os.getpid())
        verifier.group_members=lambda group:{owned.os.getpid(),888}
        with self.assertRaises(owned.OwnershipError):verifier.verify_before_spawn([])
        def unreadable(group):raise FileNotFoundError('No cgroup is not success')
        verifier.group_members=unreadable
        with self.assertRaises(FileNotFoundError):verifier.verify_before_spawn([])
        verifier.group_members=lambda group:{owned.os.getpid()}
        self.assertTrue(verifier.verify_before_spawn([])['fullMembershipVerified'])

    def test_missing_fresh_transfer_proof_retains_live_custody_and_deadline(self):
        class Missing:
            def remaining_authority_seconds(self):return 60
            def verify_cleanup_transfer(self,records):raise PermissionError('Fresh manager observation failed')
        owner=owned.ProcessCustody(lambda _:None,lambda _:False,lambda _:None,external_owner=Missing())
        child=Child();owner.records=[{'process':child,'pidfd':None,'identity':None,'cleanupVerified':False}]
        owner.cleanup_deadline=5
        with patch.object(owned.time,'monotonic',return_value=6):
            self.assertFalse(owner._transfer_if_proved());self.assertFalse(owner._transfer_if_proved())
        self.assertEqual(5,owner.cleanup_deadline)
        self.assertIsNone(owner.external_handoff);self.assertFalse(owner.closed);self.assertIsNone(child.poll())
        self.assertEqual(2,owner.cleanup_retry_count)

    def test_stop_hook_missing_or_extra_command_rejects_actual_manager(self):
        verifier,state,_,_=self.fixture();state['ExecStopPost']=''
        with self.assertRaises(owned.OwnershipError):verifier.verify_before_spawn([])

    def test_one_stop_budget_fit_is_not_five_stage_expiry_proof(self):
        verifier,state,_,_=self.fixture()
        state['RuntimeMaxUSec']='580000000';verifier.intent['managerProperties']['RuntimeMaxUSec']='580000000'
        # 580+5 fits600 but 580+5*5 does not: the former predicate was unsafe.
        self.assertLessEqual(580+5,600)
        with self.assertRaises(owned.OwnershipError):verifier.verify_before_spawn([])

    def test_full_subtree_double_census_detects_pid_instability(self):
        group='/system.slice/customer-kernel-1-1-nonce.service'
        root=owned.Path('/sys/fs/cgroup')/group.lstrip('/')
        with patch.object(owned.Path,'is_dir',return_value=True),patch.object(owned.Path,'is_symlink',return_value=False), \
             patch.object(owned.os,'walk',side_effect=lambda *a,**k:iter([(str(root),[],['cgroup.procs'])])), \
             patch.object(owned.Path,'read_text',side_effect=['123\n','124\n']):
            with self.assertRaises(owned.OwnershipError):owned.ExternalUnitCustody._group_members(group)

    def test_self_consistent_caller_chosen_adapter_not_a_sealed_source(self):
        _,_,intent,cap=self.fixture();chosen=b'#callerchosen'
        intent['externalAdapter']['sha256']=owned.hashlib.sha256(chosen).hexdigest()
        with self.assertRaises(owned.OwnershipError):owned.ExternalUnitCustody(intent,cap,adapter_reader=lambda _:chosen)

    def test_adapter_tamper_after_binding_rejected_before_new_observation(self):
        verifier,_,_,_=self.fixture();verifier.verify_before_spawn([])
        verifier.adapter_reader=lambda _:b'#modified-after-binding'
        with self.assertRaises(owned.OwnershipError):verifier.verify_cleanup_transfer([])
        verifier,state,_,_=self.fixture();state['ExecStopPost']+=' { argv[]=/foreign/command ; }'
        with self.assertRaises(owned.OwnershipError):verifier.verify_before_spawn([])

    def test_native_pre_syscall_non_oserror_does_not_assert_retirement(self):
        calls = []
        def native_stub(fd):
            calls.append(fd)
            raise MemoryError('mock before syscall')
        with patch.object(owned,'NATIVE_CLOSE',native_stub),patch.object(owned.sys,'platform','linux'):
            owner = owned.ProcessCustody(lambda _:None,lambda _:False,lambda _:None,close_fd=native_stub)
        record = {'pidfd':55,'process':Child(),'cleanupVerified':False,'identity':None}
        with self.assertRaises(MemoryError):
            owner._retire_handle(record)
        with self.assertRaises(owned.OwnershipError):
            owner._retire_handle(record)
        self.assertEqual([55], calls)
        self.assertEqual(55, record['pidfd'])
        self.assertEqual('retirement-unproved', record['pidfdState'])


if __name__ == '__main__':
    unittest.main(verbosity=2)
