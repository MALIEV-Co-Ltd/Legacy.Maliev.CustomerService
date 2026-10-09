"""SOURCE-ONLY same GitHub allocation Root-status/immutable-blob kernel relay.

Status success means authority publication, NEVER tests passed. Jobs perform GET
only and never mint capability bytes. Root separately issues the exact8 fields.
"""
from __future__ import annotations
import argparse
import base64
import datetime as dt
import hashlib
import json
import os
from pathlib import Path
import re
import signal
import sys
import time
import types
from urllib.request import Request, build_opener, HTTPRedirectHandler

REPOSITORY='MALIEV-Co-Ltd/Legacy.Maliev.CustomerService'
ROOT_USER_ID=18647332
HARNESS_SHA256='c61da7a397069e73b0fff919f87f9b1d944fa0a1ad8b40eb02487dfd194996a1'
OWNER_HELPER_SHA256='834cbb4f0380ef4460c9ff71b9632a952ab5a662d79074b6d35ff54484e8ce07'
EXTERNAL_OWNER_SHA256='9b1f9278beb9f9fbc6ae20fff2d356f6b588b8ebcfe905fc0faa867ef9f35162'
PACKET_SHA256='eccc5ddac7342c0297c177945030426fabca183455901021463d084d2eefe079'
SOURCE_BASE='b50e12d66cf0c3d4211a41febe3b01ca265a0c15'
PROBE_BLOB_OID='e3743644a33ca41347258e5c8461fb12f1d180cc'
MAX_HTTP_BYTES=1048576
MAX_REQUESTS=40
MAX_POLLS=18
MAX_PAGES=2
POLL_SECONDS=20
CLEANUP_RESERVE=120
FLOOR_KIB=4194304
BOOT=re.compile(r'[a-f0-9]{8}-[a-f0-9]{4}-[a-f0-9]{4}-[a-f0-9]{4}-[a-f0-9]{12}\Z')

def need(value,message):
    if not value:raise ValueError(message)

def digest(raw):return hashlib.sha256(raw).hexdigest()

def canonical(value):return json.dumps(value,sort_keys=True,separators=(',',':'),allow_nan=False).encode()

def parse(raw):
    need(isinstance(raw,bytes) and len(raw)<=MAX_HTTP_BYTES,'Bounded JSON bytes required')
    def pairs(rows):
        result={}
        for key,value in rows:
            need(key not in result,'Duplicate JSON key');result[key]=value
        return result
    return json.loads(raw,object_pairs_hook=pairs,parse_constant=lambda _:(_ for _ in ()).throw(ValueError('Nonfinite JSON')))

def utc(value):
    need(isinstance(value,str),'UTC string required')
    result=dt.datetime.fromisoformat(value.replace('Z','+00:00'))
    need(result.utcoffset()==dt.timedelta(0),'UTC required')
    return result

def now():return dt.datetime.now(dt.timezone.utc)

def regular(path,limit=8388608):
    path=Path(path)
    for item in (path,*path.parents):
        need(not item.is_symlink() and not (item.exists() and getattr(item.lstat(),'st_file_attributes',0)&0x400),'Links/reparse refused')
    need(path.is_file() and path.stat().st_size<=limit,'Bounded regular source required')
    raw=path.read_bytes();need(len(raw)<=limit,'File grew');return raw

def save_new(path,row):
    path=Path(path);raw=canonical(row)
    need(len(raw)<=65536,'Receipt bound')
    with path.open('xb') as stream:stream.write(raw);stream.flush();os.fsync(stream.fileno())

def capture_identity(packet):
    raw=regular(Path(packet)/'driver-manifest.json',65536)
    need(digest(raw)==PACKET_SHA256,'Reviewed18packet identity manifest differs')
    manifest=parse(raw);need(manifest.get('nativeExecutionGranted') is False and len(manifest['files'])==18,'Exact source-only18packet required')
    captured = {}
    for row in manifest['files']:
        name = row['path']
        need(not name.startswith('/') and '\\' not in name and ':' not in name
             and all(p not in ('', '.', '..') for p in name.split('/')) and name not in captured,
             'Closed carrier member path differs')
        data = regular(Path(packet)/name)
        need(len(data) == row['bytes'] and digest(data) == row['sha256'], 'Closed carrier member seal differs')
        captured[name] = data
    driver = types.ModuleType('route_captured_driver')
    driver.__file__ = str(Path(packet)/'customer_native_build_driver.py')
    exec(compile(captured['customer_native_build_driver.py'], driver.__file__, 'exec'), driver.__dict__)
    driver.load_packet(packet, PACKET_SHA256)
    rows=[row for row in manifest['files'] if row['path']=='customer_guardian_process.py']
    need(len(rows)==1,'Captured reviewed identity module required')
    source=regular(Path(packet)/rows[0]['path']);need(len(source)==rows[0]['bytes'] and digest(source)==rows[0]['sha256'],'Captured identity source seal differs')
    value=types.ModuleType('captured_reviewed_identity');value.__file__=str(Path(packet)/rows[0]['path'])
    exec(compile(source,value.__file__,'exec'),value.__dict__)
    return value.identity

def native_census(identity_reader,proc):
    rows=[];count=0
    for entry in proc.iterdir():
        if not entry.name.isdecimal():continue
        count+=1;need(count<=4096,'Native census process bound exceeded')
        try:
            exe=os.readlink(entry/'exe')
        except FileNotFoundError:continue
        except PermissionError as error:raise ValueError('Native census inaccessible; competing work unknown') from error
        name=Path(exe).name.lower()
        if name in {'dotnet','msbuild','vbc','csc','docker','dockerd','containerd','postgres','redis-server','vstest.console','testhost'}:
            rows.append(identity_reader(int(entry.name),proc))
    need(len(rows)<=256,'Native census result bound exceeded')
    return rows

