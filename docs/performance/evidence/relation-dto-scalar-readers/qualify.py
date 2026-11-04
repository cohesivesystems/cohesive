from pathlib import Path
import subprocess,json,csv
root=Path('/private/tmp/automapper-gap-evidence');out=root/'qualification';out.mkdir(exist_ok=True)
rows=[]
cases=[]
for count in [1,32]:
 for scenario in ['Simple','Joined']:
  cases.append((count,[('control','CompiledKernelOnly'+scenario),('candidate','CompiledKernelOnly'+scenario),('candidate','AutoMapperCanonicalRows'+scenario)]))
for scenario in ['Simple','Joined']:cases.append((1024,[('control','CompiledCanonical'+scenario),('candidate','CompiledCanonical'+scenario)]))
for repeat in [1,2,3]:
 for count,pairs in cases:
  for side,method in (pairs if repeat%2 else list(reversed(pairs))):
   name=f'{repeat}-{count}-{side}-{method}'
   with (out/(name+'.log')).open('w') as stream:
    subprocess.run(['dotnet',str(root/side/'Cohesive.Relations.Benchmarks.dll'),'--filter',f'*RelationDtoWarmBenchmarks.{method}(RowCount: {count})','--inProcess','--warmupCount','4','--iterationCount','6','--iterationTime','200','--artifacts',str(out/name)],stdout=stream,stderr=subprocess.STDOUT,check=True)
   data=json.loads(next((out/name).glob('results/*full.json')).read_text())['Benchmarks'];assert len(data)==1 and data[0]['Statistics']
   b=data[0];rows.append(dict(repeat=repeat,count=count,side=side,method=method,ns=b['Statistics']['Mean'],bytes=b['Memory']['BytesAllocatedPerOperation']))
   with (root/'qualification.csv').open('w') as stream:
    writer=csv.DictWriter(stream,fieldnames=rows[0],lineterminator="\n");writer.writeheader();writer.writerows(rows)
   print(len(rows),name,round(rows[-1]['ns']/1000,3),flush=True)
