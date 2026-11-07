"""Render saved automated output without editing predictions or the database."""
import html
import json
import sqlite3
import sys
from pathlib import Path

root = Path(sys.argv[1]).resolve()
con = sqlite3.connect((root / 'benchmark.db').as_uri() + '?mode=ro', uri=True)
docs = {p.replace('\\', '/').split('/data/', 1)[1]: json.loads(v)
        for p, v in con.execute('select path,content from documents')}
fixture = docs['fixture.json']
episodes = {e['id']: e for e in fixture['catalog']['episodes']}
labels = {e['case_id']: e for e in fixture['labels']}
results = sorted(docs.get('results.json', {}).get('cases', []), key=lambda r:r['case_id'])
summary = docs.get('summary.json', {})
groups = docs.get('groups.json', [])
groups_by_path = {row['path'].casefold(): group for group in groups for row in group.get('rows', [])}
observations = docs.get('runtime-observations.json', [])
contended = {o.get('case') for o in observations if any(s.get('busy') for s in o.get('other_scout', []))}
E = lambda v: html.escape(str(v), quote=True)
def title(episode_id):
    e = episodes[episode_id]
    return f"S{e['season']:02d}E{e['number']:02d} · {e['title']}"
def number(value):
    return f'{value:,.1f}'
parts = ['''<!doctype html><html lang="en"><meta charset="utf-8"><meta name="viewport" content="width=device-width,initial-scale=1"><title>Scout · Compact verification</title>
<style>body{font:16px/1.55 Segoe UI,sans-serif;max-width:1250px;margin:40px auto;padding:0 24px;background:#111923;color:#e8eff8}h1,h2{color:#8dc7ff}table{width:100%;border-collapse:collapse;margin:24px 0}td,th{text-align:left;vertical-align:top;padding:12px;border-bottom:1px solid #38506a}th{background:#223348}small,.muted{color:#afbdcf}details{border:1px solid #38506a;padding:14px;margin:12px 0;border-radius:8px}summary{cursor:pointer}code{overflow-wrap:anywhere}.metrics{padding:18px;background:#1c293b;border-radius:8px}a{color:#8dc7ff}pre{white-space:pre-wrap}</style>
<h1>Compact dialogue verification</h1><p>Gemma 4 31B · CPU only · Thinking disabled · Full original dialogue · No manual corrections</p>''']
state = docs.get('status.json', {}).get('state', 'unknown')
parts.append(f'<div class="metrics"><strong>{E(state.title())}</strong> · {len(results)}/13 cases saved')
if summary:
    parts.append(f"<br>{summary['unique_dialogues']} unique dialogues · {summary['reused']} identical-dialogue reuse · {summary['errors']} errors")
    parts.append(f"<br>Total inference: {number(summary['seconds']/60)} minutes · Verification: {number(summary['verification_seconds']/60)} minutes")
    parts.append(f"<br>Verification cache hits: {summary['verification_cache_hits']}/{summary['verification_calls']} · {summary['cached_verification_tokens']:,} input tokens reused")
    parts.append(f"<br>Reference agreement: {summary['reference_agreement']}/{summary['cases']} · Saved Gemma agreement: {summary['saved_gemma_agreement']}/{summary['cases']}")
    parts.append(f"<br>Collection: {summary['accepted_groups']} episode assignments · {summary['held_groups']} unassigned groups (including {summary['conflict_groups']} conflicts)")
    parts.append(f"<br>Automatic validation retries: {summary['validation_retries']}")
parts.append('</div>')
baseline_path=root/'baseline'/'baseline.db'
if baseline_path.exists():
    baseline_con=sqlite3.connect(baseline_path.as_uri()+'?mode=ro',uri=True)
    baseline_docs={Path(p).name:json.loads(v) for p,v in baseline_con.execute('select path,content from documents')}
    baseline_con.close()
    if 'summary.json' in baseline_docs:
        parts.append('<h2>Controlled verification timing</h2><p>Same original dialogue, candidate lists, CPU model, threads and thinking-off settings. The old verifier starts without a reusable prefix; the updated verifier uses its preceding identification cache and compact literal-text prompts. Initial identification time is excluded from these stage timings.</p><table><tr><th>Control case</th><th>Original verifier</th><th>Updated verifier</th><th>Decision comparison</th></tr>')
        for control in baseline_docs['summary.json']['cases']:
            newer=[r for r in control['new_requests'] if r.get('stage')=='verify']
            seconds=sum(r.get('seconds',0) for r in newer)
            a=control.get('result',{});b=control.get('new_result',{})
            decision=lambda r:(r.get('assessment'),r.get('coverage'),[m['episode_id'] for m in r.get('matches',[])])
            comparison='Same decision' if decision(a)==decision(b) else 'Decision differs; outputs preserved'
            if control.get('error'):comparison='Control error: '+control['error']
            parts.append(f'<tr><td>{E(control["case_id"])}</td><td>{number(control["seconds"]/60)} min</td><td>{number(seconds/60)} min</td><td>{E(comparison)}</td></tr>')
        parts.append('</table>')
