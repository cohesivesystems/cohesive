from pathlib import Path
import subprocess,csv
root=Path('/private/tmp/cohesive-layout-runs');rows=[]
for repeat in (1,2,3):
 for width in (4,16,32):
  for route in (('generic','shared') if repeat%2 else ('shared','generic')):
   line=subprocess.check_output(['dotnet','/private/tmp/cohesive-layout-frozen/Cohesive.Relations.Benchmarks.dll','--shared-layout-retained',str(width),route,'20000'],text=True).strip()
   values={name:(value if name=='route' else int(value)) for name,value in (item.split('=') for item in line.split(','))}
   rows.append(dict(repeat=repeat,**values));print(repeat,line,flush=True)
with (root/'retained.csv').open('w') as output:
 writer=csv.DictWriter(output,fieldnames=list(rows[0]));writer.writeheader();writer.writerows(rows)
