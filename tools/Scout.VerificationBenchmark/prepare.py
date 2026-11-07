"""Freeze saved difficult-case evidence and references; never edit source databases."""
import hashlib
import json
import sqlite3
import sys
from pathlib import Path

if len(sys.argv) not in (4,5):
    raise SystemExit('prepare.py <original model-comparison benchmark.db> <non-secret settings input.json> <new output directory> [previous isolated benchmark.db]')
database=Path(sys.argv[1]).resolve();settings_file=Path(sys.argv[2]).resolve();root=Path(sys.argv[3]).resolve()
root.mkdir(parents=True,exist_ok=True)
if (root/'fixture.json').exists() or (root/'benchmark.db').exists():
    raise SystemExit('Refusing to overwrite an existing frozen benchmark.')
con=sqlite3.connect(database.as_uri()+'?mode=ro',uri=True)
documents={}
for path,content in con.execute('select path,content from documents'):
    key=path.replace('\\','/').split('/data/',1)[1]
    value=json.loads(content)
    if key.endswith('report.json'):
        rows=[json.loads(r[0]) for r in con.execute('select payload from file_results where document=? order by ordinal',(path,))]
        if rows:value['files']=rows
    documents[key]=value
settings=json.loads(settings_file.read_text(encoding='utf-8'))['settings']
if settings.get('provider')!='managed' or settings.get('gpu_layers')!=0 or settings.get('thinking_enabled') is not False:
    raise SystemExit('Fixture requires managed CPU-only inference with thinking explicitly disabled.')
if any('key' in key.lower() or 'secret' in key.lower() for key in settings):
    raise SystemExit('Use a settings input without credentials.')
fixture={
    'inputs':documents['inputs.json'],
    'catalog':documents['catalog.json'],
    'labels':documents['evaluation-labels.json'],
    'saved_results':documents['gemma31-thinking/results.json'],
    'saved_summary':documents['gemma31-thinking/summary.json'],
    'saved_report':documents['gemma31-thinking/report.json'],
    'settings':settings,
    'source_sha256':hashlib.sha256(database.read_bytes()).hexdigest(),
    'limitations':'Labels are inherited, not independently confirmed. Saved Gemma comparison used thinking and 8 GPU layers, so its timing is historical, not a controlled speed measurement.'
}
assert len(fixture['inputs'])==len(fixture['labels'])==len(fixture['saved_report']['files'])
labels={r['case_id']:r for r in fixture['labels']}
assert all(labels[r['case_id']]['dialogue_sha256']==r['dialogue_sha256'] for r in fixture['inputs'])
if len(sys.argv)==5:
    previous=Path(sys.argv[4]).resolve()
    prior=sqlite3.connect(previous.as_uri()+'?mode=ro',uri=True)
    failures=[]
    for path,content in prior.execute('select path,content from documents'):
        if '/cases/' in path.replace('\\','/'):
            case=json.loads(content)
            if case.get('error'):failures.append(case['case_id'])
    # Exercise malformed-output cases and the previously selected Playdate control
    # before spending CPU on the remaining regression suite. Labels never gate progress.
    priority=list(dict.fromkeys(sorted(failures)+['case-07']))
    fixture['preflight_case_ids']=priority
    fixture['previous_attempt']=str(previous)
    fixture['inputs'].sort(key=lambda c:(priority.index(c['case_id']) if c['case_id'] in priority else len(priority),c['case_id']))
    fixture['limitations']+=' This is a development regression suite: malformed-output failures informed the generic prompt revision; it is not a blinded accuracy evaluation.'
(root/'fixture.json').write_text(json.dumps(fixture,ensure_ascii=False),encoding='utf-8')
print(f'Frozen {len(fixture["inputs"])} cases and saved recording proofs in {root / "fixture.json"}')
