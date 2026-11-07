"""Bounded decoding probes on unchanged input. No probe is an assignment."""
import collections, copy, json, pathlib, socket, sqlite3, subprocess, threading, time, urllib.request, sys
root = pathlib.Path('D:/coding-with-calvin/Scout/.build/gemma-off-pilot')
models = pathlib.Path('C:/Users/calvi/Documents/Codex/2026-10-26/i/work/comparison-models')
source = sqlite3.connect((root / 'results/pilot.db').as_uri() + '?mode=ro', uri=True)
body = json.loads(source.execute("select content from documents where path like '%video-11-01-request.json'").fetchone()[0])
source.close()
db = sqlite3.connect(root / 'constraints.db')
db.execute('create table if not exists documents(name text primary key, content text not null)')
def save(name, value):
    db.execute('insert or replace into documents values(?,?)', (name, json.dumps(value))); db.commit()
def remove_limits(node):
    if isinstance(node, dict):
        node.pop('maxLength', None)
        for value in node.values(): remove_limits(value)
    elif isinstance(node, list):
        for value in node: remove_limits(value)
port = 18779
with socket.socket() as sock: sock.bind(('127.0.0.1', port))
process = subprocess.Popen([str(models/'runtimes/b11908-vulkan/llama-server.exe'), '-m', str(models/'gemma-4-31B-it-Q4_K_M.gguf'), '--host', '127.0.0.1', '--port', str(port), '-c', '32768', '-ngl', '0' if '--cpu' in sys.argv else '8', '-np', '1', '-t', '8', '-tb', '8', '--reasoning', 'off', '--reasoning-budget', '-1' if '--budget-disabled' in sys.argv else '0', '--reasoning-format', 'deepseek'], stdout=subprocess.PIPE, stderr=subprocess.STDOUT, text=True, encoding='utf-8', errors='replace', creationflags=subprocess.CREATE_NO_WINDOW)
lines = collections.deque(maxlen=200)
def drain():
    for line in process.stdout: lines.append(line.rstrip())
threading.Thread(target=drain, daemon=True).start()
watchdog = threading.Timer(1200, lambda: process.terminate() if process.poll() is None else None); watchdog.start()
try:
    save('status', {'state':'loading'})
    for _ in range(120):
        if process.poll() is not None: raise RuntimeError('Runtime exited during load')
        try:
            if urllib.request.urlopen(f'http://127.0.0.1:{port}/health', timeout=2).status == 200: break
        except Exception: time.sleep(2)
    else: raise TimeoutError('Runtime load timed out')
    arms = ['simple_greedy', 'recommended_sampling'] if '--sampling' in sys.argv else ['schema_without_string_limits', 'unconstrained']
    if '--budget-disabled' in sys.argv: arms=['simple_greedy_no_budget', 'original_schema_no_budget']
    if '--cpu' in sys.argv: arms=['simple_cpu']
    for arm in arms:
        if db.execute('select 1 from documents where name=?',(arm+'/result',)).fetchone():
            print('Reusing completed probe '+arm,flush=True);continue
        payload = copy.deepcopy(body); payload['max_tokens'] = 256
        if arm == 'schema_without_string_limits': remove_limits(payload['response_format'])
        elif arm == 'unconstrained': payload.pop('response_format')
        elif arm in ['simple_greedy','simple_greedy_no_budget','simple_cpu']:
            payload.pop('response_format'); payload['max_tokens']=64
            payload['messages']=[{'role':'user','content':'Reply with one short sentence explaining what a television episode is.'}]
        elif arm == 'recommended_sampling':
            payload.update(temperature=1.0,top_p=0.95,top_k=64,min_p=0.0)
        save(arm+'/request', payload); save('status', {'state':'running', 'arm':arm,'started':time.time(),'budget':-1 if '--budget-disabled' in sys.argv else 0})
        print('Starting '+arm, flush=True); start = time.monotonic(); events = []
        req = urllib.request.Request(f'http://127.0.0.1:{port}/v1/chat/completions', data=json.dumps(payload).encode(), headers={'Content-Type':'application/json'})
        with urllib.request.urlopen(req, timeout=300) as response:
            for raw in response:
                if raw.startswith(b'data: ') and raw.strip()!=b'data: [DONE]': events.append(json.loads(raw[6:]))
        answer = ''.join(c.get('delta',{}).get('content','') or '' for e in events for c in e.get('choices',[]))
        thought = ''.join(c.get('delta',{}).get('reasoning_content','') or '' for e in events for c in e.get('choices',[]))
        result = {'seconds':time.monotonic()-start, 'answer':answer, 'reasoning_characters':len(thought), 'events':events, 'assignment':False}
        save(arm+'/result', result)
        print(json.dumps({'arm':arm,'seconds':result['seconds'],'answer':answer,'reasoning_characters':len(thought)},ensure_ascii=True), flush=True)
    save('status', {'state':'complete'})
except Exception as ex:
    save('status', {'state':'failed','error':str(ex)}); raise
finally:
    watchdog.cancel()
    if process.poll() is None: process.terminate(); process.wait(timeout=30)
    save('runtime_tail', list(lines)); db.close()
