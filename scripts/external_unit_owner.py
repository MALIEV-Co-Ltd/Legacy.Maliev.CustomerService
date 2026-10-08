"""SOURCE-ONLY adaptation of WebV8 exact external systemd/cgroup custody.
No native operations occur in pure controls. External manager is armed before
captured kernel entry; receiver/Worker stays outside the disposable service.
"""
import datetime as dt
import hashlib
import json
import os
from pathlib import Path
import re
import selectors
import stat
import subprocess
import time

PROPERTIES='Id,Description,InvocationID,ControlGroup,MainPID,LoadState,ActiveState,SubState,Result,ExecMainStatus,ExecMainStartTimestampMonotonic,ExecMainExitTimestampMonotonic,ExecStart,ExecStopPost,RuntimeMaxUSec,TimeoutStopUSec,TimeoutStartUSec,Type,NotifyAccess,ExecStop,MemoryMax,MemorySwapMax,CPUQuotaPerSecUSec,TasksMax,KillMode,SendSIGKILL,Restart,Delegate,NoNewPrivileges,ProtectControlGroups'
TIME_KEYS={'RuntimeMaxUSec','TimeoutStopUSec','TimeoutStartUSec','CPUQuotaPerSecUSec'}

def need(value,message):
    if not value:raise ValueError(message)
def utc(value):
    parsed=dt.datetime.fromisoformat(value.replace('Z','+00:00'));need(parsed.utcoffset()==dt.timedelta(0),'UTC required');return parsed
def now():return dt.datetime.now(dt.timezone.utc)
def canonical(row):return json.dumps(row,sort_keys=True,separators=(',',':'),allow_nan=False).encode()
def digest(raw):return hashlib.sha256(raw).hexdigest()
def duration(value):
    if re.fullmatch(r'[0-9]{1,16}',value):return int(value)
    scale={'us':1,'ms':1000,'s':1000000,'min':60000000,'h':3600000000}
    parts=re.findall(r'([0-9]+)(us|ms|min|s|h)',value)
    need(parts and ''.join(a+b for a,b in parts)==value.replace(' ',''),'Finite manager duration required')
    result=sum(int(a)*scale[b] for a,b in parts);need(0<=result<=3600000000,'Duration cap');return result

def atomic_new(path,row):
    path=Path(path);raw=canonical(row);need(len(raw)<=65536,'Receipt byte bound')
    temporary=path.with_name(path.name+'.publishing')
    with temporary.open('xb') as stream:stream.write(raw);stream.flush();os.fsync(stream.fileno())
    try:os.link(temporary,path)
    finally:temporary.unlink()
    if os.name=='posix':
        descriptor=os.open(path.parent,os.O_RDONLY|getattr(os,'O_DIRECTORY',0))
        try:os.fsync(descriptor)
        finally:os.close(descriptor)
    return digest(raw)

