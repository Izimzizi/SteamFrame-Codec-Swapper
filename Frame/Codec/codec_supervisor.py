"""Bounded H.264 keep service for the SteamFrame tray. Password enters stdin once; never saved.

It keeps the patched Frame client and the PC-side NVENC hook applied while the tray runs, changes only the
bitrate (and the stream width the H.264 hook requires) in SteamVR, and restores everything on close, when the
tray exits, or when it cannot keep H.264 applied. Requests are fixed operations, not an arbitrary command broker.
"""
import argparse,base64,ctypes as c,json,os,pathlib,queue,re,subprocess,sys,threading,time,uuid

RATES=(72,80,90,120)  # the refresh rates the hook accepts; read from SteamVR, never set by this service
# The only stream settings a session changes. Everything else, including multi-link and automatic bandwidth, is the user's.
HOST_KEYS=('targetBandwidth','recommendedBandwidthMbit','automaticStreamFormatWidth','streamFormatWidth')
H264_WIDTH=1024  # the Frame's H.264 decoder needs this stream width
# Keep mode swaps in a fresh administrator session well before the old one expires (the Frame root helper ends after two hours).
RENEW_AFTER=100*60;RETRY_INTERVAL=15
# The paired H.264 encoder's highest accepted bitrate; the hook is installed at it so SteamVR's live bitrate drives the encoder.
H264_MAXIMUM_MBPS=500
TRAY_CHECK_SECONDS=5       # how often the tray process is checked; if it is gone the session restores stock and ends
RESTORE_RETRY_SECONDS=600  # how long a restore keeps trying when the Frame is unreachable at the end
ENCODER_RATE_CONTROLS=('vbr','cbr')
CLIENT_STOCK_SHA='31addef0c9553d3770b5570487699e96d70271d4073cd6c03b080fdee6ae2153'
CLIENT_H264_SHA='12a19ed7744ddb79e871bbb235ceb04b6d2c163eda06fbadd319c69f2294d831'
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
 def __init__(self,pid,what='SteamVR'):
  self.handle=kernel.OpenProcess(0x101000,0,pid)
  if not self.handle:raise RuntimeError(what+' exited during startup.')
 def alive(self):return bool(self.handle) and kernel.WaitForSingleObject(self.handle,0)==0x102
 def close(self):
  if self.handle:kernel.CloseHandle(self.handle);self.handle=None
def validate_request(value):
 if value.get('protocol_version')!=1:raise ValueError('Unsupported request version')
 identifier=str(uuid.UUID(value['id']))
 if identifier!=value['id']:raise ValueError('Noncanonical request ID')
 action=value.get('action')
 if action not in ('close','rate','encoder'):raise ValueError('Unknown codec action')
 if action=='rate':validate_target(value.get('target_mbps'))
 if action=='encoder':validate_encoder_options(value.get('rate_control'),value.get('preset'),value.get('vbv_frames'))
 return identifier,action
def validate_target(target):
 if type(target) is not int or not 25<=target<=H264_MAXIMUM_MBPS:raise ValueError('Unsupported bitrate')
def validate_encoder_options(rate_control,preset,vbv=1):
 """Experimental NVENC settings for the hook: rate control mode, preset P1..P7 and VBV buffer frames (the default is vbr / P2 / 1)."""
 if rate_control not in ENCODER_RATE_CONTROLS or type(preset) is not int or not 1<=preset<=7 or type(vbv) is not int or not 1<=vbv<=16:raise ValueError('Unsupported encoder option')
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
def valid_host(value):
 """The Frame's address: its hostname or an IPv4 address. Nothing an ssh option or a port could hide in."""
 return type(value) is str and re.fullmatch(r'[A-Za-z0-9](?:[A-Za-z0-9.-]{0,251}[A-Za-z0-9])?',value) is not None and '..' not in value
