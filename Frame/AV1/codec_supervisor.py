"""Bounded paired-codec service. Password enters stdin once; never saved.

Only exact-build temporary client mounts/decoder changes are supported. UI
requests are fixed codec operations; this is not an arbitrary command broker.
"""
import argparse,base64,ctypes as c,json,os,pathlib,queue,re,subprocess,sys,threading,time,uuid

MODES=('HEVC','AV1','H.264')
# Stream settings a paired session changes. HEVC restores the values found before the session.
HOST_KEYS=('targetBandwidth','recommendedBandwidthMbit','automaticBandwidth','allowMultipleLinks','automaticStreamFormatWidth','streamFormatWidth')
SOFT_LIMIT=90*60
# The Frame root helper exits two hours after authentication; restore well before that.
ROOT_LIFETIME=7200;RESTORE_MARGIN=300
kernel=c.WinDLL('kernel32',use_last_error=True)
kernel.CreateToolhelp32Snapshot.argtypes=[c.c_uint32,c.c_uint32];kernel.CreateToolhelp32Snapshot.restype=c.c_void_p
kernel.OpenProcess.argtypes=[c.c_uint32,c.c_int,c.c_uint32];kernel.OpenProcess.restype=c.c_void_p
kernel.QueryFullProcessImageNameW.argtypes=[c.c_void_p,c.c_uint32,c.c_wchar_p,c.POINTER(c.c_uint32)]
kernel.WaitForSingleObject.argtypes=[c.c_void_p,c.c_uint32];kernel.WaitForSingleObject.restype=c.c_uint32
kernel.CloseHandle.argtypes=[c.c_void_p]
class PROCESSENTRY32W(c.Structure):
 _fields_=[('dwSize',c.c_uint32),('cntUsage',c.c_uint32),('th32ProcessID',c.c_uint32),('th32DefaultHeapID',c.c_size_t),
  ('th32ModuleID',c.c_uint32),('cntThreads',c.c_uint32),('th32ParentProcessID',c.c_uint32),('pcPriClassBase',c.c_long),
  ('dwFlags',c.c_uint32),('szExeFile',c.c_wchar*260)]
kernel.Process32FirstW.argtypes=kernel.Process32NextW.argtypes=[c.c_void_p,c.POINTER(PROCESSENTRY32W)]
def process_paths(name):
 """(pid, image path) of running processes with this file name, without starting a shell."""
 snapshot=kernel.CreateToolhelp32Snapshot(2,0)
 if not snapshot or snapshot==c.c_void_p(-1).value:raise RuntimeError('Process inventory unavailable')
 entry=PROCESSENTRY32W();entry.dwSize=c.sizeof(entry);matches=[]
 try:
  more=kernel.Process32FirstW(snapshot,c.byref(entry))
  while more:
   if entry.szExeFile.lower()==name.lower():matches.append(entry.th32ProcessID)
   more=kernel.Process32NextW(snapshot,c.byref(entry))
 finally:kernel.CloseHandle(snapshot)
 result=[]
 for pid in matches:
  handle=kernel.OpenProcess(0x1000,0,pid)
  if not handle:continue
  try:
   size=c.c_uint32(32768);path=c.create_unicode_buffer(size.value)
   if kernel.QueryFullProcessImageNameW(handle,0,path,c.byref(size)):result.append((pid,path.value))
  finally:kernel.CloseHandle(handle)
 return result
class ProcessWatch:
 """Holds one process handle, so exit detection is a wait check and immune to PID reuse."""
 def __init__(self,pid):
  self.handle=kernel.OpenProcess(0x101000,0,pid)
  if not self.handle:raise RuntimeError('SteamVR exited during startup.')
 def alive(self):return bool(self.handle) and kernel.WaitForSingleObject(self.handle,0)==0x102
 def close(self):
  if self.handle:kernel.CloseHandle(self.handle);self.handle=None
def validate_request(value):
 if value.get('protocol_version')!=1:raise ValueError('Unsupported request version')
 identifier=str(uuid.UUID(value['id']))
 if identifier!=value['id']:raise ValueError('Noncanonical request ID')
 action=value.get('action')
 if action not in ('switch','close'):raise ValueError('Unknown codec action')
 if action=='switch':validate_mode(value.get('codec'),value.get('target_mbps'),value.get('fps'))
 return identifier,action
