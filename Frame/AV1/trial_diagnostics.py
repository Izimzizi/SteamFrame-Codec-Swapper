"""Timestamp-scoped, nonsecret receiver fault counts for a paired trial."""
import argparse,json,pathlib,subprocess
parser=argparse.ArgumentParser();parser.add_argument('--since',required=True);a=parser.parse_args()
import datetime
datetime.datetime.fromisoformat(a.since)
root=pathlib.Path(__file__).resolve().parents[2]
remote=r'''
import datetime,json,pathlib,re
since=datetime.datetime.fromisoformat(__SINCE__)
patterns={
 'hard_reset':r'SVLCodecV4L2::HardReset: Called',
 'decoder_stuck':r'SVLCodecV4L2::CheckStuck:',
 'capture_setup':r'SVLCodecV4L2::CaptureSetup -> Starting',
 'backtrack_reset':r'Received frame outside of backtrack window',
 'fec_retry':r'SVLFEC: substantive retry',
 'fec_unrecoverable':r'SVLFEC Received ExperiencedAnUnrecoverableError',
 'late_metadata':r'received late metadata for frame',
 'empty_buffer':r'SubmitFrameForDecode: Called submit with an empty buffer',
 'buffer_mismatch':r'SubmitFrameForDecode: .*Buffer from the pointer !=',
}
counts={k:0 for k in patterns};events=[]
for line in (pathlib.Path.home()/'.steam/steam/logs/vrlink.txt').read_text(errors='replace').splitlines():
 m=re.match(r'^(.+?\d{2}:\d{2}:\d{2}\.\d+) \[',line)
 if not m:continue
 try:t=datetime.datetime.strptime(m[1],'%a %b %d %Y %H:%M:%S.%f')
 except ValueError:continue
 if t<since:continue
 matched=[k for k,p in patterns.items() if re.search(p,line,re.I)]
 for key in matched:counts[key]+=1
 if matched:events.append({'wall':t.isoformat(),'categories':matched})
files=sorted((pathlib.Path.home()/'.steam/steam/logs').glob('vrlink_aftm_*.csv'),key=lambda p:p.stat().st_mtime,reverse=True)
print(json.dumps({'since':since.isoformat(),'frame_wall':datetime.datetime.now().isoformat(),
 'counts':counts,'last_events':events[-12:],
 'latest_aftm':{'name':files[0].name,'header':files[0].open().readline().strip(),'modified':datetime.datetime.fromtimestamp(files[0].stat().st_mtime).isoformat()} if files else None}))
'''.replace('__SINCE__',repr(a.since))
options=['-i',r'C:\Users\ellio\AppData\Local\steamos-devkit\devkit_rsa','-o','IdentitiesOnly=yes',
 '-o','BatchMode=yes','-o','StrictHostKeyChecking=yes','-o',
 r'UserKnownHostsFile=C:\Users\ellio\AppData\Local\SteamLinkVRSettings\ssh-known-hosts','-o','ConnectTimeout=3']
p=subprocess.run([r'C:\Windows\System32\OpenSSH\ssh.exe',*options,'steamos@10.35.78.1','python3 -'],
 input=remote,text=True,capture_output=True,timeout=10)
if p.returncode:raise RuntimeError('Paired receiver diagnostics unavailable')
print(p.stdout.strip())
