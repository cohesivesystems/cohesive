from pathlib import Path
import subprocess,json,hashlib
root=Path('/private/tmp/cohesive-layout-runs');root.mkdir(exist_ok=True)
folder=Path('/private/tmp/cohesive-layout-frozen')
manifest={p.name:hashlib.sha256(p.read_bytes()).hexdigest() for p in sorted(folder.iterdir()) if p.suffix=='.dll' or p.name.endswith(('.deps.json','.runtimeconfig.json'))}
(root/'manifest.json').write_text(json.dumps(manifest,indent=2)+'\n')
# Guard against incremental builds retaining the rejected vector experiment.
assert manifest['Cohesive.dll']=='698305941afad42c05f26b4e4edd3ed28b6e094760d04db6259de6c708246f5a', 'Verify Core against the accepted baseline before timing.'
smoke=subprocess.check_output(['dotnet',str(folder/'Cohesive.Relations.Benchmarks.dll'),'--shared-layout-retained','16','generic','1000'],text=True).strip()
assert 'allocated_bytes=1680000,' in smoke,smoke
(root/'baseline-allocation-smoke.txt').write_text(smoke+'\n')
def run_case(repeat,width,reads,method):
 stem=root/f'{repeat}-{width}-{reads}-{method}'
 existing=list((stem/'results').glob('*full.json'))
 if existing and len(json.loads(existing[0].read_text())['Benchmarks'])==1:
  print('reuse '+stem.name,flush=True);return
 with stem.with_suffix('.log').open('w') as output:
  subprocess.run(['dotnet',str(folder/'Cohesive.Relations.Benchmarks.dll'),'--filter',f'*SharedLayoutBenchmarks.{method}(Width: {width}, Reads: {reads})','--inProcess','--warmupCount','4','--iterationCount','6','--iterationTime','200','--artifacts',str(stem)],stdout=output,stderr=subprocess.STDOUT,check=True)
 paths=list((stem/'results').glob('*full.json'))
 assert len(paths)==1 and len(json.loads(paths[0].read_text())['Benchmarks'])==1,stem
 case=json.loads(paths[0].read_text())['Benchmarks'][0]
 assert case['Method']==method and case['Parameters']==f'Width={width}&Reads={reads}',stem
 print(stem.name,flush=True)
for repeat in (1,2,3):
 for width in (4,16,32):
  for reads in (0,1,16,64):
   methods=('ParseGenericAndRead','ParseSharedAndRead') if repeat%2 else ('ParseSharedAndRead','ParseGenericAndRead')
   for method in methods:run_case(repeat,width,reads,method)
  for method in ('ReadGeneric','ReadSharedNames','ReadOrdinal','ParseIndexedAndRead'):run_case(repeat,width,16,method)
print('Timing complete',flush=True)
