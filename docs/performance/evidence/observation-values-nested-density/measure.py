from pathlib import Path
import subprocess,os,json,csv,hashlib
root=Path('/private/tmp/cohesive-nested-density-runs');root.mkdir(exist_ok=True)
folder=Path('/private/tmp/cohesive-density-frozen');assembly=folder/'Cohesive.Relations.Benchmarks.dll'
source=Path('/Users/eulerfx/.codex/worktrees/observation-nested-layouts/cohesive')
files=[source/'src/Cohesive.Relations.Benchmarks'/name for name in ('NestedLayoutCandidate.cs','NestedLayoutConformance.cs','NestedLayoutProfile.cs','ObservationNestedLayoutBenchmarks.cs','Program.cs')]
manifest={'base_commit':subprocess.check_output(['git','rev-parse','HEAD'],cwd=source,text=True).strip(),'source_hashes':{str(p.relative_to(source)):hashlib.sha256(p.read_bytes()).hexdigest() for p in files},'assembly_hashes':{p.name:hashlib.sha256(p.read_bytes()).hexdigest() for p in sorted(folder.iterdir()) if p.suffix=='.dll' or p.name.endswith(('.deps.json','.runtimeconfig.json'))}}
(root/'manifest.json').write_text(json.dumps(manifest,indent=2)+'\n')
# Trace/analysis and retained heap collection complete before all timed runs.
stem=root/'separated'
env=dict(os.environ,DOTNET_JitDisasm='ParseSeparated TryParseOnly ParseSharedSeparated Fallback ParseExisting',DOTNET_JitDisasmAssemblies='Cohesive Cohesive.Relations.Benchmarks',DOTNET_JitStdOutFile=str(stem.with_suffix('.jit.txt')))
with stem.with_suffix('.trace.log').open('w') as output:
 subprocess.run(['/private/tmp/ari-profile-tools/dotnet-trace','collect','--providers','Microsoft-DotNETCore-SampleProfiler:0x0:5,Microsoft-Windows-DotNETRuntime:0x20018:5','--show-child-io','--output',str(stem.with_suffix('.nettrace')),'--','dotnet',str(assembly),'--nested-layout-profile','separated','8'],env=env,stdout=output,stderr=subprocess.STDOUT,check=True)
with stem.with_suffix('.profile.json').open('w') as output:
 subprocess.run(['dotnet','/private/tmp/cohesive-nested-analyze/bin/Release/net10.0/Profile.dll',str(stem.with_suffix('.nettrace'))],stdout=output,check=True)
print('Separated trace complete',flush=True)
cases=[(w,128,'array',p) for w in (16,32,256) for p in (25,50,100)]+[(32,128,'array',75),(256,128,'array',6),(4,128,'array',100),(16,4096,'array',100),(32,128,'mixed-array',100)]
assert len(cases)==14
for width,count,scenario,percent in cases:
 for route in ('existing','shared','density'):
  line=subprocess.check_output(['dotnet',str(assembly),'--nested-layout-retained',str(width),str(count),scenario,route,str(max(2,2000//count)),str(percent)],text=True)
  with (root/'retained.txt').open('a') as out:out.write(line)
print('Retained diagnostics complete',flush=True)
rows=[]
def run(repeat,width,count,scenario,percent,method):
 stem=root/f'{repeat}-{scenario}-{width}-{count}-{percent}-{method}'
 pattern=f'*ObservationNestedLayoutBenchmarks.{method}(Width: {width}, Count: {count}, Scenario: "{scenario}", PresentPercent: {percent})'
 warm,iterations,millis=(6,10,300) if scenario=='case-array' else (4,6,200)
 with stem.with_suffix('.log').open('w') as output:
  subprocess.run(['dotnet',str(assembly),'--filter',pattern,'--inProcess','--warmupCount',str(warm),'--iterationCount',str(iterations),'--iterationTime',str(millis),'--artifacts',str(stem)],stdout=output,stderr=subprocess.STDOUT,check=True)
 reports=list(stem.glob('results/*full.json'));assert len(reports)==1,(stem,reports)
 items=json.loads(reports[0].read_text())['Benchmarks'];assert len(items)==1 and items[0]['Statistics'],stem
 b=items[0];assert f'Width: {width}, Count: {count}, Scenario: "{scenario}", PresentPercent: {percent}' in b['FullName'],b['FullName']
 row=dict(repeat=repeat,width=width,count=count,scenario=scenario,present_percent=percent,method=method,mean_ns=b['Statistics']['Mean'],allocated_bytes=b['Memory']['BytesAllocatedPerOperation'])
 rows.append(row)
 with (root/'summary.csv').open('a',newline='') as output:
  writer=csv.DictWriter(output,fieldnames=row.keys(),lineterminator='\n')
  if output.tell()==0:writer.writeheader()
  writer.writerow(row)
 print(stem.name,flush=True)
for repeat in (1,2,3):
 for width,count,scenario,percent in cases:
  methods=('ParseExisting','ParseSharedSeparated','ParseDensityAware')
  if repeat==2:methods=methods[::-1]
  for method in methods:run(repeat,width,count,scenario,percent,method)
 methods=('ParseExisting','ParseShared','ParseSharedSeparated','ParseDensityAware')
 if repeat==2:methods=methods[::-1]
 for method in methods:run(repeat,16,128,'case-array',100,method)
 methods=('ReadExisting','ReadOrdinals','ReadDensityAware')
 if repeat==2:methods=methods[::-1]
 for method in methods:run(repeat,16,128,'array',25,method)
 methods=('ParseExistingAndRead','ParseSharedSeparatedAndRead','ParseDensityAwareAndRead')
 if repeat==2:methods=methods[::-1]
 for method in methods:run(repeat,32,128,'array',25,method)
assert len(rows)==156,len(rows)
print('Complete',flush=True)
