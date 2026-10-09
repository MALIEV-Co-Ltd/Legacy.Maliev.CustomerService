"""Finite source check for the existing Customer intake; never dispatches a backend."""
from pathlib import Path
import argparse,hashlib,json,re
BASE='b50e12d66cf0c3d4211a41febe3b01ca265a0c15'
PATHS={
 'Legacy.Maliev.CustomerService.Api/Controllers/CompaniesController.cs',
 'Legacy.Maliev.CustomerService.Api/Controllers/AddressesController.cs',
 'Legacy.Maliev.CustomerService.Tests/Controllers/CompanyOriginalNullBoundaryTests.cs',
 'Legacy.Maliev.CustomerService.Tests/Controllers/CustomerAddressOriginalNullBoundaryTests.cs'}
def digest(raw):return hashlib.sha256(raw).hexdigest()
def require(value,message):
    if not value:raise ValueError(message)
def check(packet,trusted):
    p=Path(packet);raw=(p/'manifest.json').read_bytes();require(digest(raw)==trusted,'Manifest seal mismatch');m=json.loads(raw)
    require(m['baseSha']==BASE and m['sourceOnly'] is True and m['nativeExecutionGranted'] is False and m['nativeStarts']==0,'Source-only exact-base binding missing')
    require(len(m['files'])==4 and {r['path'] for r in m['files']}==PATHS,'Four-file allowlist mismatch')
    for r in m['files']:
        raw=(p/'files'/r['path']).read_bytes();require(len(raw)==r['bytes'] and digest(raw)==r['sha256'],'Candidate postimage drift')
    for key,name in [('patchSha256','association.patch'),('baselineSourceInventorySha256','baseline-source-inventory.json'),('originalSourceMappingSha256','original-full-sha-obligations.json'),('authoredCaseContractSha256','thirteen-authored-case-contract.json'),('actualHistoricalBaselineRosterSha256','actual-main-baseline-roster.json')]:
        require(digest((p/name).read_bytes())==m[key],name+' seal mismatch')
    inventory=json.loads((p/'baseline-source-inventory.json').read_bytes());require(inventory['baseSha']==BASE,'Inventory base mismatch')
    require(digest((p/'baseline-b50.zip').read_bytes())==inventory['archiveSha256'],'Baseline archive drift')
    pre={r['path']:r['sha256'] for r in inventory['files']}
    require(all(r['preimageSha256']==pre.get(r['path']) for r in m['files']),'Preimage mismatch')
    originals=json.loads((p/'original-full-sha-obligations.json').read_bytes());require(len(originals)==6 and len({r['path'] for r in originals})==6,'Six original obligations missing')
    for r in originals:
        raw=(p/'original-source'/r['path']).read_bytes();require(digest(raw)==r['sha256'],'Original SHA obligation mismatch')
        require(hashlib.sha1(b'blob '+str(len(raw)).encode()+b'\0'+raw).hexdigest()==r['blob'],'Original Git blob mismatch')
    contract=json.loads((p/'thirteen-authored-case-contract.json').read_bytes());require(contract['caseCount']==13 and sum(len(r['rows']) for r in contract['methods'])==13 and len(contract['methods'])==6,'Thirteen authored row contract mismatch')
    require(contract['compiledCases'] is None and contract['forecastOnly'] is True,'Invented compiled case proof')
    for cls,count in [('CompanyOriginalNullBoundaryTests',6),('CustomerAddressOriginalNullBoundaryTests',7)]:
        source=(p/'files'/'Legacy.Maliev.CustomerService.Tests/Controllers'/f'{cls}.cs').read_text(encoding='utf-8')
        require(len(re.findall(r'\[Fact\]',source))+len(re.findall(r'\[InlineData\(',source))==count,'Authored source case census mismatch')
        for r in contract['methods']:
            if r['class']==cls:require('Task '+r['method']+'(' in source,'Authored method absent')
    roster=json.loads((p/'actual-main-baseline-roster.json').read_bytes());require(roster['baseSha']==BASE and roster['caseCount']==387 and len(roster['cases'])==387,'Actual main baseline identity mismatch')
    require(all(c['outcome']=='Passed' for c in roster['cases']),'Actual baseline failure')
    for f in roster['files']:require(digest((p/f['path']).read_bytes())==f['sha256'],'Actual artifact evidence drift')
    require(m['baselineCompiledRoster'] is None and m['candidateCompiledRoster'] is None and m['fullSuiteCount'] is None,'Historical evidence claimed as fresh qualification')
    return {'sourceAssociationVerified':True,'postimages':4,'originalObligations':6,'historicalBaselineCases':387,'authoredNewCases':13,'forecastCandidateCases':400,'nativeExecutionGranted':False}
def main():
    parser=argparse.ArgumentParser(description=__doc__);parser.add_argument('--packet',required=True);parser.add_argument('--manifest-sha256',required=True);args=parser.parse_args()
    try:print(json.dumps(check(args.packet,args.manifest_sha256)))
    except (ValueError,KeyError,OSError,json.JSONDecodeError) as error:parser.exit(78,str(error)+'\n')
if __name__=='__main__':main()
