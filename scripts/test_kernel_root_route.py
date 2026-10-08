"""Pure synthetic route protocol controls; no HTTP, capability issuance or kernel helpers."""
import base64
import copy
import datetime as dt
import hashlib
import json
from pathlib import Path
import tempfile
import unittest
from unittest.mock import patch
import kernel_root_route as r

H='a'*64
class Controls(unittest.TestCase):
    def setUp(self):
        self.clock=dt.datetime(2026,10,8,tzinfo=dt.timezone.utc);self.mono=0;self.calls=[]
        boot='12345678-1234-1234-1234-123456789abc'
        ident={'pid':100,'startTicks':123,'executable':'/usr/bin/python3','hostBootId':boot,'actualStartUtc':self.clock.isoformat()}
        runner=dict(ident,pid=10,executable='/runner/bin/Runner.Worker')
        self.host={'checkedUtc':self.clock.isoformat(),'hostBootId':boot,'kernel':'synthetic-linux','observer':ident,'runnerWorker':runner,'freePhysicalKiB':4194304,'censusComplete':True,'nativeProcesses':[],'competingNativeProcesses':[]}
        self.obs={'schemaVersion':1,'repository':r.REPOSITORY,'runId':'123','runAttempt':'1','sourceHead':'b'*40,'rootDispatchNonce':'c'*32,'statusContext':r.context('123','1','c'*32),'issuedUtc':self.clock.isoformat(),'allocationDeadlineUtc':(self.clock+dt.timedelta(seconds=600)).isoformat(),'host':self.host}
        self.cap={'schemaVersion':1,'scope':'customer-kernel-only-lifecycle','rootIssued':True,'harnessSha256':H,'packetManifestSha256':r.PACKET_SHA256,'hostBootId':boot,'issuedUtc':self.clock.isoformat(),'expiresUtc':(self.clock+dt.timedelta(seconds=500)).isoformat()}
        self.raw=r.canonical(self.cap);self.sha=r.digest(self.raw);self.oid=hashlib.sha1(b'blob '+str(len(self.raw)).encode()+b'\0'+self.raw).hexdigest()
        self.status={'id':1,'context':self.obs['statusContext'],'state':'success','creator':{'id':r.ROOT_USER_ID,'type':'User'},'description':'authority-sha256='+self.sha,'target_url':'https://api.github.com/repos/'+r.REPOSITORY+'/git/blobs/'+self.oid,'created_at':self.clock.isoformat(),'updated_at':self.clock.isoformat()}
        self.rows=[self.status];self.blobraw=self.raw;self.bloboid=self.oid
    def transport(self,path,timeout,limit):
        self.calls.append((path,timeout,limit))
        if '/statuses?' in path:return 200,r.canonical(self.rows)
        if '/git/blobs/' in path:return 200,r.canonical({'sha':self.bloboid,'size':len(self.blobraw),'encoding':'base64','content':base64.b64encode(self.blobraw).decode()})
        raise AssertionError('Unexpected test API path')
    def observe(self):return dict(self.host,checkedUtc=self.clock.isoformat())
    def sleep(self,seconds):self.assertGreaterEqual(seconds,20);self.mono+=seconds;self.clock+=dt.timedelta(seconds=seconds)
    def receive(self,output,invoke=None,**kwargs):
        return r.receive(self.obs,r.PublicGitHub(self.transport),self.observe,invoke or (lambda *_:0),output,clock=lambda:self.clock,monotonic=lambda:self.mono,sleep=self.sleep,harness_sha=H,**kwargs)
    def fail_receive(self,error=ValueError,**kwargs):
        with tempfile.TemporaryDirectory() as tmp:
            with self.assertRaises(error):self.receive(Path(tmp),**kwargs)
            terminal=r.parse((Path(tmp)/'route-terminal.json').read_bytes())
            self.assertFalse(terminal['kernelQualified']);self.assertEqual(0,terminal['remainingRouteHelpers']);return terminal
    def test_supported_authenticated_unique_status_and_exact_blob_once(self):
        invoked=[]
        with tempfile.TemporaryDirectory() as tmp:
            result=self.receive(Path(tmp),lambda p,sha:invoked.append((p.read_bytes(),sha)) or 0)
            self.assertTrue(result['kernelQualified']);self.assertTrue(result['authorityPublished']);self.assertEqual([(self.raw,self.sha)],invoked)
            self.assertEqual([10,10],[x[1] for x in self.calls]);self.assertEqual(2,len(self.calls))
    def test_creator_spoof_refused_even_exact_rawsha(self):
        self.status['creator']['id']=123;self.fail_receive();self.assertEqual(1,len(self.calls))
    def test_token_bot_and_numeric_bool_not_root_user(self):
        for creator in [{'id':r.ROOT_USER_ID,'type':'Bot'},{'id':True,'type':'User'}]:
            with self.subTest(creator=creator):
                row=dict(self.status,creator=creator)
                with self.assertRaises(ValueError):r.select_status([row],self.obs)
    def test_duplicate_and_equivocal_context_refused(self):
        for second in [copy.deepcopy(self.status),dict(self.status,description='authority-sha256='+'d'*64)]:
            with self.subTest(second=second):
                with self.assertRaises(ValueError):r.select_status([self.status,second],self.obs)
    def test_wrong_head_and_run_attempt_nonce_context_no_authority(self):
        for key,value in [('sourceHead','e'*39),('runId','0'),('runAttempt','0'),('rootDispatchNonce','f'*31)]:
            changed=copy.deepcopy(self.obs);changed[key]=value
            with self.subTest(key=key),self.assertRaises(ValueError):r.validate_observation(changed,self.clock)
        for key,value in [('runId','124'),('runAttempt','2'),('rootDispatchNonce','e'*32)]:
            changed=copy.deepcopy(self.obs);changed[key]=value;changed['statusContext']=r.context(changed['runId'],changed['runAttempt'],changed['rootDispatchNonce'])
            self.assertIsNone(r.select_status([self.status],changed))
    def test_status_target_foreign_mutable_ref_redirect_query_refused(self):
        for url in ['https://api.github.com/repos/foreign/repo/git/blobs/'+self.oid,self.status['target_url']+'?x=1','https://github.com/status','https://api.github.com/repos/'+r.REPOSITORY+'/contents/cap.json']:
            with self.subTest(url=url),self.assertRaises(ValueError):r.select_status([dict(self.status,target_url=url)],self.obs)
    def test_status_success_only_authority_publication_not_testpass(self):
        for state in ['pending','failure','error']:
            with self.subTest(state=state),self.assertRaises(ValueError):r.select_status([dict(self.status,state=state)],self.obs)
        recipe=self.recipe();self.assertIn('NEVER',recipe['meaning']);self.assertEqual('success',recipe['statusPost']['body']['state'])
    def test_capability_wrong_boot_harness_packet_scope_extra_field(self):
        for key,value in [('hostBootId','00000000-0000-0000-0000-000000000000'),('harnessSha256','d'*64),('packetManifestSha256','e'*64),('scope','strict-release-build'),('rootIssued',False),('extra',1)]:
            cap=dict(self.cap);cap[key]=value;raw=r.canonical(cap)
            with self.subTest(key=key),self.assertRaises(ValueError):r.validate_capability(raw,r.digest(raw),self.obs,self.clock,H)
    def test_stale_overlong_capability_and_cleanup_reserve(self):
        for issued,expires in [(self.clock-dt.timedelta(seconds=1),self.clock+dt.timedelta(seconds=500)),(self.clock,self.clock+dt.timedelta(seconds=601)),(self.clock,self.clock+dt.timedelta(seconds=119))]:
            cap=dict(self.cap,issuedUtc=issued.isoformat(),expiresUtc=expires.isoformat());raw=r.canonical(cap)
            with self.subTest(expires=expires),self.assertRaises(ValueError):r.validate_capability(raw,r.digest(raw),self.obs,self.clock,H)
    def test_capability_sha_alone_no_creator_permission(self):
        self.status['creator']={};self.fail_receive();self.assertEqual(1,len(self.calls))
    def test_capability_blob_git_identity_and_raw_digest_differ(self):
        self.blobraw=b'{}';self.fail_receive()
    def test_rate_permission403_fails_closed_with_terminal(self):
        self.transport=lambda *args:(403,b'{}');self.fail_receive()
    def test_http_response_oversize_and_nonbytes_refused(self):
        for raw in [b' '* (r.MAX_HTTP_BYTES+1),'{}']:
            api=r.PublicGitHub(lambda *_:(200,raw))
            with self.subTest(rawtype=type(raw)),self.assertRaises(ValueError):api.statuses(self.obs['sourceHead'])
    def test_oversize_paginated_status_collection_no_uniqueness_claim(self):
        self.rows=[dict(self.status,context='other')]*100
        self.fail_receive();self.assertEqual(2,len(self.calls))
    def test_timeout_finite_poll_budget_and_intervals_terminal(self):
        self.rows=[];terminal=self.fail_receive(TimeoutError)
        self.assertEqual(18,len(self.calls));self.assertEqual(360,self.mono);self.assertFalse(terminal['kernelExecuted'])
    def test_cancel_before_poll_no_invoke_and_false_terminal(self):
        terminal=self.fail_receive(cancelled=lambda:True);self.assertFalse(terminal['kernelExecuted']);self.assertEqual([],self.calls)
    def test_fresh_caps_reobserve_floor_runner_boot_kernel(self):
        for key,value in [('freePhysicalKiB',4194303),('hostBootId','00000000-0000-0000-0000-000000000000'),('kernel','foreign')]:
            current=self.observe();current[key]=value
            with self.subTest(key=key),self.assertRaises(ValueError):r.reobserve(self.obs,current,self.clock)
        current=self.observe();current['runnerWorker']=dict(self.host['runnerWorker'],startTicks=124)
        with self.assertRaises(ValueError):r.reobserve(self.obs,current,self.clock)
    def test_reobserve_after_status_prevents_call_on_lost_allocation(self):
        invoked=[];count=[0]
        def moved():
            count[0]+=1;host=self.observe()
            if count[0]>=3:host['runnerWorker']=dict(host['runnerWorker'],pid=11)
            return host
        with tempfile.TemporaryDirectory() as tmp,self.assertRaises(ValueError):
            r.receive(self.obs,r.PublicGitHub(self.transport),moved,lambda *_:invoked.append(True) or 0,Path(tmp),clock=lambda:self.clock,harness_sha=H)
        self.assertEqual([],invoked)
    def test_need_reseal_refuses_before_api_or_kernel(self):
        with tempfile.TemporaryDirectory() as tmp,self.assertRaisesRegex(ValueError,'NEEDS_RESEAL'):
            r.receive(self.obs,r.PublicGitHub(self.transport),self.observe,lambda *_:self.fail('kernel invoked'),Path(tmp),harness_sha='NEEDS_RESEAL')
        self.assertEqual([],self.calls)
    def test_finally_false_on_invoke_exception(self):
        def failed(*_):raise RuntimeError('synthetic kernel failed')
        terminal=self.fail_receive(RuntimeError,invoke=failed);self.assertTrue(terminal['kernelExecuted']);self.assertFalse(terminal['kernelQualified'])
    def recipe(self):
        raw=r.canonical(self.obs)
        return r.publisher_recipe(self.raw,self.sha,raw,r.digest(raw),{'id':r.ROOT_USER_ID,'login':'natthapolvanasrivilai','type':'User'},self.clock,H)
    def test_publisher_recipe_external_only_exact_body_no_cap_mint(self):
        recipe=self.recipe();self.assertTrue(recipe['recipeOnly']);self.assertFalse(recipe['capabilityCreated']);self.assertEqual(0,recipe['apiWritesPerformed'])
        self.assertEqual(self.raw,base64.b64decode(recipe['blobPost']['body']['content']));self.assertEqual(self.obs['statusContext'],recipe['statusPost']['body']['context'])
    def test_publisher_external_observation_hash_and_issuer_spoof_refused(self):
        raw=r.canonical(self.obs)
        for trusted,issuer in [('d'*64,{'id':r.ROOT_USER_ID,'login':'natthapolvanasrivilai','type':'User'}),(r.digest(raw),{'id':r.ROOT_USER_ID,'login':'bot','type':'Bot'})]:
            with self.subTest(issuer=issuer),self.assertRaises(ValueError):r.publisher_recipe(self.raw,self.sha,raw,trusted,issuer,self.clock,H)
    def test_job_api_only_fixed_get_routes_no_token_or_post(self):
        api=r.PublicGitHub(lambda *_:(200,b'{}'))
        self.assertFalse(hasattr(api,'post'))
        for path in ['/repos/'+r.REPOSITORY+'/statuses/'+self.obs['sourceHead'],'/repos/foreign/repo/git/blobs/'+self.oid,'https://evil']:
            with self.subTest(path=path),self.assertRaises(ValueError):api.get(path)
    def test_source_manifest_identity_harness_missing_does_not_import(self):
        with tempfile.TemporaryDirectory() as tmp,self.assertRaises((ValueError,FileNotFoundError)):r.capture_identity(Path(tmp))
    def test_json_alias_duplicate_and_nonfinite_refused(self):
        for raw in [b'{"x":1,"x":2}',b'{"x":NaN}']:
            with self.subTest(raw=raw),self.assertRaises(ValueError):r.parse(raw)
    def test_workflow_additions_remove_to_exact_original_bytes(self):
        folder=Path(__file__).parent;draft=(folder/'customer-source-intake.draft.yml').read_text()
        restored=draft[:draft.index('\n  kernel-only-same-allocation:')].rstrip()+'\n'
        start=restored.index('    inputs:\n');end=restored.index('\npermissions:',start);restored=restored[:start]+restored[end:]
        restored=restored.replace("    if: github.event_name != 'workflow_dispatch' || inputs.source_mode != 'kernel-only'\n",'')
        for entry in ('scripts/kernel_root_route.py','scripts/publisher_recipe.py','scripts/test_kernel_root_route.py','scripts/external_unit_owner.py','scripts/test_external_unit_owner.py','scripts/customer-source-intake.*','scripts/customer-kernel-source/**','scripts/customer-kernel-packet/**'):restored=restored.replace("      - '"+entry+"'\n",'')
        start=restored.index('      - name: Compile route and captured R7;');end=restored.index('      - name: Create private disposable source parent',start);restored=restored[:start]+restored[end:]
        for log in ('customer-kernel-compile.log','customer-kernel-route-controls.log','customer-kernel-r7-mocked-controls.log'):restored=restored.replace('            ${{ runner.temp }}/'+log+'\n','')
        self.assertEqual((folder/'customer-source-intake.preimage.yml').read_bytes(),restored.encode())
        self.assertIn('default: source',draft);self.assertIn('persist-credentials: false',draft)
        self.assertNotIn('GITHUB_TOKEN',draft[draft.index('  kernel-only-same-allocation:'):])
        source=draft[draft.index('  source-controls:'):draft.index('  kernel-only-same-allocation:')]
        self.assertIn('-m unittest test_kernel_root_route test_external_unit_owner -v',source)
        self.assertIn('-m unittest fault_controls -v',source);self.assertIn('assert len(paths) == 11',source)
        self.assertIn('test ! -e outputs',source);self.assertIn("trap 'rmdir -- outputs/customer-native-lifecycle-qualification-20261008-r7 outputs' EXIT",source)
        self.assertEqual(3,source.count('timeout --signal=TERM --kill-after=5s 20s'))
        for log in ('customer-kernel-compile.log','customer-kernel-route-controls.log','customer-kernel-r7-mocked-controls.log'):self.assertEqual(2,source.count(log))
        manual=draft[draft.index('  kernel-only-same-allocation:'):]
        self.assertLess(manual.index('Independently settle only the exact external kernel invocation'),manual.index('Preserve actual authority/kernel/cleanup evidence'))
        self.assertIn('if: always()',manual);self.assertIn('120s python3 -B scripts/external_unit_owner.py settle',manual)
    def test_absolute_deadline_no_renewal_and_stale_status(self):
        changed=copy.deepcopy(self.obs);changed['allocationDeadlineUtc']=(self.clock+dt.timedelta(seconds=601)).isoformat()
        with self.assertRaises(ValueError):r.validate_observation(changed,self.clock)
        before=(self.clock-dt.timedelta(seconds=1)).isoformat()
        with self.assertRaises(ValueError):r.select_status([dict(self.status,created_at=before)],self.obs)

    def test_primary_exception_preserved_if_terminal_write_fails(self):
        original=LookupError('actual API failure');old=r.save_new
        def fail(path,row):
            if path.name=='route-terminal.json':raise OSError('fsync fault')
            return old(path,row)
        with tempfile.TemporaryDirectory() as tmp,patch.object(r,'save_new',side_effect=fail):
            api=r.PublicGitHub(lambda *_:(_ for _ in ()).throw(original))
            with self.assertRaises(LookupError) as caught:r.receive(self.obs,api,self.observe,lambda *_:0,Path(tmp),clock=lambda:self.clock,harness_sha=H)
            self.assertIs(caught.exception,original);self.assertTrue(any('fsync fault' in note for note in original.__notes__))
    def test_late_cancel_after_kernel_call_fails_closed(self):
        state={'cancelled':False}
        def invoke(*_):state['cancelled']=True;return 0
        self.fail_receive(invoke=invoke,cancelled=lambda:state['cancelled'])
    def test_cancel_during_terminal_fsync_invalidates_candidate(self):
        state={'cancelled':False};old=r.save_new
        def save(path,row):
            old(path,row)
            if path.name=='route-terminal.json':state['cancelled']=True
        with tempfile.TemporaryDirectory() as tmp,patch.object(r,'save_new',side_effect=save):
            with self.assertRaises(ValueError):self.receive(Path(tmp),cancelled=lambda:state['cancelled'])
            self.assertTrue(r.parse((Path(tmp)/'route-invalidation.json').read_bytes())['terminalInvalid'])
    def test_all_receipt_writes_fail_keep_primary_and_secondary(self):
        original=RuntimeError('original');api=r.PublicGitHub(lambda *_:(_ for _ in ()).throw(original))
        with tempfile.TemporaryDirectory() as tmp,patch.object(r,'save_new',side_effect=OSError('disk')):
            with self.assertRaises(RuntimeError) as caught:r.receive(self.obs,api,self.observe,lambda *_:0,Path(tmp),clock=lambda:self.clock,harness_sha=H)
            self.assertIs(caught.exception,original);self.assertEqual(2,len(original.__notes__))
    def test_shared_service_census_is_recorded_not_treated_as_sdk_ownership(self):
        host=dict(self.host,nativeProcesses=[{'pid':3,'executable':'/usr/bin/dockerd','ownership':'unknown'}])
        r.validate_observation(dict(self.obs,host=host),self.clock)
        r.reobserve(self.obs,host,self.clock)
    def test_invalidation_write_failure_cannot_return_qualification(self):
        state={'cancelled':False};old=r.save_new
        def save(path,row):
            if path.name=='route-invalidation.json':raise OSError('invalidation fsync fault')
            old(path,row)
            if path.name=='route-terminal.json':state['cancelled']=True
        with tempfile.TemporaryDirectory() as tmp,patch.object(r,'save_new',side_effect=save):
            with self.assertRaises(ValueError) as caught:self.receive(Path(tmp),cancelled=lambda:state['cancelled'])
            self.assertTrue(any('invalidation fsync fault' in note for note in caught.exception.__notes__))
            self.assertFalse((Path(tmp)/'route-invalidation.json').exists())
            # Durable terminal alone can be true; raised outer outcome vetoes it.
            self.assertTrue(r.parse((Path(tmp)/'route-terminal.json').read_bytes())['kernelQualified'])
    def test_native_census_required_at_observation_and_reobservation(self):
        for update in [{'censusComplete':False},{'competingNativeProcesses':[{'pid':1}]}]:
            host=dict(self.host,**update);obs=dict(self.obs,host=host)
            with self.assertRaises(ValueError):r.validate_observation(obs,self.clock)
            with self.assertRaises(ValueError):r.reobserve(self.obs,host,self.clock)

if __name__=='__main__':unittest.main()
