from pathlib import Path
import csv,json,hashlib,statistics,shutil,tarfile,io
w=Path('/Users/eulerfx/.codex/worktrees/observation-nested-layouts/cohesive');r=Path('/private/tmp/cohesive-nested-adoption-gated-runs');initial=Path('/private/tmp/cohesive-nested-adoption-runs');d=w/'docs/performance/evidence/observation-values-nested-adoption';d.mkdir(parents=True,exist_ok=True)
sha=lambda b:hashlib.sha256(b).hexdigest()
m=json.loads((r/'manifest.json').read_text());
prior=json.loads((w/'docs/performance/evidence/observation-values-ordinal-validation/manifest.json').read_text())
assert m['assembly_hashes']['before']['Cohesive.dll']==prior['assembly_hashes']['Cohesive.dll']
assert all(sha((Path('/private/tmp/cohesive-nested-adoption-gated-source')/n).read_bytes())==v for n,v in m['source_hashes'].items())
for route,hashes in m['assembly_hashes'].items():assert all(sha((Path('/private/tmp/cohesive-nested-adoption-gated-'+route)/n).read_bytes())==v for n,v in hashes.items())
rows=list(csv.DictReader((r/'summary.csv').open()));mainrows=rows.copy();
joined=Path('/private/tmp/cohesive-nested-adoption-joined-runs');jm=json.loads((joined/'manifest.json').read_text());
assert all(sha((w/n).read_bytes())==v for n,v in jm['source_hashes'].items())
assert all(jm['assembly_hashes'][route]['Cohesive.dll']==m['assembly_hashes'][route]['Cohesive.dll'] for route in ('before','after'))
for route,hashes in jm['assembly_hashes'].items():assert all(sha((Path('/private/tmp/cohesive-nested-adoption-joined-'+route)/n).read_bytes())==v for n,v in hashes.items())
joinedrows=list(csv.DictReader((joined/'summary.csv').open()));assert len(joinedrows)==12
rows+=joinedrows
old=list(csv.DictReader((initial/'summary.csv').open()));assert len(mainrows)==len(old)==60 and len(rows)==72
med=lambda g,k:statistics.median(float(x[k]) for x in g)
report=Path('/private/tmp/nested-adoption-intro.md').read_text()
report+='\n## Final application and control measurements\n\nMedian of three fresh-process means; times are μs, allocation is B/complete ingestion.\n\n| Workload | Before μs | Integrated μs | Speedup | Before B | Integrated B | Allocation change |\n|---|---:|---:|---:|---:|---:|---:|\n'
for case in dict.fromkeys(x['workload'] for x in rows):
 a=[x for x in rows if x['workload']==case and x['route']=='before'];b=[x for x in rows if x['workload']==case and x['route']=='after'];assert len(a)==len(b)==3
 x=med(a,'mean_ns');y=med(b,'mean_ns');u=med(a,'allocated_bytes');v=med(b,'allocated_bytes')
 report+=f'| {case} | {x/1000:.3f} | {y/1000:.3f} | {x/y:.2f}× | {u:.0f} | {v:.0f} | {(v/u-1)*100:+.1f}% |\n'
report+='\n## First-integration allocation finding\n\nThe initial candidate boxed the inner storage enumerator, including its cached key/value pair. The final nested view boxes only its cursor state, and canonical ordering recognizes its dense storage without a field buffer. The final production policy also excludes narrow records; the table below reflects all these changes together, not an isolated enumeration speedup. Canonical traversal continues to use the existing unboxed enumerators. This adds no new value representation or serialization semantics.\n\n| Workload | Initial integrated B | Final integrated B |\n|---|---:|---:|\n'
for case in ('freight-8','freight-128','freight-large'):
 a=[x for x in old if x['workload']==case and x['route']=='after'];b=[x for x in rows if x['workload']==case and x['route']=='after'];report+=f'| {case} | {med(a,"allocated_bytes"):.0f} | {med(b,"allocated_bytes"):.0f} |\n'
