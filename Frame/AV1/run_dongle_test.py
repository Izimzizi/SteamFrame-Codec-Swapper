"""Paired 120-fps synthetic AV1 transport test; isolated from SteamVR."""
import argparse,json,pathlib,subprocess,time
parser=argparse.ArgumentParser();parser.add_argument('backend',type=pathlib.Path);parser.add_argument('--width',type=int,default=512);parser.add_argument('--height',type=int,default=512);parser.add_argument('--frames',type=int,default=1200);a=parser.parse_args()
directory=a.backend.resolve().parent
options=['-i',r'C:\Users\ellio\AppData\Local\steamos-devkit\devkit_rsa','-o','IdentitiesOnly=yes','-o','BatchMode=yes','-o','StrictHostKeyChecking=yes','-o',r'UserKnownHostsFile=C:\Users\ellio\AppData\Local\SteamLinkVRSettings\ssh-known-hosts','-o','ConnectTimeout=3']
receiver=subprocess.Popen([r'C:\Windows\System32\OpenSSH\ssh.exe',*options,'steamos@10.35.78.1','python3 /tmp/steam-frame-av1-lab-20261011/dongle_receiver.py'],stdout=subprocess.PIPE,stderr=subprocess.PIPE,text=True)
try:
 # The receiver has a bounded accept timeout, and is owned by this process.
 line=receiver.stdout.readline()
 if line.strip()!='AV1_LAB_READY':raise RuntimeError('Frame receiver did not become ready')
 sender=subprocess.run([str(a.backend.resolve()),str(a.width),str(a.height),str(a.frames),'dongle'],cwd=directory,capture_output=True,text=True,timeout=a.frames/120+40)
 out,err=receiver.communicate(timeout=35)
 receipt={'sender_exit_code':sender.returncode,'receiver_exit_code':receiver.returncode,'sender_stderr':sender.stderr[:1000],'receiver_stderr':err[:1000]}
 if sender.stdout.strip():receipt['sender']=json.loads(sender.stdout)
 if out.strip():receipt['receiver']=json.loads(out)
 receipt['functional_decode_passed']=receipt.get('receiver',{}).get('passed',False) and sender.returncode==0
 receipt['paced_120fps_passed']=receipt['functional_decode_passed'] and receipt.get('sender',{}).get('deadline_misses',1)==0
 receipt['vrlink_end_to_end_passed']=False
 name=f'dongle-{a.width}x{a.height}-{a.frames}.json'
 (directory/name).write_text(json.dumps(receipt,indent=2));print(json.dumps(receipt))
finally:
 if receiver.poll() is None:receiver.kill();receiver.wait()
