from pathlib import Path
import subprocess,json,hashlib,csv
root=Path('/private/tmp/cohesive-nested-runs');root.mkdir(exist_ok=True)
folder=Path('/private/tmp/cohesive-nested-frozen');assembly=folder/'Cohesive.Relations.Benchmarks.dll'
source=Path('/Users/eulerfx/.codex/worktrees/observation-nested-layouts/cohesive')
manifest={'base_commit':subprocess.check_output(['git','rev-parse','HEAD'],cwd=source,text=True).strip(), 'assembly_hashes':{p.name:hashlib.sha256(p.read_bytes()).hexdigest() for p in sorted(folder.iterdir()) if p.suffix=='.dll' or p.name.endswith(('.deps.json','.runtimeconfig.json'))},'source_hashes':{str(p.relative_to(source)):hashlib.sha256(p.read_bytes()).hexdigest() for p in sorted((source/'src/Cohesive.Relations.Benchmarks').glob('*Nested*.cs'))}}
(root/'manifest.json').write_text(json.dumps(manifest,indent=2)+'\n')
parse_cases=[]
for scenario,count in [('record',1),('nested',1),('array',128),('array',4096)]:
 for width in (4,16,32):parse_cases.append((width,count,scenario))
for scenario,widths in [('named-array',(16,32)),('sparse-array',(16,32,256)),('reordered-array',(16,32)),('case-array',(16,))]:
 for width in widths:parse_cases.append((width,128,scenario))
# Retained-memory diagnostics finish before all timed runs. Each command is a fresh process.
retained_cases=[(16,1,'record'),(16,1,'nested')]+[(w,n,'array') for w in (4,16,32) for n in (128,4096)]+[(w,128,'sparse-array') for w in (16,32,256)]
for width,count,scenario in retained_cases:
 for route in ('existing','shared'):
  result=subprocess.check_output(['dotnet',str(assembly),'--nested-layout-retained',str(width),str(count),scenario,route,str(max(2,min(5000,2000//count)))],text=True)
  with (root/'retained.txt').open('a') as output:output.write(result)
print('Retained diagnostics complete',flush=True)
rows=[]
def run(repeat,width,count,scenario,method):
 stem=root/f'{repeat}-{scenario}-{width}-{count}-{method}'
 pattern=f'*ObservationNestedLayoutBenchmarks.{method}(Width: {width}, Count: {count}, Scenario: "{scenario}")'
 with stem.with_suffix('.log').open('w') as output:
  subprocess.run(['dotnet',str(assembly),'--filter',pattern,'--inProcess','--warmupCount','4','--iterationCount','6','--iterationTime','200','--artifacts',str(stem)],stdout=output,stderr=subprocess.STDOUT,check=True)
 results=list(stem.glob('results/*full.json'));assert len(results)==1,(stem,results)
 benchmarks=json.loads(results[0].read_text())['Benchmarks'];assert len(benchmarks)==1 and benchmarks[0]['Statistics'],stem
 b=benchmarks[0]
 assert f'Width: {width}, Count: {count}, Scenario: "{scenario}"' in b['FullName'],b['FullName']
 row=dict(repeat=repeat,scenario=scenario,width=width,count=count,method=method,full_name=b['FullName'],mean_ns=b['Statistics']['Mean'],allocated_bytes=b['Memory']['BytesAllocatedPerOperation'])
 rows.append(row)
 with (root/'summary.csv').open('a',newline='') as output:
  writer=csv.DictWriter(output,fieldnames=row.keys(),lineterminator='\n')
  if output.tell()==0:writer.writeheader()
  writer.writerow(row)
 print(stem.name,flush=True)
for repeat in (1,2,3):
 for width,count,scenario in parse_cases:
  for method in (('ParseExisting','ParseShared') if repeat%2 else ('ParseShared','ParseExisting')):run(repeat,width,count,scenario,method)
 for width in (4,16,32):
  methods=('ReadExisting','ReadSharedNames','ReadOrdinals')
  if not repeat%2:methods=methods[::-1]
  for method in methods:run(repeat,width,128,'array',method)
 for count in (128,4096):
  for method in (('ParseExistingAndRead','ParseSharedAndRead') if repeat%2 else ('ParseSharedAndRead','ParseExistingAndRead')):run(repeat,16,count,'array',method)
assert len(rows)==159,len(rows)
print('Complete',flush=True)
