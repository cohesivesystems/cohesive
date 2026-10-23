import subprocess,os,json,hashlib
from pathlib import Path
cases=['current','before-reuse','no-cache-lookup','no-cache-or-wrapper']
for case in ['no-cache-lookup','no-cache-or-wrapper']:
 folder='/private/tmp/cohesive-cpu-'+case
 env=dict(os.environ, DOTNET_JitDisasm='ReadValue ReadValueCore ReadUntypedObject Set Get',DOTNET_JitDisasmAssemblies='Cohesive',DOTNET_JitStdOutFile=folder+'-jit.txt')
 with open(folder+'-jit.log','w') as output:
  subprocess.run(['dotnet',folder+'/Cohesive.Relations.Benchmarks.dll','--json-parse-profile','flat','2'],env=env,stdout=output,stderr=subprocess.STDOUT,check=True)
for run in [1,2,3]:
 for case in (cases if run!=2 else cases[::-1]):
  folder='/private/tmp/cohesive-cpu-'+case
  stem=folder+'-run'+str(run)
  with open(stem+'.log','w') as output:
   subprocess.run(['dotnet',folder+'/Cohesive.Relations.Benchmarks.dll','--filter','*ParseDirect*flat*','--inProcess','--warmupCount','6','--iterationCount','8','--iterationTime','250','--artifacts',stem+'-results'],stdout=output,stderr=subprocess.STDOUT,check=True)
manifest={}
for case in cases:
 folder=Path('/private/tmp/cohesive-cpu-'+case)
 manifest[case]={p.name:hashlib.sha256(p.read_bytes()).hexdigest() for p in sorted(folder.iterdir()) if p.suffix=='.dll' or p.name.endswith(('.deps.json','.runtimeconfig.json'))}
for case in cases[1:]:assert [n for n,h in manifest['current'].items() if manifest[case].get(n)!=h]==['Cohesive.dll']
Path('/private/tmp/cohesive-cpu-manifest.json').write_text(json.dumps(manifest,indent=2)+'\n')
print('Only Core differs across all four diagnostic cases.')
