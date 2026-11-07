"""Automated isolated fast-workflow experiment. Uses fixed routing, never reference labels for decisions."""
import collections,html,json,os,pathlib,re,sqlite3,subprocess,sys,time
HERE=pathlib.Path(__file__).resolve().parent;REPO=HERE.parents[1]
ROOT=REPO/'.build/fast-victorious';MODELS=pathlib.Path('C:/Users/calvi/Documents/Codex/2026-10-26/i/work/comparison-models');DOTNET='C:/Users/calvi/Documents/Codex/2026-10-26/i/work/dotnet11/dotnet.exe'
def read(db,col='name'):
 c=sqlite3.connect(db.as_uri()+'?mode=ro',uri=True);d={k:json.loads(v) for k,v in c.execute('select '+col+',content from documents')};c.close();return d
fixture=read(ROOT/'fixture.db','path');jobs=fixture['data\\prepared-jobs.json'];catalog=fixture['data\\catalog.json']['episodes'];preparation=fixture['data\\preparation.json']
db=sqlite3.connect(ROOT/'pipeline.db');db.execute('create table if not exists documents(name text primary key,content text not null)')
def save(k,v):db.execute('insert or replace into documents values (?,?)',(k,json.dumps(v)));db.commit()
def status(stage,message):save('status',{'stage':stage,'message':message,'utc':time.strftime('%Y-%m-%dT%H:%M:%SZ',time.gmtime())});print(message,flush=True)

def route(j,record):
 if record.get('error'):return None,'Kev error'
 answers=record['raw_response']['answers'];rank=sorted(((v['probabilities']['supported'],int(k),v['choice']) for k,v in answers.items()),reverse=True)
 if not rank:return None,'No candidates'
 p,eid,verdict=rank[0];second=rank[1][0] if len(rank)>1 else 0
 if verdict!='supported' or p<.61:return None,'Low support'
 if p-second<.15:return None,'Small candidate margin'
 if sum(x[0]>=.61 and x[2]=='supported' for x in rank)>1:return None,'Multiple supported episodes'
 if j['multipart_hint']:return None,'Multipart filename'
 if j['filename_ids'] and set(j['filename_ids'])!={eid}:return None,'Filename disagreement'
 e=next(e for e in j['candidates'] if e['id']==eid)
 if e['season']==0 or re.search(r'(?i)podcast|behind.the.scenes|making.of|interview|bloopers|deleted.scene',e['title']):return None,'Possible non-episode feature'
 return eid,'Kev supported with margin'