def snapshot(identity_reader,proc=Path('/proc'),pid=None):
    need(sys.platform=='linux','Actual Linux observation required')
    observer=identity_reader(pid or os.getpid(),proc);walk=observer;seen=set();runner=None
    for _ in range(32):
        need(walk['pid'] not in seen,'Ancestor cycle');seen.add(walk['pid'])
        if Path(walk['executable']).name=='Runner.Worker':runner=walk;break
        # Read only the ancestry edge; identity itself uses the reviewed helper.
        stat_raw=(proc/str(walk['pid'])/'stat').read_text();parts=stat_raw[stat_raw.rfind(')')+2:].split()
        need(len(parts)>=20 and int(parts[19])==walk['startTicks'] and identity_reader(walk['pid'],proc)==walk,
             'Ancestor edge/identity changed')
        parent_pid=int(parts[1]);need(parent_pid>0,'Actual owning Runner.Worker not found')
        walk=identity_reader(parent_pid,proc)
    need(runner is not None and runner['hostBootId']==observer['hostBootId'],'Actual GitHub worker ancestor required')
    values=[line.split() for line in (proc/'meminfo').read_text().splitlines() if line.startswith('MemFree:')]
    need(len(values)==1 and values[0][2]=='kB','Actual physical MemFree required');free=int(values[0][1])
    need(free>=FLOOR_KIB,'Physical floor refused')
    native=native_census(identity_reader,proc)
    competitors=[row for row in native if Path(row['executable']).name.lower() in {'dotnet','testhost','msbuild','vstest.console','vbc','csc'}]
    need(not competitors,'Competing SDK/build admission signal observed; ownership remains unknown')
    boot=(proc/'sys/kernel/random/boot_id').read_text().strip();need(boot==observer['hostBootId'],'Boot changed during observation')
    return {'checkedUtc':now().isoformat(),'hostBootId':boot,'kernel':os.uname().release,
            'observer':observer,'runnerWorker':runner,'freePhysicalKiB':free,'nativeProcesses':native,'competingNativeProcesses':competitors,'censusComplete':True}

def context(run,attempt,nonce):
    need(re.fullmatch(r'[1-9][0-9]{0,19}',str(run)) and re.fullmatch(r'[1-9][0-9]{0,8}',str(attempt))
         and re.fullmatch(r'[a-f0-9]{32}',nonce or ''),'Exact run/attempt/Root nonce required')
    return 'crm-kernel-root/'+str(run)+'-'+str(attempt)+'-'+nonce

def validate_observation(obs,clock):
    need(set(obs)=={'schemaVersion','repository','runId','runAttempt','sourceHead','rootDispatchNonce','statusContext','issuedUtc','allocationDeadlineUtc','host'},'Exact observation schema required')
    need(type(obs['schemaVersion']) is int and obs['schemaVersion']==1 and obs['repository']==REPOSITORY,'Observation repo/schema differs')
    need(re.fullmatch(r'[a-f0-9]{40}',obs['sourceHead'] or '') and obs['statusContext']==context(obs['runId'],obs['runAttempt'],obs['rootDispatchNonce']),'Observation identity differs')
    issued,deadline=utc(obs['issuedUtc']),utc(obs['allocationDeadlineUtc'])
    need(issued<=clock<deadline and 0<(deadline-issued).total_seconds()<=600,'Finite absolute allocation required')
    host=obs['host'];need(BOOT.fullmatch(host.get('hostBootId','')) and type(host.get('freePhysicalKiB')) is int and host['freePhysicalKiB']>=FLOOR_KIB,'Observation host/floor differs')
    need(0<=(clock-utc(host['checkedUtc'])).total_seconds()<=600,'Observation timestamp differs')
    for key in ('observer','runnerWorker'):
        item=host[key];need(type(item.get('pid')) is int and item['pid']>0 and type(item.get('startTicks')) is int
                           and item['startTicks']>0 and item.get('hostBootId')==host['hostBootId'] and isinstance(item.get('executable'),str) and utc(item['actualStartUtc'])<=utc(host['checkedUtc']),'Actual proc identity fields required')
    need(host.get('censusComplete') is True and isinstance(host.get('nativeProcesses'),list) and host.get('competingNativeProcesses')==[],'Fresh complete native census required; absence is not predecessor handoff')
    need(Path(host['runnerWorker']['executable']).name=='Runner.Worker','Owning worker executable differs')
    return obs

def reobserve(obs,current,clock):
    validate_observation(obs,clock)
    need(current['hostBootId']==obs['host']['hostBootId'] and current['runnerWorker']==obs['host']['runnerWorker']
         and current['kernel']==obs['host']['kernel'],'Same worker/boot/kernel allocation required')
    need(type(current['freePhysicalKiB']) is int and current['freePhysicalKiB']>=FLOOR_KIB
         and current.get('censusComplete') is True and isinstance(current.get('nativeProcesses'),list) and current.get('competingNativeProcesses')==[]
         and 0<=(clock-utc(current['checkedUtc'])).total_seconds()<=10,'Fresh physical reobservation required')
    return current

class NoRedirect(HTTPRedirectHandler):
    def redirect_request(self,*args,**kwargs):raise ValueError('Redirect refused')

