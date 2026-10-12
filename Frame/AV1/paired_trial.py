"""Start the exact paired AV1 prototype and restore HEVC on exit/SteamVR stop.

Run from a terminal. Administrator password uses hidden input, never arguments
or files. Owns the privileged SSH lifetime; installed boot files stay stock.
"""
import argparse,getpass,json,pathlib,queue,subprocess,sys,threading,time

ROOT=pathlib.Path(__file__).resolve().parents[2]
PYTHON=sys.executable
SSH=r'C:\Windows\System32\OpenSSH\ssh.exe'
SCP=r'C:\Windows\System32\OpenSSH\scp.exe'
OPTIONS=['-i',r'C:\Users\ellio\AppData\Local\steamos-devkit\devkit_rsa','-o','IdentitiesOnly=yes',
 '-o','BatchMode=yes','-o','StrictHostKeyChecking=yes','-o',
 r'UserKnownHostsFile=C:\Users\ellio\AppData\Local\SteamLinkVRSettings\ssh-known-hosts','-o','ConnectTimeout=3']
parser=argparse.ArgumentParser();parser.add_argument('--minutes',type=int,default=90)
parser.add_argument('--mbps',type=int,default=100);parser.add_argument('--peak-mbps',type=int,default=115)
parser.add_argument('--fixed-target',action='store_true');parser.add_argument('--fps',type=int,choices=(90,120),default=120);a=parser.parse_args()
if not 1<=a.minutes<=90:raise ValueError('Use 1-90 minutes per temporary session')
if not 1<=a.mbps<=200 or not a.mbps<=a.peak_mbps<=230:raise ValueError('Use 1-200 Mbps average and average..230 Mbps peak')

def command(args,timeout=30):
 p=subprocess.run(args,cwd=ROOT,capture_output=True,text=True,timeout=timeout)
 if p.returncode:raise RuntimeError('Trial command failed: '+p.stderr[-600:])
 return p.stdout.strip()
def powershell(code):return command(['powershell.exe','-NoProfile','-Command',code])
def cef(script):
 result=json.loads(command([PYTHON,str(ROOT/'artifacts/FrameQualityLab/query_frame.py'),
  str(ROOT/('artifacts/AdvancedControls/'+script+'_stream.js')),'file']))
 if result.get('error'):raise RuntimeError('Paired stream command failed')
 return result
def stop_server():
 powershell("$ErrorActionPreference='Stop'; $p='C:\\Program Files (x86)\\Steam\\steamapps\\common\\SteamVR\\bin\\win64\\'; $matches=@(Get-Process vrserver,vrcompositor,vrmonitor -ErrorAction SilentlyContinue | Where-Object {$_.Path -and $_.Path.StartsWith($p,[StringComparison]::OrdinalIgnoreCase)}); foreach($item in $matches){Stop-Process -Id $item.Id -ErrorAction Stop}; exit 0")
def launch_stock():
 powershell("Start-Process -FilePath 'C:\\Program Files (x86)\\Steam\\steam.exe' -ArgumentList '-applaunch','250820' -WindowStyle Hidden")
 time.sleep(5);cef('start')

secret=getpass.getpass('Frame administrator password (hidden): ')
if not secret:raise SystemExit('No authentication supplied')
command([SCP,*OPTIONS,str(ROOT/'Frame/AV1/root_session.py'),
 'steamos@10.35.78.1:/tmp/steam-frame-av1-lab-20261011/root_session.py'])
process=subprocess.Popen([SSH,*OPTIONS,'steamos@10.35.78.1',
 "sudo -k -S -p '' /usr/bin/python3 /tmp/steam-frame-av1-lab-20261011/root_session.py"],
 stdin=subprocess.PIPE,stdout=subprocess.PIPE,stderr=subprocess.PIPE,text=True,
 creationflags=subprocess.CREATE_NO_WINDOW)
responses=queue.Queue()
def read():
 for line in process.stdout:responses.put(line)
 responses.put(None)
