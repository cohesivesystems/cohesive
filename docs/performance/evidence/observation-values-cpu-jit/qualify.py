from pathlib import Path
import subprocess,json,hashlib
cases={'control':'/private/tmp/cohesive-cpu-warm-current','candidate':'/private/tmp/cohesive-cpu-cache-outlined'}
phases=[('pipeline',3,'*ObservationValuePipelineBenchmarks.ParseDirect*'),('tagged',1,'*ObservationBoundaryConversionBenchmarks.DecodePortable*'),('escaped',1,'*ObservationJsonStringReuseBenchmarks.ParseDirect*escaped*'),('flat',1,'*ObservationValuePipelineBenchmarks.ParseDirect*flat*')]
for phase,runs,filter in phases:
 for run in range(1,runs+1):
  for side in (['control','candidate'] if run!=2 else ['candidate','control']):
   stem=f'/private/tmp/cohesive-cpu-final-{phase}-{side}-{run}'
   with open(stem+'.log','w') as output:
    subprocess.run(['dotnet',cases[side]+'/Cohesive.Relations.Benchmarks.dll','--filter',filter,'--inProcess','--warmupCount','6','--iterationCount','8','--iterationTime','250','--artifacts',stem+'-results'],stdout=output,stderr=subprocess.STDOUT,check=True)
manifest={}
for side,folder in cases.items():
 manifest[side]={p.name:hashlib.sha256(p.read_bytes()).hexdigest() for p in sorted(Path(folder).iterdir()) if p.suffix=='.dll' or p.name.endswith(('.deps.json','.runtimeconfig.json'))}
assert [n for n,h in manifest['control'].items() if manifest['candidate'].get(n)!=h]==['Cohesive.dll']
Path('/private/tmp/cohesive-cpu-final-manifest.json').write_text(json.dumps(manifest,indent=2)+'\n')
print('Qualification finished; only Core differs.')