class PublicGitHub:
    def __init__(self,transport=None):self.transport=transport;self.requests=0
    def get(self,path):
        prefix='/repos/'+REPOSITORY+'/'
        need(path.startswith(prefix) and re.fullmatch(re.escape(prefix)+r'(?:commits/[a-f0-9]{40}/statuses\?per_page=(?:1|100)&page=[12]|git/blobs/[a-f0-9]{40}|commits/[a-f0-9]{40})',path),'Read-only exact ownrepo API path required')
        self.requests+=1;need(self.requests<=MAX_REQUESTS,'Anonymous HTTP request budget exhausted')
        if self.transport:status,raw=self.transport(path,10,MAX_HTTP_BYTES)
        else:
            request=Request('https://api.github.com'+path,headers={'Accept':'application/vnd.github+json','User-Agent':'crm-kernel-source-route'},method='GET')
            with build_opener(NoRedirect()).open(request,timeout=10) as response:
                status=response.status;raw=response.read(MAX_HTTP_BYTES+1)
        need(type(status) is int and status==200,'Actual API GET200 required; rate/permission failure refuses')
        need(isinstance(raw,bytes) and len(raw)<=MAX_HTTP_BYTES,'HTTP response cap exceeded')
        return parse(raw)
    def statuses(self,head):
        result=[]
        for page in range(1,MAX_PAGES+1):
            rows=self.get('/repos/'+REPOSITORY+'/commits/'+head+'/statuses?per_page=100&page='+str(page))
            need(isinstance(rows,list) and len(rows)<=100,'Status page bound')
            result.extend(rows)
            if len(rows)<100:return result
        raise ValueError('Status collection truncated; uniqueness unproved')
    def blob(self,oid):
        row=self.get('/repos/'+REPOSITORY+'/git/blobs/'+oid)
        need(row.get('sha')==oid and row.get('encoding')=='base64' and type(row.get('size')) is int and 0<row['size']<=65536,'Immutable blob metadata differs')
        content=row.get('content');need(isinstance(content,str) and len(content)<=100000,'Blob encoding cap')
        raw=base64.b64decode(''.join(content.split()),validate=True)
        need(len(raw)==row['size'] and hashlib.sha1(b'blob '+str(len(raw)).encode()+b'\0'+raw).hexdigest()==oid,'Actual immutable Git blob differs')
        return raw

def select_status(rows,obs):
    need(isinstance(rows,list) and len(rows)<=200,'Bounded actual status collection required')
    matches=[row for row in rows if isinstance(row,dict) and row.get('context')==obs['statusContext']]
    need(len(matches)<=1,'Duplicate/equivocal context refused')
    if not matches:return None
    status=matches[0];creator=status.get('creator') or {}
    need(type(creator.get('id')) is int and creator['id']==ROOT_USER_ID and creator.get('type')=='User','Pinned external Root User authority required; bot/hash alone insufficient')
    need(status.get('state')=='success','Status success authority publication required; never testpass')
    description=status.get('description','');need(re.fullmatch(r'authority-sha256=[a-f0-9]{64}',description),'Exact authority digest description required')
    target=status.get('target_url','');prefix='https://api.github.com/repos/'+REPOSITORY+'/git/blobs/'
    need(target.startswith(prefix) and re.fullmatch(r'[a-f0-9]{40}',target[len(prefix):]),'Exact immutable own Git blob URL required')
    for key in ('created_at','updated_at'):
        need(utc(obs['issuedUtc'])<=utc(status[key])<=utc(obs['allocationDeadlineUtc']),'Status publication outside allocation')
    return {'capabilitySha256':description.split('=',1)[1],'blobOid':target[len(prefix):],
            'creator':{'id':creator['id'],'type':creator['type']},'statusId':status.get('id'),'context':status['context']}

def validate_capability(raw,trusted,obs,clock,harness_sha=HARNESS_SHA256,packet_sha=PACKET_SHA256):
    need(re.fullmatch(r'[a-f0-9]{64}',harness_sha or ''),'NEEDS_RESEAL: reviewed R7 harness hash unset')
    need(re.fullmatch(r'[a-f0-9]{64}',trusted or '') and digest(raw)==trusted,'Externally authenticated capability raw SHA differs')
    need(len(raw)<=65536,'Capability byte cap');cap=parse(raw)
    need(set(cap)=={'schemaVersion','scope','rootIssued','harnessSha256','packetManifestSha256','hostBootId','issuedUtc','expiresUtc'},'Exact8field capability required')
    need(type(cap['schemaVersion']) is int and cap['schemaVersion']==1 and cap['rootIssued'] is True
         and cap['scope']=='customer-kernel-only-lifecycle' and cap['harnessSha256']==harness_sha
         and cap['packetManifestSha256']==packet_sha and cap['hostBootId']==obs['host']['hostBootId'],'Capability scope/source/boot differs')
    validate_observation(obs,clock);issued,expiry=utc(cap['issuedUtc']),utc(cap['expiresUtc'])
    need(utc(obs['issuedUtc'])<=issued<=clock and clock+dt.timedelta(seconds=CLEANUP_RESERVE)<=expiry<=utc(obs['allocationDeadlineUtc'])
         and 0<(expiry-issued).total_seconds()<=600,'Fresh bounded capability/cleanup reserve required')
    return cap

def publisher_recipe(raw,trusted,observation_raw,observation_sha,issuer,clock,harness_sha=HARNESS_SHA256):
    need(digest(observation_raw)==observation_sha,'Externally trusted immutable observation SHA differs')
    need(issuer=={'id':ROOT_USER_ID,'login':'natthapolvanasrivilai','type':'User'},'Authenticated external Root issuer must match')
    obs=parse(observation_raw);validate_capability(raw,trusted,obs,clock,harness_sha)
    oid=hashlib.sha1(b'blob '+str(len(raw)).encode()+b'\0'+raw).hexdigest()
    return {'recipeOnly':True,'capabilityCreated':False,'apiWritesPerformed':0,
            'requiredAuthenticatedIssuer':issuer,'requiredOAuthScope':'repo',
            'blobPost':{'path':'/repos/'+REPOSITORY+'/git/blobs','body':{'content':base64.b64encode(raw).decode(),'encoding':'base64'},'expected201GitBlobOid':oid},
            'statusPost':{'path':'/repos/'+REPOSITORY+'/statuses/'+obs['sourceHead'],
                          'body':{'state':'success','context':obs['statusContext'],'description':'authority-sha256='+trusted,
                                  'target_url':'https://api.github.com/repos/'+REPOSITORY+'/git/blobs/'+oid}},
            'requiredAuthenticatedProbe':{'method':'GET','url':'https://api.github.com/user','requiredId':ROOT_USER_ID,'requiredType':'User','recheckBeforePost':True},
            'meaning':'Root authority publication only; success NEVER means tests pass. External Root validates GET/user then publishes once; no job POST permissions.'}

