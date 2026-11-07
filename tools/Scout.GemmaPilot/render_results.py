"""Render only the new automated pilot results, without modifying predictions."""
import html,json,pathlib,sqlite3,sys
root=pathlib.Path(sys.argv[1]).resolve()
con=sqlite3.connect((root/'pilot.db').as_uri()+'?mode=ro',uri=True)
docs={p:json.loads(v) for p,v in con.execute('select path,content from documents')};con.close()
def doc(name):return next(v for p,v in docs.items() if p.endswith('\\'+name) or p.endswith('/'+name))
status=doc('status.json')
if status['state']!='complete':raise SystemExit('Pilot is not complete; report was not replaced.')
records=doc('results.json');inp=doc('input.json');catalog={e['id']:e for e in inp['catalog']}
E=lambda x:html.escape(str(x))
def episode(mid):
 e=catalog[mid];return f"S{e['season']:02}E{e['number']:02} - {e['title']}"
def stamp(seconds):
 seconds=int(seconds);return f'{seconds//3600:02}:{seconds//60%60:02}:{seconds%60:02}'
parts=['''<!doctype html><html lang="en"><meta charset="utf-8"><meta name="viewport" content="width=device-width,initial-scale=1"><title>Scout - Gemma CPU results</title><style>
:root{color-scheme:dark}*{box-sizing:border-box}body{margin:0;background:#101723;color:#e6edf7;font:16px/1.55 Segoe UI,Arial,sans-serif}main{max-width:1160px;margin:40px auto;padding:0 24px}h1{font-size:30px;margin:0 0 8px}h2{font-size:22px}h3{font-size:17px;color:#9dccff}.muted{color:#aebed3}.card{border:1px solid #354459;background:#182231;border-radius:10px;padding:22px;margin:24px 0}table{border-collapse:collapse;width:100%}th,td{text-align:left;padding:12px;border-bottom:1px solid #354459;vertical-align:top}.scroll{overflow-x:auto}blockquote{border-left:3px solid #66b3ff;padding:8px 16px;margin:8px 0;color:#cad7e7}summary{cursor:pointer;color:#9dccff}code{color:#bddcff}.pill{display:inline-block;background:#234362;padding:4px 12px;border-radius:20px;color:#c4e4ff}p{margin:8px 0}details{margin:14px 0}</style><main><h1>Gemma CPU results</h1><p class="muted">Same two saved recordings. Fully automated identification and independent verification. No manual corrections.</p><p>Gemma 4 31B Q4_K_M · CPU · Thinking off · 8 threads</p>''']
parts.append('<div class="card scroll"><table><thead><tr><th>Recording</th><th>Automated suggestion</th><th>Validation</th><th>Matching time</th><th>Retries</th></tr></thead><tbody>')
for r in records:
 v=r.get('result');ids=[m['episode_id'] for m in v.get('matches',[])] if v else []
 suggestion=' + '.join(episode(i) for i in ids) or (v.get('assessment') if v else 'No assignment')
 retries=len(v.get('validation_retries',[]))+len(v.get('verification',{}).get('initial_result',{}).get('validation_retries',[])) if v else 0
 validation='Passed both stages' if v else 'Held: '+r.get('error','Unknown error')
 parts.append(f'<tr><td>{E(r["id"])}</td><td>{E(suggestion)}</td><td>{E(validation)}</td><td>{r["seconds"]/60:.1f} min</td><td>{retries}</td></tr>')
parts.append('</tbody></table></div><p class="muted">These recordings have no consistent reference labels. Passing validation does not independently establish that a match is correct. Times cover model calls, including verification; they exclude model loading and the interruption between recordings.</p>')
for r in records:
 v=r.get('result');parts.append(f'<section class="card"><h2>{E(r["id"])}</h2>')
 if not v:parts.append('<p>'+E(r.get('error','No accepted output'))+'</p></section>');continue
 parts.append(f'<span class="pill">{E(v["assessment"])} · {E(v["coverage"])}</span><h3>Model summary</h3><p>{E(v["summary"])}</p>')
 for m in v['matches']:
  parts.append(f'<h3>{E(episode(m["episode_id"]))}</h3><p>{E(m["reason"])}</p><details><summary>Supporting original dialogue</summary>')
  for block in m.get('evidence',[]):parts.append(f'<blockquote><strong>{stamp(block["start"])}</strong><br>{E(block["source_text"])}</blockquote>')
  parts.append('</details>')
 parts.append('<details><summary>Candidate comparisons</summary>')
 for check in v['candidate_checks']:
  parts.append(f'<h3>{E(check["episode_title"])}</h3><p>{E(check["event_comparison"])}</p>')
  if check.get('guide_event'):parts.append(f'<p class="muted">Guide: {E(check["guide_event"])}</p>')
 parts.append('</details></section>')
progress=[v for p,v in docs.items() if p.endswith('-progress.json')]
parts.append(f'<p class="muted">{len(records)} recordings · {sum(r.get("usage",{}).get("requests",0) for r in records)} model requests · {sum(v.get("reasoning_characters",0) for v in progress)} thinking characters · No Scout data or media changed.</p></main></html>')
(root/'results.html').write_text('\n'.join(parts),encoding='utf-8')
print(json.dumps({'report':str(root/'results.html'),'cases':len(records),'errors':sum('error' in r for r in records)}))
