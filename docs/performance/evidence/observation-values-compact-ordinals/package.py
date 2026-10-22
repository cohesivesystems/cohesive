from pathlib import Path
import json,csv,statistics,hashlib,subprocess,shutil,tarfile,io
work=Path('/Users/eulerfx/.codex/worktrees/observation-nested-layouts/cohesive');root=Path('/private/tmp/cohesive-packed-runs');guards=Path('/private/tmp/cohesive-packed-guards');dest=work/'docs/performance/evidence/observation-values-compact-ordinals';dest.mkdir(parents=True,exist_ok=True)
sha=lambda data:hashlib.sha256(data).hexdigest()
manifest=json.loads((root/'manifest.json').read_text());assert all(sha((work/n).read_bytes())==h for n,h in manifest['source_hashes'].items())
assert all(sha((Path('/private/tmp/cohesive-packed-frozen')/n).read_bytes())==h for n,h in manifest['assembly_hashes'].items())
rows=list(csv.DictReader((root/'summary.csv').open()));assert len(rows)==168
for key in {(r['width'],r['count'],r['scenario'],r['present_percent'],r['method']) for r in rows}:assert sum((r['width'],r['count'],r['scenario'],r['present_percent'],r['method'])==key for r in rows)==3,key
retained=[dict(p.split('=',1) for p in line.split(',')) for line in (root/'retained.txt').read_text().splitlines()];assert len(retained)==44
grows=list(csv.DictReader((guards/'summary.csv').open()));assert len(grows)==12
med=lambda g,k:statistics.median(float(r[k]) for r in g)
report=Path('/private/tmp/packed-report-intro.md').read_text()
report+='## Parsing and complete admission\n\nTimes are μs per complete tree. Columns are existing reader, fixed ordinal vector, density-aware small/dictionary, and packed ordinal. Allocation is B/tree for the last two routes.\n\n| Scenario | Width | Rows | Present % | Existing μs | Fixed μs | Density μs | Packed μs | Density B | Packed B |\n|---|---:|---:|---:|---:|---:|---:|---:|---:|---:|\n'
parse=[r for r in rows if r['method']=='ParseExisting']
for key in dict.fromkeys((r['scenario'],r['width'],r['count'],r['present_percent']) for r in parse):
 groups=[[r for r in rows if (r['scenario'],r['width'],r['count'],r['present_percent'])==key and r['method']==m] for m in ('ParseExisting','ParseSharedSeparated','ParseDensityAware','ParseCompactOrdinal')]
 report+='| '+' | '.join(key)+' | '+' | '.join(f'{med(g,"mean_ns")/1000:.2f}' for g in groups)+' | '+' | '.join(f'{med(g,"allocated_bytes"):.0f}' for g in groups[2:])+' |\n'
report+='\n## Prebound reads and combined work\n\nRead-only results are for 128 rows and 2,048 accesses per operation; every route allocates zero bytes.\n\n| Fixture | Route | Median μs | Process range μs |\n|---|---|---:|---:|\n'
for s,w in [('array','16'),('sparse-array','256')]:
 for m in ('ReadExisting','ReadOrdinals','ReadDensityAware','ReadCompactOrdinal'):
  g=[r for r in rows if r['method']==m and r['scenario']==s and r['width']==w];v=[float(r['mean_ns'])/1000 for r in g];assert all(float(r['allocated_bytes'])==0 for r in g)
  report+=f'| {s}, width {w}, quarter-full | {m} | {statistics.median(v):.3f} | {min(v):.3f}–{max(v):.3f} |\n'
report+='\n| Parse + same reads | Existing μs | Fixed μs | Density μs | Packed μs |\n|---|---:|---:|---:|---:|\n'
for w,c in [('32','128'),('16','4096')]:
 groups=[[r for r in rows if r['method']==m and r['width']==w and r['count']==c] for m in ('ParseExistingAndRead','ParseSharedSeparatedAndRead','ParseDensityAwareAndRead','ParseCompactOrdinalAndRead')]
 report+=f'| Width {w}, {c} quarter-full rows | '+' | '.join(f'{med(g,"mean_ns")/1000:.2f}' for g in groups)+' |\n'
