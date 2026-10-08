"""Pure synthetic guardian transport controls; no sockets, SDK, grant or helper process."""
import copy
import datetime as dt
import json
from pathlib import Path
import tempfile
import unittest
from unittest.mock import patch
import customer_owned_build as owned
import customer_build_guardian as guardian
import test_customer_owned_build as controls

class GuardianBackend(controls.Backend):
    def __init__(self,test):
        super().__init__(test); self.events=[]; self.delete_failures=0; self.delay=0; self.after_create=None
    def request(self,method,path,body=None,headers=None):
        if path.startswith('/events?'):
            self.calls.append((method,path)); return 200,b'\n'.join(json.dumps(row).encode() for row in self.events)
        if path.startswith('/containers/create?'):
            failed=self.fail_create_ack; self.fail_create_ack=False
            response=super().request(method,path,body,headers)
            attrs=dict(json.loads(body)['Labels'],name='customer-build-'+self.test.grant['runId'],image=self.test.stage['sdkImage']['reference'])
            self.events.append({'Type':'container','Action':'create','Actor':{'ID':self.item['Id'],'Attributes':attrs},
                                'timeNano':int(self.test.clock.timestamp()*1000000000)})
            self.test.sleep(self.delay)
            if self.after_create: self.after_create()
            self.fail_create_ack=failed
            if failed: raise TimeoutError('synthetic lost acknowledgement')
            return response
        if method=='DELETE' and self.delete_failures:
            self.calls.append((method,path)); self.delete_failures-=1; return 500,b'{}'
        return super().request(method,path,body,headers)