def validate_mode(codec,target,fps):
 maximum=200 if codec=='AV1' else 500
 if codec not in MODES or type(target) is not int or not 25<=target<=maximum or type(fps) is not int or fps not in (90,120):
  raise ValueError('Unsupported codec, bitrate or refresh rate')
def creation_epoch():
 win=c.WinDLL('kernel32',use_last_error=True);win.GetCurrentProcess.restype=c.c_void_p
 win.GetProcessTimes.argtypes=[c.c_void_p,c.c_void_p,c.c_void_p,c.c_void_p,c.c_void_p]
 values=[c.c_uint64() for _ in range(4)]
 if not win.GetProcessTimes(win.GetCurrentProcess(),*[c.byref(v) for v in values]):raise RuntimeError('Owner identity unavailable')
 return values[0].value/10000000-11644473600
def atomic_json(path,value):
 temporary=path.with_name(path.name+'.'+uuid.uuid4().hex+'.tmp')
 try:
  temporary.write_text(json.dumps(value),encoding='utf-8')
  # A short-lived Windows reader can omit FILE_SHARE_DELETE. Do not lose the
  # service heartbeat permanently because one atomic replacement races it.
  for attempt in range(10):
   try:os.replace(temporary,path);return
   except PermissionError:
    if attempt==9:raise
    time.sleep(.02)
 finally:temporary.unlink(missing_ok=True)
def host_values(driver):return {key:driver[key] for key in HOST_KEYS if key in driver}
def host_original(current,saved):
 """The user's stream settings. A session that ended before restoring leaves its own values
 in SteamVR; reuse its saved originals only while SteamVR still holds exactly those values."""
 if isinstance(saved,dict) and saved.get('protocol_version')==1 and isinstance(saved.get('original'),dict) and \
    saved.get('applied') is not None and saved.get('applied')==current and set(saved['original'])<=set(HOST_KEYS):return dict(saved['original'])
 return dict(current)
def codec_host_values(codec,target,original):
 width=original.get('streamFormatWidth',1536)
 return {'targetBandwidth':float(target),'recommendedBandwidthMbit':target,'automaticBandwidth':False,'allowMultipleLinks':False,
  'automaticStreamFormatWidth':False,'streamFormatWidth':1024 if codec=='H.264' else width if type(width) is int and 128<=width<=1536 else 1536}

