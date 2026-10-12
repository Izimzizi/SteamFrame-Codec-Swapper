"""Non-GUI variant: reads an authorized password from stdin without echo.

No password is passed in command arguments, stored in a file, or returned in
tool output. Keeps only an ephemeral authenticated SSH process thereafter.
"""
import argparse,getpass,json,pathlib,queue,subprocess,sys,threading,time
parser=argparse.ArgumentParser();parser.add_argument('session',type=pathlib.Path);a=parser.parse_args()
a.session.mkdir(parents=True,exist_ok=True);status=a.session/'status.json'
def state(value):status.write_text(json.dumps(value))
state({'state':'awaiting_stdin_authentication'})
print('{"awaiting_stdin_authentication":true}',flush=True)
secret=getpass.getpass('Frame user password: ')
if not secret:state({'state':'cancelled'});raise SystemExit(1)
options=['-i',r'C:\Users\ellio\AppData\Local\steamos-devkit\devkit_rsa','-o','IdentitiesOnly=yes','-o','BatchMode=yes','-o','StrictHostKeyChecking=yes','-o',r'UserKnownHostsFile=C:\Users\ellio\AppData\Local\SteamLinkVRSettings\ssh-known-hosts','-o','ConnectTimeout=3']
process=subprocess.Popen([r'C:\Windows\System32\OpenSSH\ssh.exe',*options,'steamos@10.35.78.1',"sudo -k -S -p '' /usr/bin/python3 /tmp/steam-frame-av1-lab-20261011/root_session.py"],stdin=subprocess.PIPE,stdout=subprocess.PIPE,stderr=subprocess.PIPE,text=True,encoding='utf-8',creationflags=subprocess.CREATE_NO_WINDOW)
output=queue.Queue()
def reader():
 for line in process.stdout:output.put(line)
 output.put(None)
threading.Thread(target=reader,daemon=True).start()
try:
 process.stdin.write(secret+'\n');process.stdin.flush();secret=None
 line=output.get(timeout=15)
 if not line or not json.loads(line).get('root_ready'):raise RuntimeError('Authentication unavailable')
 state({'state':'authenticated','uid':0,'expires_in_seconds':1800});print('{"authenticated":true,"uid":0}',flush=True)
 began=time.monotonic()
 while time.monotonic()-began<1800 and process.poll() is None:
  requests=sorted(a.session.glob('request-*.json'))
  if not requests:time.sleep(.15);continue
  close=False
  for file in requests:
   try:
    request=json.loads(file.read_text());action=request.get('action');file.unlink()
    process.stdin.write(json.dumps({'action':action})+'\n');process.stdin.flush()
    if action=='close':close=True;break
    response=output.get(timeout=45)
    if response is None:raise RuntimeError('Frame session ended')
    (a.session/file.name.replace('request-','reply-')).write_text(json.dumps(json.loads(response)))
   except Exception:
    (a.session/file.name.replace('request-','reply-')).write_text(json.dumps({'ok':False,'error':'Privileged action failed or timed out'}));raise
  if close:break
 process.stdin.close();process.wait(timeout=20)
 state({'state':'closed','ssh_exit_code':process.returncode,'diagnostic':process.stderr.read()[-1200:]})
except Exception:
 if process.poll() is None:process.kill();process.wait()
 state({'state':'authentication_or_session_failed'});print('{"authenticated":false,"session_ended":true}',flush=True)
 raise SystemExit(1)