if contended:
    parts.append(f'<p><strong>Timing limitation:</strong> Another Scout was observed processing concurrently during {E(", ".join(sorted(contended)))}. Those wall times include CPU contention and must not be used as clean speed measurements. The recorded decisions remain unchanged.</p>')
parts.append('<p class="muted">Reference agreement compares inherited labels, not independently confirmed truth. The historical Gemma run used different thinking/GPU settings; its timing is not a controlled comparison. A held non-episode is not counted as an incorrect episode assignment.</p>')
if fixture.get('previous_attempt'):
    parts.append('<p class="muted">This is a development regression suite. An earlier validation failure led to an explicit one-check-per-candidate instruction. Rejected outputs were preserved; no predictions or reference labels were manually corrected. These results do not measure blinded general accuracy.</p>')
parts.append('<table><thead><tr><th>Case / original filename</th><th>New automated result</th><th>Collection decision</th><th>Time</th><th>Verification</th><th>Reference comparison</th></tr></thead><tbody>')
for r in results:
    ident = r['case_id']; result = r.get('result', {}); matches = result.get('matches', [])
    label = labels[ident]; filename = label['path'].replace('\\', '/').rsplit('/', 1)[-1]
    answer = ' + '.join(title(m['episode_id']) for m in matches) or ('Error' if r.get('error') else 'Held · ' + result.get('assessment', 'unknown'))
    group = groups_by_path.get(label['path'].casefold())
    if not group:
        collection = 'Awaiting complete collection'
    elif group.get('status') == 'unresolved_conflict':
        collection = 'Held · competing recordings; suggestion is not assigned'
    elif group.get('episodes'):
        collection = 'Assigned · ' + group.get('code', '')
    elif group.get('feature'):
        collection = 'Separate non-episode feature'
    else:
        collection = 'Held · ' + group.get('status', 'unassigned').replace('_', ' ')
    if group and group.get('duplicate'):
        collection += f" · {group.get('copies', len(group.get('rows', [])))} duplicate copies grouped"
    calls = [v for v in r.get('requests', []) if v.get('stage') == 'verify']
    secs = sum(v.get('seconds', 0) for v in calls)
    cached = sum(v.get('cached_tokens', 0) for v in calls)
    expected = label['expected_ids']; actual = [m['episode_id'] for m in matches]
    if r.get('error'): comparison = 'Unscored error'
    elif r.get('agrees_with_reference'): comparison = 'Agrees'
    elif not actual and not expected: comparison = 'No episode assigned; content classification differs'
    elif not actual: comparison = 'Held reference episode'
    else: comparison = 'Different episode assignment'
    duration = 'Reused identical dialogue' if r.get('reused_identical_dialogue') else number(r['seconds']/60)+' min'
    if ident in contended: duration += ' (CPU contention observed)'
    verification = 'Reused' if r.get('reused_identical_dialogue') else f'{number(secs/60)} min · {cached:,} cached tokens'
    parts.append(f'<tr><td>{E(ident)}<br><small>{E(filename)}</small></td><td>{E(answer)}</td><td>{E(collection)}</td><td>{E(duration)}</td><td>{E(verification)}</td><td>{E(comparison)}</td></tr>')
parts.append('</tbody></table><h2>Automated evidence review</h2>')
for r in results:
    result=r.get('result', {});ident=r['case_id']
    parts.append(f'<details><summary>{E(ident)} · {E(result.get("assessment", "error"))}</summary><p>{E(r.get("error") or result.get("summary", ""))}</p>')
    for c in result.get('candidate_checks', []):
        parts.append(f'<p><strong>{E(c.get("episode_title", ""))}</strong> · {E(c.get("verdict", ""))}<br>{E(c.get("event_comparison", ""))}<br><small>Supporting blocks: {E(c.get("supporting_blocks", []))} · Contradicting blocks: {E(c.get("contradicting_blocks", []))}</small></p>')
    for request in r.get('requests', []):
        timings=request.get('timings') or {}
        parts.append(f'<p class="muted">{E(request["stage"])}: {number(request.get("seconds", 0)/60)} min; {request.get("prompt_tokens",0):,} input tokens, {request.get("cached_tokens",0):,} cached, {request.get("output_tokens",0):,} output. Prefill {number(timings.get("prompt_ms",0)/60000)} min; generation {number(timings.get("predicted_ms",0)/60000)} min.</p>')
    parts.append('</details>')
parts.append('<p class="muted">Only isolated benchmark artifacts were written. Application Scout results and video files were not opened or changed.</p></html>')
(root / 'results.html').write_text('\n'.join(parts), encoding='utf-8')
print(f'Rendered {len(results)} saved cases: {root / "results.html"}')