class Supervisor:
 def __init__(self,directory,config):
  validate_mode(config['initial_codec'],config['target_mbps'],config['fps'])
  if config.get('protocol_version')!=1 or config.get('ssh_host')!='10.35.78.1':raise ValueError('Use the paired dedicated-dongle Frame')
  self.directory=directory.resolve();self.directory.mkdir(parents=True,exist_ok=True)
  self.requests=self.directory/'requests';self.requests.mkdir(exist_ok=True)
  self.package=pathlib.Path(__file__).resolve().parent
  if self.package.name!='CodecLab':self.package=self.package.parents[1]/'artifacts/CodecLab'
  self.backend=self.package/'Backend';self.config=config
  for name in ('ssh_key','ssh_known_hosts'):
   path=pathlib.Path(config[name]);
   if not path.is_absolute() or not path.is_file():raise ValueError('Paired SSH files are unavailable')
  windows=pathlib.Path(os.environ['SystemRoot'])/'System32'
  self.ssh=windows/'OpenSSH/ssh.exe';self.scp=windows/'OpenSSH/scp.exe'
  self.options=['-i',config['ssh_key'],'-o','IdentitiesOnly=yes','-o','BatchMode=yes','-o','StrictHostKeyChecking=yes',
   '-o','UserKnownHostsFile='+config['ssh_known_hosts'],'-o','ConnectTimeout=3']
  self.steam=pathlib.Path(config.get('steam_root',r'C:\Program Files (x86)\Steam')).resolve()
  self.vr=pathlib.Path(config.get('steamvr_root',str(self.steam/'steamapps/common/SteamVR'))).resolve()/'bin/win64'
  if not (self.steam/'steam.exe').is_file() or not (self.vr/'vrserver.exe').is_file():raise ValueError('SteamVR installation unavailable')
  self.pid=0;self.watch=None;self.root=None;self.root_started=0;self.authenticated=False;self.root_replies=queue.Queue();self.stopping=False
  # True while a non-HEVC codec is active or a switch may have left partial changes.
  self.experimental=False;self.restart_on_close=True
  self.mutex=threading.Lock()
  self.status={'protocol_version':1,'owner_pid':os.getpid(),'owner_created_epoch':creation_epoch(),'updated_epoch':time.time(),
   'state':'starting','active_codec':'Unknown','message':'Starting the paired codec session.','operation_id':'',
   'last_result':None,'target_mbps':config['target_mbps'],'fps':config['fps'],'stream_pid':0,'maximum_h264_mbps':500}
  self.host_path=self.directory/'host-original.json';self.host_state=None
  self.phase='initialization'
  self.stop_heartbeat=threading.Event()
 def update(self,**values):
  with self.mutex:
   self.status.update(values);self.status['updated_epoch']=time.time();atomic_json(self.directory/'status.json',self.status)
 def heartbeat(self):
  while not self.stop_heartbeat.wait(1):
   try:self.update()
   except OSError:pass # Leave the status stale and retry; never report false readiness.
 def command(self,args,input=None,timeout=30):
  p=subprocess.run(list(map(str,args)),input=input,capture_output=True,text=True,timeout=timeout,creationflags=subprocess.CREATE_NO_WINDOW)
  if p.returncode:raise RuntimeError('A paired codec command failed; check connection and installation.')
  return p.stdout.strip()
 def frame(self,script,timeout=12):return self.command([self.ssh,*self.options,'steamos@10.35.78.1','python3 -'],input=script,timeout=timeout)
 def cef(self,which):
  expression=(self.package/(which+'_stream.js')).read_text()
  script=(self.package/'frame_cef_stdio.py').read_text().replace('__EXPRESSION_BASE64__',base64.b64encode(expression.encode()).decode())
  result=json.loads(self.frame(script,20))
  if not isinstance(result,dict) or result.get('error'):raise RuntimeError('Frame stream command failed.')
  return result
 def powershell(self,code,timeout=30):return self.command(['powershell.exe','-NoProfile','-Command',code],timeout=timeout)
 def server_pid(self):
  # Values are fixed installation paths, not text from IPC requests.
  prefix=str(self.vr).lower()+'\\'
  for pid,path in process_paths('vrserver.exe'):
   if path.lower().startswith(prefix):return pid
  return 0
 def server_alive(self):return bool(self.watch) and self.watch.alive()
 def stop_server(self):
  prefix=(str(self.vr)+'\\').replace("'","''")
  self.powershell("$ErrorActionPreference='Stop'; $matches=@(Get-Process vrserver,vrcompositor,vrmonitor -ErrorAction SilentlyContinue | Where-Object {$_.Path -and $_.Path.StartsWith('"+prefix+"',[StringComparison]::OrdinalIgnoreCase)}); foreach($item in $matches){Stop-Process -Id $item.Id -ErrorAction Stop}; exit 0")
  if self.watch:self.watch.close();self.watch=None
  self.pid=0
 def root_receive(self,timeout=50):
  value=self.root_replies.get(timeout=timeout)
  if value is None:raise RuntimeError('Frame administrator session ended.')
  return json.loads(value)
 def action(self,name):
  self.root.stdin.write(json.dumps({'action':name})+'\n');self.root.stdin.flush()
  response=self.root_receive()
  if not response.get('ok'):raise RuntimeError(response.get('error','Frame rejected codec operation.'))
  return response['result']
 def authenticate(self,password):
  self.phase='Frame staging and authentication'
  self.command([self.ssh,*self.options,'steamos@10.35.78.1',
   'mkdir -p /tmp/steam-frame-av1-lab-20261011/iris-7.0-frame-candidate'])
  self.command([self.scp,*self.options,self.package/'root_session.py',
   'steamos@10.35.78.1:/tmp/steam-frame-av1-lab-20261011/root_session.py'])
  self.command([self.scp,*self.options,self.package/'Assets/steam-frame-av1-lab.ko',
   'steamos@10.35.78.1:/tmp/steam-frame-av1-lab-20261011/iris-7.0-frame-candidate/steam-frame-av1-lab.ko'])
  self.root_started=time.monotonic()
  self.root=subprocess.Popen([str(self.ssh),*self.options,'steamos@10.35.78.1',
   "sudo -k -S -p '' /usr/bin/python3 /tmp/steam-frame-av1-lab-20261011/root_session.py"],
   stdin=subprocess.PIPE,stdout=subprocess.PIPE,stderr=subprocess.PIPE,text=True,creationflags=subprocess.CREATE_NO_WINDOW)
  def replies():
   for line in self.root.stdout:self.root_replies.put(line)
   self.root_replies.put(None)
  threading.Thread(target=replies,daemon=True).start()
  self.root.stdin.write(password+'\n');self.root.stdin.flush();password=None
  if not self.root_receive(15).get('root_ready'):raise RuntimeError('Frame administrator authentication failed.')
  inventory=self.action('inventory')
  if inventory['current_driver']!='qcom-iris' or inventory['client_sha256']!='31addef0c9553d3770b5570487699e96d70271d4073cd6c03b080fdee6ae2153':
   raise RuntimeError('Another codec trial is active. Restore it before starting this session.')
  self.action('prepare')
  self.authenticated=True
 def frame_rate(self,fps):
  script=(self.package/'frame_rate_remote.py').read_text().replace('__FPS__',str(fps))
  value=json.loads(self.frame(script))
  if value.get('error') or abs(value['display_hz']-fps)>.1:raise RuntimeError('Frame did not accept the requested display rate.')
 def read_settings(self):return json.loads((self.steam/'config/steamvr.vrsettings').read_text(encoding='utf-8-sig'))
 def load_host_state(self):
  # Called while this process holds the owner lock, before any setting changes.
  try:saved=json.loads(self.host_path.read_text(encoding='utf-8'))
  except (OSError,ValueError):saved=None
  self.host_state={'protocol_version':1,'original':host_original(host_values(self.read_settings().get('driver_vrlink',{})),saved),'applied':None}
  atomic_json(self.host_path,self.host_state)
 def save_host_settings(self,codec,target,fps):
  if self.server_pid():raise RuntimeError('Stop SteamVR before writing codec settings.')
  path=self.steam/'config/steamvr.vrsettings';data=self.read_settings();driver=data.setdefault('driver_vrlink',{})
  original=self.host_state['original']
  data.setdefault('steamvr',{})['preferredRefreshRate']=fps
  if codec=='HEVC':
   # Return the user's own stream settings, not the experimental session's.
   for key in HOST_KEYS:
    if key in original:driver[key]=original[key]
    else:driver.pop(key,None)
   atomic_json(path,data)
   self.host_state['applied']=None;atomic_json(self.host_path,self.host_state)
  else:
   applied=codec_host_values(codec,target,original)
   # Record the session's values first so an interrupted session can still be restored.
   self.host_state['applied']=applied;atomic_json(self.host_path,self.host_state)
   driver.update(applied);atomic_json(path,data)
 def start_server(self,codec,target,fps):
  subprocess.Popen([str(self.steam/'steam.exe'),'-applaunch','250820'],creationflags=subprocess.CREATE_NO_WINDOW)
  deadline=time.monotonic()+25
  while time.monotonic()<deadline:
   pid=self.server_pid()
   if pid:
    if codec=='HEVC':self.pid=pid;self.watch=ProcessWatch(pid);return
    # A non-fixed budget lets SteamVR's own rate updates lower the encoder below the
    # requested ceiling after bad-link events, instead of pinning a rate the link cannot carry.
    p=subprocess.run([str(self.backend/'trial_control.exe'),str(pid),str(self.backend/'SteamFrameAV1Backend.dll'),
     'install',str(target*1000000),str(target*1000000),'0',str(fps),'1' if codec=='AV1' else '2'],
     capture_output=True,text=True,timeout=12,creationflags=subprocess.CREATE_NO_WINDOW)
    if not p.returncode:self.pid=pid;self.watch=ProcessWatch(pid);return
    try:
     code=json.loads(p.stdout).get('code')
     if code not in (11,None):raise RuntimeError('PC codec installation rejected; incompatible monitor or encoder.')
    except json.JSONDecodeError:pass
   time.sleep(.1)
  raise RuntimeError('PC codec installation timed out.')
 def stats(self,action='stats'):
  return json.loads(self.command([self.backend/'trial_control.exe',self.pid,self.backend/'SteamFrameAV1Backend.dll',action],timeout=12))
 def check_encoder_errors(self,state,codec,target,fps):
  if not state['errors']:return
  # Preserve the counter-only diagnosis before recovery destroys the encoder.
  atomic_json(self.directory/'encoder-failure.json',{'observed_epoch':time.time(),
   'codec':codec,'target_mbps':target,'fps':fps,'encoder':state})
  raise RuntimeError('Encoder errors after codec change: initialization status '+str(state['initialize_status'])+
   ', source '+str(state['width'])+'x'+str(state['height'])+' at '+str(state['source_fps_num'])+'/'+str(state['source_fps_den'])+
   ' fps. See encoder-failure.json in the codec session directory.')
 def switch(self,codec,target,fps,operation_id=''):
  validate_mode(codec,target,fps)
  self.update(state='switching',active_codec='Unknown',operation_id=operation_id,last_result=None,
   message='Reconnecting SteamVR and the Frame with matching codecs.')
  self.experimental=True
  self.phase='stopping stream'
  try:self.cef('stop')
  except Exception:pass
  self.stop_server();time.sleep(2)
  recovered=self.action('rollback')['inventory']
  if recovered['current_driver']!='qcom-iris' or recovered['client_trial_active']:raise RuntimeError('Stock codec recovery failed.')
  self.phase='Frame display rate';self.frame_rate(fps)
  self.phase='host settings';self.save_host_settings(codec,target,fps)
  self.phase='Frame codec activation'
  if codec=='AV1':
   if not self.action('load-av1').get('bound'):raise RuntimeError('AV1 hardware decoder did not bind.')
   if not self.action('client-av1').get('mounted'):raise RuntimeError('AV1 client activation failed.')
  elif codec=='H.264':
   if not self.action('client-h264').get('mounted'):raise RuntimeError('H.264 client activation failed.')
  self.phase='host codec activation';self.start_server(codec,target,fps)
  self.phase='starting Frame stream';self.cef('start')
  self.phase='verifying hardware stream'
  deadline=time.monotonic()+20;ready=False
  while time.monotonic()<deadline:
   receiver=self.action('inventory')
   expected='steam-frame-av1-lab' if codec=='AV1' else 'qcom-iris'
   if receiver['current_driver']==expected and receiver['open_decoder_users']:
    if codec=='HEVC':ready=True;break
    state=self.stats();identity=self.stats('identity')
    self.check_encoder_errors(state,codec,target,fps)
    if state['initializes'] and state['locks'] and identity['codec']==codec and state['source_fps_num']==fps*state['source_fps_den']:
     before=state['locks'];time.sleep(.5)
     if self.stats()['locks']>before:ready=True;break
   time.sleep(.5)
  if not ready:raise RuntimeError('Matching hardware stream did not start.')
  self.experimental=codec!='HEVC'
  self.update(state='ready',active_codec=codec,target_mbps=target,fps=fps,stream_pid=self.pid,
   message=codec+' connected over the dedicated dongle.',last_result={'ok':True},operation_id=operation_id)
 def recover(self,restart=True):
  """Restore the stock Frame client/decoder and the user's HEVC settings.

  SteamVR only needs stopping while an experimental codec is (or may be) active.
  restart=False leaves SteamVR closed, e.g. after the user quit it."""
  if self.experimental:
   self.stop_server()
   try:self.cef('stop')
   except Exception:pass
  restored=self.action('rollback')['inventory']
  if restored['current_driver']!='qcom-iris' or restored['client_trial_active']:raise RuntimeError('Stock codec restoration could not be confirmed.')
  if self.experimental:
   self.save_host_settings('HEVC',self.status['target_mbps'],self.status['fps'])
   if restart:self.start_server('HEVC',self.status['target_mbps'],self.status['fps']);self.cef('start')
  self.experimental=False
 def run(self,credentials):
  self.update();threading.Thread(target=self.heartbeat,daemon=True).start()
  try:
   password=credentials.pop()
   self.authenticate(password);password=None
   self.phase='host settings';self.load_host_state()
   self.switch(self.config['initial_codec'],self.config['target_mbps'],self.config['fps'])
   soft=time.monotonic()+SOFT_LIMIT;hard=self.root_started+ROOT_LIFETIME-RESTORE_MARGIN;expiring=False
   while time.monotonic()<hard:
    if self.root.poll() is not None:raise RuntimeError('Frame administrator session ended.')
    for path in sorted(self.requests.glob('request-*.json')):
     try:
      if path.is_symlink() or path.stat().st_size>4096:raise ValueError('Invalid request file')
      value=json.loads(path.read_text(encoding='utf-8-sig'));identifier,action=validate_request(value)
      if path.name!='request-'+identifier+'.json':raise ValueError('Request filename does not match its ID')
     except Exception:
      path.unlink(missing_ok=True);continue
     path.unlink()
     if action=='close':
      self.update(state='switching',active_codec='Unknown',operation_id=identifier,last_result=None,message='Restoring stock HEVC and closing the temporary session.')
      self.stopping=True;break
     try:self.switch(value['codec'],value['target_mbps'],value['fps'],identifier)
     except Exception:
      self.recover()
      self.update(state='error',active_codec='HEVC',stream_pid=self.pid,operation_id=identifier,
       message='Codec switch failed; stock HEVC was restored.',last_result={'ok':False,'error':'Codec switch failed; stock HEVC was restored.'})
    if self.stopping:break
    if self.pid and not self.server_alive():
     # SteamVR exited. Restore without relaunching it unless it was already started again.
     self.restart_on_close=bool(self.server_pid());break
    if time.monotonic()>=soft:
     if not self.experimental:break
     if not expiring:
      # Never stop a running game at the soft limit; restore when SteamVR next exits.
      expiring=True;self.update(message='The 90-minute codec session limit was reached. Stock HEVC is restored when SteamVR exits, or automatically within '+str(max(1,round((hard-time.monotonic())/60)))+' minutes.')
    time.sleep(.5)
  finally:
   password=None
   if self.authenticated:
    try:
     self.recover(self.restart_on_close);self.update(active_codec='HEVC',stream_pid=self.pid)
    finally:
     if self.root and self.root.poll() is None:
      self.root.stdin.write('{"action":"close"}\n');self.root.stdin.flush();self.root.stdin.close();self.root.wait(timeout=20)
   elif self.root and self.root.poll() is None:self.root.stdin.close();self.root.wait(timeout=20)
   if self.watch:self.watch.close();self.watch=None
   self.stop_heartbeat.set()
   self.update(state='closed',message='Temporary codec session closed; HEVC restored.' if self.restart_on_close or not self.authenticated
    else 'Temporary codec session closed after SteamVR exited; HEVC settings restored.',last_result={'ok':True})

