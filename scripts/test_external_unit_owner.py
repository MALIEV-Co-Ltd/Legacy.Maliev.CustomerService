"""Pure actual route/adapter graph controls; every manager/native edge is mocked."""
import copy
import datetime as dt
import hashlib
import json
from pathlib import Path
import tempfile
import unittest
from unittest.mock import patch
import external_unit_owner as e
import kernel_root_route as r
import test_kernel_root_route as route_tests

class Backend:
    def __init__(self,intent,argv):
        self.intent=intent;self.argv=argv;self.created=False;self.gone=False;self.inv='d'*32;self.stops=0;self.dispatches=0;self.nested=[];self.dispatch_failure=None;self.stop_failure=None;self.gc=False;self.active=False;self.onstop=lambda:None
    def boot(self):return self.intent['routeIdentity']['hostBootId']
    def show(self,unit):
        if not self.created or self.gone:return {'Id':unit,'LoadState':'not-found'}
        return dict(self.intent['managerProperties'],Id=unit,Description=self.intent['description'],InvocationID=self.inv,ControlGroup=self.intent['controlGroup'],MainPID='0',LoadState='loaded',ActiveState='active' if self.active else 'inactive',SubState='running' if self.active else 'exited',Result='success',ExecMainStatus='0',ExecMainStartTimestampMonotonic='100',ExecMainExitTimestampMonotonic='200',ExecStart='{ argv[]='+' '.join(self.argv)+' ; }',ExecStopPost='{ argv[]='+self.stop_hook_command+' ; }')
    def dispatch(self,intent,argv):
        self.intent=intent;self.argv=argv;self.created=True;self.dispatches+=1
        if self.dispatch_failure:raise self.dispatch_failure
    def stop(self,unit,timeout=40):
        self.stops+=1;self.onstop()
        if self.stop_failure:raise self.stop_failure
        self.active=False
        self.saved=self.show(unit)
        if self.gc:self.gone=True
    def reset(self,unit):self.gone=True
    def members(self,group):return self.nested
    def witness(self,directory):
        return {'intentSha256':e.digest(e.canonical(self.intent)),'unit':self.intent['unit'],'description':self.intent['description'],'invocationId':self.inv,'controlGroup':self.intent['controlGroup'],'bootId':self.boot(),'MainPID':0,'mainExited':True,'onlyWitnessMember':True,'terminalManager':self.saved}