def execute_once(harness,packet,output,capfile,cap_sha,*,external_backend=None,observation=None,identity_reader=None,clock=now,cancelled=lambda:False):
    raw=regular(harness);need(digest(raw)==HARNESS_SHA256,'NEEDS_RESEAL: exact captured R7 harness required')
    need(digest(regular(Path(harness).parent/'owned_process.py'))==OWNER_HELPER_SHA256,'NEEDS_RESEAL: R7 owner helper differs')
    adapter_path=Path(__file__).parent/'external_unit_owner.py';adapter_raw=regular(adapter_path)
    need(digest(adapter_raw)==EXTERNAL_OWNER_SHA256,'NEEDS_RESEAL: exact outside manager adapter required')
    value=types.ModuleType('captured_external_owner_r7');value.__file__=adapter_path.absolute().as_posix()
    exec(compile(adapter_raw,str(adapter_path),'exec'),value.__dict__)
    need(observation is not None and identity_reader is not None,'Original same-allocation observation/identity required')
    obs_raw=regular(Path(capfile).parent/'observation.json',65536);need(parse(obs_raw)==observation,'Original immutable observer bytes differ')
    cap_raw=regular(capfile,65536);cap=validate_capability(cap_raw,cap_sha,observation,clock(),HARNESS_SHA256)
    fresh=snapshot(identity_reader);reobserve(observation,fresh,clock())
    runtime=int((utc(cap['expiresUtc'])-clock()).total_seconds())-40;need(runtime>0,'Original authority manager/stop reserve exhausted')
    unit='customer-kernel-'+observation['runId']+'-'+observation['runAttempt']+'-'+observation['rootDispatchNonce']+'.service'
    import uuid
    description='CustomerKernelProof:'+uuid.uuid4().hex
    intent={'schemaVersion':1,'scope':'customer-kernel-external-custody','originalObserverSha256':digest(obs_raw),'capabilitySha256':cap_sha,'harnessSha256':HARNESS_SHA256,'packetManifestSha256':PACKET_SHA256,'unit':unit,'description':description,'controlGroup':'/system.slice/'+unit,'issuedUtc':clock().isoformat(),'expiresUtc':cap['expiresUtc'],'routeIdentity':{'repository':REPOSITORY,'runId':observation['runId'],'runAttempt':observation['runAttempt'],'sourceHead':observation['sourceHead'],'rootDispatchNonce':observation['rootDispatchNonce'],'hostBootId':fresh['hostBootId'],'runnerWorker':fresh['runnerWorker'],'supervisorProcess':fresh['observer']},'managerProperties':{'MemoryMax':'268435456','MemorySwapMax':'0','CPUQuotaPerSecUSec':'250000','TasksMax':'64','KillMode':'control-group','SendSIGKILL':'yes','Restart':'no','Delegate':'no','NoNewPrivileges':'yes','ProtectControlGroups':'yes','RuntimeMaxUSec':str(runtime*1000000),'TimeoutStopUSec':'5000000','TimeoutStartUSec':'10000000','Type':'exec','NotifyAccess':'none','ExecStop':''},'externalAdapter':{'path':adapter_path.absolute().as_posix(),'sha256':EXTERNAL_OWNER_SHA256,'execStopPost':['/usr/bin/python3','-B',adapter_path.absolute().as_posix(),'stop-witness','--directory',(Path(capfile).parent/'external-owner').absolute().as_posix()]}}
    owner_output=Path(capfile).parent/'external-owner';owner_output.mkdir(mode=0o700,exist_ok=False)
    intent_path=owner_output/'external-owner-intent.json'
    argv=['/usr/bin/python3','-B',Path(harness).absolute().as_posix(),'--kernel-only','--packet',Path(packet).absolute().as_posix(),'--manifest-sha256',PACKET_SHA256,'--output',Path(output).absolute().as_posix(),'--capability',Path(capfile).absolute().as_posix(),'--capability-sha256',cap_sha,'--external-owner-receipt',intent_path.absolute().as_posix(),'--external-owner-sha256',digest(value.canonical(intent))]
    backend=external_backend or value.LinuxBackend()
    owner=value.ExternalOwner(backend,clock=clock,cancelled=cancelled,worker_alive=lambda:reobserve(observation,snapshot(identity_reader),clock()) is not None)
    result=owner.launch(intent,argv,owner_output)
    need(result.get('kernelQualified') is True and result.get('cleanupVerified') is True and result.get('managerUnitAbsent') is True,'Independent external kernel settlement unverified')
    return 0

