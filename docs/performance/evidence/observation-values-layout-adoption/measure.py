from pathlib import Path
import subprocess,json,hashlib
root=Path('/private/tmp/cohesive-adoption-runs');root.mkdir(exist_ok=True)
folders={'control':Path('/private/tmp/cohesive-adoption-control'),'current':Path('/private/tmp/cohesive-adoption-current'),'adoption':Path('/private/tmp/cohesive-adoption-frozen')}
manifest={side:{p.name:hashlib.sha256(p.read_bytes()).hexdigest() for p in sorted(folder.iterdir()) if p.suffix=='.dll' or p.name.endswith(('.deps.json','.runtimeconfig.json'))} for side,folder in folders.items()}
assert [name for name,digest in manifest['control'].items() if manifest['current'].get(name)!=digest]==['Cohesive.Relations.dll','Cohesive.dll']
(root/'manifest.json').write_text(json.dumps(manifest,indent=2)+'\n')
def run(stem,folder,pattern):
 with stem.with_suffix('.log').open('w') as output:
  subprocess.run(['dotnet',str(folder/'Cohesive.Relations.Benchmarks.dll'),'--filter',pattern,'--inProcess','--warmupCount','4','--iterationCount','6','--iterationTime','200','--artifacts',str(stem)],stdout=output,stderr=subprocess.STDOUT,check=True)
 results=list((stem/'results').glob('*full.json'))
 assert len(results)==1 and len(json.loads(results[0].read_text())['Benchmarks'])==1,stem
 print(stem.name,flush=True)
for repeat in (1,2,3):
 for workload in ('flat','nested','collection','large'):
  methods=('IndexedNamePlan','IndexedOrdinalPlan','StrictOrdinalPlan') if repeat%2 else ('StrictOrdinalPlan','IndexedOrdinalPlan','IndexedNamePlan')
  for method in methods:
   run(root/f'adoption-{repeat}-{workload}-{method}',folders['adoption'],f'*JsonMaterializationBenchmarks.{method}(*{workload}*)')
 for width in (4,16,32):
  for side in (('control','current') if repeat%2 else ('current','control')):
   run(root/f'guard-{repeat}-{width}-{side}',folders[side],f'*SharedLayoutBenchmarks.ParseIndexedAndRead(Width: {width}, Reads: 16)')
print('Complete',flush=True)
