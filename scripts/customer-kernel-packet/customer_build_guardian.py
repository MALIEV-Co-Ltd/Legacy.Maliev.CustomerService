"""Independent, finite SDK CREATE custodian. SOURCE ONLY until externally admitted.

Caller validates actual child/parent registration before construction and supplies an
identity-aware parent_alive callback. Backend IO must have finite request deadlines.
No inventory/name adoption: only a durable actual201 or bounded causal CREATE event.
"""
from __future__ import annotations
import datetime as dt
import json
from pathlib import Path
import re
import time
from urllib.parse import quote

class CustodyError(ValueError): pass

def need(value, message):
    if not value: raise CustodyError(message)

def parse(raw):
    def pairs(rows):
        result = {}
        for key, value in rows:
            need(key not in result, 'Duplicate JSON key'); result[key] = value
        return result
    return json.loads(raw, object_pairs_hook=pairs, parse_constant=lambda _: (_ for _ in ()).throw(CustodyError('Nonfinite JSON')))

class Custodian:
    def __init__(self, backend, owned_module, stage, grant, rootGrantSha, guardian_directory,
                 parent_alive, clock=None, monotonic=None):
        self.backend, self.owned, self.stage = backend, owned_module, stage
        self.grant = dict(grant); self.root_sha = rootGrantSha
        need(re.fullmatch(r'[a-f0-9]{64}', rootGrantSha or '') is not None
             and grant.get('rootGrantSha256') == rootGrantSha, 'External trusted Root byte binding required')
        need(callable(parent_alive), 'Actual parent identity callback required')
        self.parent_alive = parent_alive
        self.clock = clock or (lambda: dt.datetime.now(dt.timezone.utc))
        self.monotonic = monotonic or time.monotonic
        self.directory = Path(guardian_directory); owned_module.reject_links(self.directory)
        self.directory.mkdir(parents=True, exist_ok=True, mode=0o700)
        need(self.directory.is_dir(), 'Ordinary guardian directory required')
        self.admission = None; self.intent = None; self.custody = None; self.actual_response = None; self.create_dispatched = False
        for index in range(16):
            ledger = self.directory / ('custody-sdk' if index == 0 else 'custody-sdk-' + str(index))
            if not ledger.exists(): break
        else: raise CustodyError('Finite guardian adapter roster exhausted')
        self.owner = owned_module.OwnedBuild(backend, stage, self.grant, ledger, self._admit)
        self.name, self.expected = owned_module.make_create_request(stage, self.grant)
        self.owner.name, self.owner.expected = self.name, self.expected
        self.expected_digest = owned_module.digest(owned_module.encode(self.expected))

    def _admit(self, phase, stage, grant):
        need(callable(self.admission), 'Fresh independently verified admission callback required')
        return self.admission(phase, stage, grant)

    def save(self, filename, row):
        self.owned.atomic_new(self.directory / filename, row)

    def load(self, filename):
        path = self.directory / filename; self.owned.reject_links(path)
        need(path.is_file() and path.stat().st_size <= 65536, 'Bounded durable receipt required')
        return parse(path.read_bytes())

    def binding(self, row):
        need(row.get('runId') == self.grant['runId'] and row.get('daemonId') == self.grant['daemonId']
             and row.get('name') == self.name and row.get('requestSha256') == self.expected_digest
             and row.get('rootGrantSha256') == self.root_sha
             and row.get('stageSha256') == self.grant['stageSha256'], 'Guardian durable intent drift')
        issued = self.owned.utc(row['issuedUtc'])
        need(self.owned.utc(self.grant['issuedUtc']) <= issued <= self.owned.utc(self.grant['expiresUtc']), 'Intent outside Root interval')
        return row

    def create(self, admission_callback):
        return self.create_owned(self.expected, admission_callback)

    def create_owned(self, expected, admission_callback=None):
        need(expected == self.expected, 'Guardian exact immutable create spec required')
        need(not (self.directory / 'create-intent.json').exists(), 'No duplicate CREATE allowed')
        self.admission = admission_callback or self.admission
        self.owner.validate_grant(); self.owner.daemon(self.backend); self.owner.image()
        self.owner.admit('create')
        need(self.parent_alive() is True and self.clock() < self.owned.utc(self.grant['expiresUtc']), 'Parent identity/lease lost before CREATE')
        self.intent = {'schemaVersion':1,'runId':self.grant['runId'],'daemonId':self.grant['daemonId'],
                       'issuedUtc':self.clock().isoformat(),'name':self.name,'requestSha256':self.expected_digest,
                       'rootGrantSha256':self.root_sha,'stageSha256':self.grant['stageSha256']}
        self.save('create-intent.json', self.intent)
        self.owner.daemon(self.backend)
        try:
            self.create_dispatched=True
            status, raw = self.owner.request(self.backend, 'POST', '/containers/create?name=' + self.name,
                                            self.owned.encode(self.expected), {'Content-Type':'application/json'})
        except Exception:
            return self.recover()
        response = parse(raw)
        need(status == 201 and self.owned.IDS.fullmatch(response.get('Id','')) is not None, 'Actual full CREATE201 required; uncertainty retained')
        record = {'receiptKind':'actual-http-create','httpStatus':status,'containerId':response['Id'],
                  'daemonId':self.grant['daemonId'],'intentSha256':self.owned.digest(self.owned.encode(self.intent))}
        # Retain the actual returned full ID even if durable publication fails.
        self.actual_response=record; self.owner.cid=record['containerId']
        self.save('create-response.json', record)
        return self._seal(record)

    def _seal(self, response):
        need(response.get('daemonId') == self.grant['daemonId']
             and response.get('intentSha256') == self.owned.digest(self.owned.encode(self.intent))
             and self.owned.IDS.fullmatch(response.get('containerId','')) is not None, 'Actual response binding drift')
        need((response.get('receiptKind') == 'actual-http-create' and response.get('httpStatus') == 201)
             or (response.get('receiptKind') == 'actual-daemon-create-event' and response.get('httpStatus') is None), 'Causal receipt kind invalid')
        self.owner.cid = response['containerId']
        item = self.owner.inspect(self.backend)
        need(item is not None, 'Created identity unavailable; do not certify absence')
        created = self.owned.utc(item['Created'])
        need(self.owned.utc(self.intent['issuedUtc']) <= created <= self.clock(), 'Actual Created precedes intent or future')
        spec = {'Id':item['Id'],'Created':item['Created'],'Image':item['Image'],'Name':item['Name'],
                'configSha256':self.owned.digest(self.owned.encode(item['Config'])),
                'hostConfigSha256':self.owned.digest(self.owned.encode(item['HostConfig']))}
        path = self.directory / 'created-identity.json'
        if path.exists(): need(self.load('created-identity.json') == spec, 'Immutable actual created spec drift')
        else: self.save('created-identity.json', spec)
        self.owner.spec = spec
        receipt = dict(response, actualCreatedRaw=spec['Created'],createdSpec=spec,
                       expectedConfigDigest=self.expected_digest,rootGrantSha256=self.root_sha,guardianDirectory=str(self.directory))
        if (self.directory / 'custody.json').exists(): need(self.load('custody.json') == receipt, 'Immutable custody drift')
        else: self.save('custody.json', receipt)
        self.custody = receipt
        return receipt

    def recover(self):
        self.intent = self.binding(self.load('create-intent.json'))
        self.owner.daemon(self.backend); self.owner.image()
        existing = [name for name in ('create-response.json','create-event-response.json') if (self.directory/name).exists()]
        need(len(existing) <= 1, 'Ambiguous durable create receipts')
        if existing: return self._seal(self.load(existing[0]))
        if self.actual_response is not None:
            self.save('create-response.json',self.actual_response)
            return self._seal(self.actual_response)
        until = self.clock(); since = self.owned.utc(self.intent['issuedUtc'])
        need(until >= since, 'Recovery clock precedes intent')
        filters = {'type':['container'],'event':['create'],'label':['codexrun.run=' + self.grant['runId']]}
        path = '/events?since=' + quote(str(since.timestamp()),safe='') + '&until=' + quote(str(until.timestamp()),safe='') + '&filters=' + quote(json.dumps(filters,separators=(',',':')),safe='')
        self.owner.daemon(self.backend)
        status, raw = self.owner.request(self.backend,'GET',path)
        need(status == 200 and len(raw) <= 1024*1024, 'Bounded same-daemon events query required')
        rows = raw.splitlines(); need(len(rows) <= 256, 'Finite CREATE event roster required')
        candidates = []
        for rawrow in rows:
            event = parse(rawrow); actor = event.get('Actor') or {}; attributes = actor.get('Attributes') or {}
            need(type(event.get('timeNano')) is int, 'Actual nanosecond CREATE event timestamp required')
            stamp = event['timeNano']/1000000000
            if event.get('Type') != 'container' or event.get('Action') != 'create': continue
            if not (since.timestamp() <= stamp <= until.timestamp()): continue
            if attributes.get('name') != self.name or attributes.get('image') != self.expected['Image']: continue
            if any(attributes.get(k) != v for k,v in self.expected['Labels'].items()): continue
            need(self.owned.IDS.fullmatch(actor.get('ID','')) is not None, 'Actual event full ID required')
            candidates.append(event)
        need(len(candidates) == 1, 'No unique causal CREATE event; allocation remains unresolved')
        self.owner.daemon(self.backend)
        event = candidates[0]
        # Retain actual own SDK causal metadata for independent readback.
        # Refuse unexpected attributes rather than copying arbitrary daemon data.
        allowed_attributes=set(self.expected['Labels']) | {'name','image'}
        need(set(event['Actor']['Attributes']) <= allowed_attributes, 'Unexpected causal CREATE event attributes refused')
        need(set(event) <= {'Type','Action','Actor','time','timeNano','scope','status','id','from'},
             'Unexpected causal CREATE event metadata refused')
        for key,value in {'scope':'local','status':'create','id':event['Actor']['ID'],'from':self.expected['Image']}.items():
            need(key not in event or event[key] == value, 'Causal CREATE metadata drift: ' + key)
        need('time' not in event or type(event['time']) is int and event['time'] == event['timeNano']//1000000000,
             'Causal CREATE seconds timestamp drift')
        need(set(event['Actor']) == {'ID','Attributes'} and len(self.owned.encode(event)) <= 8192,
             'Bounded own causal CREATE event required')
        record = {'receiptKind':'actual-daemon-create-event','httpStatus':None,'containerId':event['Actor']['ID'],
                  'daemonId':self.grant['daemonId'],'intentSha256':self.owned.digest(self.owned.encode(self.intent)),
                  'eventSha256':self.owned.digest(self.owned.encode(event)),'eventTimeNano':event['timeNano'],
                  'actualCreateEvent':event,'eventQueryHttpStatus':status,
                  'eventObservedUntilUtc':until.isoformat()}
        self.save('create-event-response.json',record)
        return self._seal(record)

    def ensure_custody(self, expected, receipt=None):
        need(expected == self.expected, 'PreSTART spec drift')
        actual = self.recover()
        need(receipt is None or actual == receipt, 'PreSTART custody proof drift')
        need(self.parent_alive() is True and self.clock() < self.owned.utc(self.grant['expiresUtc']), 'PreSTART parent/Root lease lost')
        return {'verified':True,'containerId':actual['containerId'],'actualCreatedRaw':actual['actualCreatedRaw'],
                'expectedConfigDigest':self.expected_digest,'checkedUtc':self.clock().isoformat(),
                'custodyReceiptSha256':self.owned.digest(self.owned.encode(actual))}

    def proven_zero_dispatch(self):
        return (not self.create_dispatched and self.actual_response is None and self.owner.cid is None
                and not any((self.directory/name).exists() for name in
                            ('create-intent.json','create-response.json','create-event-response.json','created-identity.json','custody.json')))

    def run(self, cancelled=lambda:False, parent_cleanup_complete=lambda:False, sleep=time.sleep):
        if self.proven_zero_dispatch():
            terminal={'schemaVersion':1,'reason':'preCREATE-no-dispatch','checkedUtc':self.clock().isoformat(),
                      'cleanupVerified':True,'allocationUnresolved':False,'containerId':None,'createDispatched':False,
                      'rootGrantSha256':self.root_sha,'failureType':None,'runId':self.grant['runId'],
                      'daemonId':self.grant['daemonId'],'intentName':self.name,'intentSha256':None}
            self.save('terminal.json',terminal)
            return terminal
        reason = 'Root-expiry'; failure = None; verified = False
        try:
            while self.clock() < self.owned.utc(self.grant['expiresUtc']):
                try:
                    cancel_observed=cancelled()
                except Exception:
                    reason='cancel-observation-uncertain'; break
                if cancel_observed: reason='cancel'; break
                try:
                    parent_observed=self.parent_alive()
                except Exception:
                    reason='parent-observation-uncertain'; break
                if parent_observed is not True: reason='parent-identity-lost'; break
                try:
                    complete_observed=parent_cleanup_complete()
                except Exception:
                    reason='parent-cleanup-observation-uncertain'; break
                if complete_observed: reason='parent-cleanup-complete'; break
                sleep(min(0.2,max(0,(self.owned.utc(self.grant['expiresUtc'])-self.clock()).total_seconds())))
            deadline = self.monotonic() + self.stage['cleanupSeconds']
            # Root-expired ordinary transport cannot be reused for cleanup/recovery.
            # Obtain a separately bounded cleanup transport BEFORE wrapping it.
            factory=getattr(self.backend,'cleanup_backend_factory',None)
            recovery_backend=factory(deadline) if callable(factory) else self.backend
            self.backend=DeadlineBackend(recovery_backend,deadline,self.monotonic)
            self.owner.backend=self.backend
            for attempt in range(8):
                if self.monotonic() >= deadline: break
                retry_dir=self.directory / ('cleanup-attempt-' + str(attempt))
                retry_dir.mkdir(mode=0o700); self.owner.ledger=retry_dir
                try:
                    if (self.directory/'custody.json').exists():
                        self.intent=self.binding(self.load('create-intent.json'))
                        receipt=self.load('custody.json'); spec=self.load('created-identity.json')
                        need(receipt.get('createdSpec') == spec and receipt.get('expectedConfigDigest') == self.expected_digest
                             and receipt.get('rootGrantSha256') == self.root_sha
                             and receipt.get('intentSha256') == self.owned.digest(self.owned.encode(self.intent)), 'Cleanup durable custody drift')
                        self.owner.cid=receipt['containerId']; self.owner.spec=spec
                        need(spec.get('Id') == self.owner.cid and self.owned.IDS.fullmatch(self.owner.cid), 'Cleanup durable ID drift')
                        self.owner.daemon(self.backend); self.owner.image()
                    else: self.recover()
                    self.owner.cleanup(); verified=self.owner.report.get('cleanupVerified') is True
                    if verified: break
                except Exception as caught: failure=type(caught).__name__
                sleep(min(0.2,max(0,deadline-self.monotonic())))
            need(verified, 'Exact cleanup unresolved within separate finite lease')
            failure=None
        except Exception as caught: failure=type(caught).__name__
        terminal={'schemaVersion':1,'reason':reason,'checkedUtc':self.clock().isoformat(),
                  'cleanupVerified':verified,'allocationUnresolved':not verified,'containerId':self.owner.cid,
                  'createDispatched':self.create_dispatched,
                  'rootGrantSha256':self.root_sha,'failureType':failure,'runId':self.grant['runId'],
                  'daemonId':self.grant['daemonId'],'intentName':self.name,
                  'intentSha256':self.owned.digest(self.owned.encode(self.intent)) if self.intent else None}
        self.save('terminal.json',terminal)
        return terminal


class DeadlineBackend:
    """Clamp every recovery/cleanup request and derived backend to one lease."""
    def __init__(self,backend,deadline,monotonic):
        self.backend,self.deadline,self.monotonic=backend,deadline,monotonic
    def check(self): need(self.monotonic()<self.deadline,'Custodian cleanup lease expired')
    def request(self,*args,**kwargs):
        self.check(); result=self.backend.request(*args,**kwargs); self.check(); return result
    def json(self,*args,**kwargs):
        self.check(); result=self.backend.json(*args,**kwargs); self.check(); return result
    def cleanup_backend_factory(self,deadline):
        self.check(); bound=min(deadline,self.deadline)
        factory=getattr(self.backend,'cleanup_backend_factory',None)
        backend=factory(bound) if callable(factory) else self.backend
        return DeadlineBackend(backend,bound,self.monotonic)
