from pathlib import Path
import subprocess,shutil,json,hashlib,csv
root=Path('/private/tmp/cohesive-validation-guards');root.mkdir(exist_ok=True)
folders={}
for route in ('before','after'):
 folder=Path('/private/tmp/cohesive-validation-guard-'+route);shutil.copytree('/private/tmp/cohesive-validation-before',folder,dirs_exist_ok=True)
 if route=='after':
  for name in ('Cohesive.dll','Cohesive.pdb'):shutil.copyfile(Path('/private/tmp/cohesive-validation-final')/name,folder/name)
 folders[route]=folder
sha=lambda p:hashlib.sha256(p.read_bytes()).hexdigest()
manifest={route:{p.name:sha(p) for p in sorted(folder.iterdir()) if p.suffix=='.dll' or p.name.endswith(('.deps.json','.runtimeconfig.json'))} for route,folder in folders.items()}
assert [n for n in manifest['before'] if manifest['before'][n]!=manifest['after'][n]]==['Cohesive.dll']
(root/'manifest.json').write_text(json.dumps(manifest,indent=2)+'\n')
for rep in (1,2,3):
 for workload in ('flat','collection'):
  for route in (('before','after') if rep!=2 else ('after','before')):
   stem=root/f'{rep}-{workload}-{route}';pattern=f'*ObservationValuePipelineBenchmarks.ParseDirect(Workload: "{workload}")'
   with stem.with_suffix('.log').open('w') as out:subprocess.run(['dotnet',str(folders[route]/'Cohesive.Relations.Benchmarks.dll'),'--filter',pattern,'--inProcess','--warmupCount','4','--iterationCount','6','--iterationTime','200','--artifacts',str(stem)],stdout=out,stderr=subprocess.STDOUT,check=True)
   reports=list(stem.glob('results/*full.json'));assert len(reports)==1,(stem,reports)
   items=json.loads(reports[0].read_text())['Benchmarks'];assert len(items)==1 and items[0]['Statistics'];b=items[0]
   row=dict(repeat=rep,workload=workload,route=route,mean_ns=b['Statistics']['Mean'],allocated_bytes=b['Memory']['BytesAllocatedPerOperation'])
   with (root/'summary.csv').open('a',newline='') as out:
    w=csv.DictWriter(out,fieldnames=row.keys(),lineterminator='\n')
    if out.tell()==0:w.writeheader()
    w.writerow(row)
   print(stem.name,flush=True)