class LinuxBackend:
    """Actual IO backend: called only after external Root authority and review."""
    def __init__(self):
        self.command_resources=[]
        need(os.name=='posix' and Path('/sys/fs/cgroup/cgroup.controllers').is_file(),'Unified Linux manager required')
    def command(self,*argv,timeout=10):
        need(0<timeout<=40,'Finite manager command timeout required')
        if argv[:2]==('sudo','-n'):
            argv=('sudo','-n','/usr/bin/timeout','--signal=TERM','--kill-after=2s',str(timeout)+'s',*argv[2:])
        else:
            need(argv[:2]==('/usr/bin/systemctl','show'),'Only readonly unprivileged manager query allowed')
            argv=('/usr/bin/timeout','--signal=TERM','--kill-after=2s',str(timeout)+'s',*argv)
        def limits():
            import resource
            resource.setrlimit(resource.RLIMIT_AS,(134217728,134217728));resource.setrlimit(resource.RLIMIT_CPU,(10,10));resource.setrlimit(resource.RLIMIT_CORE,(0,0))
        process=subprocess.Popen(argv,stdin=subprocess.DEVNULL,stdout=subprocess.PIPE,stderr=subprocess.PIPE,preexec_fn=limits)
        selector=None;birth=None;primary=None;traceback=None;result=None;deadline=time.monotonic()+timeout+3
        try:
            birth=Path('/proc/'+str(process.pid)+'/stat').read_text().rsplit(')',1)[1].split()[19]
            selector=selectors.DefaultSelector();selector.register(process.stdout,selectors.EVENT_READ);selector.register(process.stderr,selectors.EVENT_READ)
            chunks=[];total=0
            while selector.get_map():
                need(time.monotonic()<deadline,'Manager helper finite deadline elapsed')
                for key,_ in selector.select(min(.1,max(0,deadline-time.monotonic()))):
                    raw=os.read(key.fileobj.fileno(),8192)
                    if not raw:selector.unregister(key.fileobj);continue
                    total+=len(raw);need(total<=65536,'Manager output bound exceeded')
                    if key.fileobj is process.stdout:chunks.append(raw)
            process.wait(timeout=max(.01,deadline-time.monotonic()));need(process.returncode==0,'Actual manager command failed')
            result=b''.join(chunks).decode('utf-8')
        except BaseException as error:primary=error;traceback=error.__traceback__
        try:
            if selector is not None:selector.close()
            process.stdout.close();process.stderr.close()
            if process.poll() is None:
                if birth is not None:
                    current=Path('/proc/'+str(process.pid)+'/stat').read_text().rsplit(')',1)[1].split()[19]
                    need(current==birth,'Manager helper PID generation changed')
                    try:process.terminate()
                    except PermissionError:pass
                # Independent root-side GNUtimeout remains the expiry owner.
                process.wait(timeout=max(.01,deadline+1-time.monotonic()))
        except BaseException as error:
            if primary is None:primary=error;traceback=error.__traceback__
            else:primary.add_note('Manager helper cleanup uncertainty: '+repr(error))
        self.command_resources.append({'pid':process.pid,'startTicks':birth,'exitCode':process.returncode,'remainingHelper':process.returncode is None,'maxOutputBytes':65536,'memoryLimitBytes':134217728,'CPUTimeLimitSeconds':10})
        if primary is not None:raise primary.with_traceback(traceback)
        return result
    def boot(self):return Path('/proc/sys/kernel/random/boot_id').read_text().strip()
    def show(self,unit):
        raw=self.command('/usr/bin/systemctl','show',unit,'--no-pager','--property='+PROPERTIES)
        rows={}
        for line in raw.splitlines():
            key,value=line.split('=',1);need(key not in rows,'Duplicate manager property');rows[key]=value
        return rows
    def dispatch(self,intent,argv):
        props=intent['managerProperties'];args=['sudo','-n','/usr/bin/systemd-run','--unit='+intent['unit'],'--description='+intent['description'],'--service-type=exec','--remain-after-exit']
        import pwd
        args+=['--property=User='+pwd.getpwuid(os.getuid()).pw_name]
        for key,value in props.items():
            if key=='RuntimeMaxUSec':key='RuntimeMaxSec';value=str(int(value)//1000000)
            elif key in {'TimeoutStopUSec','TimeoutStartUSec'}:key=key.replace('USec','Sec');value=str(int(value)//1000000)
            elif key=='CPUQuotaPerSecUSec':key='CPUQuota';value='25%'
            args.append('--property='+key+'='+value)
        args+=['--property=ExecStopPost='+self.stop_hook_command,'--property=LimitCORE=0','--property=LimitFSIZE=8388608','--setenv=PYTHONDONTWRITEBYTECODE=1',*argv]
        return self.command(*args)
    def stop(self,unit,timeout=40):return self.command('sudo','-n','/usr/bin/systemctl','stop',unit,timeout=timeout)
    def reset(self,unit):return self.command('sudo','-n','/usr/bin/systemctl','reset-failed',unit)
    def members(self,group):
        need(re.fullmatch(r'/system.slice/customer-kernel-[0-9]+-[0-9]+-[a-f0-9]{32}\.service',group),'Exact private cgroup required')
        root=Path('/sys/fs/cgroup')/group.lstrip('/')
        def census():
            try:rootinfo=root.lstat()
            except FileNotFoundError:return None
            need(stat.S_ISDIR(rootinfo.st_mode) and not stat.S_ISLNK(rootinfo.st_mode),'Actual cgroup directory required')
            rows=[];queue=[root];dirs=[]
            while queue:
                path=queue.pop();info=path.lstat();need(stat.S_ISDIR(info.st_mode) and not stat.S_ISLNK(info.st_mode),'Actual subtree directory required')
                dirs.append((str(path),info.st_dev,info.st_ino));need(len(dirs)<=64,'Subtree directory bound')
                with (path/'cgroup.procs').open('rb') as stream:raw=stream.read(8193)
                need(len(raw)<=8192,'Cgroup membership byte bound');values=raw.split()
                need(all(x.isdigit() and len(x)<=10 for x in values),'Malformed actual cgroup members')
                rows.extend(int(x) for x in values);need(len(rows)<=512,'Cgroup member bound')
                with os.scandir(path) as children:
                    for entry in children:
                        info=entry.stat(follow_symlinks=False);need(not stat.S_ISLNK(info.st_mode),'Cgroup link refused')
                        if stat.S_ISDIR(info.st_mode):queue.append(Path(entry.path))
            return (sorted(dirs),sorted(set(rows)))
        first=census();second=census();need(first==second,'Cgroup subtree changed during census')
        return [] if first is None else first[1]
    def witness(self,directory):
        path=Path(directory)/'external-owner-stop-witness.json'
        info=path.lstat();need(stat.S_ISREG(info.st_mode) and info.st_size<=65536,'Actual bounded stop witness required')
        return json.loads(path.read_bytes())


class ExternalOwner:
    def __init__(self,backend,clock=now,monotonic=time.monotonic,sleep=time.sleep,writer=atomic_new,cancelled=lambda:False,worker_alive=lambda:True):
        self.expected_argv=None;self.expected_hook=None
        self.backend=backend;self.clock=clock;self.monotonic=monotonic;self.sleep=sleep;self.writer=writer;self.cancelled=cancelled;self.worker_alive=worker_alive
    def identity(self,intent,state,invocation=None):
        need(self.backend.boot()==intent['routeIdentity']['hostBootId'],'External owner boot changed')
        need(state.get('Id')==intent['unit'] and state.get('Description')==intent['description'] and state.get('ControlGroup')==intent['controlGroup'],'Exact manager owner changed')
        actual=state.get('InvocationID','');need(re.fullmatch(r'[a-f0-9]{32}',actual),'Actual InvocationID required')
        need(invocation is None or actual==invocation,'Retained InvocationID reused')
        for key,value in intent['managerProperties'].items():
            actualvalue=state.get(key,'')
            if key in TIME_KEYS:actualvalue=str(duration(actualvalue))
            need(actualvalue==value,'Actual manager caps differ: '+key)
        need(int(state.get('ExecMainStartTimestampMonotonic','0'))>0,'Actual activation evidence required')
        if self.expected_argv is not None:
            need(state.get('ExecStart','').count('{')==1 and state.get('ExecStart','').count('}')==1 and state.get('ExecStart','').count('argv[]=')==1,'Exactly one actual ExecStart command required')
            match=re.search(r'argv\[\]=([^;]+)',state.get('ExecStart',''))
            need(match is not None and match.group(1).strip().split()==self.expected_argv,'Exact sealed manager ExecStart differs')
        if self.expected_hook is not None:
            need(state.get('ExecStopPost','').count('{')==1 and state.get('ExecStopPost','').count('}')==1 and state.get('ExecStopPost','').count('argv[]=')==1,'Exactly one actual stop hook required')
            match=re.search(r'argv\[\]=([^;]+)',state.get('ExecStopPost',''))
            need(match is not None and match.group(1).strip().split()==self.expected_hook,'Exact sealed stop-witness ExecStopPost differs')
        return actual
    def settle(self,intent,ledger,deadline_seconds=100):
        self.expected_hook=ledger.get('stopHookArgv');self.expected_argv=ledger.get('argv');need(self.expected_argv is not None,'Retained exact ExecStart required')
        deadline=self.monotonic()+deadline_seconds;invocation=ledger.get('invocationId')
        need(ledger.get('dispatchAttempted') is True,'No dispatched resource to settle')
        last=None
        while self.monotonic()<deadline:
            try:
                state=self.backend.show(intent['unit'])
                if state.get('LoadState')=='not-found':
                    witness=self.backend.witness(ledger['directory'])
                    need(witness['intentSha256']==ledger['intentSha256'] and witness['unit']==intent['unit'] and witness['description']==intent['description'] and witness['controlGroup']==intent['controlGroup'],'Actual causal stop witness differs')
                    need(witness['MainPID']==0 and witness['mainExited'] is True and witness['onlyWitnessMember'] is True,'Actual main/member terminal witness required')
                    invocation=self.identity(intent,witness['terminalManager'],invocation)
                    need(witness['invocationId']==invocation and witness['bootId']==intent['routeIdentity']['hostBootId'] and not self.backend.members(intent['controlGroup']),'Independent actual stop witness/member census failed')
                    need(self.backend.show(intent['unit']).get('LoadState')=='not-found' and not self.backend.members(intent['controlGroup']),'Unit/member identity changed after stop census')
                    ledger.update(invocationId=invocation,controlGroup=intent['controlGroup'],stopWitness=witness,terminalManager=witness['terminalManager'],terminalManagerObservation=state,cleanupVerified=True,allSubtreeMembersAbsent=True,managerUnitAbsent=True)
                    return ledger
                invocation=self.identity(intent,state,invocation)
                ledger['invocationId']=invocation;ledger['controlGroup']=intent['controlGroup']
                # Reobserve exact generation immediately before the manager stop.
                self.identity(intent,self.backend.show(intent['unit']),invocation)
                ledger.setdefault('acquiredManager',state)
                if not ledger.get('settlementAcquisitionSaved'):
                    self.writer(Path(ledger['directory'])/'external-owner-settlement-acquired.json',ledger);ledger['settlementAcquisitionSaved']=True
                self.backend.stop(intent['unit'],timeout=min(40,max(.01,deadline-self.monotonic())))
                after=self.backend.show(intent['unit'])
                unloaded=after.get('LoadState')=='not-found'
                if unloaded:
                    ledger['terminalManagerObservation']=after
                    witness=self.backend.witness(ledger['directory'])
                    need(witness['invocationId']==invocation and witness['intentSha256']==ledger['intentSha256'] and witness['bootId']==intent['routeIdentity']['hostBootId'],'Actual invocation-bound stop witness required')
                    need(witness['MainPID']==0 and witness['mainExited'] is True and witness['onlyWitnessMember'] is True,'Actual post-stop main/member witness required')
                    after=witness['terminalManager'];ledger['stopWitness']=witness
                self.identity(intent,after,invocation)
                need((unloaded or after['ActiveState'] in {'inactive','failed'}) and int(after.get('MainPID','0'))==0,'Owned service still active')
                need(int(after.get('ExecMainExitTimestampMonotonic','0'))>=int(after['ExecMainStartTimestampMonotonic']),'Manager exit evidence missing')
                need(not self.backend.members(intent['controlGroup']),'Live nested owned members remain')
                # Repeat after the full census, so reuse cannot certify absence.
                current=self.backend.show(intent['unit'])
                if current.get('LoadState')!='not-found':self.identity(intent,current,invocation)
                else:need('stopWitness' in ledger,'Actual retained stop witness required before absence')
                ledger['cleanupVerified']=True;ledger['terminalManager']=after;ledger['allSubtreeMembersAbsent']=True
                current=self.backend.show(intent['unit'])
                if current.get('LoadState')!='not-found':self.identity(intent,current,invocation);self.backend.reset(intent['unit'])
                gone=self.backend.show(intent['unit'])
                need(gone.get('LoadState')=='not-found' and not self.backend.members(intent['controlGroup']),'Disposable manager/cgroup retained')
                ledger['managerUnitAbsent']=True;return ledger
            except (OSError,subprocess.SubprocessError) as error:
                last=error;self.sleep(min(.25,max(0,deadline-self.monotonic())))
        raise TimeoutError('Bounded manager settlement unresolved') from last
    def launch(self,intent,argv,output):
        output=Path(output);self.expected_argv=list(argv)
        self.expected_hook=['/usr/bin/python3','-B',Path(__file__).absolute().as_posix(),'stop-witness','--directory',output.absolute().as_posix()]
        need(all(re.fullmatch(r'[-a-zA-Z0-9_./:=]+',x) for x in self.expected_hook),'Controlled unambiguous stop hook argv required')
        self.backend.stop_hook_command=' '.join(self.expected_hook)
        need(all(re.fullmatch(r'[-a-zA-Z0-9_./:=]+',x) for x in argv),'Controlled unambiguous manager argv required')
        ledger={'schemaVersion':1,'unit':intent['unit'],'description':intent['description'],'intent':intent,'directory':output.absolute().as_posix(),'stopHookArgv':self.expected_hook,'argv':list(argv),'intentSha256':digest(canonical(intent)),'dispatchAttempted':False,'dispatchCalled':False,'cleanupVerified':False,'kernelQualified':False}
        primary=None;traceback=None;invocation=None
        try:
            need(not self.cancelled() and self.worker_alive(),'Cancelled or original Worker changed before dispatch')
            need(self.backend.show(intent['unit']).get('LoadState')=='not-found','Existing manager owner refused')
            self.writer(output/'external-owner-intent.json',intent)
            ledger['dispatchAttempted']=True
            self.writer(output/'external-owner-dispatch.json',{'unit':intent['unit'],'intentSha256':ledger['intentSha256'],'dispatchAttempted':True,'argv':list(argv),'stopHookArgv':self.expected_hook})
            need((utc(intent['expiresUtc'])-self.clock()).total_seconds()>=int(intent['managerProperties']['RuntimeMaxUSec'])/1000000+5*int(intent['managerProperties']['TimeoutStopUSec'])/1000000+int(intent['managerProperties']['TimeoutStartUSec'])/1000000,'Original authority start/stop budget elapsed')
            ledger['dispatchCalled']=True
            self.backend.dispatch(intent,argv)
            acquire=self.monotonic()+10
            while self.monotonic()<acquire:
                state=self.backend.show(intent['unit'])
                if state.get('InvocationID'):
                    invocation=self.identity(intent,state);ledger['invocationId']=invocation
                    ledger['acquiredManager']=state;ledger['bootId']=self.backend.boot()
                    self.writer(output/'external-owner-acquired.json',ledger);break
                self.sleep(.1)
            need(invocation is not None,'Actual manager acquisition unresolved')
            end=utc(intent['expiresUtc'])-dt.timedelta(seconds=30)
            mono_end=self.monotonic()+max(0,(end-self.clock()).total_seconds())
            while self.clock()<end and self.monotonic()<mono_end:
                need(not self.cancelled() and self.worker_alive(),'Cancelled or original Worker changed during service')
                state=self.backend.show(intent['unit']);self.identity(intent,state,invocation)
                if state.get('SubState')=='exited' or state.get('ActiveState') in {'inactive','failed'}:
                    need(state.get('Result')=='success' and state.get('ExecMainStatus')=='0','Captured kernel service failed')
                    ledger['kernelQualified']=True;break
                self.sleep(.25)
            else:raise TimeoutError('Original capability kernel runtime elapsed')
        except BaseException as error:primary=error;traceback=error.__traceback__;ledger['kernelQualified']=False;ledger['failureType']=type(error).__name__
        try:
            if ledger['dispatchCalled']:self.settle(intent,ledger)
            else:ledger['cleanupVerified']=True;ledger['noDispatch']=True
        except BaseException as error:
            ledger['kernelQualified']=False;ledger['cleanupVerified']=False;ledger['cleanupFailureType']=type(error).__name__
            if primary is None:primary=error;traceback=error.__traceback__
            else:primary.add_note('External settlement uncertainty: '+repr(error))
        try:
            need(not self.cancelled(),'Cancellation during external settlement')
            self.writer(output/'external-owner-terminal.json',ledger)
            need(not self.cancelled(),'Cancellation during external terminal durability')
        except BaseException as error:
            ledger['kernelQualified']=False
            if primary is None:primary=error;traceback=error.__traceback__
            else:primary.add_note('External terminal/cancellation uncertainty: '+repr(error))
        if primary is not None:raise primary.with_traceback(traceback)
        return ledger


def read_receipt(directory,name):
    path=Path(directory)/name;info=path.lstat()
    need(stat.S_ISREG(info.st_mode) and info.st_size<=65536,'Bounded regular retained receipt required')
    raw=path.read_bytes();need(len(raw)<=65536,'Receipt grew')
    def pairs(rows):
        result={}
        for key,value in rows:need(key not in result,'Duplicate receipt key');result[key]=value
        return result
    return raw,json.loads(raw,object_pairs_hook=pairs,parse_constant=lambda _:(_ for _ in ()).throw(ValueError('Nonfinite receipt refused')))

def stop_witness(directory,backend=None):
    directory=Path(directory);raw,intent=read_receipt(directory,'external-owner-intent.json');_,dispatch=read_receipt(directory,'external-owner-dispatch.json')
    need(dispatch['intentSha256']==digest(raw) and dispatch['unit']==intent['unit'],'Exact predispatch intent witness required')
    need(intent['externalAdapter']['path']==Path(__file__).absolute().as_posix() and intent['externalAdapter']['sha256']==digest(Path(__file__).read_bytes()),'Stop hook adapter seal differs')
    backend=backend or LinuxBackend();owner=ExternalOwner(backend);owner.expected_argv=dispatch['argv'];owner.expected_hook=dispatch['stopHookArgv']
    need(owner.expected_hook==intent['externalAdapter']['execStopPost'],'Actual sealed stop hook differs')
    state=backend.show(intent['unit']);invocation=owner.identity(intent,state)
    need(int(state['MainPID'])==0 and int(state['ExecMainExitTimestampMonotonic'])>=int(state['ExecMainStartTimestampMonotonic']),'Actual main exit witness required')
    self_pid=os.getpid();need(Path('/proc/self/cgroup').read_text().strip()=='0::'+intent['controlGroup'],'Stop witness actual unit membership differs')
    need(backend.members(intent['controlGroup'])==[self_pid],'Unexpected surviving owned members during stop witness')
    owner.identity(intent,backend.show(intent['unit']),invocation)
    row={'schemaVersion':1,'intentSha256':digest(raw),'unit':intent['unit'],'description':intent['description'],'invocationId':invocation,'bootId':backend.boot(),'controlGroup':intent['controlGroup'],'MainPID':0,'mainExited':True,'onlyWitnessMember':True,'witnessPID':self_pid,'terminalManager':state,'SERVICE_RESULT':os.environ.get('SERVICE_RESULT'),'EXIT_CODE':os.environ.get('EXIT_CODE'),'EXIT_STATUS':os.environ.get('EXIT_STATUS'),'checkedUtc':now().isoformat()}
    atomic_new(directory/'external-owner-stop-witness.json',row)
    return row

def settle_from_directory(directory,backend=None):
    directory=Path(directory);raw,intent=read_receipt(directory,'external-owner-intent.json');_,dispatch=read_receipt(directory,'external-owner-dispatch.json')
    need(dispatch['intentSha256']==digest(raw) and dispatch['unit']==intent['unit'],'Original dispatched intent seal differs')
    need(intent['externalAdapter']['path']==Path(__file__).absolute().as_posix() and intent['externalAdapter']['sha256']==digest(Path(__file__).read_bytes()),'Always-settlement adapter source differs')
    backend=backend or LinuxBackend();acquired=None
    for name in ('external-owner-terminal.json','external-owner-acquired.json','external-owner-settlement-acquired.json'):
        try:_,candidate=read_receipt(directory,name)
        except FileNotFoundError:continue
        need(candidate['intentSha256']==digest(raw) and candidate['intent']==intent,'Retained acquisition intent changed')
        if candidate.get('invocationId'):acquired=candidate;break
    if acquired is None:
        witness=backend.witness(directory)
        need(witness['intentSha256']==digest(raw) and witness['unit']==intent['unit'] and witness['description']==intent['description'] and witness['controlGroup']==intent['controlGroup'],'Actual causal stop witness differs')
        acquired={'schemaVersion':1,'unit':intent['unit'],'description':intent['description'],'intent':intent,'intentSha256':digest(raw),'argv':dispatch['argv'],'stopHookArgv':dispatch['stopHookArgv'],'directory':str(directory.absolute()),'dispatchAttempted':True,'invocationId':witness['invocationId'],'recoveredFromActualStopWitness':True}
    need(acquired.get('invocationId'),'Actual acquired InvocationID missing; absence does not prove cleanup')
    owner=ExternalOwner(backend);primary=None
    try:
        state=backend.show(intent['unit'])
        if state.get('LoadState')=='not-found' and acquired.get('cleanupVerified') is True and acquired.get('allSubtreeMembersAbsent') is True and acquired.get('managerUnitAbsent') is True:
            owner.expected_argv=acquired['argv'];owner.expected_hook=acquired['stopHookArgv']
            owner.identity(intent,acquired['terminalManager'],acquired['invocationId'])
            need(backend.boot()==intent['routeIdentity']['hostBootId'] and not backend.members(intent['controlGroup']),'Independent post-workflow boot/member census failed')
            need(backend.show(intent['unit']).get('LoadState')=='not-found' and not backend.members(intent['controlGroup']),'Unit reused during independent census')
        else:owner.settle(intent,acquired,deadline_seconds=100)
        acquired['independentAlwaysSettlementVerified']=True
    except BaseException as error:primary=error;acquired['cleanupVerified']=False;acquired['alwaysSettlementFailure']=type(error).__name__
    try:atomic_new(directory/'external-owner-always-settlement.json',acquired)
    except BaseException as secondary:
        if primary is None:primary=secondary
        else:primary.add_note('Always-settlement durability uncertainty: '+repr(secondary))
    if primary is not None:raise primary
    return acquired

if __name__=='__main__':
    import argparse
    parser=argparse.ArgumentParser();parser.add_argument('mode',choices=['settle','stop-witness']);parser.add_argument('--directory',type=Path,required=True)
    args=parser.parse_args()
    if args.mode=='stop-witness':stop_witness(args.directory)
    else:settle_from_directory(args.directory)
