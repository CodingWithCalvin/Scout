from pathlib import Path
import sqlite3,json,html,sys

root=Path(sys.argv[1]).resolve()
with sqlite3.connect((root/'test.db').as_uri()+'?mode=ro',uri=True) as db:
    docs={Path(name).name:json.loads(raw) for name,raw in db.execute('select path,content from documents')}
    summary=docs['summary.json'];manifest=docs['manifest.json']
    rows=[json.loads(raw) for (raw,) in db.execute('select payload from file_results')]
e=lambda value:html.escape(str(value))
minutes=lambda seconds:f'{seconds/60:.1f} min'
success=summary.get('success',False)
episodes=summary.get('episodes',[])
title=' + '.join(f"S{x['season']:02}E{x['number']:02} · {x['title']}" for x in episodes) or 'Held for review'
requests=summary.get('requests',[])
completed=[r for r in requests if r.get('state')=='complete' and r.get('http_status')==200]
longest=max((r.get('seconds',0) for r in completed),default=0)
crossed=longest>manifest['previous_timeout_seconds']
proof=(f'A completed request took {minutes(longest)}, exceeding the old 10-minute cutoff.' if crossed
       else 'The test completed inside the old cutoff; this run alone does not demonstrate crossing ten minutes.') if success else 'This test did not produce a completed verified result. See the error below.'
parts=['''<!doctype html><html lang="en"><meta charset="utf-8"><meta name="viewport" content="width=device-width,initial-scale=1"><title>Scout · CPU timeout check</title><style>
body{font:16px/1.6 "Segoe UI",sans-serif;background:#111923;color:#eff4fc;margin:0}main{max-width:1050px;margin:auto;padding:36px 24px}h1{font-size:28px;margin:8px 0}h2{font-size:19px;margin:28px 0 10px}.muted{color:#a9b8ca}.status{color:#8bc3ff;text-transform:uppercase;letter-spacing:.08em;font-size:13px}section,.metrics{background:#182536;border:1px solid #34485f;border-radius:6px;padding:18px 22px;margin:18px 0}.metrics{display:flex;flex-wrap:wrap;gap:30px}.metric strong{display:block;font-size:24px;color:#99cbff}.path{overflow-wrap:anywhere}table{width:100%;border-collapse:collapse}th,td{text-align:left;padding:10px;border-bottom:1px solid #34485f}pre{white-space:pre-wrap;overflow-wrap:anywhere;font:inherit}blockquote{border-left:3px solid #78b8ff;margin:12px 0;padding:8px 18px;color:#c7d5e7}.error{color:#ffb7a8}</style><main>''']
parts.append(f'<div class="status">{"Completed" if success else "Needs attention"} · CPU-only · Thinking disabled</div><h1>{e(title)}</h1><p class="muted path">{e(summary["source"])}</p>')
parts.append('<div class="metrics">'+''.join(f'<div class="metric"><strong>{e(value)}</strong>{e(label)}</div>' for value,label in [(minutes(summary['seconds']),'Total test time'),(minutes(longest),'Longest completed request'),(str(len(requests)),'Model requests'),('30 min','Limit per request')])+'</div>')
parts.append(f'<section><h2 style="margin-top:0">Timeout check</h2><p>{e(proof)}</p><p>{"Independent dialogue verification completed." if summary.get("verification_completed") else "Independent dialogue verification did not complete."} {"Source size, modification time and sampled digest are unchanged." if summary.get("media_unchanged") else "Source unchanged check did not pass."}</p>')
if summary.get('error'):parts.append(f'<p class="error">{e(summary["error"])}</p>')
parts.append('<p class="muted">The test ran automatically using the published Scout matcher and saved TVDB guide. No manual corrections, media edits, or writes to application Scouts. A supported model suggestion is not independently confirmed ground truth.</p></section>')
parts.append('<h2>Request timings</h2><table><tr><th>Request</th><th>Time</th><th>Result</th></tr>')
for r in requests:parts.append(f'<tr><td>{e(r["index"])}</td><td>{e(minutes(r.get("seconds",0)))}</td><td>{e(r.get("state",""))} {e(r.get("http_status",""))}</td></tr>')
parts.append('</table>')
for row in rows:
    reasoning=row.get('reasoning',{})
    if reasoning:
        parts.append(f'<h2>Dialogue assessment</h2><section><p>{e(reasoning.get("summary",""))}</p>')
        for m in reasoning.get('matches',[]):
            parts.append(f'<h3>{e(m.get("episode_title",""))}</h3><p>{e(m.get("reason",""))}</p>')
            for b in m.get('evidence',[])[:4]:
                seconds=b.get('start',0);stamp=f'{int(seconds)//60:02}:{int(seconds)%60:02}'
                parts.append(f'<blockquote><strong>{stamp}</strong><br>{e(b.get("source_text",""))}</blockquote>')
        parts.append('</section>')
parts.append(f'<p class="muted">Runtime {e(manifest["runtime"])} · {manifest["cpu_threads"]} CPU threads · {manifest["gpu_layers"]} GPU layers · {manifest["context"]:,} context tokens · {manifest["output_tokens"]:,} output limit</p></main></html>')
(root/'results.html').write_text(''.join(parts),encoding='utf-8')
print(root/'results.html')
