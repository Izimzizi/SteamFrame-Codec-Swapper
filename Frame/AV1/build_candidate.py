"""Transfer/build the isolated Iris port on the Frame; never install or modprobe."""
import argparse,json,pathlib,subprocess,tarfile
p=argparse.ArgumentParser();p.add_argument('candidate',type=pathlib.Path);p.add_argument('results',type=pathlib.Path)
p.add_argument('--module',default='steam-frame-av1-lab')
a=p.parse_args();a.results.mkdir(parents=True,exist_ok=True)
name=a.candidate.name
import re
if not re.fullmatch(r'[A-Za-z0-9][A-Za-z0-9_.-]{0,80}',name):raise ValueError('Invalid candidate directory name')
if not re.fullmatch(r'[A-Za-z0-9][A-Za-z0-9_-]{0,80}',a.module):raise ValueError('Invalid module name')
archive=a.results/(name+'.tar')
with tarfile.open(archive,'w') as t:
 for path in a.candidate.iterdir():
  if path.is_file():t.add(path,arcname=path.name)
options=['-i',r'C:\Users\ellio\AppData\Local\steamos-devkit\devkit_rsa','-o','IdentitiesOnly=yes','-o','BatchMode=yes','-o','StrictHostKeyChecking=yes','-o',r'UserKnownHostsFile=C:\Users\ellio\AppData\Local\SteamLinkVRSettings\ssh-known-hosts','-o','ConnectTimeout=3']
remote='/tmp/steam-frame-av1-lab-20261011'
subprocess.run([r'C:\Windows\System32\OpenSSH\scp.exe',*options,str(archive),'steamos@10.35.78.1:'+remote+'/'+name+'.tar'],capture_output=True,check=True,timeout=20)
script=r'''
import json,pathlib,subprocess,tarfile,time
lab=pathlib.Path('/tmp/steam-frame-av1-lab-20261011')
name=LAB_CANDIDATE_NAME
module_name=LAB_MODULE_NAME
out=lab/name;out.mkdir(exist_ok=True)
with tarfile.open(lab/(name+'.tar')) as t:t.extractall(out,filter='data')
headers=lab/'kernel/headers/usr/lib/modules/6.18.0-ge66bc2ca6f8c/build'
start=time.monotonic()
command=['make','-C',str(headers),'M='+str(out),'-j2','CONFIG_DEBUG_INFO_BTF_MODULES=','modules']
try:
 p=subprocess.run(command,capture_output=True,text=True,timeout=45)
 r={'exit_code':p.returncode,'wall_seconds':time.monotonic()-start,'log':p.stdout+p.stderr,'module_exists':p.returncode==0 and (out/(module_name+'.ko')).exists(),'loaded':False,'module_btf':False}
 if r['module_exists']:
  import hashlib
  r['module_sha256']=hashlib.sha256((out/(module_name+'.ko')).read_bytes()).hexdigest()
  info=subprocess.run(['modinfo',str(out/(module_name+'.ko'))],capture_output=True,text=True)
  r['module_info']=info.stdout
except subprocess.TimeoutExpired as e:
 r={'timeout':45,'log':(e.stdout or b'').decode(errors='replace')+(e.stderr or b'').decode(errors='replace'),'loaded':False}
print(json.dumps(r))
'''
script=script.replace('LAB_CANDIDATE_NAME',repr(name))
script=script.replace('LAB_MODULE_NAME',repr(a.module))
item=subprocess.run([r'C:\Windows\System32\OpenSSH\ssh.exe',*options,'steamos@10.35.78.1','python3 -'],input=script,capture_output=True,text=True,timeout=55)
if item.returncode:raise RuntimeError('Paired kernel build failed')
r=json.loads(item.stdout);(a.results/'kernel-build.log').write_text(r.pop('log'));(a.results/'kernel-build.json').write_text(json.dumps(r,indent=2))
print(json.dumps(r));print((a.results/'kernel-build.log').read_text()[-6000:])
