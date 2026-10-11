"""Pure in-memory parser controls. No SDK, testhost, Docker or focused proof."""
import copy,importlib.util,json,unittest,uuid,xml.etree.ElementTree as E
from pathlib import Path
HERE=Path(__file__).resolve().parent
spec=importlib.util.spec_from_file_location('validator',HERE/'verify_customer_email_focused.py');v=importlib.util.module_from_spec(spec);spec.loader.exec_module(v)
MANIFEST=json.loads((HERE/'customer-email-focused-manifest.json').read_bytes())
URI=v.NS['t']
def q(name):return '{'+URI+'}'+name
def fixture():
    # Test-only synthetic schema, preserving actual manifest names/test IDs.
    root=E.Element(q('TestRun'));results=E.SubElement(root,q('Results'));defs=E.SubElement(root,q('TestDefinitions'));entries=E.SubElement(root,q('TestEntries'))
    for i,row in enumerate(MANIFEST['selected'],1):
        execution=str(uuid.UUID(int=i));E.SubElement(results,q('UnitTestResult'),testId=row['testId'],executionId=execution,testName=row['name'],outcome='Passed')
        definition=E.SubElement(defs,q('UnitTest'),id=row['testId'],name=row['name']);E.SubElement(definition,q('Execution'),id=execution)
        E.SubElement(entries,q('TestEntry'),testId=row['testId'],executionId=execution)
    summary=E.SubElement(root,q('ResultSummary'),outcome='Completed');E.SubElement(summary,q('Counters'),total='77',executed='77',passed='77',**{k:'0' for k in v.NONPASSING})
    return root
def encode(root):return E.tostring(root)
class ParserControls(unittest.TestCase):
    def reject(self,root):
        with self.assertRaises((ValueError,KeyError)):v.validate_trx(encode(root),MANIFEST)
    def test_clean_producer_completed_and_passed_labels(self):
        for label in ['Completed','Passed']:
            root=fixture();root.find(q('ResultSummary')).set('outcome',label);self.assertEqual(77,v.validate_trx(encode(root),MANIFEST)['selectedCount'])
    def test_invalid_guid_in_every_join_role(self):
        roles=[('Results/UnitTestResult','executionId'),('Results/UnitTestResult','testId'),('TestDefinitions/UnitTest','id'),('TestDefinitions/UnitTest/Execution','id'),('TestEntries/TestEntry','executionId'),('TestEntries/TestEntry','testId')]
        for route,key in roles:
            for value in [None,'','junk','00000000-0000-0000-0000-000000000000','1','AAAAAAAA-AAAA-AAAA-AAAA-AAAAAAAAAAAA']:
                with self.subTest(route=route,key=key,value=value):
                    root=fixture();node=root.find('/'.join(q(x) for x in route.split('/')))
                    if value is None:del node.attrib[key]
                    else:node.set(key,value)
                    self.reject(root)
    def test_reused_valid_execution_guid_across_all77(self):
        root=fixture();guid=str(uuid.UUID(int=1))
        for row in root.findall('t:Results/t:UnitTestResult',v.NS)+root.findall('t:TestEntries/t:TestEntry',v.NS):row.set('executionId',guid)
        for row in root.findall('t:TestDefinitions/t:UnitTest/t:Execution',v.NS):row.set('id',guid)
        self.reject(root)
    def test_reused_second_execution_guid_consistently(self):
        root=fixture();guid=str(uuid.UUID(int=1));root.find('t:Results',v.NS)[1].set('executionId',guid);root.find('t:TestEntries',v.NS)[1].set('executionId',guid);root.find('t:TestDefinitions',v.NS)[1].find(q('Execution')).set('id',guid);self.reject(root)
    def test_every_nonpassing_counter_nonzero_missing_and_malformed(self):
        for key in v.NONPASSING:
            for value in [None,'1','junk','00','-0']:
                with self.subTest(counter=key,value=value):
                    root=fixture();counter=root.find('t:ResultSummary/t:Counters',v.NS)
                    if value is None:del counter.attrib[key]
                    else:counter.set(key,value)
                    self.reject(root)
    def test_bad_summary_labels(self):
        for label in [None,'Failed','Aborted','Error','NotExecuted','InProgress','junk']:
            root=fixture();summary=root.find(q('ResultSummary'))
            if label is None:del summary.attrib['outcome']
            else:summary.set('outcome',label)
            self.reject(root)
    def test_result_and_summary_errorinfo(self):
        for route in ['Results/UnitTestResult','ResultSummary']:
            root=fixture();node=root.find('/'.join(q(x) for x in route.split('/')));E.SubElement(E.SubElement(node,q('Output')),q('ErrorInfo'));self.reject(root)
    def test_bad_runinfo(self):
        for outcome in [None,'Error','Warning','Failed','Aborted','junk']:
            root=fixture();row=E.SubElement(E.SubElement(root.find(q('ResultSummary')),q('RunInfos')),q('RunInfo'))
            if outcome is not None:row.set('outcome',outcome)
            self.reject(root)
    def test_missing_duplicate_and_wrong_join(self):
        for route in ['Results','TestDefinitions','TestEntries']:
            for mode in ['missing','duplicate']:
                root=fixture();parent=root.find(q(route))
                if mode=='missing':parent.remove(parent[0])
                else:parent.append(copy.deepcopy(parent[0]))
                self.reject(root)
        root=fixture();root.find('t:TestEntries',v.NS)[0].set('executionId',str(uuid.UUID(int=999)));self.reject(root)
    def test_changed_manifest_name_and_result_outcome(self):
        root=fixture();root.find('t:Results',v.NS)[0].set('testName','wrong');self.reject(root)
        root=fixture();root.find('t:Results',v.NS)[0].set('outcome','NotExecuted');self.reject(root)
    def test_total_executed_passed_require_exact77(self):
        for key in ['total','executed','passed']:
            for value in [None,'76','78','077','junk']:
                root=fixture();row=root.find('t:ResultSummary/t:Counters',v.NS)
                if value is None:del row.attrib[key]
                else:row.set(key,value)
                self.reject(root)
    def test_missing_multiple_summaries_or_counters(self):
        for mode in ['missing-summary','duplicate-summary','missing-counter','duplicate-counter']:
            root=fixture();summary=root.find(q('ResultSummary'));counter=summary.find(q('Counters'))
            if mode=='missing-summary':root.remove(summary)
            elif mode=='duplicate-summary':root.append(copy.deepcopy(summary))
            elif mode=='missing-counter':summary.remove(counter)
            else:summary.append(copy.deepcopy(counter))
            self.reject(root)
if __name__=='__main__':unittest.main()