def main():
 parser=argparse.ArgumentParser();parser.add_argument('--service',action='store_true');parser.add_argument('--session-dir',type=pathlib.Path,required=True);parser.add_argument('--config',type=pathlib.Path,required=True);a=parser.parse_args()
 config=json.loads(a.config.read_text(encoding='utf-8-sig'))
 if a.config.stat().st_size>8192 or 'password' in config:raise ValueError('Invalid public codec configuration')
 supervisor=Supervisor(a.session_dir,config)
 # Hold one OS file lock for the entire owner lifetime. Never replace its file.
 import msvcrt
 lock=open(supervisor.directory/'owner.lock','a+b');lock.seek(0);lock.write(b'0');lock.flush();lock.seek(0)
 msvcrt.locking(lock.fileno(),msvcrt.LK_NBLCK,1)
 try:
  if sys.stdin.isatty():
   import getpass
   password=getpass.getpass('Frame administrator password (hidden): ')
  else:
   value=json.loads(sys.stdin.readline(2048));password=value.pop('password',None);value.clear()
  if not isinstance(password,str) or not password or len(password)>256 or '\n' in password or '\r' in password:raise ValueError('Administrator authentication is required')
  credentials=[password];password=None;supervisor.run(credentials)
 except KeyboardInterrupt:
  pass
 except Exception as error:
  supervisor.stop_heartbeat.set()
  reason=str(error) if isinstance(error,RuntimeError) else type(error).__name__
  supervisor.update(state='error',active_codec='Unknown',message='Codec service could not complete at '+supervisor.phase+'. '+reason,last_result={'ok':False,'error':reason})
  raise SystemExit(1)
 finally:
  password=None;lock.seek(0);msvcrt.locking(lock.fileno(),msvcrt.LK_UNLCK,1);lock.close()
if __name__=='__main__':main()
