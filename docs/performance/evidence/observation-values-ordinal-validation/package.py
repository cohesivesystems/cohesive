from pathlib import Path
import json,csv,statistics,hashlib,subprocess,shutil,tarfile,io
work=Path('/Users/eulerfx/.codex/worktrees/observation-nested-layouts/cohesive');root=Path('/private/tmp/cohesive-ordinal-validation-runs');guards=Path('/private/tmp/cohesive-validation-guards');profiles=Path('/private/tmp/cohesive-validation-profiles');dest=work/'docs/performance/evidence/observation-values-ordinal-validation';dest.mkdir(parents=True,exist_ok=True)
sha=lambda data:hashlib.sha256(data).hexdigest()
manifest=json.loads((root/'manifest.json').read_text());assert all(sha((work/n).read_bytes())==h for n,h in manifest['source_hashes'].items())
assert all(sha((Path('/private/tmp/cohesive-validation-final')/n).read_bytes())==h for n,h in manifest['assembly_hashes'].items())
rows=list(csv.DictReader((root/'summary.csv').open()));assert len(rows)==138
for key in {(r['width'],r['count'],r['scenario'],r['present_percent'],r['method']) for r in rows}:assert sum((r['width'],r['count'],r['scenario'],r['present_percent'],r['method'])==key for r in rows)==3,key
retained=[dict(p.split('=',1) for p in line.split(',')) for line in (root/'retained.txt').read_text().splitlines()];assert len(retained)==14
grows=list(csv.DictReader((guards/'summary.csv').open()));assert len(grows)==12
med=lambda g,k:statistics.median(float(r[k]) for r in g)
keys=lambda g:list(dict.fromkeys((r['scenario'],r['width'],r['count'],r['present_percent']) for r in g))
def select(key,m):return [r for r in rows if (r['scenario'],r['width'],r['count'],r['present_percent'])==key and r['method']==m]
report=Path('/private/tmp/ordinal-validation-intro.md').read_text()
for title,a,b in [('Validation of packed trees','ValidateCompactOrdinal','ValidateBoundCompactOrdinal'),('Complete packed ingestion','ParseCompactOrdinal','ParseBoundCompactOrdinal')]:
 report+=f'## {title}\n\nTimes are μs per complete tree; allocation is B/operation.\n\n| Scenario | Width | Rows | Present % | Reference μs | Bound μs | Speedup | Reference B | Bound B |\n|---|---:|---:|---:|---:|---:|---:|---:|---:|\n'
 for key in keys([r for r in rows if r['method']==a and r['scenario']!='case-array']):
  x=select(key,a);y=select(key,b);v=med(x,'mean_ns');z=med(y,'mean_ns')
  if a.startswith('Validate'):assert all(float(r['allocated_bytes'])==0 for r in y)
  report+='| '+' | '.join(key)+f' | {v/1000:.3f} | {z/1000:.3f} | {v/z:.2f}× | {med(x,"allocated_bytes"):.0f} | {med(y,"allocated_bytes"):.0f} |\n'
 report+='\n'
report+='## Fixed-vector controls and combined reads\n\nThese controls use the same binding mechanism over fixed vectors; the physical format is unchanged within each pair.\n\n| Operation | Present % | Reference μs | Bound μs | Reference B | Bound B |\n|---|---:|---:|---:|---:|---:|\n'
for p in ('25','100'):
 key=('array','16','128',p)
 for label,a,b in [('Validation','ValidateShared','ValidateBoundShared'),('Complete parse','ParseSharedSeparated','ParseBoundShared')]:
  x=select(key,a);y=select(key,b);report+=f'| {label} | {p} | {med(x,"mean_ns")/1000:.3f} | {med(y,"mean_ns")/1000:.3f} | {med(x,"allocated_bytes"):.0f} | {med(y,"allocated_bytes"):.0f} |\n'
