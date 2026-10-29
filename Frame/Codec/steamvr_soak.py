"""Bounded, nonsecret live SteamVR AV1/dongle counter observation."""
import argparse,datetime,json,pathlib,subprocess,time,sys
parser=argparse.ArgumentParser();parser.add_argument('--seconds',type=int,default=40);parser.add_argument('--label',default='20261012')
parser.add_argument('--pid',type=int);parser.add_argument('--backend',type=pathlib.Path)
options=parser.parse_args()
if options.seconds<10 or options.seconds>900 or options.seconds%10:raise ValueError('Duration must be a multiple of 10, within 10-900')
if not options.label.replace('-','').replace('_','').isalnum():raise ValueError('Invalid output label')
ROOT=pathlib.Path(__file__).resolve().parents[2]
PID=options.pid or int((ROOT/'artifacts/H264Hook/steamvr-trial.pid').read_text())
BACKEND=options.backend.resolve() if options.backend else ROOT/'artifacts/H264Hook'
OUT=ROOT/('artifacts/H264Hook/steamvr-soak-'+options.label+'.json')
SSH=[r'C:\Windows\System32\OpenSSH\ssh.exe','-i',r'C:\Users\ellio\AppData\Local\steamos-devkit\devkit_rsa',
 '-o','IdentitiesOnly=yes','-o','BatchMode=yes','-o','StrictHostKeyChecking=yes','-o',
 r'UserKnownHostsFile=C:\Users\ellio\AppData\Local\SteamFrameTray\known_hosts',
 '-o','ConnectTimeout=3','steamos@10.35.78.1','python3 -']
REMOTE=r'''
import datetime,json,pathlib,subprocess,time
p=subprocess.run(['/opt/steamvr/bin/linuxarm64/vrcmd','--stats'],capture_output=True,text=True,timeout=5)
values=json.loads(p.stdout)
result={'monotonic':time.monotonic(),'wall':datetime.datetime.now().astimezone().isoformat(),
 'compositor':next((x for x in values if x.get('key')=='vrlink.client'),None),'interfaces':{},'thermals':[]}
for iface in ('wlanap','wlan0','usb0'):
 base=pathlib.Path('/sys/class/net')/iface
 if base.exists():
  result['interfaces'][iface]={name:(base/name).read_text().strip() for name in
   ('statistics/rx_bytes','statistics/tx_bytes','statistics/rx_errors','statistics/tx_errors','statistics/rx_dropped','statistics/tx_dropped')}
for zone in pathlib.Path('/sys/class/thermal').glob('thermal_zone*'):
 try:result['thermals'].append({'type':(zone/'type').read_text().strip(),'millidegrees':int((zone/'temp').read_text())})
 except OSError:pass
print(json.dumps(result))
'''
def read():
 p=subprocess.run([str(BACKEND/'hook_control.exe'),str(PID),
  str(BACKEND/'SteamFrameH264Hook.dll'),'stats'],capture_output=True,text=True,timeout=12)
 if p.returncode:raise RuntimeError('PC AV1 counter read failed')
 pc=json.loads(p.stdout);pc['monotonic']=time.monotonic()
 p=subprocess.run(SSH,input=REMOTE,capture_output=True,text=True,timeout=10)
 if p.returncode:raise RuntimeError('Frame compositor counter read failed')
 return {'pc':pc,'frame':json.loads(p.stdout)}
samples=[]
OUT.parent.mkdir(parents=True,exist_ok=True)
for i in range(options.seconds//10+1):
 if i:time.sleep(10)
 try:sample=read()
 except Exception as error:
  OUT.write_text(json.dumps({'observed_at':datetime.datetime.now().astimezone().isoformat(),
   'pid':PID,'completed':False,'error':str(error),'samples':samples},indent=2))
  raise
 samples.append(sample)
 # Preserve each observation if a later disconnect interrupts the run.
 OUT.write_text(json.dumps({'pid':PID,'completed':False,'samples':samples},indent=2))
 print(json.dumps({'sample':i,'encoder_frames':sample['pc']['locks'],
  'frame_submits':sample['frame']['compositor']['frame_submits'],
  'frame_presents':sample['frame']['compositor']['frame_presents']}),flush=True)
a,b=samples[0],samples[-1];duration=b['pc']['monotonic']-a['pc']['monotonic']
summary={'duration_seconds':duration,'encoder_fps':(b['pc']['locks']-a['pc']['locks'])/duration,
 'encoded_Mbps':(b['pc']['bytes']-a['pc']['bytes'])*8/duration/1e6,
 'encode_error_delta':b['pc']['errors']-a['pc']['errors'],'frame_duration_seconds':b['frame']['monotonic']-a['frame']['monotonic'],
 'frame_delta':{k:b['frame']['compositor'][k]-a['frame']['compositor'][k] for k in
  ('frame_submits','frame_presents','dropped_frames','reprojected_frames','frame_presents_timed_out')},'interface_rates':{}}
for name,v in a['frame']['interfaces'].items():
 w=b['frame']['interfaces'][name]
 summary['interface_rates'][name]={
  'rx_Mbps':(int(w['statistics/rx_bytes'])-int(v['statistics/rx_bytes']))*8/summary['frame_duration_seconds']/1e6,
  'rx_error_delta':int(w['statistics/rx_errors'])-int(v['statistics/rx_errors']),
  'rx_drop_delta':int(w['statistics/rx_dropped'])-int(v['statistics/rx_dropped'])}
OUT.write_text(json.dumps({'observed_at':datetime.datetime.now().astimezone().isoformat(),'pid':PID,'completed':True,
 'summary':summary,'samples':samples,'limitations':'Local reprojects/adapter drops are not unique streaming losses; screenshots establish visible content separately.'},indent=2))
print(json.dumps({'output':str(OUT),'summary':summary}),flush=True)