threading.Thread(target=read,daemon=True).start()
def receive(timeout=50):
 line=responses.get(timeout=timeout)
 if line is None:raise RuntimeError('Privileged Frame session ended')
 return json.loads(line)
def action(name):
 process.stdin.write(json.dumps({'action':name})+'\n');process.stdin.flush()
 result=receive()
 if not result.get('ok'):raise RuntimeError(result.get('error','Frame action rejected'))
 return result['result']
authenticated=False;swapped=False;ready=False
try:
 process.stdin.write(secret+'\n');process.stdin.flush();secret=None
 if not receive(15).get('root_ready'):raise RuntimeError('Administrator authentication failed')
 authenticated=True
 cef('stop');stop_server();time.sleep(2)
 before=action('inventory')
 if before['client_trial_active'] or before['current_driver']!='qcom-iris':
  raise RuntimeError('Another decoder trial is active; restore it before starting this launcher')
 action('prepare')
 loaded=action('load-av1');swapped=bool(loaded.get('bound'))
 if not swapped:raise RuntimeError('AV1 decoder did not bind')
 mounted=action('client-av1')
 if not mounted.get('mounted'):raise RuntimeError('AV1 client mount failed')
 launch=['powershell.exe','-NoProfile','-File',str(ROOT/'Native/AV1Lab/start_steamvr_trial.ps1'),
  '-Mbps',str(a.mbps),'-PeakMbps',str(a.peak_mbps),'-Fps',str(a.fps)]
 if a.fixed_target:launch.append('-FixedTarget')
 command(launch)
 cef('start');time.sleep(5)
 pid=int((ROOT/'artifacts/AV1Lab/steamvr-trial.pid').read_text())
 backend=ROOT/'artifacts/AV1Lab/backend'
 def stats():return json.loads(command([str(backend/'trial_control.exe'),str(pid),str(backend/'SteamFrameAV1Backend.dll'),'stats']))
 state=stats()
 if not state['initializes'] or state['errors'] or state['source_fps_num']!=a.fps*state['source_fps_den']:raise RuntimeError('PC AV1 encoder did not initialize cleanly at the requested rate')
 receiver=action('inventory')
 if not receiver['open_decoder_users'] or receiver['current_driver']!='steam-frame-av1-lab':
  raise RuntimeError('Frame AV1 decoder is not open')
 ready=True
 print(json.dumps({'av1_trial_ready':True,'pid':pid,'fps':a.fps,'bit_depth':8,
  'maximum_budget_Mbps':a.mbps,'peak_budget_Mbps':a.peak_mbps,'fixed_target':a.fixed_target,
  'accepted_average_Mbps':state['average_bps']/1e6,'session_limit_minutes':a.minutes,
  'stop':'Ctrl+C or quit SteamVR; HEVC is restored automatically'}),flush=True)
 began=time.monotonic()
 while time.monotonic()-began<a.minutes*60:
  time.sleep(3)
  if process.poll() is not None:raise RuntimeError('Frame administrator session ended')
  # An exited SteamVR process ends the pair instead of leaving AV1 on Frame
  # for a fresh, unmodified HEVC server.
  try:stats()
  except (RuntimeError,subprocess.TimeoutExpired):break
except KeyboardInterrupt:
 print('{"stop_requested":true}',flush=True)
finally:
 secret=None
 if authenticated:
  if swapped:
   stop_server()
   try:cef('stop')
   except Exception:pass
   restored=action('rollback')
   stock=restored['inventory']['current_driver']=='qcom-iris' and not restored['inventory']['client_trial_active']
   if not stock:raise RuntimeError('Stock recovery was not confirmed')
   print('{"stock_decoder_and_client_restored":true}',flush=True)
  process.stdin.write('{"action":"close"}\n');process.stdin.flush();process.stdin.close()
  process.wait(timeout=20)
  if ready:launch_stock();print('{"hevc_stream_restarted":true}',flush=True)
 elif process.poll() is None:
  process.stdin.close();process.wait(timeout=20)
