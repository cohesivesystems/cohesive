from pathlib import Path
import subprocess,csv,json
root=Path('/private/tmp/cohesive-vector-runs');rows=[]
for repeat in (1,2,3):
 for width in (4,8,9,16,32,33,64):
  for side in (('control','candidate') if repeat%2 else ('candidate','control')):
   result=subprocess.check_output(['dotnet',f'/private/tmp/cohesive-vector-retained-{side}/Cohesive.Relations.Benchmarks.dll','--field-storage-retained',str(width),'20000'],text=True)
   values={name:int(value) for name,value in (item.split('=') for item in result.strip().split(','))}
   rows.append(dict(repeat=repeat,side=side,**values))
   print(repeat,side,result.strip(),flush=True)
with (root/'retained.csv').open('w') as output:
 writer=csv.DictWriter(output,fieldnames=list(rows[0]));writer.writeheader();writer.writerows(rows)