def host_values(driver):return {key:driver[key] for key in HOST_KEYS if key in driver}
def host_original(current,saved):
 """The user's stream settings. A session that ended before restoring leaves its own values
 in SteamVR; reuse its saved originals only while SteamVR still holds exactly those values."""
 # 'previous' is the values in force just before a live bitrate change, which may not have reached the file yet.
 if isinstance(saved,dict) and saved.get('protocol_version')==1 and isinstance(saved.get('original'),dict) and \
    saved.get('applied') is not None and current in (saved.get('applied'),saved.get('previous')) and set(saved['original'])<=set(HOST_KEYS):return dict(saved['original'])
 return dict(current)
def codec_host_values(target):
 return {'targetBandwidth':float(target),'recommendedBandwidthMbit':target,'automaticStreamFormatWidth':False,'streamFormatWidth':H264_WIDTH}

class Supervisor:
 resume=False;restore_only=False;password=None;applied=None;frame_mounted=False
 closing_message=None;next_attempt=0
 log_last=None
 verify_seconds=20  # how long a new stream may take to start; a user-started SteamVR needs longer
 live_rate=False  # the NVENC hook was installed uncapped, so SteamVR's own live bitrate drives the encoder
 automatic_cache=(0.0,False)
 def __init__(self,directory,config):
  validate_target(config['target_mbps'])
  self.resume=config.get('resume') is True;self.restore_only=config.get('restore_only') is True
  self.rate_control=config.get('rate_control','vbr');self.preset=config.get('preset',2);self.vbv=config.get('vbv_frames',2)
  validate_encoder_options(self.rate_control,self.preset,self.vbv)
  if config.get('protocol_version')!=1 or not valid_host(config.get('ssh_host')):raise ValueError('Use the paired Frame address')
  self.login='steamos@'+config['ssh_host']
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
  # The tray that started this service. When it is gone the service restores stock and ends: nothing is left applied.
  self.tray=None;self.tray_checked=0.0
  tray_pid=config.get('tray_pid')
  if type(tray_pid) is int and tray_pid>0:self.tray=ProcessWatch(tray_pid,'The tray')
  self.pid=0;self.watch=None;self.root=None;self.root_started=0;self.authenticated=False;self.root_replies=queue.Queue();self.stopping=False
  # True while H.264 is active or a switch may have left partial changes.
  self.experimental=False;self.restart_on_close=True
  self.mutex=threading.Lock()
  self.status={'protocol_version':1,'owner_pid':os.getpid(),'owner_created_epoch':creation_epoch(),'updated_epoch':time.time(),
   'state':'starting','active_codec':'Unknown','message':'Starting the paired codec session.','operation_id':'',
   'last_result':None,'target_mbps':config['target_mbps'],'fps':0,'stream_pid':0,'keep':True,'rate_live':False,'automatic_bandwidth':False,
   'rate_control':self.rate_control,'preset':self.preset,'vbv_frames':self.vbv}
  self.host_path=self.directory/'host-original.json';self.host_state=None
  self.phase='initialization'
  self.stop_heartbeat=threading.Event()
 def rate_available(self):
  """A bitrate-only change needs no restart: SteamVR is running with the uncapped hook (its own live
  setting then drives the encoder), or SteamVR is closed and the session applies it at the next start."""
  if self.pid and self.live_rate and self.status.get('active_codec')=='H.264':return self.server_alive()
  return bool(self.applied and not self.pid)
 def automatic_bandwidth(self):
  """SteamVR's Automatic bandwidth is the user's choice and is left alone; the tray shows a note when it is on."""
  now=time.monotonic()
  if now-self.automatic_cache[0]>=5:
   try:value=self.read_settings().get('driver_vrlink',{}).get('automaticBandwidth') is True
   except (OSError,ValueError):value=self.automatic_cache[1]
   self.automatic_cache=(now,value)
  return self.automatic_cache[1]
 def log(self,text):
  """A short history of what the session did and why, kept beside status.json (UTC times)."""
  directory=getattr(self,'directory',None)
  if directory is None:return
  try:
   path=directory/'supervisor.log'
   if path.exists() and path.stat().st_size>300000:path.replace(directory/'supervisor.log.1')
   with open(path,'a',encoding='utf-8') as handle:handle.write(time.strftime('%H:%M:%S',time.gmtime())+' '+text+chr(10))
  except OSError:pass
 def update(self,**values):
  with self.mutex:
   self.status.update(values);self.status['updated_epoch']=time.time()
   mark=(self.status.get('state'),self.status.get('active_codec'),self.status.get('message'))
   if mark!=self.log_last:self.log('state '+str(mark[0])+' / '+str(mark[1])+': '+str(mark[2])+' [phase '+str(getattr(self,'phase',''))+']');self.log_last=mark
   try:self.status['rate_live']=self.rate_available()
   except Exception:self.status['rate_live']=False
   self.status['automatic_bandwidth']=self.automatic_bandwidth()
   atomic_json(self.directory/'status.json',self.status)
 def heartbeat(self):
  while not self.stop_heartbeat.wait(1):
   try:self.update()
   except OSError:pass # Leave the status stale and retry; never report false readiness.
 def command(self,args,input=None,timeout=30):
  p=subprocess.run(list(map(str,args)),input=input,capture_output=True,text=True,timeout=timeout,creationflags=subprocess.CREATE_NO_WINDOW)
  if p.returncode:raise RuntimeError('A paired codec command failed; check connection and installation.')
  return p.stdout.strip()
 def frame(self,script,timeout=12):return self.command([self.ssh,*self.options,self.login,'python3 -'],input=script,timeout=timeout)
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
 def start_root(self,password):
  self.phase='Frame staging and authentication'
  self.command([self.ssh,*self.options,self.login,'mkdir -p /tmp/steam-frame-av1-lab-20261011'])
  self.command([self.scp,*self.options,self.package/'root_session.py','%s:/tmp/steam-frame-av1-lab-20261011/root_session.py'%self.login])
  self.root_started=time.monotonic()
  self.root_replies=replies=queue.Queue()
  self.root=root=subprocess.Popen([str(self.ssh),*self.options,self.login,
   "sudo -k -S -p '' /usr/bin/python3 /tmp/steam-frame-av1-lab-20261011/root_session.py"],
   stdin=subprocess.PIPE,stdout=subprocess.PIPE,stderr=subprocess.PIPE,text=True,creationflags=subprocess.CREATE_NO_WINDOW)
  def pump():
   for line in root.stdout:replies.put(line)
   replies.put(None)
  threading.Thread(target=pump,daemon=True).start()
  root.stdin.write(password+'\n');root.stdin.flush();password=None
  if not self.root_receive(15).get('root_ready'):raise RuntimeError('Frame administrator authentication failed.')
 def authenticate(self,password):
  self.start_root(password)
  inventory=self.action('inventory')
  mounted=inventory['client_sha256']==CLIENT_H264_SHA
  if inventory['current_driver']!='qcom-iris' or inventory['client_sha256'] not in (CLIENT_STOCK_SHA,CLIENT_H264_SHA):
   raise RuntimeError('Another codec trial is active. Restore it before starting this session.')
  # An H.264 client mount left by an earlier session is taken over, never rewritten while it may be running.
  self.action('adopt' if mounted else 'prepare')
  self.frame_mounted=mounted
  self.authenticated=True
 def swap_root(self):
  """Replace the administrator session before its two-hour limit without dropping the client mount."""
  old=(self.root,self.root_replies,self.root_started)
  try:
   self.start_root(self.password)
   if self.action('inventory')['client_sha256']!=CLIENT_H264_SHA:raise RuntimeError('The H.264 client mount is missing.')
   self.action('adopt')
  except Exception:
   fresh=self.root
   self.root,self.root_replies,self.root_started=old
   if fresh is not None and fresh is not old[0]:
    try:fresh.stdin.close();fresh.wait(timeout=20)
    except Exception:pass
   raise
  try:
   old[0].stdin.write('{"action":"detach"}\n');old[0].stdin.flush();old[0].stdin.close();old[0].wait(timeout=20)
  except Exception:pass
 def current_refresh(self):
  rate=self.read_settings().get('steamvr',{}).get('preferredRefreshRate')
  if type(rate) is float and rate.is_integer():rate=int(rate)
  if type(rate) is not int or rate not in RATES:
   raise RuntimeError('The SteamVR refresh rate is not one of '+', '.join(map(str,RATES))+' Hz. Set it in SteamVR.')
  return rate
 def read_settings(self):return json.loads((self.steam/'config/steamvr.vrsettings').read_text(encoding='utf-8-sig'))
 def load_host_state(self):
  # Called while this process holds the owner lock, before any setting changes.
  try:saved=json.loads(self.host_path.read_text(encoding='utf-8'))
  except (OSError,ValueError):saved=None
  self.host_state={'protocol_version':1,'original':host_original(host_values(self.read_settings().get('driver_vrlink',{})),saved),'applied':None}
  atomic_json(self.host_path,self.host_state)
 def save_host_settings(self,target):
  """Write the session's bitrate and stream width, or with None put the user's own values back."""
  if self.server_pid():raise RuntimeError('Stop SteamVR before writing codec settings.')
  path=self.steam/'config/steamvr.vrsettings';data=self.read_settings();driver=data.setdefault('driver_vrlink',{})
  original=self.host_state['original']
  if target is None:
   # Return the user's own stream settings, not the session's.
   for key in HOST_KEYS:
    if key in original:driver[key]=original[key]
    else:driver.pop(key,None)
   atomic_json(path,data)
   self.host_state['applied']=None;self.host_state.pop('previous',None);atomic_json(self.host_path,self.host_state)
  else:
   applied=codec_host_values(target)
   # Record the session's values first so an interrupted session can still be restored.
   self.host_state['applied']=applied;atomic_json(self.host_path,self.host_state)
   driver.update(applied);atomic_json(path,data)
 def start_server(self,fps,launch=True,hook=True):
  if launch:subprocess.Popen([str(self.steam/'steam.exe'),'-applaunch','250820'],creationflags=subprocess.CREATE_NO_WINDOW)
  deadline=time.monotonic()+25;detail='SteamVR did not appear'
  while time.monotonic()<deadline:
   pid=self.server_pid()
   if pid:
    if not hook:self.pid=pid;self.watch=ProcessWatch(pid);self.live_rate=False;return
    # The hook is installed at the 500 Mbps maximum, so SteamVR's own live bitrate (the session's target,
    # changeable without a restart) is the limit rather than a value fixed at install.
    budget=H264_MAXIMUM_MBPS
    p=subprocess.run([str(self.backend/'hook_control.exe'),str(pid),str(self.backend/'SteamFrameH264Hook.dll'),
     'install',str(budget*1000000),str(budget*1000000),'0',str(fps),'2',self.rate_control,str(self.preset),str(self.vbv)],
     capture_output=True,text=True,timeout=12,creationflags=subprocess.CREATE_NO_WINDOW)
    if not p.returncode:self.pid=pid;self.watch=ProcessWatch(pid);self.live_rate=True;return
    detail=((p.stdout or '').strip()+' '+(p.stderr or '').strip()).strip() or 'no output'
    try:
     code=json.loads(p.stdout).get('code')
     if code not in (11,None):raise RuntimeError('PC codec installation rejected (hook code '+str(code)+('; the HEVC monitor is loaded in SteamVR' if code==18 else '')+').')
    except json.JSONDecodeError:pass
   time.sleep(.1)
  raise RuntimeError('PC codec installation timed out: '+detail[:200])
 def stats(self,action='stats'):
  return json.loads(self.command([self.backend/'hook_control.exe',self.pid,self.backend/'SteamFrameH264Hook.dll',action],timeout=12))
 def check_encoder_errors(self,state,target,fps):
  if not state['errors']:return
  # Preserve the counter-only diagnosis before recovery destroys the encoder.
  atomic_json(self.directory/'encoder-failure.json',{'observed_epoch':time.time(),
   'codec':'H.264','target_mbps':target,'fps':fps,'rate_control':self.rate_control,'preset':self.preset,'vbv_frames':self.vbv,'encoder':state})
  raise RuntimeError('Encoder errors after codec change: initialization status '+str(state['initialize_status'])+
   ', source '+str(state['width'])+'x'+str(state['height'])+' at '+str(state['source_fps_num'])+'/'+str(state['source_fps_den'])+
   ' fps. See encoder-failure.json in the codec session directory.')
 def switch(self,target,operation_id=''):
  """Restart SteamVR with H.264: stop the stream and SteamVR, mount the Frame client, write the settings, start SteamVR hooked."""
  validate_target(target);self.verify_seconds=20
  fps=self.current_refresh()
  self.update(state='switching',active_codec='Unknown',operation_id=operation_id,last_result=None,
   message='Reconnecting SteamVR and the Frame with matching codecs.')
  self.experimental=True
  self.phase='stopping stream'
  try:self.cef('stop')
  except Exception:pass
  self.stop_server();time.sleep(2)
  recovered=self.action('rollback')['inventory']
  if recovered['current_driver']!='qcom-iris' or recovered['client_trial_active']:raise RuntimeError('Stock codec recovery failed.')
  self.phase='host settings';self.save_host_settings(target)
  self.phase='Frame codec activation'
  if not self.action('client-h264').get('mounted'):raise RuntimeError('H.264 client activation failed.')
  self.phase='host codec activation';self.start_server(fps)
  self.phase='starting Frame stream';self.cef('start')
  self.phase='verifying hardware stream';self.verify_stream(target,fps)
  self.experimental=True
  self.applied=target
  self.restart_on_close=True
  self.update(state='ready',active_codec='H.264',target_mbps=target,fps=fps,stream_pid=self.pid,
   message='H.264 connected.',last_result={'ok':True},operation_id=operation_id)
 def set_rate(self,target,operation_id=''):
  """Change only the bitrate of a running or waiting session, without stopping SteamVR.

  With SteamVR running, this records the new values so crash recovery stays correct; the caller then
  writes them through SteamVR's live settings API. With SteamVR closed the values go into its settings file."""
  validate_target(target)
  if not self.rate_available():raise RuntimeError('A live bitrate change is not available; apply the bitrate again.')
  applied=codec_host_values(target)
  # The slider is the user's own bitrate choice: when the session ends it must stay, not go back to the value SteamVR had at arm time.
  original=self.host_state.get('original')
  if isinstance(original,dict):original['targetBandwidth']=applied['targetBandwidth'];original['recommendedBandwidthMbit']=applied['recommendedBandwidthMbit']
  if self.pid:
   self.host_state['previous']=self.host_state.get('applied');self.host_state['applied']=applied;atomic_json(self.host_path,self.host_state)
  else:
   self.save_host_settings(target)
  if self.applied:self.applied=target
  self.update(target_mbps=target,operation_id=operation_id,last_result={'ok':True})
 def set_encoder(self,rate_control,preset,vbv,operation_id=''):
  """Queue new NVENC options on the resident hook; the encoder SteamVR builds next (after a stream reset) uses them."""
  validate_encoder_options(rate_control,preset,vbv)
  if not self.rate_available():raise RuntimeError('A live encoder change is not available; arm H.264 again to apply it.')
  fps=self.status.get('fps')
  if fps not in RATES:fps=self.current_refresh()
  budget=H264_MAXIMUM_MBPS
  p=subprocess.run([str(self.backend/'hook_control.exe'),str(self.pid),str(self.backend/'SteamFrameH264Hook.dll'),
   'configure',str(budget*1000000),str(budget*1000000),'0',str(fps),'2',rate_control,str(preset),str(vbv)],
   capture_output=True,text=True,timeout=12,creationflags=subprocess.CREATE_NO_WINDOW)
  if p.returncode:raise RuntimeError('The hook refused the encoder options ('+((p.stdout or '')+' '+(p.stderr or '')).strip()[:160]+').')
  self.rate_control=rate_control;self.preset=preset;self.vbv=vbv
  self.log('encoder options queued: '+rate_control+', P'+str(preset)+', VBV '+str(vbv))
  self.update(rate_control=rate_control,preset=preset,vbv_frames=vbv)
  # SteamVR keeps one encoder session across stream resets of its own, so restart the Frame's stream: the new session uses the queued options.
  self.restream('Applying the new encoder options; the stream restarts for a moment.','H.264 running with the new encoder options.',operation_id,stop=True)
 def verify_stream(self,target,fps):
  deadline=time.monotonic()+self.verify_seconds;ready=False;seen='no receiver reading'
  while time.monotonic()<deadline:
   receiver=self.action('inventory')
   seen='Frame decoder '+str(receiver.get('current_driver'))+', '+str(receiver.get('open_decoder_users'))+' decoder user(s)'
   if receiver['current_driver']=='qcom-iris' and receiver['open_decoder_users']:
    state=self.stats();identity=self.stats('identity')
    seen+='; encoder initializes '+str(state.get('initializes'))+', frames '+str(state.get('locks'))+', source '+str(state.get('source_fps_num'))+'/'+str(state.get('source_fps_den'))+', expected '+str(fps)+' Hz'
    self.check_encoder_errors(state,target,fps)
    if state['initializes'] and state['locks'] and identity['codec']=='H.264' and state['source_fps_num']==fps*state['source_fps_den']:
     before=state['locks'];time.sleep(.5)
     if self.stats()['locks']>before:ready=True;break
   time.sleep(.5)
  if not ready:raise RuntimeError('Matching hardware stream did not start ('+seen+').')
 def recover(self,restart=True):
  """Restore the stock Frame client and the user's own stream settings.

  SteamVR only needs stopping while H.264 is (or may be) active.
  restart=False leaves SteamVR closed, e.g. after the user quit it."""
  if self.experimental:
   self.stop_server()
   try:self.cef('stop')
   except Exception:pass
  restored=self.action('rollback')['inventory']
  if restored['current_driver']!='qcom-iris' or restored['client_trial_active']:raise RuntimeError('Stock codec restoration could not be confirmed.')
  if self.experimental:
   self.save_host_settings(None)
   if restart:self.start_server(0,hook=False);self.cef('start')
  self.experimental=False
 def restore_without_frame(self):
  # The Frame is unreachable; its client mount ends when it restarts. Restore the PC side now.
  if self.experimental:
   self.stop_server()
   self.save_host_settings(None)
   self.experimental=False
  self.closing_message=self.closing_message or 'The Frame was unreachable; its H.264 client resets when the Frame restarts. PC settings restored.'
 def retry_frame_restore(self):
  """The Frame could not be reached when the session ended: keep trying, for a bounded time, to unmount the patched client."""
  if not self.password:return
  self.update(state='switching',keep=False,message='The Frame is unreachable. Retrying the restore for up to 10 minutes.')
  deadline=time.monotonic()+RESTORE_RETRY_SECONDS
  while time.monotonic()<deadline:
   try:
    self.start_root(self.password)
    self.authenticated=True
    self.action('rollback')
    self.closing_message='The Frame came back and its stock client was restored.'
    return
   except Exception:
    failed=self.root;self.root=None
    if failed is not None:
     try:failed.stdin.close()
     except Exception:pass
    time.sleep(RETRY_INTERVAL)
 def frame_lost(self):
  self.root=None;self.frame_mounted=False;self.next_attempt=0
  self.update(state='waiting',active_codec='Unknown',message='The Frame connection was lost. H.264 is re-applied when it returns.')
 def server_exited(self):
  if self.watch:self.watch.close();self.watch=None
  self.pid=0;self.restart_on_close=False
  try:
   if self.applied:self.save_host_settings(self.applied)
  except Exception:pass
  self.update(state='waiting',active_codec='Unknown',stream_pid=0,message='SteamVR closed. H.264 is re-applied the next time SteamVR starts.')
 def ensure_frame(self):
  """An administrator session exists and the Frame client is the H.264 one."""
  if self.root is None or self.root.poll() is not None:
   self.root=None;self.authenticate(self.password)
  inventory=self.action('inventory')
  if inventory['client_sha256']==CLIENT_H264_SHA:self.frame_mounted=True;return
  if inventory['client_sha256']!=CLIENT_STOCK_SHA or inventory['current_driver']!='qcom-iris':
   raise RuntimeError('The Frame client changed; keep mode cannot continue safely.')
  try:self.cef('stop')
  except Exception:pass
  if not self.action('client-h264').get('mounted'):raise RuntimeError('H.264 client activation failed.')
  self.frame_mounted=True
 def reapply(self):
  target=self.applied
  self.update(state='switching',active_codec='Unknown',message='SteamVR started; re-applying H.264.')
  failure='';self.verify_seconds=45
  for attempt in range(3):
   try:
    rate=self.current_refresh()
    # Hook the encoder first so a Frame that connects early still gets the matching stream.
    self.phase='host codec activation'
    if not (self.pid and self.server_alive()):self.start_server(rate,launch=False)
    self.phase='Frame codec activation';self.ensure_frame()
    self.phase='starting Frame stream';self.cef('start')
    self.phase='verifying hardware stream';self.verify_stream(target,rate)
    self.restart_on_close=True
    self.update(state='ready',active_codec='H.264',target_mbps=target,fps=rate,stream_pid=self.pid,
     message='H.264 re-applied after SteamVR restarted.',last_result={'ok':True})
    return
   except Exception as error:
    failure=str(error);self.log('re-apply attempt '+str(attempt+1)+' failed ('+self.phase+'): '+failure[:300])
    atomic_json(self.directory/'reapply-failure.json',{'observed_epoch':time.time(),'phase':self.phase,'attempt':attempt+1,'error':failure[:400]})
    if attempt<2:time.sleep(5)
  # Never leave a patched Frame client facing an unhooked SteamVR: restore everything and end the session.
  self.closing_message='H.264 could not be re-applied ('+self.phase+(': '+failure[:160] if failure else '')+'). Stock HEVC was restored and the session ended.'
  self.applied=None;self.stopping=True
  self.update(keep=False)
  self.recover()
 def restream(self,message='The Frame returned; reconnecting the stream.',done='H.264 reconnected after the Frame returned.',operation_id='',stop=False):
  """Start the Frame's stream again against the hooked SteamVR that is still running (stop=True ends it first, so SteamVR builds a new encoder)."""
  target=self.applied;self.verify_seconds=20
  self.update(state='switching',active_codec='Unknown',message=message,operation_id=operation_id,last_result=None)
  try:
   rate=self.status['fps']
   if stop:
    try:self.cef('stop')
    except Exception:pass
    time.sleep(1)
   self.ensure_frame();self.cef('start');self.verify_stream(target,rate)
   self.update(state='ready',active_codec='H.264',message=done,last_result={'ok':True})
  except Exception as error:
   self.log('stream restart failed: '+str(error)[:300])
   self.update(state='waiting',active_codec='Unknown',message='The stream could not be reconnected; H.264 is re-applied the next time SteamVR starts.',
    last_result={'ok':False,'error':'The stream did not come back: '+str(error)[:160]} if operation_id else None)
 def resume_session(self):
  """Arm while SteamVR is closed (or after the app or PC restarted): adopt what is there and restart nothing the user is using."""
  target=self.config['target_mbps']
  self.applied=target;self.experimental=True
  if self.server_pid() and self.frame_mounted:
   # The Frame already expects H.264 but this SteamVR is not hooked: restart it once.
   self.switch(target);return
  if not self.server_pid():self.save_host_settings(target)
  self.restart_on_close=False
  self.update(state='waiting',active_codec='Unknown',message='Armed. H.264 is applied when SteamVR starts.')
 def keep_tick(self):
  now=time.monotonic()
  if self.root is None:
   if now<self.next_attempt:return
   self.next_attempt=now+RETRY_INTERVAL
   try:self.authenticate(self.password)
   except Exception:
    failed=self.root;self.root=None
    if failed is not None:
     try:failed.stdin.close()
     except Exception:pass
    return
   if self.applied and self.pid and self.server_alive():self.restream()
   else:self.update(state='waiting',active_codec='Unknown',message='The Frame returned. H.264 is re-applied the next time SteamVR starts.')
   return
  if now-self.root_started>=RENEW_AFTER and now>=self.next_attempt:
   try:self.swap_root()
   except Exception:self.next_attempt=now+RETRY_INTERVAL*4 # the current session stays valid until its limit
  if not self.pid and self.applied and self.server_pid():self.reapply()
 def tray_gone(self):
  """True once the tray process that started this service has exited (checked every few seconds)."""
  if self.tray is None:return False
  now=time.monotonic()
  if now-self.tray_checked<TRAY_CHECK_SECONDS:return False
  self.tray_checked=now
  return not self.tray.alive()
 def handle_requests(self):
  for path in sorted(self.requests.glob('request-*.json')):
   try:
    if path.is_symlink() or path.stat().st_size>4096:raise ValueError('Invalid request file')
    value=json.loads(path.read_text(encoding='utf-8-sig'));identifier,action=validate_request(value)
    if path.name!='request-'+identifier+'.json':raise ValueError('Request filename does not match its ID')
   except Exception:
    path.unlink(missing_ok=True);continue
   path.unlink()
   if action=='close':
    # Restoring needs SteamVR stopped; bring it back as stock only if it was running.
    self.restart_on_close=bool(self.server_pid())
    self.update(state='switching',active_codec='Unknown',operation_id=identifier,last_result=None,message='Restoring SteamVR\'s own codec and closing the session.')
    self.stopping=True;return
   if action=='encoder':
    try:self.set_encoder(value['rate_control'],value['preset'],value['vbv_frames'],identifier)
    except Exception as error:
     self.update(operation_id=identifier,last_result={'ok':False,'error':'Live encoder change unavailable: '+str(error)[:200]})
    continue
   # A refused bitrate-only change leaves the session exactly as it was; the caller may re-apply instead.
   try:self.set_rate(value['target_mbps'],identifier)
   except Exception as error:
    self.update(operation_id=identifier,last_result={'ok':False,'error':'Live bitrate change unavailable: '+str(error)[:200]})
 def run(self,credentials):
  self.update();threading.Thread(target=self.heartbeat,daemon=True).start()
  try:
   password=credentials.pop()
   self.password=password
   self.authenticate(password);password=None
   self.phase='host settings';self.load_host_state()
   if self.restore_only:
    # Leftovers from a session that ended without restoring: put the saved originals back and end.
    self.experimental=True;self.restart_on_close=bool(self.server_pid());self.stopping=True
    self.closing_message='Leftover H.264 settings were restored.'
   elif self.resume:self.resume_session()
   else:self.switch(self.config['target_mbps'])
   while not self.stopping:
    if self.tray_gone():
     self.closing_message='The tray exited, so SteamVR\'s own codec was restored.'
     self.restart_on_close=bool(self.server_pid());break
    if self.root is None or self.root.poll() is not None:
     if self.root is not None:self.frame_lost()
    self.handle_requests()
    if self.stopping:break
    if self.pid and not self.server_alive():self.server_exited()
    self.keep_tick()
    if self.stopping:break
    time.sleep(.5)
  finally:
   try:
    if self.authenticated:
     try:
      if self.root is None or self.root.poll() is not None:
       self.restore_without_frame();self.retry_frame_restore()
      else:self.recover(self.restart_on_close)
      self.update(active_codec='HEVC',stream_pid=self.pid)
     finally:
      if self.root and self.root.poll() is None:
       try:self.root.stdin.write('{"action":"close"}\n');self.root.stdin.flush();self.root.stdin.close();self.root.wait(timeout=20)
       except Exception:pass
    elif self.root and self.root.poll() is None:self.root.stdin.close();self.root.wait(timeout=20)
   finally:
    password=None;self.password=None
    if self.watch:self.watch.close();self.watch=None
    if self.tray:self.tray.close()
    self.stop_heartbeat.set()
    self.update(state='closed',keep=False,message=self.closing_message or ('SteamVR\'s own codec was restored.' if self.restart_on_close or not self.authenticated
     else 'The session closed after SteamVR exited; its settings were restored.'),last_result={'ok':True})

def main():
 parser=argparse.ArgumentParser();parser.add_argument('--session-dir',type=pathlib.Path,required=True);parser.add_argument('--config',type=pathlib.Path,required=True);a=parser.parse_args()
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
