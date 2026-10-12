"""Stop only the hash-verified experimental client, even if decoder open failed."""
import json,subprocess
REMOTE=r'''
import hashlib,json,os,pathlib,signal,time
sha='59231d5b681022d56c697583ccc3ecdc0f32b97cf6110b3d15ad33b8e819d731'
killed=[]
for path in pathlib.Path('/proc').glob('[0-9]*'):
 try:
  if path.stat().st_uid!=os.getuid() or (path/'comm').read_text().strip() not in ('vrlink','vrlinkrunthread'):continue
  if hashlib.sha256((path/'exe').read_bytes()).hexdigest()!=sha:continue
  os.kill(int(path.name),signal.SIGTERM);killed.append(int(path.name))
 except (OSError,ProcessLookupError):pass
for i in range(50):
 if all(not pathlib.Path('/proc',str(pid)).exists() for pid in killed):break
 time.sleep(.1)
print(json.dumps({'terminated_trial_client_pids':killed,'still_present':[pid for pid in killed if pathlib.Path('/proc',str(pid)).exists()]}))
'''
args=[r'C:\Windows\System32\OpenSSH\ssh.exe','-i',r'C:\Users\ellio\AppData\Local\steamos-devkit\devkit_rsa',
 '-o','IdentitiesOnly=yes','-o','BatchMode=yes','-o','StrictHostKeyChecking=yes','-o',
 r'UserKnownHostsFile=C:\Users\ellio\AppData\Local\SteamLinkVRSettings\ssh-known-hosts',
 '-o','ConnectTimeout=3','steamos@10.35.78.1','python3 -']
p=subprocess.run(args,input=REMOTE,text=True,capture_output=True,timeout=10)
if p.returncode:print(json.dumps({'ok':False,'error':'Paired client stop failed'}));raise SystemExit(1)
print(p.stdout.strip())
