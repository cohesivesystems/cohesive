from pathlib import Path
import subprocess,json
root=Path('/private/tmp/cohesive-adoption-runs')
folder=Path('/private/tmp/cohesive-adoption-frozen')
for repeat in (1,2,3,4):
 for method in (('StrictOrdinalPlan','IndexedNamePlan') if repeat%2 else ('IndexedNamePlan','StrictOrdinalPlan')):
  stem=root/f'qualify-{repeat}-large-{method}'
  with stem.with_suffix('.log').open('w') as output:
   subprocess.run(['dotnet',str(folder/'Cohesive.Relations.Benchmarks.dll'),'--filter',f'*JsonMaterializationBenchmarks.{method}(*large*)','--inProcess','--warmupCount','6','--iterationCount','10','--iterationTime','300','--artifacts',str(stem)],stdout=output,stderr=subprocess.STDOUT,check=True)
  results=list((stem/'results').glob('*full.json'))
  assert len(results)==1 and len(json.loads(results[0].read_text())['Benchmarks'])==1
  print(stem.name,flush=True)
print('Complete',flush=True)
