from pathlib import Path
import shutil,subprocess
cache=Path('src/Cohesive/Model/ObservationJsonPropertyNames.cs')
reader=Path('src/Cohesive/Model/Serialization/ObservationJsonReader.cs')
original_cache=cache.read_bytes();original_reader=reader.read_bytes()
try:
 s=cache.read_text();start=s.index('        internal string Get(ref Utf8JsonReader reader)\n        {');end=s.index('\n        public void Dispose()',start)
 s=s[:start]+'        internal string Get(ref Utf8JsonReader reader) => reader.GetString()!;\n'+s[end:]
 cache.write_text(s)
 for case in ['no-cache-lookup','no-cache-or-wrapper']:
  if case=='no-cache-or-wrapper':
   s=reader.read_text().replace('''        try { return scope.ReadValueCore(ref reader); }
        finally { scope.Dispose(); }''','''        return scope.ReadValueCore(ref reader);''',1)
   reader.write_text(s)
  folder=Path('/private/tmp/cohesive-cpu-'+case)
  shutil.copytree('/private/tmp/cohesive-cpu-current',folder,dirs_exist_ok=True)
  with open('/private/tmp/cohesive-cpu-'+case+'-build.log','w') as output:
   subprocess.run(['dotnet','build','src/Cohesive','-c','Release','--no-restore','-m:1','-p:UseSharedCompilation=false','-v','quiet'],stdout=output,stderr=subprocess.STDOUT,check=True)
  shutil.copy2('src/Cohesive/bin/Release/net10.0/Cohesive.dll',folder/'Cohesive.dll')
finally:
 cache.write_bytes(original_cache);reader.write_bytes(original_reader)
print('Diagnostic-only libraries frozen; both production source files restored.')
