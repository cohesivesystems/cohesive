from pathlib import Path
import subprocess,os
f=Path('/private/tmp/cohesive-validation-final');r=Path('/private/tmp/cohesive-validation-profiles')
for route,width,percent in [('validate-packed',16,25),('validate-bound-packed',16,25),('parse-bound-packed',16,25),('validate-bound-packed',256,6)]:
 stem=r/f'after-{route}-{width}-{percent}'
 env=dict(os.environ,DOTNET_JitDisasm='TryMatchObject TryFindUnknownProperty TryGetInsensitive TryValidateFieldValue',DOTNET_JitDisasmAssemblies='Cohesive',DOTNET_JitStdOutFile=str(stem.with_suffix('.jit.txt')))
 with stem.with_suffix('.log').open('w') as out:subprocess.run(['/private/tmp/ari-profile-tools/dotnet-trace','collect','--providers','Microsoft-DotNETCore-SampleProfiler:0x0:5,Microsoft-Windows-DotNETRuntime:0x20018:5','--show-child-io','--output',str(stem.with_suffix('.nettrace')),'--','dotnet',str(f/'Cohesive.Relations.Benchmarks.dll'),'--nested-layout-profile',route,'8',str(width),'128','array',str(percent)],stdout=out,stderr=subprocess.STDOUT,env=env,check=True)
 with stem.with_suffix('.profile.json').open('w') as out:subprocess.run(['dotnet','/private/tmp/cohesive-nested-analyze/bin/Release/net10.0/Profile.dll',str(stem.with_suffix('.nettrace'))],stdout=out,check=True)
 print(stem.name,flush=True)
