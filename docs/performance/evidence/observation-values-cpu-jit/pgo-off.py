import os,subprocess,csv
from pathlib import Path
rows=[]
for side,warm in [('current','flat'),('before-reuse','flat'),('current','collection'),('before-reuse','collection')]:
 env=dict(os.environ,DOTNET_TieredPGO='0')
 stem=f'/private/tmp/cohesive-cpu-pgo-off-{side}-{warm}'
 result=subprocess.run(['dotnet',f'/private/tmp/cohesive-cpu-warm-{side}/Cohesive.Relations.Benchmarks.dll','--json-parse-profile','flat','5',warm],env=env,capture_output=True,text=True,check=True)
 Path(stem+'.log').write_text(result.stdout+result.stderr)
 values=dict(pair.split('=') for pair in result.stdout.strip().split(','));n=int(values['operations'])
 rows.append(dict(side=side,warmup=warm,ns_per_op=int(values['elapsed_ns'])/n,bytes_per_op=int(values['allocated_bytes'])/n))
with open('/private/tmp/cohesive-cpu-pgo-off.csv','w') as output:
 writer=csv.DictWriter(output,fieldnames=list(rows[0]));writer.writeheader();writer.writerows(rows)
