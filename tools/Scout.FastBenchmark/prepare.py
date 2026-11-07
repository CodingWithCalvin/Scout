import collections,json,math,pathlib,re,sqlite3,sys
sys.path.insert(0,str(pathlib.Path(__file__).resolve().parents[1]/'Scout.JudgeBenchmark'))
from judge import digest,words
root=pathlib.Path('D:/coding-with-calvin/Scout/.build/fast-victorious');root.mkdir(parents=True,exist_ok=True)
src=pathlib.Path('C:/Users/calvi/AppData/Local/Scout/scout.db')
if pathlib.Path(str(src)+'-wal').exists():raise RuntimeError('Source has active WAL; need consistent backup')
stamp=(src.stat().st_size,src.stat().st_mtime_ns)
c=sqlite3.connect(src.as_uri()+'?mode=ro&immutable=1',uri=True)
docs={p:json.loads(v) for p,v in c.execute("select path,content from documents where kind in ('report','catalog.json')")}
groups={};catalog=None;scanned=0
for doc,rtext in c.execute('select document,payload from file_results order by document,ordinal'):
 if 'analysis-history' in doc:continue
 meta=docs[doc]
 if meta.get('series_identity',{}).get('id')!='152721' and '9ac0edf099474fd8a21a96d7d9571d90' not in doc:continue
 r=json.loads(rtext);cues=r.get('dialogue',{}).get('cues',[]);scanned+=1
 if not cues:continue
 catalog=docs[doc.rsplit('\\',1)[0]+'\\catalog.json']['episodes']
 normalized=' '.join(re.findall(r'\w+',' '.join(q['text'] for q in cues).casefold()));key=digest(normalized)
 g=groups.setdefault(key,{'cues':cues,'sources':[],'labels':set(),'normalized':normalized})
 # Original filename avoids leaking names produced by prior move operations.
 path=r.get('original_path') or r.get('path_history',[{}])[0].get('source') or r['path']
 g['sources'].append({'path':path,'current_path':r['path'],'report':doc})
 reasoning=r.get('reasoning',{})
 if reasoning.get('assessment')=='supported':g['labels'].add(tuple(sorted(m['episode_id'] for m in reasoning['matches'])))
c.close()
if stamp!=(src.stat().st_size,src.stat().st_mtime_ns):raise RuntimeError('Source changed during read')
old=sqlite3.connect('file:D:/coding-with-calvin/Scout/.build/model-comparison/benchmark.db?mode=ro',uri=True)
prev={p.split('data\\')[-1].replace('\\','/'):json.loads(v) for p,v in old.execute('select path,content from documents')};old.close()
reused={}
for inp in prev['inputs.json']:
 norm=' '.join(re.findall(r'\w+',' '.join(q['text'] for q in inp['cues']).casefold()))
 rec=prev.get('gemma31-thinking/'+inp['case_id']+'.json')
 if rec and rec.get('result'):reused[digest(norm)]={'cues':inp['cues'],'record':rec}
 for label in prev['evaluation-labels.json']:
  if label['case_id']==inp['case_id'] and digest(norm) in groups:groups[digest(norm)]['labels'].add(tuple(sorted(label['expected_ids'])))
tokens={e['id']:collections.Counter(words(e['title']+' '+e['overview'])) for e in catalog};df=collections.Counter(w for t in tokens.values() for w in t)
idf={w:math.log((len(catalog)+1)/(n+1))+1 for w,n in df.items()}
def vector(text):
 counts=collections.Counter(re.findall(r'[a-z]{3,}',text.lower()));return {w:(1+math.log(n))*idf[w] for w,n in counts.items() if w in idf}
def cosine(a,b):return sum(v*b.get(w,0) for w,v in a.items())/max(1e-9,math.sqrt(sum(v*v for v in a.values())*sum(v*v for v in b.values())))
vectors={e['id']:vector(e['title']+' '+e['overview']) for e in catalog};jobs=[]
for key,g in sorted(groups.items()):
 query=vector(' '.join(q['text'] for q in g['cues']));rank=sorted(catalog,key=lambda e:(-cosine(query,vectors[e['id']]),e['id']))[:5]
 hintids=set();multipart=False
 for source in g['sources']:
  name=source['path'].split('\\')[-1]
  multipart|=bool(re.search(r'(?i)S\d+E\d+[- ._]*E\d+|\bpart[ ._-]*\d',name))
  for m in re.finditer(r'(?i)S(\d+)E(\d+)',name):hintids.update(e['id'] for e in catalog if e['season']==int(m[1]) and e['number']==int(m[2]))
  normalized=' '.join(re.findall(r'\w+',name.lower()))
  hintids.update(e['id'] for e in catalog if len(e['title'])>=5 and ' '.join(re.findall(r'\w+',e['title'].lower())) in normalized)
 ids={e['id'] for e in rank}|hintids
 candidates=[e for e in catalog if e['id'] in ids];candidates.sort(key=lambda e:digest([key,e['id']]))
 ref=list(next(iter(g['labels']))) if len(g['labels'])==1 else None
 cached=reused.get(key)
 # Reuse Gemma only for byte-equivalent cue data, not fuzzy dialogue matches.
 gemma=cached['record'] if cached and cached['cues']==g['cues'] else None
 jobs.append({'case_ids':['video-%02d'%(len(jobs)+1)],'dialogue_hash':digest(g['cues']),'cues':g['cues'],'candidates':candidates,'proposed_ids':[],'expected_ids':ref or [],'reference_ids':ref,'sources':g['sources'],'filename_ids':sorted(hintids),'multipart_hint':multipart,'gemma_cache':gemma})
summary={'saved_file_rows':scanned,'unique_dialogues':len(jobs),'current_paths':len({s['current_path'] for j in jobs for s in j['sources']}),'reference_available':sum(j['reference_ids'] is not None for j in jobs),'reusable_gemma':sum(j['gemma_cache'] is not None for j in jobs),'shortlist_contains_all_reference_ids':sum(j['reference_ids'] is not None and set(j['reference_ids'])<={e['id'] for e in j['candidates']} for j in jobs),'rules':{'threshold':.61,'minimum_margin':.15,'multiple_supported':'fallback','multipart_filename':'fallback','filename_disagreement':'fallback','competing_recordings':'fallback','non_episode_candidates':'fallback'},'caveat':'Prototype benchmark. References are inherited/unverified, missing or conflicting references are unscored. Shortlisting never reads reference labels or saved model suggestions.'}
db=sqlite3.connect(root/'fixture.db');db.execute('create table if not exists documents(path text primary key,content text not null)')
for name,value in [('prepared-jobs.json',jobs),('catalog.json',{'episodes':catalog}),('preparation.json',summary)]:db.execute('insert or replace into documents values (?,?)',('data\\'+name,json.dumps(value)))
db.commit();print(json.dumps(summary,indent=2))