def main():
 save('preparation',preparation);start=time.monotonic()
 pending=[j for j in jobs if j['gemma_cache'] is None]
 kfixture=ROOT/'kev-fixture.db';c=sqlite3.connect(kfixture);c.execute('create table if not exists documents(path text primary key,content text not null)')
 for k,v in [('prepared-jobs.json',pending),('preparation.json',preparation)]:c.execute('insert or replace into documents values (?,?)',('data\\'+k,json.dumps(v)))
 c.commit();c.close()
 status('kev',f'Checking {len(pending)} uncached dialogues with Kev; {len(jobs)-len(pending)} Gemma results reused.')
 subprocess.run([sys.executable,str(HERE.parent/'Scout.JudgeBenchmark/decision.py'),str(kfixture),str(ROOT/'kev'),str(MODELS),'0.61'],check=True)
 kev=read(ROOT/'kev/decision.db');routes={};fallback=[]
 for j in jobs:
  cid=j['case_ids'][0]
  if j['gemma_cache'] is not None:routes[cid]={'route':'saved_gemma','result':j['gemma_cache']['result'],'seconds':0};continue
  record=kev['result/'+j['dialogue_hash']];eid,why=route(j,record)
  routes[cid]={'route':'kev' if eid else 'gemma','reason':why,'episode_ids':[eid] if eid else [],'seconds':record['seconds']}
 # Distinct dialogue recordings competing for the same episode require content review.
 claimed=collections.defaultdict(list)
 for cid,r in routes.items():
  for eid in r.get('episode_ids',[m['episode_id'] for m in r.get('result',{}).get('matches',[])]):claimed[eid].append(cid)
 for ids in claimed.values():
  if len(ids)>1:
   for cid in ids:
    if routes[cid]['route']=='kev':routes[cid].update(route='gemma',reason='Competing recordings',episode_ids=[])
 for j in jobs:
  cid=j['case_ids'][0]
  if routes[cid]['route']=='gemma':fallback.append({'id':cid,'cues':j['cues']})
 save('routes',routes);save('routing_summary',{'total':len(jobs),'saved_gemma':sum(r['route']=='saved_gemma' for r in routes.values()),'kev_accepted':sum(r['route']=='kev' for r in routes.values()),'fresh_gemma_required':len(fallback),'reasons':dict(collections.Counter(r.get('reason','saved') for r in routes.values()))})
 if fallback:
  request=ROOT/'fallback-input.json';request.write_text(json.dumps({'jobs':fallback,'catalog':catalog}),encoding='utf-8')
  status('gemma',f'{len(fallback)} dialogues require Gemma; other results are checkpointed.')
  env=os.environ.copy();env['DOTNET_CLI_TELEMETRY_OPTOUT']='1'
  subprocess.run([DOTNET,str(HERE/'bin/Release/net11.0-windows/Scout.FastBenchmark.dll'),str(request),str(ROOT/'fallback'),str(MODELS)],env=env,check=True)
  gdata=read(ROOT/'fallback/fallback.db','path')
  for j in fallback:
   rec=next(v for k,v in gdata.items() if k.endswith('\\'+j['id']+'.json'))
   routes[j['id']].update(rec)
 finalclaims=collections.defaultdict(list)
 for cid,r in routes.items():
  result=r.get('result');r['accepted_ids']=r.get('episode_ids',[]) if r['route']=='kev' else [m['episode_id'] for m in (result or {}).get('matches',[])] if result and result.get('assessment')=='supported' and result.get('coverage')=='complete' else []
  for eid in r['accepted_ids']:finalclaims[eid].append(cid)
 for ids in finalclaims.values():
  if len(ids)>1:
   for cid in ids:routes[cid]['conflict']=True
 counts=collections.Counter();table=[]
 for j in jobs:
  r=routes[j['case_ids'][0]];accepted=[] if r.get('conflict') else r['accepted_ids'];counts[r['route']]+=1
  counts['accepted' if accepted else 'held']+=1
  if r.get('error'):counts['errors']+=1
  if r.get('conflict'):counts['conflicts']+=1
  ref=j['reference_ids']
  if ref is not None:
   counts['scored']+=1
   # Empty reference denotes a feature, not successful identification of an episode.
   if accepted:counts['accepted_reference_agreement' if sorted(accepted)==sorted(ref) else 'accepted_reference_disagreement']+=1
   else:counts['reference_held']+=1
  else:counts['unscored']+=1
  names=', '.join(next(e['title'] for e in catalog if e['id']==i) for i in accepted)
  table.append([j['case_ids'][0],r['route'],names or 'Held',r.get('reason',''), 'Unscored' if ref is None else 'Agrees' if accepted and sorted(accepted)==sorted(ref) else 'Disagrees' if accepted else 'Held'])
 measured_kev=sum(v['seconds'] for k,v in kev.items() if k.startswith('result/'))
 measured_gemma=sum(r.get('seconds',0) for r in routes.values() if r['route']=='gemma')
 summary={'counts':dict(counts),'kev_seconds':measured_kev,'fresh_gemma_seconds':measured_gemma,'this_invocation_seconds':time.monotonic()-start,'rules':preparation['rules'],'caveat':preparation['caveat'],'manual_corrections':False}
 save('results',routes);save('summary',summary)
 page='<meta charset="utf-8"><title>Scout fast workflow benchmark</title><h1>Victorious fast workflow benchmark</h1><pre>'+html.escape(json.dumps(summary,indent=2))+'</pre><table border="1"><tr><th>Recording</th><th>Route</th><th>Assignment</th><th>Routing reason</th><th>Saved reference comparison</th></tr>'
 for row in table:page+='<tr>'+''.join('<td>'+html.escape(x)+'</td>' for x in row)+'</tr>'
 (ROOT/'results.html').write_text(page+'</table>',encoding='utf-8');status('complete','Fast-workflow benchmark complete; Scout data unchanged.')
try:main()
except Exception as ex:status('failed',str(ex));raise
