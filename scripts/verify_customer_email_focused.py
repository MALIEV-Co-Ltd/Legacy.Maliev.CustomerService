"""Admission and exact TRX verification for a separate77-case hosted proof."""
from __future__ import annotations
import collections,hashlib,json,os,re,sys,uuid,xml.etree.ElementTree as E
from pathlib import Path
ROOT=Path(__file__).resolve().parents[1]
MANIFEST=ROOT/'scripts/customer-email-focused-manifest.json'
def need(value,reason):
    if not value: raise ValueError(reason)
def guard():
    need(sys.platform=='linux' and os.environ.get('GITHUB_REPOSITORY')=='MALIEV-Co-Ltd/Legacy.Maliev.CustomerService','Exact hosted Customer repository required')
    manifest=json.loads(MANIFEST.read_bytes())
    mem=dict(re.findall(r'^(\w+):\s+(\d+) kB$',Path('/proc/meminfo').read_text(),re.M))
    need(int(mem['MemFree'])>=4194304,'Fresh physical MemFree4GiB required before native phase')
    native=[]
    for directory in Path('/proc').iterdir():
        if not directory.name.isdigit(): continue
        try: executable=Path(os.readlink(directory/'exe')).name
        except FileNotFoundError: continue
        if executable.lower() in {'dotnet','testhost','msbuild','vbcscompiler'}: native.append(int(directory.name))
    need(not native,'Existing native process prevents a new focused worker')
    for row in manifest['codePins']:
        data=(ROOT/row['path']).read_bytes();need(len(data)==row['bytes'] and hashlib.sha256(data).hexdigest()==row['sha256'],'Frozen behavior/dependency/workflow code drift')
    print(json.dumps({'mode':'guard','physicalMemFreeKiB':int(mem['MemFree']),'nativeProcesses':native,'codePins':len(manifest['codePins']),'githubSha':os.environ.get('GITHUB_SHA'),'runId':os.environ.get('GITHUB_RUN_ID')}))
NONPASSING=('failed','error','timeout','aborted','inconclusive','passedButRunAborted','notRunnable','notExecuted','disconnected','warning','completed','inProgress','pending')
NS={'t':'http://microsoft.com/schemas/VisualStudio/TeamTest/2010'}
def guid(value):
    need(isinstance(value,str) and bool(value),'Missing GUID')
    try: parsed=uuid.UUID(value)
    except ValueError: raise ValueError('Malformed GUID') from None
    need(parsed.int!=0 and str(parsed)==value,'Noncanonical or empty GUID')
    return value
def validate_trx(raw,manifest):
    doc=E.fromstring(raw)
    expected=[(x['name'],guid(x['testId'])) for x in manifest['selected']]
    need(len(expected)==len(set(expected))==77 and len({x[1] for x in expected})==77,'Exact77 unique manifest identities')
    results=doc.findall('t:Results/t:UnitTestResult',NS);defs=doc.findall('t:TestDefinitions/t:UnitTest',NS);entries=doc.findall('t:TestEntries/t:TestEntry',NS)
    need(len(results)==len(defs)==len(entries)==77,'Exact77 complete TRX joins')
    byId={guid(x.get('id')):x for x in defs};need(len(byId)==77,'Unique77 definition GUIDs')
    actual=[];pairs=[];executionIds=[];definitionExecs=[]
    for row in results:
        testId=guid(row.get('testId'));executionId=guid(row.get('executionId'));need(testId in byId,'Unknown result testId')
        definition=byId[testId];executions=definition.findall('t:Execution',NS);need(len(executions)==1,'One definition execution required')
        definitionId=guid(executions[0].get('id'));need(definitionId==executionId and definition.get('name')==row.get('testName'),'Exact definition/result identity join')
        need(row.get('outcome')=='Passed','Focused case not Passed');actual.append((row.get('testName'),testId));pairs.append((testId,executionId));executionIds.append(executionId);definitionExecs.append(definitionId)
    entryPairs=[(guid(x.get('testId')),guid(x.get('executionId'))) for x in entries]
    need(len(set(executionIds))==len(set(definitionExecs))==len({x[1] for x in entryPairs})==77,'Globally unique77 execution GUIDs')
    need(collections.Counter(actual)==collections.Counter(expected),'Exact focused manifest name/testId multiset')
    need(len({x[0] for x in pairs})==len({x[0] for x in entryPairs})==77 and collections.Counter(pairs)==collections.Counter(entryPairs),'Definition/result/entry execution bijection')
    summaries=doc.findall('t:ResultSummary',NS);need(len(summaries)==1,'One ResultSummary required');summary=summaries[0]
    # Original passing VSTest artifact reports outcome="Completed", while every
    # UnitTestResult is Passed. Completed is accepted only with strict77 clean
    # counters/joins; it is not an exemption for errors or aborted runs.
    need(summary.get('outcome') in {'Passed','Completed'},'Nonpassing ResultSummary')
    counters=summary.findall('t:Counters',NS);need(len(counters)==1,'One Counters required');counter=counters[0].attrib
    need(all(counter.get(k)=='77' for k in ('total','executed','passed')),'Exact77 executed/passed counters')
    need(all(counter.get(k)=='0' for k in NONPASSING),'Every13 nonpassing counter required canonical zero')
    need(doc.find('.//t:ErrorInfo',NS) is None,'ErrorInfo forbidden anywhere in TRX')
    for info in doc.findall('.//t:RunInfo',NS):need(info.get('outcome') in {'Passed','Completed','Information'},'Bad or missing RunInfo outcome')
    return {'selectedCount':77,'summaryOutcome':summary.get('outcome'),'executionBijection':True,'all13NonpassingCountersZero':True}
def verify():
    manifest=json.loads(MANIFEST.read_bytes());folder=ROOT/'runner-focused';files=list(folder.rglob('*.trx'));need(len(files)==1,'One original focused TRX required')
    raw=files[0].read_bytes();structure=validate_trx(raw,manifest)
    build=(folder/'build.log').read_text();need('Build succeeded.' in build and re.search(r'0 Warning\(s\)',build) and re.search(r'0 Error\(s\)',build) and not re.search(r'\b[1-9]\d* (?:Warning|Error)\(s\)',build),'Actual focused strict Release0WE required')
    result={'actualFocusedPassed':77,'failed':0,'skipped':0,'trxSha256':hashlib.sha256(raw).hexdigest(),'original77IdentitiesExact':True,'strictTrxStructure':structure,'behaviorHead':manifest['behaviorHead'],'githubSha':os.environ.get('GITHUB_SHA'),'runId':os.environ.get('GITHUB_RUN_ID'),'strictRelease0WE':True,'full496AcceptedByThisFocusedRun':False,'consumerAccepted':False,'mutantsExecuted':False}
    (folder/'focused-proof.json').write_text(json.dumps(result,indent=2));print(json.dumps(result))
if __name__=='__main__':
    try:
        if sys.argv[1]=='guard': guard()
        elif sys.argv[1]=='verify': verify()
        else: raise ValueError('Unknown mode')
    except Exception as error:
        print(type(error).__name__+': '+str(error),file=sys.stderr);sys.exit(2)
