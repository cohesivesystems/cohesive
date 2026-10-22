from pathlib import Path
import subprocess
root=Path('/private/tmp/cohesive-vector-runs')
cases=[]
# Confirm the combined outcome in both training regimes, and isolate the components at sixteen fields.
for repeat in (1,2,3):
 for warmup in (4,64):
  for width in (9,16,32):
   for method in (('Parse','Lookup','ParseAndLookup') if width==16 and repeat==1 and warmup==64 else ('ParseAndLookup',)):
    if repeat==1 and warmup==4: continue # Already measured by the screening run.
    cases.append((repeat,warmup,width,method))
for number,(repeat,warmup,width,method) in enumerate(cases):
 for side in (('control','candidate') if (number+repeat)%2 else ('candidate','control')):
  stem=root/f'{repeat}-{warmup}-{width}-{method}-{side}'
  with stem.with_suffix('.log').open('w') as output:
   subprocess.run(['dotnet',f'/private/tmp/cohesive-vector-{side}/Cohesive.Relations.Benchmarks.dll','--filter',f'*FieldStorageBenchmarks.{method}(*Width: {width}, WarmupWidth: {warmup}*','--inProcess','--warmupCount','4','--iterationCount','6','--iterationTime','200','--artifacts',str(stem)],stdout=output,stderr=subprocess.STDOUT,check=True)
  print(stem.name,flush=True)
print('Qualification complete',flush=True)
