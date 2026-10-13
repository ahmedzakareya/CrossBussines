from pathlib import Path
import hashlib,json,shutil,subprocess,tempfile
r=Path(__file__).resolve().parents[2];o=Path(tempfile.mkdtemp(prefix='crossbuy-quarantine-proof-')).resolve()
raw=(r/'script.sql').read_bytes();sha=lambda b:hashlib.sha256(b).hexdigest()
results=[]
def fixture(name):
 p=o/name;(p/'governance/tools').mkdir(parents=True,exist_ok=True);(p/'governance/registry').mkdir(parents=True,exist_ok=True);(p/'CrossBuy/deploy/sql').mkdir(parents=True,exist_ok=True);(p/'docs/deployment').mkdir(parents=True,exist_ok=True)
 for f in ['sql-quarantine-policy.ps1','validate-sql-governance.ps1','apply-sql-slices.ps1']:shutil.copyfile(r/'governance/tools'/f,p/'governance/tools'/f)
 shutil.copyfile(r/'governance/registry/sql-quarantine.json',p/'governance/registry/sql-quarantine.json');shutil.copyfile(r/'docs/deployment/R1-SQL-Quarantine.md',p/'docs/deployment/R1-SQL-Quarantine.md')
 (p/'script.sql').write_bytes(raw)
 manifest={'scripts':[{'path':'script.sql','name':'script.sql','rawSha256':sha(raw),'sha256':sha(raw.replace(b'\r\n',b'\n')),'idempotency':'not-deployable','excludedFromDeploy':True,'excludedReason':'Historical unsafe scratch','rank':90}],'duplicateNames':[]}
 (p/'CrossBuy/deploy/sql/manifest.json').write_text(json.dumps(manifest));(p/'governance/registry/sql-slices.json').write_text(json.dumps({'canonicalAuthoredRoot':'CrossBuy/deploy/sql','slices':[]}))
 return p
def update(p,relative,change):
 f=p/relative;v=json.loads(f.read_text());change(v);f.write_text(json.dumps(v))
def execute(name,change=lambda p:None,expected=0,apply=False):
 p=fixture(name);change(p)
 args=['powershell','-NoProfile','-ExecutionPolicy','Bypass','-File',str(p/'governance/tools'/('apply-sql-slices.ps1' if apply else 'validate-sql-governance.ps1'))]
 if apply:args+=['-ConnectionString','Server=unreachable.invalid;Database=CrossBuyProbe_Quarantine;Trusted_Connection=True;Connect Timeout=1','-Only','script.sql','-Apply']
 run=subprocess.run(args,capture_output=True,text=True,errors='replace',timeout=40)
 (p/'result.log').write_text(run.stdout+run.stderr)
 ok=run.returncode==expected
 if apply:ok=ok and 'explicitly requested excluded script' in run.stdout and 'applying script.sql' not in run.stdout
 results.append({'Case':name,'Passed':ok,'Exit':run.returncode,'ExpectedExit':expected,'SqlExecuted':0})
execute('valid-exact-quarantine')
execute('changed-scratch-bytes',lambda p:(p/'script.sql').write_bytes(raw+b'\n-- changed\n'),3)
execute('wildcard-refused',lambda p:update(p,'governance/registry/sql-quarantine.json',lambda v:v['entries'][0].update(path='*.sql')),3)
execute('excluded-flag-required',lambda p:update(p,'CrossBuy/deploy/sql/manifest.json',lambda v:v['scripts'][0].update(excludedFromDeploy=False)),3)
execute('duplicate-policy-refused',lambda p:update(p,'governance/registry/sql-quarantine.json',lambda v:v['entries'].append(dict(v['entries'][0]))),3)
execute('missing-decision-refused',lambda p:(p/'docs/deployment/R1-SQL-Quarantine.md').unlink(),3)
execute('absolute-path-refused',lambda p:update(p,'governance/registry/sql-quarantine.json',lambda v:v['entries'][0].update(path=str(p/'script.sql'))),3)
def unknown(p):
 b=b'CREATE TABLE dbo.Unsafe(ID int);';(p/'CrossBuy/deploy/sql/unsafe.sql').write_bytes(b)
 update(p,'CrossBuy/deploy/sql/manifest.json',lambda v:v['scripts'].append({'path':'CrossBuy/deploy/sql/unsafe.sql','name':'unsafe.sql','rawSha256':sha(b),'sha256':sha(b),'idempotency':'not-deployable','excludedFromDeploy':False,'rank':50}))
execute('another-unsafe-script-still-blocks',unknown,2)
def crlf(p):
 b=raw.replace(b'\r\n',b'\n').replace(b'\n',b'\r\n');(p/'script.sql').write_bytes(b)
 update(p,'CrossBuy/deploy/sql/manifest.json',lambda v:v['scripts'][0].update(rawSha256=sha(b)))
execute('crlf-checkout-remains-exact-logical-identity',crlf)
execute('explicit-apply-request-refused-before-connect',expected=2,apply=True)
(o/'results.json').write_text(json.dumps({'Cases':results,'Passed':sum(x['Passed'] for x in results),'Failed':sum(not x['Passed'] for x in results),'DatabaseConnections':0,'SqlExecuted':0},indent=2))
print(json.dumps(results,indent=2))
assert all(x['Passed'] for x in results),'Quarantine control failed; do not accept gate change'

# Only remove the uniquely created test directory, never the repository.
if o.parent != Path(tempfile.gettempdir()).resolve() or not o.name.startswith('crossbuy-quarantine-proof-'):
 raise RuntimeError('Unexpected temporary proof path; refusing cleanup')
shutil.rmtree(o)
