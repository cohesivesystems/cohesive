import os,subprocess,csv
from pathlib import Path
rows=[]
for warm in ['flat','collection']:
 env=dict(os.environ,DOTNET_TieredCompilation='0',DOTNET_JitDisasm='ReadValue ReadValueCore ReadUntypedObject Get',DOTNET_JitDisasmAssemblies='Cohesive',DOTNET_JitStdOutFile=f'/private/tmp/cohesive-cpu-tiering-off-{warm}-jit.txt')
 result=subprocess.run(['dotnet','/private/tmp/cohesive-cpu-warm-current/Cohesive.Relations.Benchmarks.dll','--json-parse-profile','flat','5',warm],env=env,capture_output=True,text=True,check=True)
 Path(f'/private/tmp/cohesive-cpu-tiering-off-{warm}.log').write_text(result.stdout+result.stderr)
 values=dict(pair.split('=') for pair in result.stdout.strip().split(','));n=int(values['operations'])
 rows.append(dict(warmup=warm,ns_per_op=int(values['elapsed_ns'])/n,bytes_per_op=int(values['allocated_bytes'])/n))
with open('/private/tmp/cohesive-cpu-tiering-off.csv','w') as output:
 writer=csv.DictWriter(output,fieldnames=list(rows[0]));writer.writeheader();writer.writerows(rows)
