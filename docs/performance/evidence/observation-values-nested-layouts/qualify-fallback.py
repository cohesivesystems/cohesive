from pathlib import Path
import subprocess,json,csv,hashlib
root=Path('/private/tmp/cohesive-nested-runs')
folder=Path('/private/tmp/cohesive-nested-frozen')
manifest=json.loads((root/'manifest.json').read_text())['assembly_hashes']
assert all(hashlib.sha256((folder/name).read_bytes()).hexdigest()==digest for name,digest in manifest.items())
rows=[]
for repeat in range(1,5):
 methods=('ParseExisting','ParseShared') if repeat%2 else ('ParseShared','ParseExisting')
 for method in methods:
  stem=root/f'fallback-{repeat}-{method}'
  with stem.with_suffix('.log').open('w') as output:
   subprocess.run(['dotnet',str(folder/'Cohesive.Relations.Benchmarks.dll'),'--filter',f'*ObservationNestedLayoutBenchmarks.{method}(Width: 16, Count: 128, Scenario: "case-array")','--inProcess','--warmupCount','6','--iterationCount','10','--iterationTime','300','--artifacts',str(stem)],stdout=output,stderr=subprocess.STDOUT,check=True)
  results=list(stem.glob('results/*full.json'));assert len(results)==1,stem
  items=json.loads(results[0].read_text())['Benchmarks'];assert len(items)==1 and items[0]['Statistics'],stem
  b=items[0];rows.append(dict(repeat=repeat,method=method,mean_ns=b['Statistics']['Mean'],allocated_bytes=b['Memory']['BytesAllocatedPerOperation']))
  print(stem.name,flush=True)
with (root/'fallback-summary.csv').open('w',newline='') as output:
 writer=csv.DictWriter(output,fieldnames=rows[0].keys(),lineterminator='\n');writer.writeheader();writer.writerows(rows)
print('Complete',flush=True)