class GuardianControls(unittest.TestCase):
    def setUp(self):
        controls.BuildControls.setUp(self)
        self.alive=True; self.ledger=Path(self.temp.name)/'guardian'; self.backend=GuardianBackend(self)
        self.c=self.construct()
    def construct(self):
        return guardian.Custodian(self.backend,owned,self.stage,self.grant,self.grant['rootGrantSha256'],
                                  self.ledger,lambda:self.alive,lambda:self.clock,lambda:self.mono)
    def sleep(self,seconds):
        self.mono+=seconds; self.clock+=dt.timedelta(seconds=seconds)
    def admission(self,phase,stage,grant):
        return {'admitted':True,'trustedRootGrantVerified':True,'checkedUtc':self.clock.isoformat(),
                'freePhysicalKiB':4194304,'nativeProcesses':[],'rootGrantSha256':self.grant['rootGrantSha256']}
    def create(self): return self.c.create(self.admission)
    def run_guardian(self,**kwargs): return self.c.run(sleep=self.sleep,**kwargs)
    def mutations(self): return [x for x in self.backend.calls if x[0]!='GET']
    def test_durable_actual201_before_reply_and_before_start(self):
        proof=self.create(); self.assertEqual('actual-http-create',proof['receiptKind']); self.assertEqual(201,proof['httpStatus'])
        self.assertEqual('c'*64,proof['containerId']); self.assertTrue((self.ledger/'custody.json').is_file())
        self.assertEqual(proof['createdSpec']['Created'],proof['actualCreatedRaw'])
        fresh=self.c.ensure_custody(self.c.expected,proof); self.assertTrue(fresh['verified'])
        self.assertFalse(any('/start' in p for _,p in self.mutations()))
    def test_delayed201_parent_dies_before_reply_still_owned_cleaned(self):
        self.backend.delay=5; self.backend.after_create=lambda:setattr(self,'alive',False)
        self.create(); result=self.run_guardian(); self.assertTrue(result['cleanupVerified']); self.assertEqual('parent-identity-lost',result['reason'])
    def test_lost_parent_reply_new_custodian_reads_actual_receipt(self):
        proof=self.create(); successor=self.construct(); self.assertEqual(proof,successor.recover())
        self.alive=False; self.assertTrue(successor.run(sleep=self.sleep)['cleanupVerified'])
    def test_lost_ack_unique_actual_create_event_preserves_receipt_kind(self):
        self.backend.fail_create_ack=True; proof=self.create()
        self.assertEqual('actual-daemon-create-event',proof['receiptKind']); self.assertIsNone(proof['httpStatus'])
        self.assertFalse((self.ledger/'create-response.json').exists()); self.assertTrue((self.ledger/'create-event-response.json').is_file())
    def test_lost_ack_noevent_never_inventory_adopted(self):
        self.backend.fail_create_ack=True; self.backend.after_create=lambda:self.backend.events.clear()
        with self.assertRaises(guardian.CustodyError): self.create()
        self.assertIsNone(self.c.owner.cid); self.alive=False; result=self.run_guardian()
        self.assertFalse(result['cleanupVerified']); self.assertTrue(result['allocationUnresolved'])
        self.assertFalse(any(method=='DELETE' for method,_ in self.mutations()))
    def test_lost_ack_duplicate_actual_events_ambiguous(self):
        self.backend.fail_create_ack=True
        self.backend.after_create=lambda:self.backend.events.append(copy.deepcopy(self.backend.events[0]))
        with self.assertRaises(guardian.CustodyError):self.create()
        self.assertIsNone(self.c.owner.cid)
    def test_recovery_same_daemon_must_be_fresh(self):
        self.create(); self.backend.bad_daemon=True
        with self.assertRaises(owned.BuildError):self.c.recover()
        self.assertFalse(any(method=='DELETE' for method,_ in self.mutations()))
    def test_event_before_intent_not_causal(self):
        self.backend.fail_create_ack=True
        self.backend.after_create=lambda:self.backend.events[0].update(timeNano=int((self.clock-dt.timedelta(seconds=1)).timestamp()*1e9))
        with self.assertRaises(guardian.CustodyError):self.create()
    def test_event_foreign_run_image_name_and_id_rejected(self):
        for key,value in [('codexrun.run','f'*32),('image','foreign'),('name','foreign')]:
            with self.subTest(key=key),tempfile.TemporaryDirectory() as tmp:
                self.ledger=Path(tmp)/'guardian'; self.backend=GuardianBackend(self); self.c=self.construct()
                self.backend.fail_create_ack=True
                self.backend.after_create=lambda k=key,v=value:self.backend.events[0]['Actor']['Attributes'].update({k:v})
                with self.assertRaises(guardian.CustodyError):self.create()
                self.assertIsNone(self.c.owner.cid)
    def test_exact_inspect_config_drift_cannot_seal_or_delete(self):
        self.backend.after_create=lambda:self.backend.item['HostConfig'].update(Memory=1)
        with self.assertRaises(owned.BuildError):self.create()
        self.alive=False; result=self.run_guardian(); self.assertFalse(result['cleanupVerified'])
        self.assertFalse(any(method=='DELETE' for method,_ in self.mutations()))
    def test_unstarted_sdk_exact_nonforce_cleanup(self):
        self.create(); self.alive=False; result=self.run_guardian(); self.assertTrue(result['cleanupVerified'])
        self.assertFalse(any('/stop?' in p for _,p in self.mutations()))
        self.assertIn(('DELETE','/containers/'+'c'*64+'?force=false&v=false'),self.mutations())
    def test_running_sdk_stops_before_delete(self):
        self.create(); self.backend.item['State']['Running']=True; self.backend.force_running=True
        self.alive=False; result=self.run_guardian(); self.assertTrue(result['cleanupVerified'])
        paths=[p for _,p in self.mutations()]; self.assertLess(next(i for i,p in enumerate(paths) if '/stop?' in p),next(i for i,p in enumerate(paths) if 'force=false' in p))
    def test_cleanup_retry_one_global_lease_receipts_independent(self):
        self.create(); self.backend.delete_failures=1; self.alive=False
        result=self.run_guardian(); self.assertTrue(result['cleanupVerified'])
        self.assertTrue((self.ledger/'cleanup-attempt-0/cleanup-delete.json').exists())
        self.assertTrue((self.ledger/'cleanup-attempt-1/cleanup-delete.json').exists())
        self.assertTrue(all(x<=120 for x in self.backend.cleanup_factories))
    def test_root_expiry_triggers_separate_cleanup_not_memory_gate(self):
        self.create(); self.clock=owned.utc(self.grant['expiresUtc'])
        result=self.run_guardian(); self.assertTrue(result['cleanupVerified']); self.assertEqual('Root-expiry',result['reason'])
    def test_cancel_trigger_finite_cleanup(self):
        self.create(); result=self.run_guardian(cancelled=lambda:True); self.assertTrue(result['cleanupVerified']); self.assertEqual('cancel',result['reason'])
    def test_parent_completed_cleanup_requires_actual_absence(self):
        self.create(); self.backend.absent=True
        self.assertTrue(self.run_guardian(parent_cleanup_complete=lambda:True)['cleanupVerified'])
    def test_foreign_exec_ids_preserved_terminal_failure(self):
        self.create(); self.backend.item['ExecIDs']=['foreign-exec']; self.alive=False
        result=self.run_guardian(); self.assertFalse(result['cleanupVerified']); self.assertEqual('c'*64,result['containerId'])
        self.assertFalse(any(method=='DELETE' for method,_ in self.mutations()))
    def test_parent_identity_lost_beforecreate_no_post(self):
        self.alive=False
        with self.assertRaises(guardian.CustodyError):self.create()
        self.assertEqual([],self.mutations())
    def test_no_duplicate_create_and_terminal_immutable(self):
        self.create()
        with self.assertRaises(guardian.CustodyError):self.create()
        self.alive=False;self.run_guardian()
        with self.assertRaises(FileExistsError):self.run_guardian()
    def test_delayed_actual201_after_expiry_is_cleanup_only(self):
        self.backend.delay=1201; proof=self.create()
        with self.assertRaises(guardian.CustodyError):self.c.ensure_custody(self.c.expected,proof)
        self.assertTrue(self.run_guardian()['cleanupVerified'])
        self.assertFalse(any('/start' in p for _,p in self.mutations()))
    def test_durable_create_response_failure_retains_actual201(self):
        original=self.c.save
        def failed(filename,row):
            if filename=='create-response.json':raise OSError('synthetic disk failure')
            return original(filename,row)
        with patch.object(self.c,'save',failed):
            with self.assertRaises(OSError):self.create()
        self.assertEqual('c'*64,self.c.owner.cid)
        proof=self.c.recover();self.assertEqual('actual-http-create',proof['receiptKind'])
        self.assertEqual(201,proof['httpStatus'])
    def test_actual_identity_receipt_failure_cannot_reply_custody(self):
        original=self.c.save
        def failed(filename,row):
            if filename=='created-identity.json':raise OSError('synthetic disk failure')
            return original(filename,row)
        with patch.object(self.c,'save',failed):
            with self.assertRaises(OSError):self.create()
        self.assertFalse((self.ledger/'custody.json').exists())
        proof=self.c.recover();self.assertEqual('actual-http-create',proof['receiptKind'])
    def test_event_short_id_refused_before_adoption(self):
        self.backend.fail_create_ack=True
        self.backend.after_create=lambda:self.backend.events[0]['Actor'].update(ID='c'*12)
        with self.assertRaises(guardian.CustodyError):self.create()
        self.assertIsNone(self.c.owner.cid)
    def test_cleanup_deadline_not_extended_by_retry_factory(self):
        self.create();self.alive=False
        self.backend.delete_failures=100
        original=self.backend.request
        def slow(*args,**kwargs):
            if args[0]=='DELETE':self.sleep(119)
            return original(*args,**kwargs)
        with patch.object(self.backend,'request',slow):result=self.run_guardian()
        self.assertFalse(result['cleanupVerified']);self.assertTrue(result['allocationUnresolved'])
        self.assertTrue(all(x<=120 for x in self.backend.cleanup_factories))
    def test_delayed_event_recovery_retries_inside_same_cleanup_lease(self):
        self.backend.fail_create_ack=True
        original=self.backend.request;event_reads=[]
        def delayed(method,path,*args,**kwargs):
            if path.startswith('/events?'):
                event_reads.append(path)
                if len(event_reads)<3:return 200,b''
            return original(method,path,*args,**kwargs)
        with patch.object(self.backend,'request',delayed):
            with self.assertRaises(guardian.CustodyError):self.create()
            self.alive=False;result=self.run_guardian()
        self.assertTrue(result['cleanupVerified']);self.assertEqual(3,len(event_reads))
        self.assertEqual('actual-daemon-create-event',self.c.custody['receiptKind'])
    def test_root_expired_original_backend_replaced_before_recovery(self):
        self.create();self.clock=owned.utc(self.grant['expiresUtc'])
        original=self.backend;replacement=GuardianBackend(self);replacement.item=copy.deepcopy(original.item)
        def expired(*args,**kwargs):raise TimeoutError('ordinary Root backend expired')
        with patch.object(original,'request',expired),patch.object(original,'json',expired),\
             patch.object(original,'cleanup_backend_factory',lambda deadline:replacement):
            result=self.run_guardian()
        self.assertTrue(result['cleanupVerified']);self.assertTrue(replacement.absent)
    def test_preCREATE_admission_refusal_proves_zero_dispatch_guardian_exit(self):
        refused=lambda *args:dict(self.admission(*args),admitted=False)
        with self.assertRaises(owned.BuildError):self.c.create(refused)
        calls=list(self.backend.calls);result=self.run_guardian()
        self.assertTrue(result['cleanupVerified']);self.assertFalse(result['allocationUnresolved'])
        self.assertFalse(result['createDispatched']);self.assertEqual('preCREATE-no-dispatch',result['reason'])
        self.assertEqual(calls,self.backend.calls);self.assertEqual([],self.mutations())
        self.assertFalse((self.ledger/'create-intent.json').exists())
    def test_durable_intent_without_dispatch_is_not_proven_zero(self):
        original=self.backend.json
        reads=[]
        def failed_after_intent(*args,**kwargs):
            reads.append(args)
            if (self.ledger/'create-intent.json').exists():raise TimeoutError('synthetic predispatch daemon read failed')
            return original(*args,**kwargs)
        with patch.object(self.backend,'json',failed_after_intent):
            with self.assertRaises(TimeoutError):self.create()
        self.assertFalse(self.c.create_dispatched);self.assertFalse(self.c.proven_zero_dispatch())
        self.alive=False;result=self.run_guardian()
        self.assertFalse(result['cleanupVerified']);self.assertTrue(result['allocationUnresolved'])
    def test_uncertain_parent_observation_still_cleans_known_custody(self):
        self.create()
        def uncertain():raise PermissionError('synthetic proc identity unavailable')
        self.c.parent_alive=uncertain
        result=self.run_guardian()
        self.assertTrue(result['cleanupVerified']);self.assertEqual('parent-observation-uncertain',result['reason'])
        self.assertTrue(self.backend.absent)
    def test_uncertain_cancel_observation_still_cleans_known_custody(self):
        self.create()
        def uncertain():raise ValueError('synthetic malformed foreign cancellation')
        result=self.run_guardian(cancelled=uncertain)
        self.assertTrue(result['cleanupVerified']);self.assertEqual('cancel-observation-uncertain',result['reason'])
        self.assertTrue(self.backend.absent)
    def test_uncertain_parent_cleanup_observation_still_cleans_known_custody(self):
        self.create()
        def uncertain():raise ValueError('synthetic ambiguous parent completion')
        result=self.run_guardian(parent_cleanup_complete=uncertain)
        self.assertTrue(result['cleanupVerified']);self.assertEqual('parent-cleanup-observation-uncertain',result['reason'])
        self.assertTrue(self.backend.absent)
    def test_durable_causal_event_readback_exact_object_hash_and_query200(self):
        self.backend.fail_create_ack=True;proof=self.create()
        receipt=guardian.parse((self.ledger/'create-event-response.json').read_bytes())
        self.assertEqual(self.backend.events[0],receipt['actualCreateEvent'])
        self.assertEqual(owned.digest(owned.encode(receipt['actualCreateEvent'])),receipt['eventSha256'])
        self.assertEqual(200,receipt['eventQueryHttpStatus']);self.assertIsNone(receipt['httpStatus'])
        self.assertEqual(receipt['actualCreateEvent'],proof['actualCreateEvent'])
        self.assertEqual('c'*64,receipt['actualCreateEvent']['Actor']['ID'])
    def test_unexpected_event_attribute_not_copied_into_receipt(self):
        self.backend.fail_create_ack=True
        self.backend.after_create=lambda:self.backend.events[0]['Actor']['Attributes'].update(secret='synthetic unexpected attribute')
        with self.assertRaises(guardian.CustodyError):self.create()
        self.assertFalse((self.ledger/'create-event-response.json').exists())
    def test_deadline_clamp_refuses_io_after_global_expiry(self):
        wrapper=guardian.DeadlineBackend(self.backend,1,lambda:self.mono); self.mono=2
        with self.assertRaises(guardian.CustodyError):wrapper.json('GET','/info')
        self.assertEqual([],self.backend.calls)

if __name__=='__main__':unittest.main()