report+='\n'+Path('/private/tmp/nested-adoption-conclusion.md').read_text()
(w/'docs/performance/observation-values-nested-adoption.md').write_text(report)
for name in ('manifest.json','summary.csv','runtime-environment.json'):
 shutil.copyfile(r/name,d/name)
for name in ('manifest.json','summary.csv'):shutil.copyfile(joined/name,d/('joined-'+name))
for name in ('manifest.json','summary.csv'):
 shutil.copyfile(initial/name,d/('initial-'+name))
 shutil.copyfile(Path('/private/tmp/cohesive-nested-adoption-final-runs')/name,d/('enum-'+name))
 shutil.copyfile(Path('/private/tmp/cohesive-nested-adoption-verified-runs')/name,d/('broad-'+name))
for src,target in [('measure-nested-adoption.py','measure-initial.py'),('measure-nested-adoption-gated.py','measure.py'),('measure-nested-adoption-joined.py','measure-joined.py'),('measure-nested-adoption-verified.py','measure-broad.py'),('measure-nested-adoption-final.py','measure-enum.py'),('summarize-nested-adoption.py','summarize.py'),('package-nested-adoption.py','package.py'),('cohesive-nested-adoption-core-il.json','tested-core-il.json'),('cohesive-nested-adoption-tested-binary.json','tested-core-binary.json'),('nested-adoption-readme.md','README.md')]:shutil.copyfile(Path('/private/tmp')/src,d/target)
il=json.loads((d/'tested-core-il.json').read_text());assert not il['differences']
(d/'verification.json').write_text(json.dumps({'core_passed':5188,'core_skipped':60,'relations_passed':1149,'focused_boundary_tests':24,'new_tests':5,'semantic_scenarios':223,'candidate_variants':7,'final_timing_processes':72,'tested_and_both_final_timing_core_binaries_identical':True,'broad_candidate_timing_processes':60,'initial_timing_processes':60,'enumerator_timing_processes':60,'tested_core_methods':il['restored_methods'],'tested_core_il_differences':0,'runtime_differences_per_pair':['Cohesive.dll'],'public_api_changed':False,'production_boundary':'ObservationMaterializer<T>.MaterializeJson on explicit layout-bound plans'},indent=2)+'\n')
files=[]
for root,prefix in [(r,'final'),(joined,'joined'),(Path('/private/tmp/cohesive-nested-adoption-verified-runs'),'broad'),(Path('/private/tmp/cohesive-nested-adoption-broad-source'),'broad-source'),(Path('/private/tmp/cohesive-nested-adoption-gated-source'),'gated-source'),(initial,'initial'),(Path('/private/tmp/cohesive-nested-adoption-final-runs'),'enum'),(Path('/private/tmp/cohesive-nested-adoption-enum-source'),'enum-source'),(Path('/private/tmp/cohesive-nested-adoption-initial-source'),'initial-source')]:
 for p in sorted(root.rglob('*')):
  if p.is_file() and p.name not in ('manifest.json','summary.csv','runtime-environment.json'):files.append((p,prefix+'/'+p.relative_to(root).as_posix()))
for name in jm['source_hashes']:files.append((w/name,'source/'+name))
for p in Path('/private/tmp').glob('cohesive-nested-adoption-*.log'):files.append((p,'verification/'+p.name))
for p in Path('/private/tmp').glob('nested-adoption-*.md'):files.append((p,'templates/'+p.name))
members=[];archive=d/'raw-reports.tar.gz'
with tarfile.open(archive,'w:gz') as t:
 for p,name in files:
  b=p.read_bytes();info=tarfile.TarInfo(name);info.size=len(b);info.mtime=0;info.mode=0o644;t.addfile(info,io.BytesIO(b));members.append({'path':name,'bytes':len(b),'sha256':sha(b)})
with tarfile.open(archive,'r:gz') as t:
 for item in members:assert sha(t.extractfile(item['path']).read())==item['sha256']
(d/'raw-reports-index.json').write_text(json.dumps({'archive':archive.name,'sha256':sha(archive.read_bytes()),'members':members},indent=2)+'\n')
print('Verified 252 timed processes and',len(members),'raw members;',archive.stat().st_size,'archive bytes')