class OwnerControls(unittest.TestCase):
    def setUp(self):
        f=route_tests.Controls();f.setUp();self.f=f;self.mono=0
        self.intent={'schemaVersion':1,'scope':'customer-kernel-external-custody','originalObserverSha256':'a'*64,'capabilitySha256':f.sha,'harnessSha256':'a'*64,'packetManifestSha256':r.PACKET_SHA256,'unit':'customer-kernel-123-1-'+'c'*32+'.service','description':'CustomerKernelProof:'+'e'*32,'controlGroup':'/system.slice/customer-kernel-123-1-'+'c'*32+'.service','issuedUtc':f.clock.isoformat(),'expiresUtc':(f.clock+dt.timedelta(seconds=500)).isoformat(),'routeIdentity':{'hostBootId':f.host['hostBootId'],'runnerWorker':f.host['runnerWorker']},'managerProperties':{'RuntimeMaxUSec':'465000000','TimeoutStopUSec':'5000000','TimeoutStartUSec':'10000000','MemoryMax':'268435456','MemorySwapMax':'0','CPUQuotaPerSecUSec':'250000','TasksMax':'64','KillMode':'control-group','SendSIGKILL':'yes','Restart':'no','Delegate':'no','NoNewPrivileges':'yes','ProtectControlGroups':'yes','Type':'exec','NotifyAccess':'none','ExecStop':''},'externalAdapter':{'path':Path(e.__file__).absolute().as_posix(),'sha256':r.digest(Path(e.__file__).read_bytes()),'execStopPost':[]}}
        self.argv=['/usr/bin/python3','-B','/owned/kernel_only.py'];self.backend=Backend(self.intent,self.argv)
    def sleep(self,value):self.mono+=value;self.f.clock+=dt.timedelta(seconds=value)
    def owner(self,**kwargs):return e.ExternalOwner(self.backend,clock=lambda:self.f.clock,monotonic=lambda:self.mono,sleep=self.sleep,**kwargs)
    def test_prearmed_owner_and_exact_empty_subtree_normal(self):
        with tempfile.TemporaryDirectory() as tmp:
            result=self.owner().launch(self.intent,self.argv,Path(tmp))
            self.assertTrue(result['kernelQualified']);self.assertTrue(result['cleanupVerified']);self.assertEqual(1,self.backend.dispatches);self.assertEqual(1,self.backend.stops)
    def test_early_cancel_no_dispatch(self):
        with tempfile.TemporaryDirectory() as tmp,self.assertRaises(ValueError):self.owner(cancelled=lambda:True).launch(self.intent,self.argv,Path(tmp))
        self.assertEqual(0,self.backend.dispatches)
    def test_dispatch_ack_lost_after_actual_create_cleanup_preserves_original(self):
        original=LookupError('actual dispatch acknowledgement lost');self.backend.dispatch_failure=original
        with tempfile.TemporaryDirectory() as tmp,self.assertRaises(LookupError) as caught:self.owner().launch(self.intent,self.argv,Path(tmp))
        self.assertIs(caught.exception,original);self.assertEqual(1,self.backend.stops)
    def test_first_error_kept_when_stop_and_terminal_durability_fail(self):
        original=LookupError('first');self.backend.dispatch_failure=original;self.backend.stop_failure=ValueError('stop uncertain')
        def writer(path,row):
            if path.name=='external-owner-terminal.json':raise OSError('terminal fsync')
            return e.atomic_new(path,row)
        with tempfile.TemporaryDirectory() as tmp,self.assertRaises(LookupError) as caught:self.owner(writer=writer).launch(self.intent,self.argv,Path(tmp))
        self.assertIs(caught.exception,original);self.assertEqual(2,len(original.__notes__))
    def test_invocation_reuse_stops_no_foreign_owner(self):
        owner=self.owner();self.backend.created=True;self.backend.stop_hook_command='/usr/bin/python3'
        ledger={'dispatchAttempted':True,'argv':self.argv,'stopHookArgv':['/usr/bin/python3'],'invocationId':'f'*32}
        with self.assertRaises(ValueError):owner.settle(self.intent,ledger)
        self.assertEqual(0,self.backend.stops)
    def test_nested_live_member_never_certifies_zero(self):
        self.backend.nested=[77]
        with tempfile.TemporaryDirectory() as tmp,self.assertRaises(ValueError):self.owner().launch(self.intent,self.argv,Path(tmp))
    def test_missing_cgroup_receipt_unknown_not_zero(self):
        self.backend.members=lambda _:(_ for _ in ()).throw(PermissionError('census inaccessible'))
        # Bounded virtual clock exercises retry, not an unbounded host wait.
        with tempfile.TemporaryDirectory() as tmp,self.assertRaises(TimeoutError):self.owner().launch(self.intent,self.argv,Path(tmp))
        self.assertLessEqual(self.mono,101)
    def test_gc_after_forced_stop_requires_actual_witness(self):
        self.backend.gc=True
        with tempfile.TemporaryDirectory() as tmp:
            result=self.owner().launch(self.intent,self.argv,Path(tmp));self.assertTrue(result['managerUnitAbsent']);self.assertIn('stopWitness',result)
    def test_gc_without_actual_witness_never_accepts_absence(self):
        self.backend.gc=True;self.backend.witness=lambda _:(_ for _ in ()).throw(ValueError('actual witness missing'))
        with tempfile.TemporaryDirectory() as tmp,self.assertRaises(ValueError):self.owner().launch(self.intent,self.argv,Path(tmp))
    def test_cancellation_during_settlement_cannot_qualify(self):
        stop={'value':False};self.backend.onstop=lambda:stop.update(value=True)
        with tempfile.TemporaryDirectory() as tmp,self.assertRaises(ValueError):self.owner(cancelled=lambda:stop['value']).launch(self.intent,self.argv,Path(tmp))
        self.assertEqual(1,self.backend.stops)
    def test_multiple_manager_exec_commands_refused(self):
        owner=self.owner();self.backend.created=True;self.backend.stop_hook_command='/usr/bin/python3';owner.expected_argv=self.argv;owner.expected_hook=['/usr/bin/python3']
        for key in ('ExecStart','ExecStopPost'):
            state=self.backend.show(self.intent['unit']);state[key]+=' { argv[]=/foreign/command ; }'
            with self.subTest(key=key),self.assertRaises(ValueError):owner.identity(self.intent,state)
    def test_manager_duration_parser_bounded(self):
        self.assertEqual(560000000,e.duration('9min 20s'));self.assertEqual(250000,e.duration('250ms'))
        for value in ('infinity','-1s','1day','1s garbage'):
            with self.subTest(value=value),self.assertRaises(ValueError):e.duration(value)
    def test_hard_receiver_death_after_stop_recovers_same_actual_witness(self):
        self.backend.gc=True
        with tempfile.TemporaryDirectory() as tmp:
            path=Path(tmp);result=self.owner().launch(self.intent,self.argv,path)
            (path/'external-owner-terminal.json').unlink()  # Lost final reply/journal, keep actual acquired proof.
            recovered=e.settle_from_directory(path,self.backend)
            self.assertTrue(recovered['independentAlwaysSettlementVerified']);self.assertTrue(recovered['cleanupVerified'])
    def test_raw_witness_invocation_reuse_refuses_always_recovery(self):
        self.backend.gc=True
        with tempfile.TemporaryDirectory() as tmp:
            path=Path(tmp);self.owner().launch(self.intent,self.argv,path);(path/'external-owner-terminal.json').unlink();self.backend.inv='f'*32
            with self.assertRaises(ValueError):e.settle_from_directory(path,self.backend)
    def test_runtime_timeout_has_independent_exact_stop(self):
        self.backend.active=True
        with tempfile.TemporaryDirectory() as tmp,self.assertRaises(TimeoutError):self.owner().launch(self.intent,self.argv,Path(tmp))
        self.assertEqual(1,self.backend.stops)
    def test_actual_receiver_execute_graph_arms_owner_before_kernel(self):
        with tempfile.TemporaryDirectory() as tmp:
            path=Path(tmp);harness=path/'kernel_only.py';helper=path/'owned_process.py';harness.write_bytes(b'raise AssertionError("Direct main must never execute")');helper.write_bytes(b'pure sealed helper')
            adapter=Path(e.__file__);cap=copy.deepcopy(self.f.cap);cap['harnessSha256']=r.digest(harness.read_bytes());raw=r.canonical(cap);sha=r.digest(raw)
            (path/'root-capability.json').write_bytes(raw);(path/'observation.json').write_bytes(r.canonical(self.f.obs))
            with patch.object(r,'HARNESS_SHA256',cap['harnessSha256']),patch.object(r,'OWNER_HELPER_SHA256',r.digest(helper.read_bytes())),patch.object(r,'EXTERNAL_OWNER_SHA256',r.digest(adapter.read_bytes())),patch.object(r,'snapshot',return_value=self.f.host):
                result=r.execute_once(harness,path/'packet',path/'kernel-results',path/'root-capability.json',sha,external_backend=self.backend,observation=self.f.obs,identity_reader=lambda *_:self.f.host,clock=lambda:self.f.clock)
            self.assertEqual(0,result);self.assertEqual(1,self.backend.dispatches);self.assertEqual(1,self.backend.stops)
            intent=json.loads((path/'external-owner/external-owner-intent.json').read_bytes());self.assertEqual(14,len(intent));self.assertIn('--external-owner-sha256',self.backend.argv);self.assertEqual('5000000',intent['managerProperties']['TimeoutStopUSec'])

    def test_readonly_stoppost_manager_show_never_sudo(self):
        backend=e.LinuxBackend.__new__(e.LinuxBackend);calls=[]
        backend.command=lambda *argv,**kwargs:calls.append(argv) or 'Id=owned.service\n'
        self.assertEqual('owned.service',backend.show('owned.service')['Id'])
        self.assertEqual('/usr/bin/systemctl',calls[0][0]);self.assertNotIn('sudo',calls[0])
    def test_helper_identity_acquisition_failure_releases_handles_and_waits(self):
        import io
        class Child:
            pid=99;returncode=None
            def __init__(self):self.stdout=io.BytesIO();self.stderr=io.BytesIO();self.waited=False
            def poll(self):return self.returncode
            def wait(self,timeout):self.waited=True;self.returncode=0;return 0
        child=Child();backend=e.LinuxBackend.__new__(e.LinuxBackend);backend.command_resources=[]
        original=PermissionError('actual proc identity inaccessible')
        with patch.object(e.subprocess,'Popen',return_value=child),patch.object(Path,'read_text',side_effect=original),self.assertRaises(PermissionError) as caught:backend.command('/usr/bin/systemctl','show','owned.service')
        self.assertIs(caught.exception,original);self.assertTrue(child.waited);self.assertTrue(child.stdout.closed);self.assertTrue(child.stderr.closed);self.assertFalse(backend.command_resources[-1]['remainingHelper'])
    def test_predispatch_delayed_budget_includes_start_and_five_stop_phases(self):
        self.intent['managerProperties']['RuntimeMaxUSec']='480000000' # one-stop fits, five+start do not.
        with tempfile.TemporaryDirectory() as tmp,self.assertRaises(ValueError):self.owner().launch(self.intent,self.argv,Path(tmp))
        self.assertEqual(0,self.backend.dispatches)
    def test_reuse_during_final_member_census_refuses_cleanup(self):
        self.backend.gc=True
        with tempfile.TemporaryDirectory() as tmp:
            path=Path(tmp);self.owner().launch(self.intent,self.argv,path);(path/'external-owner-terminal.json').unlink()
            def members(_):self.backend.gone=False;self.backend.inv='f'*32;return []
            self.backend.members=members
            with self.assertRaises(ValueError):e.settle_from_directory(path,self.backend)

    def test_readonly_and_privileged_helper_group_has_independent_timeout(self):
        import io
        class Child:
            pid=99;returncode=None
            def __init__(self):self.stdout=io.BytesIO();self.stderr=io.BytesIO()
            def poll(self):return self.returncode
            def wait(self,timeout):self.returncode=0;return 0
        for argv,offset in [(('/usr/bin/systemctl','show','owned.service'),0),(('sudo','-n','/usr/bin/systemctl','stop','owned.service'),2)]:
            child=Child();backend=e.LinuxBackend.__new__(e.LinuxBackend);backend.command_resources=[]
            with patch.object(e.subprocess,'Popen',return_value=child) as popen,patch.object(Path,'read_text',side_effect=PermissionError('synthetic identity acquisition fault')):
                with self.assertRaises(PermissionError):backend.command(*argv)
            actual=popen.call_args.args[0]
            self.assertEqual('/usr/bin/timeout',actual[offset]);self.assertEqual(('--signal=TERM','--kill-after=2s','10s'),actual[offset+1:offset+4]);self.assertEqual(argv[offset:],actual[offset+4:])
            self.assertNotIn('kill',actual);self.assertFalse(backend.command_resources[-1]['remainingHelper'])

if __name__=='__main__':unittest.main()
