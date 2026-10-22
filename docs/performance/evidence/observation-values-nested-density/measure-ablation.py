from pathlib import Path
import subprocess,csv,json,os
root=Path('/private/tmp/cohesive-nested-fallback-ablation');root.mkdir(exist_ok=True)
folder=Path('/private/tmp/cohesive-fallback-profile-frozen');rows=[]
for repeat in (1,2,3):
 methods=('ParseExisting','ParseShared','ParseForcedFallback','ProbeShared')
 if repeat==2:methods=methods[::-1]
 for method in methods:
  stem=root/f'{repeat}-{method}'
  with stem.with_suffix('.log').open('w') as output:
   subprocess.run(['dotnet',str(folder/'Cohesive.Relations.Benchmarks.dll'),'--filter',f'*ObservationNestedLayoutBenchmarks.{method}(Width: 16, Count: 128, Scenario: "case-array")','--inProcess','--warmupCount','6','--iterationCount','10','--iterationTime','300','--artifacts',str(stem)],stdout=output,stderr=subprocess.STDOUT,check=True)
  files=list(stem.glob('results/*full.json'));assert len(files)==1,stem
  items=json.loads(files[0].read_text())['Benchmarks'];assert len(items)==1 and items[0]['Statistics'],stem
  b=items[0];rows.append(dict(repeat=repeat,method=method,mean_ns=b['Statistics']['Mean'],allocated_bytes=b['Memory']['BytesAllocatedPerOperation']))
  print(stem.name,flush=True)
with (root/'summary.csv').open('w',newline='') as output:
 writer=csv.DictWriter(output,fieldnames=rows[0].keys(),lineterminator='\n');writer.writeheader();writer.writerows(rows)
print('Complete',flush=True)
