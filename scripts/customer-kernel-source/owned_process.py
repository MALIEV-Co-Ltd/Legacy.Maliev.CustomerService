"""Task-created Linux children: immediate custody and exact native-handle cleanup.

No PID/name adoption. The Popen child remains owned while unreaped; pidfd is the
preferred signal handle. Fallback requires a fresh waitid(WNOWAIT) child binding.
Signals are deferred until custody is registered, then fail closed at checkpoints.
"""
import os
import signal
import subprocess
import sys
import time
import datetime as dt
from pathlib import Path
import re
import selectors
import hashlib
import json
from contextlib import contextmanager

NATIVE_CLOSE = os.close
EXPECTED_EXTERNAL_ADAPTER_SHA='9b1f9278beb9f9fbc6ae20fff2d356f6b588b8ebcfe905fc0faa867ef9f35162'


class OwnershipError(ValueError):
    pass


class DeferredInterruption(InterruptedError):
    pass


def duration_usec(value):
    if value.isdecimal():
        return int(value)
    scales={'us':1,'ms':1000,'s':1000000,'min':60000000,'h':3600000000}
    tokens=re.findall(r'(\d+(?:\.\d+)?)(us|ms|min|s|h)',value.replace(' ',''))
    if not tokens or ''.join(n+u for n,u in tokens)!=value.replace(' ',''):
        raise OwnershipError('Finite manager duration required')
    from decimal import Decimal
    result=sum(Decimal(n)*scales[u] for n,u in tokens)
    if result != int(result):
        raise OwnershipError('Exact microsecond duration required')
    return int(result)


