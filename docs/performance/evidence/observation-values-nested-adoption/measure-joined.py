from pathlib import Path
import subprocess,os,json,csv,hashlib,shutil,platform
w=Path('/Users/eulerfx/.codex/worktrees/observation-nested-layouts/cohesive');root=Path('/private/tmp/cohesive-nested-adoption-joined-runs');root.mkdir(exist_ok=True)
folders={}
for route in ('before','after'):
 folder=Path('/private/tmp/cohesive-nested-adoption-joined-'+route);shutil.copytree(w/'src/Cohesive.Relations.Benchmarks/bin/Release/net10.0',folder,dirs_exist_ok=True)
 if route=='before':
  for n in ('Cohesive.dll','Cohesive.pdb'):shutil.copyfile(Path('/private/tmp/cohesive-validation-final')/n,folder/n)
 folders[route]=folder
sha=lambda p:hashlib.sha256(p.read_bytes()).hexdigest()
assemblies={route:{p.name:sha(p) for p in sorted(folder.iterdir()) if p.suffix=='.dll' or p.name.endswith(('.deps.json','.runtimeconfig.json'))} for route,folder in folders.items()}
assert [n for n in assemblies['before'] if assemblies['before'][n]!=assemblies['after'][n]]==['Cohesive.dll']
files=['src/Cohesive/Model/'+n for n in ('ObservationJsonIngestionPlan.cs','CompactOrdinalObservationFields.cs','ObservationNestedFields.cs','ObservationLayout.cs','ObservationMaterializer.cs','ObservationValue.cs','ObservationFieldTraversal.cs','ObservationValidator.cs','Serialization/ObservationJsonReader.cs','Serialization/OrderedObservationFields.cs')]+['src/Cohesive.Tests/Model/ObservationNestedIngestionTests.cs']+['src/Cohesive.Relations.Benchmarks/'+n for n in ('ObservationNestedApplicationBenchmarks.cs','NestedLayoutConformance.cs','Cohesive.Relations.Benchmarks.csproj')]
(root/'manifest.json').write_text(json.dumps({'baseline_source_commit':subprocess.check_output(['git','rev-parse','HEAD'],cwd=w,text=True).strip(),'source_hashes':{n:sha(w/n) for n in files},'assembly_hashes':assemblies},indent=2)+'\n')
(root/'runtime-environment.json').write_text(json.dumps({'platform':platform.platform(),'machine':platform.machine(),'runtime_overrides':{k:v for k,v in os.environ.items() if k.startswith(('DOTNET_','COMPlus_'))}},indent=2)+'\n')
rows=[]
cases=[('ObservationNestedApplicationBenchmarks','IngestAndProject',s) for s in ('joined-128','joined-large')]
for repeat in (1,2,3):
 for cls,method,workload in cases:
  for route in (('before','after') if repeat!=2 else ('after','before')):
   stem=root/f'{repeat}-{workload}-{route}';pattern=f'*{cls}.{method}(Workload: "{workload}")'
   with stem.with_suffix('.log').open('w') as out:subprocess.run(['dotnet',str(folders[route]/'Cohesive.Relations.Benchmarks.dll'),'--filter',pattern,'--inProcess','--warmupCount','4','--iterationCount','6','--iterationTime','200','--artifacts',str(stem)],stdout=out,stderr=subprocess.STDOUT,check=True)
   reports=list(stem.glob('results/*full.json'));assert len(reports)==1,(stem,reports)
   items=json.loads(reports[0].read_text())['Benchmarks'];assert len(items)==1 and items[0]['Statistics'],(stem,items)
   b=items[0];assert f'Workload: "{workload}"' in b['FullName']
   row=dict(repeat=repeat,benchmark=cls,method=method,workload=workload,route=route,mean_ns=b['Statistics']['Mean'],allocated_bytes=b['Memory']['BytesAllocatedPerOperation']);rows.append(row)
   with (root/'summary.csv').open('a',newline='') as out:
    writer=csv.DictWriter(out,fieldnames=row.keys(),lineterminator='\n')
    if out.tell()==0:writer.writeheader()
    writer.writerow(row)
   print(stem.name,flush=True)
assert len(rows)==12
print('Complete',flush=True)
