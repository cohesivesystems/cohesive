from pathlib import Path
import subprocess, hashlib, json, shutil
root=Path('/private/tmp')
for side in ['control','candidate']:
 folder=root/('cohesive-tags-'+side)
 args=['dotnet',str(folder/'Cohesive.Relations.Benchmarks.dll'),'--filter','*ObservationTaggedMetadataBenchmarks*','*ObservationBoundaryConversionBenchmarks.DecodePortable*','*ObservationCheckpointBenchmarks.DecodeAndAdmit*','--inProcess','--warmupCount','3','--iterationCount','5','--iterationTime','250','--artifacts',str(root/('cohesive-tags-'+side+'-final-results'))]
 with (root/('cohesive-tags-'+side+'-final.log')).open('w') as out:
  subprocess.run(args,stdout=out,stderr=subprocess.STDOUT,check=True)
 with (root/('cohesive-tags-'+side+'-wire.csv')).open('w') as out:
  subprocess.run(['dotnet',str(folder/'Cohesive.Relations.Benchmarks.dll'),'--checkpoint-wire-sizes'],stdout=out,check=True)
manifest={}
for side in ['control','candidate']:
 folder=root/('cohesive-tags-'+side)
 manifest[side]={p.name:hashlib.sha256(p.read_bytes()).hexdigest() for p in sorted(folder.iterdir()) if p.suffix=='.dll' or p.name.endswith(('.deps.json','.runtimeconfig.json'))}
(root/'cohesive-tags-manifest.json').write_text(json.dumps(manifest,indent=2)+'\n')
diff=[name for name in manifest['control'] if manifest['control'][name]!=manifest['candidate'].get(name)]
assert diff==['Cohesive.dll'],diff
assert (root/'cohesive-tags-control-wire.csv').read_bytes()==(root/'cohesive-tags-candidate-wire.csv').read_bytes()
print('Only Core differs; checkpoint sizes and hashes match.')