report+='\n| Packed parse + identical prebound reads | Reference μs | Bound μs | Speedup |\n|---|---:|---:|---:|\n'
for w,c in [('32','128'),('16','4096')]:
 key=('array',w,c,'25');a=select(key,'ParseCompactOrdinalAndRead');b=select(key,'ParseBoundCompactOrdinalAndRead');x=med(a,'mean_ns');y=med(b,'mean_ns');report+=f'| Width {w}, {c} quarter-full rows | {x/1000:.3f} | {y/1000:.3f} | {x/y:.2f}× |\n'
report+='\n## Preparation and fallback\n\nPreparation below includes fresh layout/parser allocation over warm governing graph metadata. The bound route additionally owns a private linked closure and permutations.\n\n| Width | Reference μs | Bound μs | Reference B | Bound B |\n|---|---:|---:|---:|---:|\n'
for w,p in [('16','25'),('256','6')]:
 key=('array',w,'128',p);a=select(key,'PrepareCompactOrdinal');b=select(key,'PrepareBoundCompactOrdinal');report+=f'| {w} | {med(a,"mean_ns")/1000:.3f} | {med(b,"mean_ns")/1000:.3f} | {med(a,"allocated_bytes"):.0f} | {med(b,"allocated_bytes"):.0f} |\n'
report+='\n| Case-variant fallback, width 16/128 rows | Median μs | Process range μs | B |\n|---|---:|---:|---:|\n'
for m in ('ParseCompactOrdinal','ParseBoundCompactOrdinal'):
 g=select(('case-array','16','128','100'),m);v=[float(r['mean_ns'])/1000 for r in g];report+=f'| {m} | {statistics.median(v):.3f} | {min(v):.3f}–{max(v):.3f} | {med(g,"allocated_bytes"):.0f} |\n'
report+='\n## Retained trees and ordinary-parser controls\n\nBinding does not change stored values. Complete-tree retained bytes match in every pair, excluding prepared parser metadata.\n\n| Scenario | Width | Rows | Present % | Retained B/tree, both routes | Reference allocation B/tree | Bound allocation B/tree |\n|---|---:|---:|---:|---:|---:|---:|\n'
for key in dict.fromkeys((r['scenario'],r['width'],r['rows'],r['present_percent']) for r in retained):
 a=next(r for r in retained if (r['scenario'],r['width'],r['rows'],r['present_percent'])==key and r['route']=='packed');b=next(r for r in retained if (r['scenario'],r['width'],r['rows'],r['present_percent'])==key and r['route']=='bound-packed');x=int(a['retained_bytes'])/int(a['trees']);y=int(b['retained_bytes'])/int(b['trees']);assert x==y,(key,x,y)
 report+='| '+' | '.join(key)+f' | {x:.0f} | {int(a["allocated_bytes"])/int(a["trees"]):.0f} | {int(b["allocated_bytes"])/int(b["trees"]):.0f} |\n'
report+='\nThe ordinary-parser comparison uses a matched previous harness, swapping only Core. Neither route selects a bound plan. These are bounded regression controls, not statistical equivalence proofs.\n\n| Workload | Before μs | After μs | Change | Before B | After B |\n|---|---:|---:|---:|---:|---:|\n'
for workload in ('flat','collection'):
 a=[r for r in grows if r['workload']==workload and r['route']=='before'];b=[r for r in grows if r['workload']==workload and r['route']=='after'];x=med(a,'mean_ns');y=med(b,'mean_ns');report+=f'| {workload} | {x/1000:.3f} | {y/1000:.3f} | {(y/x-1)*100:+.1f}% | {med(a,"allocated_bytes"):.0f} | {med(b,"allocated_bytes"):.0f} |\n'
report+='\n'+Path('/private/tmp/ordinal-validation-conclusion.md').read_text()
(work/'docs/performance/observation-values-ordinal-validation.md').write_text(report)
for folder,names in [(root,['manifest.json','summary.csv','retained.txt','runtime-environment.json']),(guards,['manifest.json','summary.csv'])]:
 for n in names:shutil.copyfile(folder/n,dest/(('guard-' if folder==guards else '')+n))