def receive(obs,api,observe,invoke,output,clock=now,monotonic=time.monotonic,sleep=time.sleep,cancelled=lambda:False,harness_sha=HARNESS_SHA256):
    output=Path(output);need(output.is_dir() and not output.is_symlink(),'Owned result directory required')
    terminal={'kernelExecuted':False,'kernelQualified':False,'authorityPublished':False,'SDKStarted':False,'DockerCalls':0,'nativeBuildGranted':False,'failureType':None}
    primary=None;traceback=None
    try:
        need(re.fullmatch(r'[a-f0-9]{64}',harness_sha or ''),'NEEDS_RESEAL: reviewed harness hash unset')
        validate_observation(obs,clock());reobserve(obs,observe(),clock())
        absolute=utc(obs['allocationDeadlineUtc'])-dt.timedelta(seconds=CLEANUP_RESERVE)
        remaining=(absolute-clock()).total_seconds();need(remaining>0,'Allocation wait/cleanup reserve exhausted')
        mono_deadline=monotonic()+remaining
        for _ in range(MAX_POLLS):
            need(not cancelled(),'Same job cancelled')
            need(clock()<absolute and monotonic()<mono_deadline,'Finite capability wait expired')
            reobserve(obs,observe(),clock())
            status=select_status(api.statuses(obs['sourceHead']),obs)
            if status:
                raw=api.blob(status['blobOid']);validate_capability(raw,status['capabilitySha256'],obs,clock(),harness_sha)
                reobserve(obs,observe(),clock());need(not cancelled(),'Cancelled before kernel call')
                save_new(output/'authenticated-publication.json',status)
                with (output/'root-capability.json').open('xb') as stream:stream.write(raw);stream.flush();os.fsync(stream.fileno())
                terminal['authorityPublished']=True;terminal['kernelExecuted']=True
                code=invoke(output/'root-capability.json',status['capabilitySha256'])
                need(code==0,'Actual captured kernel result failed');need(not cancelled(),'Cancelled during kernel call')
                terminal['kernelQualified']=True
                break
            delay=min(POLL_SECONDS,(absolute-clock()).total_seconds(),mono_deadline-monotonic())
            need(delay>=POLL_SECONDS,'Insufficient finite poll interval/reserve');sleep(delay)
        else:raise TimeoutError('Finite anonymous publication poll budget exhausted')
    except BaseException as error:
        primary=error;traceback=error.__traceback__;terminal['kernelQualified']=False
        terminal['failureType']=type(error).__name__;terminal['failureMessage']=str(error) if isinstance(error,ValueError) else 'bounded route failed'
    terminal['httpRequests']=api.requests;terminal['remainingRouteHelpers']=0
    try:
        if cancelled():
            terminal['kernelQualified']=False
            if primary is None:primary=ValueError('Cancelled before terminal durability')
            terminal['failureType']=type(primary).__name__;terminal['failureMessage']=str(primary)
        save_new(output/'route-terminal.json',terminal)
        need(not cancelled(),'Cancelled during terminal durability')
    except BaseException as secondary:
        terminal['kernelQualified']=False
        if primary is None:primary=secondary;traceback=secondary.__traceback__
        else:primary.add_note('Secondary terminal durability/cancellation uncertainty: '+repr(secondary))
        try:save_new(output/'route-invalidation.json',{'kernelQualified':False,'terminalInvalid':True,'reasonType':type(secondary).__name__})
        except BaseException as invalidation:
            primary.add_note('Invalidation receipt durability also failed: '+repr(invalidation))
    if primary is not None:raise primary.with_traceback(traceback)
    return terminal


def consumer_phase_working_directory(phase):
    if 'workingDirectory' in phase:return phase['workingDirectory']
    need(phase['name'] in ('format','package-audit','raw-four-coverage-floors','scaffold'),'Explicit working directory required for this phase')
    return '/work/repo'

def consumer_failure_evidence(path,payload,primary):
    try:path.write_bytes(canonical(payload))
    except BaseException:
        if primary is None:raise

def consumer_phase_sequence(phases, execute, admit, save):
    """Execute only sealed phases through an existing separately owned SDK.

    No allocator, grants or process/container creation. Each exec belongs to the
    independently verified existing consumer SDK and its original finite owner.
    """
    names=[p['name'] for p in phases]
    need(len(set(names))==len(names) and 'baseline-full387' in names and 'focused13' in names and 'full400' in names and 'baseline-focus-full-raw-trx-join' in names,'Exact current consumer sequence required')
    results=[]
    for phase in phases:
        result=None
        try:
            admit(phase)
            result=execute(phase)
            need(result.get('exitCode')==0 and result.get('completed') is True,'Actual phase failed or incomplete; never behavioral RED')
            if phase['name'].endswith('-strict-release-build'):
                import re
                raw=result.get('stdout','')+result.get('stderr','')
                need(re.findall(r'([0-9]+) Warning\(s\)',raw)[-1:]==['0'] and re.findall(r'([0-9]+) Error\(s\)',raw)[-1:]==['0'],'Strict Release actual zero-warning/error summary required')
        except BaseException as error:
            try:save(results+[{'name':phase['name'],**(result or {}),'completed':False,'failureType':type(error).__name__,'failureIsBehavioralRed':False}])
            except BaseException:pass  # Preserve the original failure; caller retains owner recovery evidence.
            raise
        results.append({'name':phase['name'],**result});save(results)
    return {'phasesCompleted':len(results),'nativeTestsAccepted':False,'full400Accepted':False,'originalOwnerTerminalEvidenceStillRequired':True}

CONSUMER_SOURCE_ROOT=Path('/work/customer-source')

def validate_consumer_host_source(source,owner,policy_sha):
    source=Path(source)
    need(source==CONSUMER_SOURCE_ROOT and source.is_dir() and not source.is_symlink(),'Actual original outside-custodian host source directory required')
    marker=source.parent/'.customer-source-owner'
    job=owner['consumerJob']
    need(marker.is_file() and not marker.is_symlink() and marker.read_text()==job['runId']+'-'+job['runAttempt'],'Original same-job host source owner marker required')
    raw=regular(source/'metadata/intake-receipt.json',65536)
    need(digest(raw)==owner.get('hostSourceReceiptSha256'),'Original outside-custodian host intake receipt byte binding differs')
    receipt=parse(raw)
    need(receipt.get('policySha256')==policy_sha and receipt.get('rawFiles')==324 and receipt.get('nativeExecutionGranted') is False and receipt.get('actualTestsRun')==0,'Actual current324 unqualified host intake receipt required')
    return receipt

