from pathlib import Path
import subprocess,os,json
root=Path('/private/tmp/cohesive-nested-fallback-profile');folder=Path('/private/tmp/cohesive-fallback-profile-frozen')
for route in ('existing','shared','forced'):
 stem=root/route
 env=dict(os.environ,DOTNET_JitDisasm='ParseExisting Parse Fallback ReadObject ReadShapeObject TryReadShape ReadRecord Read',DOTNET_JitDisasmAssemblies='Cohesive Cohesive.Relations.Benchmarks',DOTNET_JitStdOutFile=str(stem.with_suffix('.jit.txt')))
 with stem.with_suffix('.trace.log').open('w') as output:
  subprocess.run(['/private/tmp/ari-profile-tools/dotnet-trace','collect','--providers','Microsoft-DotNETCore-SampleProfiler:0x0:5,Microsoft-Windows-DotNETRuntime:0x20018:5','--show-child-io','--output',str(stem.with_suffix('.nettrace')),'--','dotnet',str(folder/'Cohesive.Relations.Benchmarks.dll'),'--nested-layout-profile',route,'8'],env=env,stdout=output,stderr=subprocess.STDOUT,check=True)
 with stem.with_suffix('.profile.json').open('w') as output:
  subprocess.run(['dotnet','/private/tmp/cohesive-nested-analyze/bin/Release/net10.0/Profile.dll',str(stem.with_suffix('.nettrace'))],stdout=output,check=True)
 print(route,flush=True)
