from pathlib import Path
import subprocess, json, hashlib
root=Path('/private/tmp/cohesive-vector-runs');root.mkdir(exist_ok=True)
folders={side:Path('/private/tmp/cohesive-vector-'+side) for side in ('control','candidate')}
manifest={side:{p.name:hashlib.sha256(p.read_bytes()).hexdigest() for p in sorted(folder.iterdir()) if p.suffix=='.dll' or p.name.endswith(('.deps.json','.runtimeconfig.json'))} for side,folder in folders.items()}
assert [name for name,digest in manifest['control'].items() if manifest['candidate'].get(name)!=digest]==['Cohesive.dll']
(root/'manifest.json').write_text(json.dumps(manifest,indent=2)+'\n')
# One case per process; paired order alternates by width/method as well as repeat.
cases=[(4,width,method) for width in (4,8,9,16,32,33,64) for method in ('Parse','Lookup','ParseAndLookup')]
for number,(warmup,width,method) in enumerate(cases):
 for side in (('control','candidate') if number%2==0 else ('candidate','control')):
  stem=root/f'1-{warmup}-{width}-{method}-{side}'
  with stem.with_suffix('.log').open('w') as output:
   subprocess.run(['dotnet',str(folders[side]/'Cohesive.Relations.Benchmarks.dll'),'--filter',f'*FieldStorageBenchmarks.{method}(*Width: {width}, WarmupWidth: {warmup}*','--inProcess','--warmupCount','4','--iterationCount','6','--iterationTime','200','--artifacts',str(stem)],stdout=output,stderr=subprocess.STDOUT,check=True)
  print(stem.name,flush=True)
print('Screen complete',flush=True)