def consumer_caller(args):
    """Opt-in caller bridge; never borrows a BUILD-only or kernel-only SDK."""
    need(sys.platform=='linux' and os.environ.get('GITHUB_REPOSITORY')==REPOSITORY and os.environ.get('GITHUB_JOB')=='validate','Existing original GitHub validation caller required')
    need(args.consumer_custody and re.fullmatch('[a-f0-9]{64}',args.consumer_custody_sha256 or '') and args.source_directory and args.output,'Independently pinned current consumer custody and source required')
    raw=regular(args.consumer_custody,65536);need(digest(raw)==args.consumer_custody_sha256,'Actual original consumer custody bytes differ')
    owner=parse(raw)
    need('rootGrantSha256' not in owner and owner.get('scope') not in ('strict-release-build','customer-kernel-only-lifecycle'),'Build/kernel-only authority cannot authorize consumer tests')
    need(owner.get('consumerJob')=={'repository':REPOSITORY,'runId':os.environ.get('GITHUB_RUN_ID'),'runAttempt':os.environ.get('GITHUB_RUN_ATTEMPT'),'job':'validate'},'Existing consumer allocation/job binding differs')
    need(owner.get('sourceHead')==os.environ.get('GITHUB_SHA') and owner.get('packetManifestSha256')==PACKET_SHA256,'Actual caller source/closed packet binding differs')
    need(0<(utc(owner['expiresUtc'])-utc(owner['issuedUtc'])).total_seconds()<=1800 and utc(owner['issuedUtc'])<=now()<utc(owner['expiresUtc']),'Original finite consumer allocation required')
    handoffs=owner.get('predecessorHandoffs')
    need(isinstance(handoffs,dict) and set(handoffs)=={'RootTracking','Intranet','Workflows'},'Actual original admission predecessor handoffs required')
    for row in handoffs.values():
        need(row.get('terminal') is True and row.get('activeOwnedNative') is False and row.get('evidenceRef') and 0<=(now()-utc(row['checkedUtc'])).total_seconds()<=120,'Fresh actual predecessor terminal evidence required at original consumer admission')
    cid=owner['containerId'];need(re.fullmatch('[a-f0-9]{64}',cid),'Exact existing consumer SDK ID required')
    expected=owner['createdSpec'];need(expected.get('Id')==cid and expected.get('Created')==owner['actualCreatedRaw'],'Original SDK created identity required')
    output=Path(args.output);need(not output.exists(),'Fresh caller evidence directory required');output.mkdir(mode=0o700)
    import types
    driver=types.ModuleType('consumer_captured_driver');driver.__file__=str(Path(args.packet)/'customer_native_build_driver.py')
    manifest_raw=regular(Path(args.packet)/'driver-manifest.json',65536)
    need(digest(manifest_raw)==PACKET_SHA256,'Independently pinned original closed packet required')
    driver_rows=[r for r in parse(manifest_raw)['files'] if r['path']=='customer_native_build_driver.py']
    data=regular(driver.__file__,1048576)
    need(len(driver_rows)==1 and digest(data)==driver_rows[0]['sha256'] and len(data)==driver_rows[0]['bytes'],'Capture and seal the driver bytes before execution')
    exec(compile(data,driver.__file__,'exec'),driver.__dict__)
    packet=driver.load_packet(args.packet,PACKET_SHA256);manifest,stage,policy,helper,intake,admission,*_=packet
    source=Path(args.source_directory);validate_consumer_host_source(source,owner,intake.POLICY_SHA256)
    driver.source_archive(source,policy,stage,helper,intake)
    plan=parse(regular(source/'metadata/provider-plan.json',1048576))
    deadline=time.monotonic()+(utc(owner['expiresUtc'])-now()).total_seconds()
    backend=driver.DockerAPI(lambda:deadline)
    identity_reader=capture_identity(args.packet)
    def inspect():
        outside=owner.get('outsideOwner')
        need(isinstance(outside,dict) and identity_reader(outside['pid'])==outside,'Actual original outside custodian identity required')
        actual=backend.json('GET','/containers/'+cid+'/json')
        need(actual['Id']==cid and actual['Created']==owner['actualCreatedRaw'] and actual.get('Image')==stage['sdkImage']['imageId'],'Existing SDK identity/image drift')
        need(digest(canonical(actual['Config']))==expected['configSha256'] and digest(canonical(actual['HostConfig']))==expected['hostConfigSha256'],'Original SDK caps/config drift')
        host=actual['HostConfig'];need(host['Memory']==host['MemorySwap']==2147483648 and host['NanoCpus']==1000000000 and host['PidsLimit']==256 and host['Privileged'] is False and host['ReadonlyRootfs'] is True,'Preserved original consumer SDK caps required')
        need(actual['State']['Running'] is True and not any(m.get('Destination')=='/var/run/docker.sock' for m in actual.get('Mounts',[])),'Live original SDK with no raw Docker socket required')
        env=dict(v.split('=',1) for v in actual['Config']['Env']);need(all(env.get(k)==v for k,v in plan['sdkEnvironment'].items()),'Original provider-proxy SDK environment differs')
        need(any(m.get('Destination')=='/provider-ledger' and m.get('RW') is False for m in actual.get('Mounts',[])),'Original readonly SDK provider ledger required')
        info=backend.json('GET','/info');need(info['ID']==owner['daemonId'] and info['OSType']=='linux','Exact original daemon required')
        for role in ('sdk','postgres','redis'):
            image=plan['images'][role];actual_image=backend.json('GET','/images/'+image['imageId']+'/json')
            need(actual_image['Id']==image['imageId'] and image['reference'] in actual_image['RepoDigests'],'Fresh exact original SDK/provider image pin required')
        return actual
    def admit(phase):
        host=admission.linux_host_snapshot()
        need(host['hostBootId']==owner['hostBootId'] and host['freePhysicalKiB']>=4194304 and host['nativeProcesses']==[],'Fresh original physical floor/native census required before every phase')
        need(now()<utc(owner['expiresUtc']) and (utc(owner['expiresUtc'])-now()).total_seconds()>phase['timeoutSeconds']+120,'Original phase plus cleanup reserve exhausted')
        inspect()
        # Check actual staged raw files and both business source graphs. Build
        # output is outside these immutable input rosters and never self-counts.
        for path,pin in policy['rawFiles'].items():
            need(digest(file_bytes('/work/customer-source/'+path))==pin['sha256'],'Actual SDK raw324 source/dependency/validation pin differs')
        need(parse(file_bytes('/work/control/consumer-window.json'))=={'grantStartsUtc':owner['issuedUtc'],'grantExpiresUtc':owner['expiresUtc']},'Actual SDK test window differs from original consumer allocation')
        baseline=parse(regular(source/'metadata/association-baseline-source-inventory.json',1048576))
        for row in baseline['files']:
            need(digest(file_bytes('/work/baseline/'+row['path']))==row['sha256'],'Actual SDK baseline160 source pin differs')
        for path,pin in policy['rawFiles'].items():
            if path.startswith('repo/'):
                need(digest(file_bytes('/work/'+path))==pin['sha256'],'Actual SDK candidate162 business source pin differs')
        # Re-observe after all byte checks immediately before exec admission.
        host=admission.linux_host_snapshot()
        need(host['hostBootId']==owner['hostBootId'] and host['freePhysicalKiB']>=4194304 and host['nativeProcesses']==[],'Fresh post-pin physical floor/native census required')
        inspect()
    def file_bytes(path):
        import io,tarfile
        from urllib.parse import quote
        status,raw=backend.request('GET','/containers/'+cid+'/archive?path='+quote(path,safe='/'))
        need(status==200,'Actual SDK evidence missing')
        with tarfile.open(fileobj=io.BytesIO(raw)) as tar:
            members=tar.getmembers();need(len(members)==1 and members[0].isfile(),'One actual regular SDK evidence file required');return tar.extractfile(members[0]).read()
    def execute(phase):
        import shlex
        import select
        need(callable(getattr(os,'pidfd_open',None)) and callable(getattr(signal,'pidfd_send_signal',None)),'Original Linux native-handle custody support required before exec')
        start=now();base='/work/results/consumer-phase-logs';out=base+'/'+phase['name']+'.stdout';err=base+'/'+phase['name']+'.stderr'
        shell='umask 077; mkdir -p '+base+'; set -C; exec '+shlex.join(phase['command'])+' > '+out+' 2> '+err
        body=canonical({'AttachStdout':False,'AttachStderr':False,'Tty':False,'WorkingDir':consumer_phase_working_directory(phase),'Cmd':['sh','-c',shell]})
        status,created=backend.request('POST','/containers/'+cid+'/exec',body,{'Content-Type':'application/json'});need(status==201,'Original SDK exec creation failed')
        eid=parse(created)['Id'];need(re.fullmatch('[a-f0-9]{64}',eid),'Exact SDK exec ID required')
        status,_=backend.request('POST','/exec/'+eid+'/start',canonical({'Detach':True,'Tty':False}),{'Content-Type':'application/json'});need(status==200,'Original SDK exec start failed')
        until=min(deadline-120,time.monotonic()+phase['timeoutSeconds'])
        pidfd=None;exec_identity=None;pidfd_authorized=False;primary=None
        try:
            while True:
                inspect();row=backend.json('GET','/exec/'+eid+'/json');need(row['ID']==eid and row['ContainerID']==cid,'Actual exec/SDK join differs')
                if row['Running'] is False:break
                if pidfd is None:
                    need(type(row['Pid']) is int and row['Pid']>0,'Actual owned exec PID required')
                    exec_identity=identity_reader(row['Pid']);pidfd=os.pidfd_open(row['Pid'],0)
                    refreshed=backend.json('GET','/exec/'+eid+'/json')
                    need(refreshed['ID']==eid and refreshed['ContainerID']==cid and refreshed['Pid']==exec_identity['pid'] and identity_reader(refreshed['Pid'])==exec_identity,'Exact created exec/native-handle binding differs')
                    sdk_pid=inspect()['State']['Pid']
                    need(Path('/proc/'+str(exec_identity['pid'])+'/cgroup').read_bytes()==Path('/proc/'+str(sdk_pid)+'/cgroup').read_bytes(),'Actual exec is outside the original owned SDK cgroup')
                    pidfd_authorized=True
                    (output/(phase['name']+'.exec-custody.json')).write_bytes(canonical({'execId':eid,'containerId':cid,'actualCreatedRaw':owner['actualCreatedRaw'],'identity':exec_identity,'purpose':phase['name'],'expiresUtc':owner['expiresUtc']}))
                need(time.monotonic()<until,'Actual consumer phase timeout; original outside SDK custodian retains descendant cleanup')
                time.sleep(.2)
        except BaseException:
            primary=sys.exc_info();raise
        finally:
            if pidfd is not None:
                try:
                    if pidfd_authorized and not select.select([pidfd],[],[],0)[0]:
                        # Signal only the native handle captured for this API-created
                        # exec. Never stop the SDK, foreign PIDs or a process tree.
                        signal.pidfd_send_signal(pidfd,signal.SIGTERM,None,0)
                        if not select.select([pidfd],[],[],5)[0]:signal.pidfd_send_signal(pidfd,signal.SIGKILL,None,0)
                    exited=bool(select.select([pidfd],[],[],5)[0])
                    (output/(phase['name']+'.exec-terminal.json')).write_bytes(canonical({'execId':eid,'identity':exec_identity,'nativeHandleAuthorized':pidfd_authorized,'nativeHandleExitObserved':exited,'originalOutsideSdkDescendantCleanupRequired':True}))
                    need(exited or not pidfd_authorized,'Actual task-created exec exit unverified; retain original outside cleanup lease')
                except BaseException as cleanup_error:
                    cleanup_primary=sys.exc_info()
                    consumer_failure_evidence(output/(phase['name']+'.exec-cleanup-failure.json'),{'execId':eid,'failureType':type(cleanup_error).__name__,'primaryFailurePreserved':primary is not None,'originalOutsideCleanupRequired':True},primary or cleanup_primary)
                    if primary is None:
                        primary=cleanup_primary;raise
                finally:
                    try:os.close(pidfd)
                    except BaseException as close_error:
                        # Never retry an ambiguous native close or touch a reused fd.
                        consumer_failure_evidence(output/(phase['name']+'.exec-close-failure.json'),{'execId':eid,'failureType':type(close_error).__name__,'closeRetried':False,'primaryFailurePreserved':primary is not None},primary or sys.exc_info())
                        if primary is None:raise
        stdout=file_bytes(out);stderr=file_bytes(err)
        (output/(phase['name']+'.stdout')).write_bytes(stdout);(output/(phase['name']+'.stderr')).write_bytes(stderr)
        return {'exitCode':row['ExitCode'],'completed':True,'execId':eid,'containerId':cid,'actualCreatedRaw':owner['actualCreatedRaw'],'startedUtc':start.isoformat(),'finishedUtc':now().isoformat(),'stdoutSha256':digest(stdout),'stderrSha256':digest(stderr),'stdout':stdout.decode(errors='replace'),'stderr':stderr.decode(errors='replace')}
    def save(results):
        # Retain bounded hashes rather than leaking command output in scalar receipts.
        rows=[{k:v for k,v in row.items() if k not in ('stdout','stderr')} for row in results]
        (output/'phase-results.json').write_bytes(canonical(rows))
    primary=None
    try:
        terminal=consumer_phase_sequence(plan['phases'],execute,admit,save)
        status,results=backend.request('GET','/containers/'+cid+'/archive?path=/work/results')
        need(status==200,'Actual full raw result archive required before original owner cleanup')
        (output/'actual-sdk-results.tar').write_bytes(results)
        terminal['actualSdkResultsSha256']=digest(results)
        (output/'caller-terminal.json').write_bytes(canonical(terminal));return 0
    except BaseException:
        primary=sys.exc_info()
        consumer_failure_evidence(output/'caller-failure.json',{'failureType':type(primary[1]).__name__,'failureIsBehavioralRed':False,'nativeTestsAccepted':False,'originalOutsideCustodyRequired':True},primary)
        raise
    finally:
        # These SDK/proxy/recovery resources are owned by the original outside
        # consumer custodian. Never adopt or kill them through the caller.
        try:
            (output/'original-owner-continuation.json').write_bytes(canonical({'containerId':cid,'actualCreatedRaw':owner['actualCreatedRaw'],'custodySha256':args.consumer_custody_sha256,'expiresUtc':owner['expiresUtc'],'phaseCallerFinished':True,'originalOwnerCleanupRequired':True,'nativeTestsAccepted':False}))
        except BaseException:
            if primary is None:raise