class ExternalUnitCustody:
    """Read-only binding to the existing outside systemd service owner.

    This never launches/stops a unit and never creates authority. The separately
    sealed route launcher performs exact-invocation settlement outside this unit.
    """
    FIXED={'MemoryMax':'268435456','MemorySwapMax':'0','CPUQuotaPerSecUSec':'250000',
           'TasksMax':'64','KillMode':'control-group','SendSIGKILL':'yes','Restart':'no',
           'Delegate':'no','NoNewPrivileges':'yes','ProtectControlGroups':'yes','Type':'exec','NotifyAccess':'none',
           'TimeoutStartUSec':'10000000','ExecStop':''}
    def __init__(self,intent,capability,observe=None,membership=None,boot=None,clock=None,utc=None,
                 expected_exec=None,required_exec=None,require_main=True,process_identity=None,group_members=None,adapter_reader=None):
        keys={'schemaVersion','scope','originalObserverSha256','capabilitySha256','harnessSha256',
              'packetManifestSha256','unit','description','controlGroup','issuedUtc','expiresUtc',
              'routeIdentity','managerProperties','externalAdapter'}
        if set(intent)!=keys or type(intent['schemaVersion']) is not int or intent['schemaVersion']!=1 or intent['scope']!='customer-kernel-external-custody':
            raise OwnershipError('Exact external owner intent required')
        if not re.fullmatch(r'customer-kernel-[a-zA-Z0-9-]+\.service',intent['unit']):
            raise OwnershipError('Scoped manager unit required')
        if intent['controlGroup']!='/system.slice/'+intent['unit'] or not intent['description']:
            raise OwnershipError('Exact owner nonce/cgroup required')
        self.intent,self.capability=intent,capability
        self.observe=observe or self._observe
        self.membership=membership or self._membership
        self.boot=boot or (lambda:Path('/proc/sys/kernel/random/boot_id').read_text().strip())
        self.clock=clock or time.monotonic
        self.utc=utc or (lambda:dt.datetime.now(dt.timezone.utc))
        self.invocation=None;self.witness=None
        self.expected_exec=expected_exec;self.required_exec=required_exec
        self.require_main=require_main;self.main_identity=None
        self.process_identity=process_identity or self._process_identity
        self.group_members=group_members or self._group_members
        adapter=intent['externalAdapter']
        if set(adapter)!={'path','sha256','execStopPost'} or not re.fullmatch(r'[0-9a-f]{64}',adapter['sha256']):
            raise OwnershipError('Exact sealed external adapter required')
        self.expected_adapter_sha=EXPECTED_EXTERNAL_ADAPTER_SHA
        if adapter['sha256']!=self.expected_adapter_sha:
            raise OwnershipError('Caller-chosen external adapter is not the sealed owner source')
        if adapter['execStopPost'][:5]!=['/usr/bin/python3','-B',adapter['path'],'stop-witness','--directory'] or len(adapter['execStopPost'])!=6:
            raise OwnershipError('Exact stop witness argv required')
        self.adapter_reader=adapter_reader or self._read_adapter
        adapter_raw=self.adapter_reader(adapter['path'])
        if hashlib.sha256(adapter_raw).hexdigest()!=adapter['sha256']:raise OwnershipError('Outside adapter source seal differs')
        start=self._utc(capability['issuedUtc']);end=self._utc(capability['expiresUtc'])
        if not 0 < (end-start).total_seconds() <= 600 or intent['expiresUtc']!=capability['expiresUtc']:
            raise OwnershipError('Original finite600s authority required')
        if self._utc(intent['issuedUtc'])<start or self._utc(intent['issuedUtc'])>=end:
            raise OwnershipError('External intent outside original authority')
        props=intent['managerProperties']
        if set(props)!=set(self.FIXED)|{'RuntimeMaxUSec','TimeoutStopUSec'}:
            raise OwnershipError('Exact manager property set required')
        if any(props[k]!=v for k,v in self.FIXED.items()):
            raise OwnershipError('Manager caps/security differ')
        if not 0 < duration_usec(props['RuntimeMaxUSec']) <= 600000000 or not 0 < duration_usec(props['TimeoutStopUSec']) <= 24000000:
            raise OwnershipError('Manager runtime/cleanup bounds differ')
        self.expires=end

    @staticmethod
    def _utc(value):
        result=dt.datetime.fromisoformat(value.replace('Z','+00:00'))
        if result.utcoffset()!=dt.timedelta(0):
            raise OwnershipError('UTC owner deadline required')
        return result

    def _observe(self):
        fields=['Id','Description','InvocationID','MainPID','ControlGroup','ActiveState','SubState','LoadState',
                'ActiveEnterTimestampMonotonic','ExecStart','ExecStop','ExecStopPost',*self.intent['managerProperties']]
        raw=self._bounded_command(['systemctl','show',self.intent['unit'],'--no-pager','--property='+','.join(fields)])
        rows={}
        for line in raw.decode('utf-8').splitlines():
            key,value=line.split('=',1)
            if key in rows: raise OwnershipError('Duplicate manager property')
            rows[key]=value
        return rows

    @staticmethod
    def _bounded_command(argv):
        process=None;selector=selectors.DefaultSelector();out=bytearray();err=bytearray()
        try:
            process=subprocess.Popen(argv,stdin=subprocess.DEVNULL,stdout=subprocess.PIPE,stderr=subprocess.PIPE)
            for stream,target in ((process.stdout,out),(process.stderr,err)):
                os.set_blocking(stream.fileno(),False);selector.register(stream,selectors.EVENT_READ,target)
            deadline=time.monotonic()+5
            while selector.get_map():
                if time.monotonic()>=deadline:raise TimeoutError('Manager observation5s deadline')
                for key,_ in selector.select(min(.1,max(0,deadline-time.monotonic()))):
                    chunk=os.read(key.fileobj.fileno(),4096)
                    if not chunk:selector.unregister(key.fileobj);continue
                    if len(key.data)+len(chunk)>65536:raise OwnershipError('Manager stream cap')
                    key.data.extend(chunk)
            if process.wait(timeout=max(.001,deadline-time.monotonic()))!=0:raise OwnershipError('Manager observation failed')
            return bytes(out)
        finally:
            selector.close()
            if process is not None:
                if process.poll() is None:
                    try:process.terminate();process.wait(timeout=1)
                    except subprocess.TimeoutExpired:process.kill();process.wait(timeout=1)
                process.wait(timeout=0)
                for stream in (process.stdout,process.stderr):
                    if stream is not None:stream.close()

    @staticmethod
    def _process_identity(pid):
        path=Path('/proc',str(pid));first=(path/'stat').read_text();exe=os.readlink(path/'exe');second=(path/'stat').read_text()
        def start(raw):
            fields=raw.rsplit(')',1)[1].split()
            return int(fields[19])
        if start(first)!=start(second):raise OwnershipError('Manager main PID reuse')
        return {'pid':pid,'startTicks':start(first),'executable':exe}

    @staticmethod
    def _read_adapter(value):
        path=Path(value)
        if not path.is_absolute() or any(part.is_symlink() for part in (path,*path.parents)):
            raise OwnershipError('External adapter path must be exact regular source')
        if not path.is_file() or path.stat().st_size>262144:raise OwnershipError('Bounded adapter source required')
        raw=path.read_bytes()
        if len(raw)>262144:raise OwnershipError('Adapter source grew')
        return raw

    @staticmethod
    def _argv(value):
        match=re.search(r'argv\[\]=(.*?) ;',value)
        if not match or value.count('argv[]=')!=1:raise OwnershipError('Exactly one actual manager command required')
        if value.count('{')!=1 or value.count('}')!=1:
            raise OwnershipError('Exactly one actual manager command block required')
        argv=match.group(1).split(' ')
        if any(not item or any(c in item for c in '\\;\t\n\r') for item in argv):
            raise OwnershipError('Ambiguous manager argv refused')
        return argv

    @staticmethod
    def _group_members(group):
        root=Path('/sys/fs/cgroup')/group.lstrip('/')
        if not root.is_dir():raise OwnershipError('Cgroup unavailable; not proof of terminal absence')
        def scan():
            members=set();directories=[]
            def failed(error):raise error
            for directory,children,files in os.walk(root,followlinks=False,onerror=failed):
                directories.append(str(Path(directory).relative_to(root)))
                if len(directories)>128:raise OwnershipError('Cgroup census directory cap')
                path=Path(directory)
                if path.is_symlink() or any((path/child).is_symlink() for child in children):
                    raise OwnershipError('Cgroup census links refused')
                raw=(path/'cgroup.procs').read_text()
                if len(raw)>65536:raise OwnershipError('Cgroup census cap')
                for item in raw.split():
                    if not item.isdecimal() or int(item)<=0:raise OwnershipError('Malformed actual membership')
                    members.add(int(item))
                if len(members)>64:raise OwnershipError('Unexpected manager process count')
            return tuple(sorted(directories)),frozenset(members)
        first=scan();second=scan()
        if first!=second:raise OwnershipError('Whole cgroup subtree census unstable')
        return set(first[1])

    def _verify_exec(self,state):
        if self.expected_exec is None and self.required_exec is None:return
        argv=self._argv(state.get('ExecStart',''))
        if self.expected_exec is not None and argv!=self.expected_exec:raise OwnershipError('Actual manager ExecStart differs')
        if self.required_exec is not None:
            prefix,required=self.required_exec
            if argv[:len(prefix)]!=prefix or '--worker' in argv:raise OwnershipError('Manager kernel executable differs')
            for flag,value in required.items():
                if argv.count(flag)!=1 or argv[argv.index(flag)+1]!=value:raise OwnershipError('Actual manager authority argv differs')
        return hashlib.sha256(json.dumps(argv,separators=(',',':')).encode()).hexdigest()

    @staticmethod
    def _membership(pid):
        raw=Path('/proc',str(pid),'cgroup').read_text()
        rows=[line[3:] for line in raw.splitlines() if line.startswith('0::')]
        if len(rows)!=1: raise OwnershipError('Actual unified membership required')
        return rows[0]

    def _verify(self,records,admission):
        if hashlib.sha256(self.adapter_reader(self.intent['externalAdapter']['path'])).hexdigest()!=self.expected_adapter_sha:
            raise OwnershipError('Sealed outside adapter changed after binding')
        state=self.observe();expected=self.intent
        if state.get('Id')!=expected['unit'] or state.get('Description')!=expected['description'] or state.get('ControlGroup')!=expected['controlGroup']:
            raise OwnershipError('Exact manager identity drift')
        invocation=state.get('InvocationID','')
        if not re.fullmatch(r'[0-9a-f]{32}',invocation) or (self.invocation and self.invocation!=invocation):
            raise OwnershipError('Exact invocation unavailable/drift')
        if state.get('LoadState')!='loaded' or state.get('ActiveState')!='active' or state.get('SubState')!='running':
            raise OwnershipError('Live outside manager custody unavailable')
        if self.boot()!=self.capability['hostBootId'] or self.membership(os.getpid())!=expected['controlGroup']:
            raise OwnershipError('Actual self boot/membership differs')
        if not str(state.get('MainPID','')).isdecimal() or int(state['MainPID'])<=0 or self.membership(int(state['MainPID']))!=expected['controlGroup']:
            raise OwnershipError('Actual service main ownership unproved')
        main_pid=int(state['MainPID'])
        if self.require_main and main_pid!=os.getpid():raise OwnershipError('Kernel service entry must be actual manager MainPID')
        main_identity=self.process_identity(main_pid)
        if self.main_identity is not None and main_identity!=self.main_identity:raise OwnershipError('Manager main start/executable changed')
        argv_digest=self._verify_exec(state)
        if self._argv(state.get('ExecStopPost',''))!=expected['externalAdapter']['execStopPost']:
            raise OwnershipError('Actual stop-witness command differs')
        if state.get('ExecStop') not in ('', '[]'):raise OwnershipError('Extra stop commands refused')
        for key,value in expected['managerProperties'].items():
            actual=state.get(key,'')
            if key in {'RuntimeMaxUSec','TimeoutStopUSec','CPUQuotaPerSecUSec','TimeoutStartUSec'}:
                actual=str(duration_usec(actual))
            if key=='ExecStop' and actual=='[]':actual=''
            if actual!=value: raise OwnershipError('Fresh manager caps drift')
        started=int(state['ActiveEnterTimestampMonotonic'])/1000000
        if not 0 < started <= self.clock(): raise OwnershipError('Actual manager activation unproved')
        remaining=max(0,duration_usec(expected['managerProperties']['RuntimeMaxUSec'])/1000000-(self.clock()-started))
        # systemd stop-post/final signal stages may each rearm TimeoutStopUSec.
        reserve=5*duration_usec(expected['managerProperties']['TimeoutStopUSec'])/1000000
        if admission and (self.utc()>=self.expires or remaining+reserve>(self.expires-self.utc()).total_seconds()):
            raise OwnershipError('Actual manager bound exceeds original authority')
        owned=[]
        for record in records:
            process=record['process']
            if process.poll() is None:
                if self.membership(process.pid)!=expected['controlGroup']:
                    raise OwnershipError('Retained child escaped actual owner membership')
                owned.append(process.pid)
        if self.require_main and self.group_members(expected['controlGroup'])!={os.getpid(),main_pid,*owned}:
            raise OwnershipError('Actual full cgroup contains unowned/unknown members')
        final=self.observe()
        if any(final.get(k)!=state.get(k) for k in ['Id','Description','InvocationID','ControlGroup','MainPID',
                'ActiveState','SubState','LoadState','ActiveEnterTimestampMonotonic','ExecStart','ExecStop','ExecStopPost',*expected['managerProperties']]) or self.boot()!=self.capability['hostBootId'] or self.process_identity(main_pid)!=main_identity:
            raise OwnershipError('Manager ownership changed during membership observation')
        self.invocation=invocation
        self.main_identity=main_identity
        self.witness={'unit':expected['unit'],'description':expected['description'],'invocationId':invocation,
            'controlGroup':expected['controlGroup'],'bootId':self.boot(),'selfPid':os.getpid(),
            'ownedLivePids':owned,'originalAuthorityExpiresUtc':self.capability['expiresUtc'],
            'fullMembershipVerified':self.require_main,
            'activeEnterTimestampMonotonic':state['ActiveEnterTimestampMonotonic'],
            'mainProcess':main_identity,'actualExecArgvSha256':argv_digest,
            'externalAdapterSha256':expected['externalAdapter']['sha256'],'actualStopHookArgv':expected['externalAdapter']['execStopPost'],
            'actualManagerProperties':{k:state[k] for k in expected['managerProperties']},
            'cleanupOnlyTransfer':not admission,'terminalSuccess':False}
        return dict(self.witness)

    def verify_before_spawn(self,records): return self._verify(records,True)
    def verify_after_spawn(self,records): return self._verify(records,True)
    def verify_cleanup_transfer(self,records): return self._verify(records,False)
    def remaining_authority_seconds(self): return max(0,(self.expires-self.utc()).total_seconds())


