from pathlib import Path
import subprocess,shutil,os,csv,hashlib,json
source=Path('src/Cohesive.Relations.Benchmarks/bin/Release/net10.0')
for side in ['current','before-reuse']:
 target=Path('/private/tmp/cohesive-cpu-warm-'+side)
 shutil.copytree('/private/tmp/cohesive-cpu-'+side,target,dirs_exist_ok=True)
 for suffix in ['.dll','.pdb','.deps.json','.runtimeconfig.json']:
  p=source/('Cohesive.Relations.Benchmarks'+suffix)
  if p.exists():shutil.copy2(p,target/p.name)
rows=[]
conditions=[('current','flat'),('before-reuse','flat'),('current','collection'),('before-reuse','collection')]
for run in [1,2,3]:
 for side,warm in (conditions if run!=2 else conditions[::-1]):
  stem=f'/private/tmp/cohesive-cpu-warm-{side}-{warm}-run{run}'
  env=os.environ.copy()
  if run==1:env.update(DOTNET_JitDisasm='ReadValue ReadValueCore ReadUntypedObject Set Get',DOTNET_JitDisasmAssemblies='Cohesive',DOTNET_JitStdOutFile=stem+'-jit.txt')
  result=subprocess.run(['dotnet',f'/private/tmp/cohesive-cpu-warm-{side}/Cohesive.Relations.Benchmarks.dll','--json-parse-profile','flat','5',warm],env=env,capture_output=True,text=True,check=True)
  Path(stem+'.log').write_text(result.stdout+result.stderr)
  values=dict(pair.split('=') for pair in result.stdout.strip().split(','))
  operations=int(values['operations']);assert int(values['checksum'])==operations*6
  rows.append(dict(run=run,side=side,warmup=warm,operations=operations,elapsed_ns=int(values['elapsed_ns']),allocated_bytes=int(values['allocated_bytes']),ns_per_op=int(values['elapsed_ns'])/operations,bytes_per_op=int(values['allocated_bytes'])/operations))
with open('/private/tmp/cohesive-cpu-warmup.csv','w') as output:
 writer=csv.DictWriter(output,fieldnames=list(rows[0]));writer.writeheader();writer.writerows(rows)
manifest={}
for side in ['current','before-reuse']:
 folder=Path('/private/tmp/cohesive-cpu-warm-'+side)
 manifest[side]={p.name:hashlib.sha256(p.read_bytes()).hexdigest() for p in sorted(folder.iterdir()) if p.suffix=='.dll' or p.name.endswith(('.deps.json','.runtimeconfig.json'))}
assert [n for n,h in manifest['current'].items() if manifest['before-reuse'].get(n)!=h]==['Cohesive.dll']
Path('/private/tmp/cohesive-cpu-warmup-manifest.json').write_text(json.dumps(manifest,indent=2)+'\n')
for shape,filter in [('flat-only','*ParseDirect*flat*'),('matrix','*ObservationValuePipelineBenchmarks.ParseDirect*')]:
 stem='/private/tmp/cohesive-cpu-bdn-'+shape
 env=dict(os.environ,DOTNET_JitDisasm='ReadValue ReadValueCore ReadUntypedObject Set Get',DOTNET_JitDisasmAssemblies='Cohesive',DOTNET_JitStdOutFile=stem+'-jit.txt')
 with open(stem+'.log','w') as output:
  subprocess.run(['dotnet','/private/tmp/cohesive-cpu-current/Cohesive.Relations.Benchmarks.dll','--filter',filter,'--inProcess','--warmupCount','6','--iterationCount','8','--iterationTime','250','--artifacts',stem+'-results'],env=env,stdout=output,stderr=subprocess.STDOUT,check=True)
print('Controlled warmup and matched JIT collection complete.')
