from pathlib import Path
import subprocess,json,csv
root=Path('/private/tmp/automapper-gap-batch');out=root/'timing';out.mkdir(exist_ok=True)
rows=[]
for repeat in [1,2,3]:
 for scenario in ['Simple','Joined']:
  pairs=[('candidate','CompiledBatchKernel'+scenario),('candidate','CompiledKernelOnly'+scenario),('candidate','AutoMapperCanonicalRows'+scenario)]
  if repeat%2==0:pairs.reverse()
  for side,method in pairs:
   name=f'{repeat}-{side}-{method}'
   with (out/(name+'.log')).open('w') as stream:
    subprocess.run(['dotnet',str(root/side/'Cohesive.Relations.Benchmarks.dll'),'--filter',f'*RelationDtoWarmBenchmarks.{method}(RowCount: 1024)','--inProcess','--warmupCount','4','--iterationCount','6','--iterationTime','200','--artifacts',str(out/name)],stdout=stream,stderr=subprocess.STDOUT,check=True)
   data=json.loads(next((out/name).glob('results/*full.json')).read_text())['Benchmarks'];assert len(data)==1 and data[0]['Statistics']
   b=data[0];rows.append(dict(repeat=repeat,side=side,method=method,ns=b['Statistics']['Mean'],bytes=b['Memory']['BytesAllocatedPerOperation']))
   with (root/'timing.csv').open('w') as stream:
    writer=csv.DictWriter(stream,fieldnames=rows[0]);writer.writeheader();writer.writerows(rows)
   print(name,round(rows[-1]['ns']/1000,3),rows[-1]['bytes'],flush=True)
