"""Prepare unseen saved-result fixtures without modifying application storage."""
import collections,hashlib,json,math,pathlib,re,sqlite3
from judge import digest,words
root=pathlib.Path('D:/coding-with-calvin/Scout/.build/decision-expanded');root.mkdir(parents=True,exist_ok=True)
source=pathlib.Path('C:/Users/calvi/AppData/Local/Scout/scout.db')
# immutable is safe only for this quiescent, checkpointed source (no WAL).
if pathlib.Path(str(source)+'-wal').exists():raise RuntimeError('Source WAL exists; use a consistent SQLite backup before preparing fixtures')
before=(source.stat().st_size,source.stat().st_mtime_ns)
c=sqlite3.connect(source.as_uri()+'?mode=ro&immutable=1',uri=True)
old=sqlite3.connect('file:D:/coding-with-calvin/Scout/.build/decision-comparison/decision.db?mode=ro',uri=True)
oldjobs=json.loads(old.execute("select content from documents where name='jobs'").fetchone()[0]);old.close()
def normalized(cues):return ' '.join(re.findall(r'\w+',' '.join(x['text'] for x in cues).casefold()))
def grams(text):
 w=text.split();return set(zip(w,w[1:],w[2:],w[3:],w[4:]))
oldtext=[normalized(j['cues']) for j in oldjobs];oldgrams=[grams(t) for t in oldtext]
all_docs={p:json.loads(v) for p,v in c.execute("select path,content from documents where kind in ('catalog.json','report')")}
groups={};excluded=collections.Counter()
for doc,payload in c.execute('select document,payload from file_results order by document,ordinal'):
 if 'analysis-history' in doc:continue
 r=json.loads(payload);reason=r.get('reasoning',{});cues=r.get('dialogue',{}).get('cues',[])
 if not cues or reason.get('assessment')!='supported' or r.get('error'):excluded['no_successful_supported_suggestion']+=1;continue
 ids=sorted(m['episode_id'] for m in reason.get('matches',[]))
 if not ids:continue
 parent=doc.rsplit('\\',1)[0];cat=all_docs.get(parent+'\\catalog.json',{});catalog=cat.get('episodes',[]) if isinstance(cat,dict) else cat
 if not catalog or any(i not in {e['id'] for e in catalog} for i in ids):excluded['missing_catalog']+=1;continue
 text=normalized(cues);gg=grams(text)
 if any(text==t or len(gg&g)/max(1,len(gg|g))>=.9 for t,g in zip(oldtext,oldgrams)):excluded['original_test_overlap']+=1;continue
 identity=all_docs[doc].get('series_identity',{})
 series=identity.get('name') or ('Victorious' if 1961541 in {e['id'] for e in catalog} else 'Unknown')
 key=digest([series,text]);entry=groups.setdefault(key,{'cues':cues,'catalog':catalog,'series':series,'labels':set(),'sources':[]})
 entry['labels'].add(tuple(ids));entry['sources'].append({'report':doc,'path':r['path'],'model':reason.get('model'),'status':r.get('status')})
c.close()
if before!=(source.stat().st_size,source.stat().st_mtime_ns):raise RuntimeError('Source changed while reading; retry preparation')
jobs=[]
for key,g in sorted(groups.items()):
 if len(g['labels'])!=1:excluded['conflicting_saved_labels']+=1;continue
 ids=list(next(iter(g['labels'])));catalog=g['catalog'];tok={e['id']:words(e['title']+' '+e['overview']) for e in catalog};freq=collections.Counter(w for t in tok.values() for w in t);anchor=set().union(*(tok[i] for i in ids))
 def score(e):return sum(math.log(1+len(catalog)/freq[w]) for w in anchor&tok[e['id']])
 decoys=sorted((e for e in catalog if e['id'] not in ids),key=lambda e:(-score(e),e['id']))[:2]
 candidates=[e for e in catalog if e['id'] in set(ids)|{x['id'] for x in decoys}];candidates.sort(key=lambda e:digest([key,e['id']]))
 jobs.append({'case_ids':['new-%02d'%(len(jobs)+1)],'series':g['series'],'dialogue_hash':digest(g['cues']),'cues':g['cues'],'candidates':candidates,'proposed_ids':ids,'expected_ids':ids,'reference_kind':'unverified_saved_model_suggestion','sources':g['sources']})
assert jobs and len({j['dialogue_hash'] for j in jobs})==len(jobs)
summary={'recordings':len(jobs),'series':dict(collections.Counter(j['series'] for j in jobs)),'saved_positive_candidates':sum(len(j['expected_ids']) for j in jobs),'negative_controls':2*len(jobs),'excluded':dict(excluded),'threshold':.61,'source_unchanged':True,'caveat':'Agreement with unverified saved suggestions, not independently measured accuracy. Original benchmark excluded by normalized dialogue and >=0.9 five-word-shingle Jaccard overlap; conflicting saved labels excluded.'}
fixture=root/'fixture.db';db=sqlite3.connect(fixture);db.execute('create table if not exists documents(path text primary key,content text not null)')
for name,value in [('prepared-jobs.json',jobs),('preparation.json',summary)]:db.execute('insert or replace into documents values (?,?)',('data\\'+name,json.dumps(value)))
db.commit();db.close();print(json.dumps(summary,indent=2))