for src,name in [('measure-ordinal-validation.py','measure.py'),('guard-ordinal-validation.py','measure-guards.py'),('summarize-ordinal-validation.py','summarize.py'),('profile-ordinal-validation.py','profile-before.py'),('profile-ordinal-validation-after.py','profile-after.py'),('package-ordinal-validation.py','package.py'),('cohesive-ordinal-validation-core-il.json','tested-core-il.json')]:shutil.copyfile(Path('/private/tmp')/src,dest/name)
pmanifest={'base_commit':manifest['base_commit'],'before_source_hashes':json.loads((profiles/'before-source-manifest.json').read_text()),'before_assembly_hashes':{p.name:sha(p.read_bytes()) for p in sorted(Path('/private/tmp/cohesive-validation-before').iterdir()) if p.suffix=='.dll' or p.name.endswith(('.deps.json','.runtimeconfig.json'))},'after_assembly_hashes':manifest['assembly_hashes']}
(dest/'profile-manifest.json').write_text(json.dumps(pmanifest,indent=2)+'\n')
ps={}
for p in sorted(profiles.glob('*.profile.json')):
 d=json.loads(p.read_text());ps[p.stem]={'total_samples':d['total'],'selected_samples':d['selected'],'dictionary_hash_leaf_samples':sum(x['Value'] for x in d['self'] if 'Dictionary' in x['Key'] or 'HashCodeOrdinalIgnoreCase' in x['Key']),'poll_leaf_samples':sum(x['Value'] for x in d['self'] if 'PollGC' in x['Key']),'top_nonpoll_leaf':[x for x in d['self'] if 'PollGC' not in x['Key']][:12]}
assert len(ps)==7
(dest/'profile-summary.json').write_text(json.dumps(ps,indent=2)+'\n')
il=json.loads((dest/'tested-core-il.json').read_text());assert not il['differences']
(dest/'verification.json').write_text(json.dumps({'core_passed':5183,'core_skipped':60,'relations_passed':1149,'focused_tests':11,'semantic_scenarios':223,'candidate_variants':6,'main_timing_processes':138,'guard_processes':12,'retained_processes':14,'cpu_jit_profiles':7,'tested_core_methods':il['restored_methods'],'tested_core_il_differences':0,'warm_bound_sparse_validation_allocated_bytes':0,'public_api_changed':False,'production_binding_selection_changed':False},indent=2)+'\n')
shutil.copyfile('/private/tmp/ordinal-validation-readme.md',dest/'README.md')
files=[]
for folder,prefix in [(root,'main'),(guards,'guards'),(profiles,'profiles')]:
 for p in sorted(folder.rglob('*')):
  if p.is_file() and p.suffix!='.etlx' and p.name not in ('manifest.json','summary.csv','retained.txt','runtime-environment.json'):files.append((p,prefix+'/'+p.relative_to(folder).as_posix()))
for n in manifest['source_hashes']:files.append((work/n,'source/'+n))
for n in ('Program.cs','Profile.csproj'):files.append((Path('/private/tmp/cohesive-nested-analyze')/n,'analyzer/'+n))
for p in sorted(Path('/private/tmp').glob('cohesive-ordinal-validation-*.log')):files.append((p,'verification/'+p.name))
for p in sorted(Path('/private/tmp').glob('ordinal-validation-*.md')):files.append((p,'templates/'+p.name))
archive=dest/'raw-reports.tar.gz';members=[]
with tarfile.open(archive,'w:gz') as tar:
 for p,n in files:
  data=p.read_bytes();info=tarfile.TarInfo(n);info.size=len(data);info.mode=0o644;info.mtime=0;tar.addfile(info,io.BytesIO(data));members.append({'path':n,'bytes':len(data),'sha256':sha(data)})
with tarfile.open(archive,'r:gz') as tar:
 for m in members:assert sha(tar.extractfile(m['path']).read())==m['sha256']
(dest/'raw-reports-index.json').write_text(json.dumps({'archive':archive.name,'sha256':sha(archive.read_bytes()),'members':members},indent=2)+'\n')
print('Verified 150 timed processes, 14 heap diagnostics, seven CPU/JIT profiles,',len(members),'archived files;',archive.stat().st_size,'bytes')
