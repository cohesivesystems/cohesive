import subprocess
from pathlib import Path
for run in [2,3]:
 for side in (['candidate','control'] if run==2 else ['control','candidate']):
  stem=f'/private/tmp/cohesive-tags-{side}-repeat{run}'
  args=['dotnet',f'/private/tmp/cohesive-tags-{side}/Cohesive.Relations.Benchmarks.dll','--filter','*ObservationCheckpointBenchmarks.DecodeAndAdmit*','--inProcess','--warmupCount','6','--iterationCount','8','--iterationTime','250','--artifacts',stem+'-results']
  with open(stem+'.log','w') as output: subprocess.run(args,stdout=output,stderr=subprocess.STDOUT,check=True)
