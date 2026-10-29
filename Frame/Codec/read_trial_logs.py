"""Selected nonsecret decoder/launch diagnostics through paired SSH."""
import pathlib,re,subprocess
options=['-i',r'C:\Users\ellio\AppData\Local\steamos-devkit\devkit_rsa','-o','IdentitiesOnly=yes','-o','BatchMode=yes','-o','StrictHostKeyChecking=yes','-o',r'UserKnownHostsFile=C:\Users\ellio\AppData\Local\SteamFrameTray\known_hosts','-o','ConnectTimeout=3']
script=r'''
import json,pathlib,re
root=pathlib.Path('/home/steamos/.local/share/Steam/logs')
result={}
for name in ('vrlink.txt','vrlinkmanager.txt','vrclient_vrlink.txt'):
 try:lines=(root/name).read_text(errors='replace').splitlines()[-180:]
 except OSError:continue
 selected=[]
 for line in lines:
  if not any(s in line.lower() for s in ('error','failed','codec','capture','outputsetup','initialize','starting','exit','permission','cannot')):continue
  if re.search(r'auth|token|ticket|password|cookie|steamid|certificate|command line|arguments|steamidentity',line,re.I):continue
  line=re.sub(r'[0-9]{16,20}','[id]',line)
  line=re.sub(r'vrlink.[A-Za-z0-9]+','vrlink.[device]',line)
  selected.append(line)
 result[name]=selected[-24:]
print(json.dumps(result))
'''
result=subprocess.run([r'C:\Windows\System32\OpenSSH\ssh.exe',*options,'steamos@10.35.78.1','python3 -'],input=script,text=True,capture_output=True,timeout=8)
print(result.stdout.strip() if result.returncode==0 else 'Paired trial log read failed')
