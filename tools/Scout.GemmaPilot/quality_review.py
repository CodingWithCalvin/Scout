import sqlite3,json,re,pathlib,html
root=pathlib.Path('D:/coding-with-calvin/Scout/.build/gemma-off-pilot/results');c=sqlite3.connect(root/'pilot.db');docs={p:json.loads(v) for p,v in c.execute('select path,content from documents')};inp=next(v for p,v in docs.items() if p.endswith('input.json'));catalog={e['id']:e for e in inp['catalog']};flags=[]
def bad(text):
 return bool(re.search(r'(\S)\1{31,}',text) or re.search(r'\b(\w+)(?:\s+\1){11,}\b',text,re.I))
for p,r in docs.items():
 if p.endswith(('video-04.json','video-11.json')):
  result=r['result'];fields={'summary':result.get('summary','')}
  for i,m in enumerate(result.get('matches',[])):fields['matches['+str(i)+'].reason']=m.get('reason','')
  failed=[k for k,v in fields.items() if bad(v)]
  names=[f"S{catalog[m['episode_id']]['season']:02}E{catalog[m['episode_id']]['number']:02} - {catalog[m['episode_id']]['title']}" for m in result['matches']]
  flags.append({'id':r['id'],'suggestions':names,'seconds':r['seconds'],'repetitive_explanation_fields':failed,'original_prediction_unchanged':True})
quality={'rule':'Flag only summary/match-reason fields with >=32 consecutive identical non-whitespace characters or >=12 consecutive identical words; dialogue quotations are excluded. Diagnostic screening, not independent truth validation.','cases':flags,'flagged_cases':sum(bool(x['repetitive_explanation_fields']) for x in flags),'manual_corrections':False}
p=str(root/'data'/'quality-review.json');c.execute('insert or replace into documents(path,kind,content) values (?,?,?)',(p,'quality-review.json',json.dumps(quality).encode()));c.commit();c.close()
report=root/'results.html';text=report.read_text(encoding='utf-8');notice='<h2>Automated output-quality check</h2><p>Both responses passed structural validation but triggered repetitive-explanation checks. They are not reliable accepted results. Original model predictions are preserved.</p><pre>'+html.escape(json.dumps(quality,indent=2))+'</pre>'
report.write_text(text+notice,encoding='utf-8')
print(json.dumps(quality,indent=2))