report+='\n## Retained memory\n\nBytes per complete retained tree, excluding prepared metadata. Preparation allocation and raw totals are included in `retained.txt`.\n\n| Scenario | Width | Rows | Present % | Existing B | Fixed B | Density B | Packed B | Packed vs fixed | Packed vs density |\n|---|---:|---:|---:|---:|---:|---:|---:|---:|---:|\n'
for key in dict.fromkeys((r['scenario'],r['width'],r['rows'],r['present_percent']) for r in retained):
 vals=[next(r for r in retained if (r['scenario'],r['width'],r['rows'],r['present_percent'])==key and r['route']==m) for m in ('existing','shared','density','packed')];v=[int(r['retained_bytes'])/int(r['trees']) for r in vals]
 report+='| '+' | '.join(key)+' | '+' | '.join(f'{x:.0f}' for x in v)+f' | {(v[3]/v[1]-1)*100:+.1f}% | {(v[3]/v[2]-1)*100:+.1f}% |\n'
report+='\n## Core integration controls\n\nAn independent matched harness/dependency closure compares the previous Core with the new Core; only `Cohesive.dll` differs. Neither direct-parser fixture selects compact storage. These bounded controls check for obvious integration regressions, rather than establish an equivalence bound.\n\n| Workload | Before μs | After μs | Change | Before B | After B |\n|---|---:|---:|---:|---:|---:|\n'
for workload in ('flat','collection'):
 a=[r for r in grows if r['workload']==workload and r['route']=='before'];b=[r for r in grows if r['workload']==workload and r['route']=='after'];x=med(a,'mean_ns');y=med(b,'mean_ns')
 report+=f'| {workload} | {x/1000:.3f} | {y/1000:.3f} | {(y/x-1)*100:+.1f}% | {med(a,"allocated_bytes"):.0f} | {med(b,"allocated_bytes"):.0f} |\n'
report+='\n'+Path('/private/tmp/packed-report-conclusion.md').read_text()
(work/'docs/performance/observation-values-compact-ordinals.md').write_text(report)
for folder,names in [(root,['manifest.json','summary.csv','retained.txt','runtime-environment.json']),(guards,['manifest.json','summary.csv'])]:
 for n in names:shutil.copyfile(folder/n,dest/(('guard-' if folder==guards else '')+n))
for src,name in [('measure-packed.py','measure.py'),('guard-packed.py','measure-guards.py'),('summarize-packed.py','summarize.py'),('package-packed.py','package.py'),('cohesive-packed-core-il.json','tested-core-il.json')]:shutil.copyfile(Path('/private/tmp')/src,dest/name)
il=json.loads((dest/'tested-core-il.json').read_text());assert not il['differences']
(dest/'verification.json').write_text(json.dumps({'core_passed':5172,'core_skipped':60,'relations_passed':1149,'focused_storage_tests':17,'semantic_scenarios':223,'candidate_variants':4,'timing_processes':168,'guard_processes':12,'retained_processes':44,'tested_core_methods':il['restored_methods'],'tested_core_il_differences':0,'new_internal_core_storage':True,'production_ingestion_selection_changed':False},indent=2)+'\n')
shutil.copyfile('/private/tmp/packed-readme.md',dest/'README.md')
files=[]
for folder,prefix in [(root,'main'),(guards,'guards')]:
 for p in sorted(folder.rglob('*')):
  if p.is_file() and p.name not in ('manifest.json','summary.csv','retained.txt','runtime-environment.json'):files.append((p,prefix+'/'+p.relative_to(folder).as_posix()))
for n in manifest['source_hashes']:files.append((work/n,'source/'+n))
for p in sorted(Path('/private/tmp').glob('cohesive-packed-*.log')):files.append((p,'verification/'+p.name))
archive=dest/'raw-reports.tar.gz';members=[]
with tarfile.open(archive,'w:gz') as tar:
 for p,n in files:
  data=p.read_bytes();info=tarfile.TarInfo(n);info.size=len(data);info.mode=0o644;info.mtime=0;tar.addfile(info,io.BytesIO(data));members.append({'path':n,'bytes':len(data),'sha256':sha(data)})
with tarfile.open(archive,'r:gz') as tar:
 for m in members:assert sha(tar.extractfile(m['path']).read())==m['sha256']
(dest/'raw-reports-index.json').write_text(json.dumps({'archive':archive.name,'sha256':sha(archive.read_bytes()),'members':members},indent=2)+'\n')
print('Verified 180 timed processes, 44 heap diagnostics,',len(members),'archived files;',archive.stat().st_size,'bytes')
