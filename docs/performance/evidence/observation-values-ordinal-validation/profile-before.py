from pathlib import Path
import subprocess,shutil,json,hashlib,os
w=Path('/Users/eulerfx/.codex/worktrees/observation-nested-layouts/cohesive');f=Path('/private/tmp/cohesive-validation-before');shutil.copytree(w/'src/Cohesive.Relations.Benchmarks/bin/Release/net10.0',f,dirs_exist_ok=True)
r=Path('/private/tmp/cohesive-validation-profiles');r.mkdir(exist_ok=True)
files=['src/Cohesive.Relations.Benchmarks/'+n for n in ['ObservationNestedLayoutBenchmarks.cs','NestedLayoutProfile.cs','NestedLayoutCandidate.cs']]
(r/'before-source-manifest.json').write_text(json.dumps({n:hashlib.sha256((w/n).read_bytes()).hexdigest() for n in files},indent=2)+'\n')
for n in files:
 p=r/'before-source'/n;p.parent.mkdir(parents=True,exist_ok=True);shutil.copyfile(w/n,p)
for route,width,percent in [('validate-packed',16,25),('parse-packed',16,25),('validate-packed',256,6)]:
 stem=r/f'before-{route}-{width}-{percent}'
 env=dict(os.environ,DOTNET_JitDisasm='TryMatchObject TryFindUnknownProperty TryGetInsensitive TryValidateFieldValue',DOTNET_JitDisasmAssemblies='Cohesive',DOTNET_JitStdOutFile=str(stem.with_suffix('.jit.txt')))
 with stem.with_suffix('.log').open('w') as out:subprocess.run(['/private/tmp/ari-profile-tools/dotnet-trace','collect','--providers','Microsoft-DotNETCore-SampleProfiler:0x0:5,Microsoft-Windows-DotNETRuntime:0x20018:5','--show-child-io','--output',str(stem.with_suffix('.nettrace')),'--','dotnet',str(f/'Cohesive.Relations.Benchmarks.dll'),'--nested-layout-profile',route,'8',str(width),'128','array',str(percent)],stdout=out,stderr=subprocess.STDOUT,env=env,check=True)
 with stem.with_suffix('.profile.json').open('w') as out:subprocess.run(['dotnet','/private/tmp/cohesive-nested-analyze/bin/Release/net10.0/Profile.dll',str(stem.with_suffix('.nettrace'))],stdout=out,check=True)
 print(stem.name,flush=True)
