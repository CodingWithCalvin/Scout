"""Read-only server monitoring; save contention observations to the isolated benchmark."""
import datetime
import json
import sqlite3
import time
import urllib.request
from pathlib import Path
import sys

root=Path(sys.argv[1]).resolve()
path=str(root/'data'/'runtime-observations.json')
while True:
    with sqlite3.connect(root/'benchmark.db',timeout=15) as con:
        saved=con.execute('select content from documents where path=?',(path,)).fetchone()
        observations=json.loads(saved[0]) if saved else []
        state=con.execute('select content from documents where path=?',(str(root/'data'/'status.json'),)).fetchone()
        state=json.loads(state[0]) if state else {}
        runtime=con.execute('select content from documents where path=?',(str(root/'data'/'runtime.json'),)).fetchone()
        if state.get('state') in ('complete','failed','interrupted'):
            print('Benchmark stopped:',state.get('state'),flush=True)
            break
        if not runtime:
            time.sleep(5)
            continue
        endpoint=json.loads(runtime[0])['endpoint'].split('/v1/')[0]
        row={'time':datetime.datetime.now(datetime.timezone.utc).isoformat(),'case':state.get('active'),'state':state.get('message')}
        for key,base in [('benchmark',endpoint),('other_scout','http://127.0.0.1:18765')]:
            try:
                slots=json.load(urllib.request.urlopen(base+'/slots',timeout=3))
                row[key]=[{'busy':s['is_processing'],'processed':s.get('n_prompt_tokens_processed'),'cached':s.get('n_prompt_tokens_cache'),'decoded':s.get('next_token',[{}])[0].get('n_decoded')} for s in slots]
            except Exception as error:
                row[key]=[]
                row[key+'_unavailable']=type(error).__name__
        observations.append(row)
        con.execute('insert into documents(path,kind,content) values(?,?,?) on conflict(path) do update set content=excluded.content',(path,'json',json.dumps(observations).encode()))
        con.commit()
        print(json.dumps(row),flush=True)
    time.sleep(30)
