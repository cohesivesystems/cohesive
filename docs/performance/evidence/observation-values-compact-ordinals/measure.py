from pathlib import Path
import subprocess,os,json,csv,hashlib,shutil
root=Path('/private/tmp/cohesive-packed-runs');root.mkdir(exist_ok=True)
folder=Path('/private/tmp/cohesive-packed-frozen');assembly=folder/'Cohesive.Relations.Benchmarks.dll'
source=Path('/Users/eulerfx/.codex/worktrees/observation-nested-layouts/cohesive')
files=['src/Cohesive/Model/'+n for n in ('CompactOrdinalObservationFields.cs','ObservationFieldTraversal.cs','ObservationValue.cs')]+['src/Cohesive.Relations.Benchmarks/'+n for n in ('NestedLayoutCandidate.cs','NestedLayoutConformance.cs','ObservationNestedLayoutBenchmarks.cs','Program.cs')]+['src/Cohesive.Tests/Model/CompactOrdinalObservationFieldsTests.cs']
sha=lambda p:hashlib.sha256(p.read_bytes()).hexdigest()
(root/'manifest.json').write_text(json.dumps({'base_commit':subprocess.check_output(['git','rev-parse','HEAD'],cwd=source,text=True).strip(),'source_hashes':{n:sha(source/n) for n in files},'assembly_hashes':{p.name:sha(p) for p in sorted(folder.iterdir()) if p.suffix=='.dll' or p.name.endswith(('.deps.json','.runtimeconfig.json'))}},indent=2)+'\n')
cases=[(16,128,'array',25),(32,128,'array',25),(256,128,'array',6),(256,128,'array',25),(32,128,'array',50),(16,1,'nested',25),(32,128,'named-array',25),(32,128,'mixed-array',100),(16,128,'array',100),(16,4096,'array',25)]
for w,c,s,p in cases+[(256,128,'sparse-array',25)]:
 for route in ('existing','shared','density','packed'):
  line=subprocess.check_output(['dotnet',str(assembly),'--nested-layout-retained',str(w),str(c),s,route,str(max(2,2000//c)),str(p)],text=True)
  with (root/'retained.txt').open('a') as f:f.write(line)
print('Retained complete',flush=True)
rows=[]
def run(rep,case,method):
 w,c,s,p=case;stem=root/f'{rep}-{s}-{w}-{c}-{p}-{method}'
 pattern=f'*ObservationNestedLayoutBenchmarks.{method}(Width: {w}, Count: {c}, Scenario: "{s}", PresentPercent: {p})'
 with stem.with_suffix('.log').open('w') as out:subprocess.run(['dotnet',str(assembly),'--filter',pattern,'--inProcess','--warmupCount','4','--iterationCount','6','--iterationTime','200','--artifacts',str(stem)],stdout=out,stderr=subprocess.STDOUT,check=True)
 reports=list(stem.glob('results/*full.json'));assert len(reports)==1,(stem,reports)
 items=json.loads(reports[0].read_text())['Benchmarks'];assert len(items)==1 and items[0]['Statistics'],stem
 b=items[0];assert f'Width: {w}, Count: {c}, Scenario: "{s}", PresentPercent: {p}' in b['FullName']
 row=dict(repeat=rep,width=w,count=c,scenario=s,present_percent=p,method=method,mean_ns=b['Statistics']['Mean'],allocated_bytes=b['Memory']['BytesAllocatedPerOperation']);rows.append(row)
 with (root/'summary.csv').open('a',newline='') as out:
  writer=csv.DictWriter(out,fieldnames=row.keys(),lineterminator='\n')
  if out.tell()==0:writer.writeheader()
  writer.writerow(row)
 print(stem.name,flush=True)
def order(methods,rep):return methods if rep==1 else methods[::-1] if rep==2 else methods[2:]+methods[:2]
for rep in (1,2,3):
 for case in [(16,128,'array',25),(256,128,'sparse-array',25)]:
  for m in order(('ReadExisting','ReadOrdinals','ReadDensityAware','ReadCompactOrdinal'),rep):run(rep,case,m)
 for case in cases:
  for m in order(('ParseExisting','ParseSharedSeparated','ParseDensityAware','ParseCompactOrdinal'),rep):run(rep,case,m)
 for case in [(32,128,'array',25),(16,4096,'array',25)]:
  for m in order(('ParseExistingAndRead','ParseSharedSeparatedAndRead','ParseDensityAwareAndRead','ParseCompactOrdinalAndRead'),rep):run(rep,case,m)
assert len(rows)==168
print('Complete',flush=True)