def main():
    parser=argparse.ArgumentParser();parser.add_argument('mode',choices=['observe','receive','source-check','consumer-phases']);parser.add_argument('--output',type=Path);parser.add_argument('--nonce');parser.add_argument('--observation',type=Path);parser.add_argument('--harness',type=Path);parser.add_argument('--packet',type=Path)
    parser.add_argument('--consumer-custody');parser.add_argument('--consumer-custody-sha256');parser.add_argument('--source-directory',type=Path)
    args=parser.parse_args()
    if args.mode=='consumer-phases':return consumer_caller(args)
    if args.mode=='source-check':
        need(args.packet and args.harness and args.output is None and args.nonce is None
             and args.observation is None, 'Source check cannot request allocation/authority')
        capture_identity(args.packet)
        need(digest(regular(args.harness))==HARNESS_SHA256, 'Captured current harness seal differs')
        need(digest(regular(args.harness.parent/'owned_process.py'))==OWNER_HELPER_SHA256,
             'Captured original R7 custody helper differs')
        need(digest(regular(Path(__file__).parent/'external_unit_owner.py'))==EXTERNAL_OWNER_SHA256,
             'Captured original external owner adapter differs')
        print(json.dumps({'closedMembers':18,'rawFiles':324,'sourceBase':SOURCE_BASE,
                          'nativeBuildGranted':False,'actualLinuxRun':False,'sourceOnly':True}))
        return 0
    need(args.output is not None and args.nonce is not None, 'Existing native route output/nonce required')
    need(sys.platform=='linux','Linux platform required');args.output.mkdir(mode=0o700,parents=True,exist_ok=True)
    env=os.environ;need(env.get('GITHUB_ACTIONS')=='true' and env.get('GITHUB_REPOSITORY')==REPOSITORY,'Exact existing GitHub job required')
    run,attempt,head=env['GITHUB_RUN_ID'],env['GITHUB_RUN_ATTEMPT'],env['GITHUB_SHA'];ctx=context(run,attempt,args.nonce)
    need(args.packet,'Captured reviewed18packet required for readonly identity')
    identity_reader=capture_identity(args.packet)
    observer=lambda:snapshot(identity_reader)
    if args.mode=='observe':
        host=observer();issued=now();obs={'schemaVersion':1,'repository':REPOSITORY,'runId':run,'runAttempt':attempt,'sourceHead':head,'rootDispatchNonce':args.nonce,'statusContext':ctx,'issuedUtc':issued.isoformat(),'allocationDeadlineUtc':(issued+dt.timedelta(seconds=600)).isoformat(),'host':host}
        api=PublicGitHub();actual=api.get('/repos/'+REPOSITORY+'/commits/'+head);need(actual.get('sha')==head,'Actual source HEAD API differs');api.get('/repos/'+REPOSITORY+'/commits/'+head+'/statuses?per_page=1&page=1');api.blob(PROBE_BLOB_OID)
        save_new(args.output/'observation.json',obs);save_new(args.output/'read-permission-probe.json',{'repository':REPOSITORY,'head':head,'GET200':True,'credentialsSent':False,'requests':api.requests,'blobOid':PROBE_BLOB_OID,'blobGET200':True});return 0
    need(args.observation and args.harness and args.packet,'Exact observation/harness/packet required')
    obs=parse(regular(args.observation,65536));need((obs['runId'],obs['runAttempt'],obs['sourceHead'],obs['statusContext'])==(run,attempt,head,ctx),'Same job identity differs')
    stop={'cancelled':False}
    def cancel(*_):stop['cancelled']=True
    previous={sig:signal.signal(sig,cancel) for sig in (signal.SIGTERM,signal.SIGINT)}
    try:
        result=receive(obs,PublicGitHub(),observer,lambda cap,sha:execute_once(args.harness,args.packet,args.output/'kernel-results',cap,sha,observation=obs,identity_reader=identity_reader,cancelled=lambda:stop['cancelled']),args.output,cancelled=lambda:stop['cancelled'])
        return 0 if result['kernelQualified'] else 1
    finally:
        for sig,handler in previous.items():signal.signal(sig,handler)

if __name__=='__main__':raise SystemExit(main())