class ProcessCustody:
    def __init__(self, identity, alive, writer, popen=subprocess.Popen, pidfd_open=None,
                 pidfd_signal=None, close_fd=os.close, child_binding=None, external_owner=None):
        self.identity, self.alive, self.writer, self.popen = identity, alive, writer, popen
        self.pidfd_open = pidfd_open if pidfd_open is not None else getattr(os, 'pidfd_open', None)
        self.pidfd_signal = pidfd_signal if pidfd_signal is not None else getattr(signal, 'pidfd_send_signal', None)
        self.close_fd = close_fd
        self._native_linux_close = close_fd is NATIVE_CLOSE and sys.platform == 'linux'
        self.child_binding = child_binding or self._child_binding
        self.records = []
        self.failures = []
        self.interrupted = None
        self.closed = False
        self.cleanup_retry_count = 0
        self.retention_context = {}
        self.retention_observations = 0
        self.last_cleanup_fault = None
        self.external_owner=external_owner
        self.external_handoff=None
        self.cleanup_deadline=None

    @staticmethod
    def _child_binding(process):
        if process.poll() is not None:
            return False
        # This observes, but never reaps, this exact Popen child. ECHILD refuses use.
        os.waitid(os.P_PID, process.pid, os.WEXITED | os.WNOHANG | os.WNOWAIT)
        return process.poll() is None

    def _write(self, kind):
        self.writer({'kind': kind, 'resources': [self.row(r) for r in self.records],
                     'failures': list(self.failures), 'interruption': self.interrupted,
                     'cleanupAttempts': self.cleanup_retry_count, 'lastCleanupFault': self.last_cleanup_fault,
                     'retentionContext': self.retention_context, 'externalHandoff':self.external_handoff})

    @staticmethod
    def row(record):
        process = record['process']
        return {'pid': process.pid, 'identity': record['identity'],
                'pidfdCaptured': record['pidfd'] is not None,
                'retainedPidfd': record['pidfd'],
                'originalPidfd': record.get('originalPidfd', record['pidfd']),
                'pidfdBindingAuthority': record.get('pidfdState', 'retained' if record['pidfd'] is not None else 'absent'),
                'pidfdCloseDiagnostic': record.get('pidfdCloseDiagnostic'),
                'retainedPopenChild': True, 'exitCode': process.poll(),
                'identityUnproved': record['identity'] is None,
                'cleanupVerified': record['cleanupVerified']}

    @contextmanager
    def signals(self):
        old = {sig: signal.getsignal(sig) for sig in (signal.SIGTERM, signal.SIGINT)}
        primary = None
        def defer(signum, _frame):
            self.interrupted = signum
        try:
            for sig in old:
                signal.signal(sig, defer)
            yield self
        except BaseException:
            primary = sys.exc_info()
            raise  # Installation and body exceptions retain their original object/traceback.
        finally:
            try:
                try:
                    self.cleanup()
                except BaseException as error:
                    self.failures.append({'phase': 'cleanup', 'failureType': type(error).__name__})
            finally:
                for sig, handler in old.items():
                    try:
                        signal.signal(sig, handler)
                    except BaseException as error:
                        self.failures.append({'phase': 'cleanup', 'failureType': type(error).__name__,
                                              'operation': 'restore-signal-handler'})
            if primary is None:
                self.checkpoint()  # Pending cancellation is primary; secondary faults remain recorded.
                if not self.closed or any(row['phase'] in ('cleanup', 'durability') for row in self.failures):
                    raise OwnershipError('Exact process cleanup/durability unresolved; no qualification')

    def checkpoint(self):
        if self.interrupted is not None:
            raise DeferredInterruption('Task signal deferred until process custody registered')

    def spawn(self, command, **options):
        self.checkpoint()
        if sys.platform=='linux' and self.external_owner is None:
            raise OwnershipError('Actual outside custody proof required before Linux Popen')
        if self.external_owner is not None:
            self.external_owner.verify_before_spawn(self.records)
        process = None
        record = None
        try:
            process = self.popen(command, **options)
            # No identity observation, fsync or signal checkpoint may precede this custody row.
            record = {'process': process, 'pidfd': None, 'identity': None, 'cleanupVerified': False}
            self.records.append(record)
            if self.external_owner is not None:
                self.external_owner.verify_after_spawn(self.records)
            if callable(self.pidfd_open) and callable(self.pidfd_signal):
                if self.child_binding(process):
                    record['pidfd'] = self.pidfd_open(process.pid, 0)
                    if not self.child_binding(process):
                        # It exited during acquisition; the fd cannot authorize a replacement PID.
                        process.wait(timeout=0)
            self._write('acquired-native-handle-before-identity')
            record['identity'] = self.identity(process.pid)
            self._write('observed-identity')
            self.checkpoint()
            return process
        except BaseException as error:
            self.failures.append({'phase': 'acquisition', 'failureType': type(error).__name__,
                                  'pid': process.pid if process is not None else None})
            # Even an allocation failure while adding the first record retains this local handle.
            if process is not None and record is None:
                record = {'process': process, 'pidfd': None, 'identity': None, 'cleanupVerified': False}
            if record is not None and not any(r is record for r in self.records):
                # If registry insertion itself fails, act on the retained local child immediately.
                self._finish(record)
                self.records.append(record)
            self.cleanup()
            raise

    def _signal(self, record, signum):
        process = record['process']
        if process.poll() is not None:
            return
        if record['pidfd'] is not None:
            self.pidfd_signal(record['pidfd'], signum, None, 0)
        else:
            # No identity prerequisite: actual native unreaped-child binding is stronger than /proc text.
            # Binding uncertainty/PID reuse must preserve failure rather than signal a raw PID.
            if self.child_binding(process) is not True:
                raise OwnershipError('Exact unreaped child binding unavailable; resource retained')
            process.send_signal(signum)

    def stop(self, process):
        record = next(r for r in self.records if r['process'] is process)
        self._finish(record)
        self._write('exact-child-stopped')

    def _finish(self, record):
        process = record['process']
        while process.poll() is None:
            if self._transfer_if_proved():
                return
            # A failed TERM must not suppress KILL or exact-child wait. No timeout
            # releases sole custody; all individual waits remain bounded.
            for signum in (signal.SIGTERM, signal.SIGKILL):
                try:
                    self._signal(record, signum)
                except BaseException as error:
                    self._retry_failure(error, process.pid, 'signal')
                try:
                    process.wait(timeout=1)
                except subprocess.TimeoutExpired:
                    pass
                except BaseException as error:
                    self._retry_failure(error, process.pid, 'wait')
                if process.poll() is not None:
                    break
            if process.poll() is None:
                time.sleep(0.05)
        if process.poll() is None:
            raise OwnershipError('Exact child exit not observed')
        process.wait(timeout=0)  # Explicit reap of the exact retained child.
        if record.get('pidfdState')=='retirement-unproved' and self._transfer_if_proved():
            return
        self._retire_handle(record)
        record['cleanupVerified'] = True

    def _retire_handle(self, record):
        if record['pidfd'] is None:
            return
        if record.get('pidfdState') == 'retirement-unproved':
            raise OwnershipError('Prior close callback failed; descriptor retirement unproved; no retry')
        # Attempt exactly once. Linux close(2) releases the descriptor before
        # reporting OSError; retry could close a reused foreign descriptor.
        original_fd = record['pidfd']
        record['originalPidfd'] = original_fd
        record['pidfdState'] = 'retirement-unproved'
        try:
            self.close_fd(original_fd)
        except BaseException as error:
            record['pidfdCloseDiagnostic'] = type(error).__name__
            if self._native_linux_close and isinstance(error, OSError):
                record['pidfd'] = None
                record['pidfdState'] = 'retired-native-linux-error'
                self._retry_failure(error, record['process'].pid, 'fd-close')
                return  # Diagnostic stays sticky; qualification is rejected.
            self._retry_failure(error, record['process'].pid, 'fd-close')
            raise  # Custom/non-native/pre-syscall failure proves no retirement.
        record['pidfd'] = None
        record['pidfdState'] = 'retired-success'

    def _retry_failure(self, error, pid, operation):
        self.cleanup_retry_count += 1
        self.last_cleanup_fault = {'failureType': type(error).__name__, 'pid': pid, 'operation': operation}
        # Retain failure evidence without unbounded accumulation during custody.
        if len(self.failures) < 64:
            self.failures.append({'phase': 'cleanup', 'failureType': type(error).__name__,
                                  'pid': pid, 'operation': operation})
        # Bound evidence allocation. Avoid new descriptor allocation during a
        # failed-close retry; retained native custody remains observable in memory.
        if operation != 'fd-close' and self.retention_observations < 16:
            self.retention_observations += 1
            try:
                self._write('cleanup-only-retention-not-terminal')
            except BaseException as secondary:
                if len(self.failures) < 64:
                    self.failures.append({'phase': 'durability', 'failureType': type(secondary).__name__})

    def _transfer_if_proved(self):
        if self.external_owner is None or self.cleanup_deadline is None or time.monotonic()<self.cleanup_deadline:
            return False
        try:
            witness=self.external_owner.verify_cleanup_transfer(self.records)
            if witness.get('cleanupOnlyTransfer') is not True or witness.get('terminalSuccess') is not False:
                raise OwnershipError('Actual cleanup-only outside transfer witness required')
            self.external_handoff=witness
            self._write('actual-outside-custody-transfer-not-terminal')
            return True
        except BaseException as error:
            self.external_handoff=None
            self._retry_failure(error,None,'external-transfer-observation')
            return False  # Stay inside previously armed actual manager boundary.

    def cleanup(self):
        if self.cleanup_deadline is None and self.external_owner is not None:
            self.cleanup_deadline=time.monotonic()+min(60,self.external_owner.remaining_authority_seconds())
        if self.external_handoff is not None:
            self.external_handoff=None  # Repeated cleanup must acquire a fresh actual witness.
        for record in self.records:
            while not record['cleanupVerified']:
                if self.external_handoff is not None:
                    break
                try:
                    self._finish(record)
                except BaseException as error:
                    self._retry_failure(error, record['process'].pid, 'finish')
                    time.sleep(0.05)
        self.closed = all(r['cleanupVerified'] for r in self.records)
        try:
            self._write('terminal-cleanup')
        except BaseException as error:
            self.failures.append({'phase': 'durability', 'failureType': type(error).__name__})
        return self.closed
