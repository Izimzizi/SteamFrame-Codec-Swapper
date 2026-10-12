"""Ephemeral sudo/SSH decoder test session; fixed actions, no persistent install.

Password is consumed by sudo before this process starts. This program never
receives, records or reports it. EOF/time limit restores the stock driver if a
trial is active. All module/firmware files remain unchanged on the boot volume.
"""
import hashlib,json,os,pathlib,select,shutil,signal,stat,struct,subprocess,sys,time

LAB=pathlib.Path('/tmp/steam-frame-av1-lab-20261011')
RUN=pathlib.Path('/run/steam-frame-av1-lab-20261012')
RELEASE='6.18.0-ge66bc2ca6f8c'
BASE=pathlib.Path('/lib/modules')/RELEASE/'kernel/drivers/media/platform/qcom/vcodec/iris/iris.ko'
FW=pathlib.Path('/lib/firmware/qcom/vpu/vpu33_4v.mbn')
CANDIDATE=LAB/'iris-7.0-frame-candidate/steam-frame-av1-lab.ko'
HASHES={'stock':'7a5647a009d60068965a476a887c2c73a51bedcec22727d71f010e9165d64a86',
 'firmware':'7b829fc1c8ce7cca836d10e898b99c5bcbd86e22073b690147168c9d0a5de378',
 'candidate':'c83ea764512488af7e9c3a357d78c21a4f32d4d193c39de01bb075226d440339'}
active=False
client_active=False
client_codec='HEVC'
CLIENT=pathlib.Path('/opt/steamvr/tools/vrlink/bin/linuxarm64/vrlink')
CLIENT_TRIAL=LAB/'frame-vrlink-av1-trial'
CLIENT_STOCK_SHA='31addef0c9553d3770b5570487699e96d70271d4073cd6c03b080fdee6ae2153'
CLIENT_TRIAL_SHA='59231d5b681022d56c697583ccc3ecdc0f32b97cf6110b3d15ad33b8e819d731'
CLIENT_H264_SHA='12a19ed7744ddb79e871bbb235ceb04b6d2c163eda06fbadd319c69f2294d831'
RECOVERY=LAB/'orphan-recovery/recover_orphan_firmware.ko'
RECOVERY_SHA='39fa4a86c6b381c363195065325d7db00678a28d795d8c36ec4a8625dad6f36f'
def run(args,timeout=15):
 p=subprocess.run(args,capture_output=True,text=True,timeout=timeout)
 return {'code':p.returncode,'out':p.stdout[:20000],'err':p.stderr[:4000]}
def digest(path):return hashlib.sha256(path.read_bytes()).hexdigest()
def video_path():
 for node in pathlib.Path('/sys/bus/platform/devices').glob('*aa00000*'):
  compatible=node/'of_node/compatible'
  if compatible.exists() and b'qcom,sm8650-iris\0' in compatible.read_bytes():return node.resolve()
 raise RuntimeError('SM8650 video platform device not found')
def decoder_nodes():
 platform=video_path()
 return sorted('/dev/'+p.name for p in pathlib.Path('/sys/class/video4linux').glob('video*')
               if (p/'device').resolve()==platform)

def opens():
 names=[];devices=set(decoder_nodes())
 for p in pathlib.Path('/proc').iterdir():
  if not p.name.isdigit():continue
  try:
   for fd in (p/'fd').iterdir():
    target=os.readlink(fd)
    if target.removesuffix(' (deleted)') in devices:
     names.append({'pid':int(p.name),'comm':(p/'comm').read_text().strip(),'device':target});break
  except (OSError,ProcessLookupError):pass
 return names
def inventory():
 return {'uid':os.geteuid(),'release':os.uname().release,'stock_sha256':digest(BASE),
  'firmware_sha256':digest(FW),'candidate_sha256':digest(CANDIDATE),
  'open_decoder_users':opens(),'decoder_nodes':decoder_nodes(),'platform_device':video_path().name,
  'current_driver':(video_path()/'driver').resolve().name if (video_path()/'driver').exists() else None,
  'trial_loaded':pathlib.Path('/sys/module/steam_frame_av1_lab').exists(),
  'stock_loaded':pathlib.Path('/sys/module/iris').exists(),'client_trial_active':client_active,
  'client_sha256':digest(CLIENT),'client_codec':client_codec}

def make_client(original,codec):
 if hashlib.sha256(original).hexdigest()!=CLIENT_STOCK_SHA:raise RuntimeError('Client fingerprint changed')
 if original[:6]!=b'\x7fELF\x02\x01' or struct.unpack_from('<H',original,18)[0]!=183:raise RuntimeError('Expected AArch64 ELF64 client')
 phoff=struct.unpack_from('<Q',original,32)[0];entry,count=struct.unpack_from('<HH',original,54)
 if entry!=56 or phoff+entry*count>len(original):raise RuntimeError('Invalid ELF program headers')
 segments=[]
 for i in range(count):
  position=phoff+i*entry
  if struct.unpack_from('<I',original,position)[0]==1:
   offset,va=struct.unpack_from('<QQ',original,position+8);size=struct.unpack_from('<Q',original,position+32)[0]
   if offset+size>len(original):raise RuntimeError('Invalid ELF load range')
   segments.append((offset,va,size))
 fourcc={'AV1':b'AV01','H.264':b'H264'}.get(codec)
 if fourcc is None:raise RuntimeError('Unknown codec patch')
 value=int.from_bytes(fourcc,'little');modified=bytearray(original)
 for va,before,after in ((0x32e7c,0x52800003|(0x4548<<5),0x52800003|((value&0xffff)<<5)),
                         (0x32e88,0x72a00003|(0x4356<<5),0x72a00003|((value>>16)<<5))):
  ranges=[offset+va-start for offset,start,size in segments if start<=va and va+4<=start+size]
  if len(ranges)!=1 or struct.unpack_from('<I',original,ranges[0])[0]!=before:raise RuntimeError('Client instruction layout changed')
  struct.pack_into('<I',modified,ranges[0],after)
 expected=CLIENT_TRIAL_SHA if codec=='AV1' else CLIENT_H264_SHA
 if hashlib.sha256(modified).hexdigest()!=expected:raise RuntimeError('Generated client fingerprint differs')
 return modified
def prepare():
 if os.uname().release!=RELEASE:raise RuntimeError('Kernel changed; rebuild/review required')
 if RUN.exists():
  info=RUN.lstat()
  if not stat.S_ISDIR(info.st_mode) or info.st_uid!=0:raise RuntimeError('Invalid root staging directory')
 else:RUN.mkdir(mode=0o700)
 RUN.chmod(0o700)
 for name,path in [('stock',BASE),('firmware',FW),('candidate',CANDIDATE)]:
  fd=os.open(path,os.O_RDONLY|os.O_NOFOLLOW)
  with os.fdopen(fd,'rb') as f:data=f.read()
  if hashlib.sha256(data).hexdigest()!=HASHES[name]:raise RuntimeError(name+' fingerprint changed')
  out=RUN/(name+'.bin');out.write_bytes(data);out.chmod(0o600)
 (RUN/'prepared.json').write_text(json.dumps({'release':RELEASE,'hashes':HASHES,'boot_changes':False,'prepared_utc':time.time()}))
 original=CLIENT.read_bytes()
 if hashlib.sha256(original).hexdigest()!=CLIENT_STOCK_SHA:raise RuntimeError('Client fingerprint changed')
 for name,data in [('client-stock',original),('client-trial',make_client(original,'AV1')),('client-h264',make_client(original,'H.264'))]:
  out=RUN/(name+'.bin');out.write_bytes(data);out.chmod(0o755)
 return {'prepared':True,'files':3,'persistent_changes':False,'rollback':'stock module and firmware fingerprint verified; reboot uses stock boot/module files'}
def load_disabled():
 if not (RUN/'prepared.json').exists():raise RuntimeError('Prepare rollback first')
 p=run(['insmod',str(RUN/'candidate.bin')])
 if p['code']:return p
 info=inventory()
 unloaded=run(['rmmod','steam_frame_av1_lab'])
 return {'load':p,'inventory':info,'unload':unloaded,'default_probe_was_enabled':False}
def rollback():
 global active
 client_stock()
 logs=[];node=video_path();driver=node/'driver'
 if driver.exists() and driver.resolve().name=='steam-frame-av1-lab':
  if opens():raise RuntimeError('Trial decoder is in use; close test first')
  (driver.resolve()/'unbind').write_text(node.name)
 if pathlib.Path('/sys/module/steam_frame_av1_lab').exists():logs.append(run(['rmmod','steam_frame_av1_lab']))
 if not pathlib.Path('/sys/module/iris').exists():logs.append(run(['modprobe','iris']))
 # The original module's probe attaches automatically after reload.
 stock_driver=pathlib.Path('/sys/bus/platform/drivers/qcom-iris')
 if not (node/'driver').exists() and stock_driver.exists():(stock_driver/'bind').write_text(node.name)
 active=False
 return {'operations':logs,'inventory':inventory()}

def client_for(codec):
 global client_active,client_codec
 expected_driver='steam-frame-av1-lab' if codec=='AV1' else 'qcom-iris'
 if (codec=='AV1' and not active) or (video_path()/'driver').resolve().name!=expected_driver:raise RuntimeError('Matching hardware decoder must be active')
 if opens():raise RuntimeError('Stop stream before client trial')
 expected=CLIENT_TRIAL_SHA if codec=='AV1' else CLIENT_H264_SHA
 source=RUN/('client-trial.bin' if codec=='AV1' else 'client-h264.bin')
 if digest(CLIENT)!=CLIENT_STOCK_SHA or digest(source)!=expected:raise RuntimeError('Client fingerprint changed')
 result=run(['mount','--bind',str(source),str(CLIENT)])
 if result['code']:return result
 client_active=True
 client_codec=codec
 if digest(CLIENT)!=expected:raise RuntimeError('Client bind did not take effect')
 return {'mounted':True,'codec':codec,'sha256':expected,'persistent_changes':False}
def client_av1():return client_for('AV1')
def client_h264():return client_for('H.264')

def client_stock():
 global client_active,client_codec
 if not client_active:return {'already_stock':True}
 # Stop only this patched VRLink executable if orderly stream stop was missed.
 stopped=[]
 for process in pathlib.Path('/proc').glob('[0-9]*'):
  try:
   executable=process/'exe'
   if (process/'comm').read_text().strip() in ('vrlink','vrlinkrunthread') and digest(executable) in (CLIENT_TRIAL_SHA,CLIENT_H264_SHA):
    os.kill(int(process.name),signal.SIGTERM)
    stopped.append(int(process.name))
  except (OSError,ProcessLookupError):pass
 started=time.monotonic()
 while (opens() or any(pathlib.Path('/proc',str(pid)).exists() for pid in stopped)) and time.monotonic()-started<5:time.sleep(.1)
 result=run(['umount',str(CLIENT)])
 if result['code']:raise RuntimeError('Temporary client unmount failed')
 client_active=False
 client_codec='HEVC'
 if digest(CLIENT)!=CLIENT_STOCK_SHA:raise RuntimeError('Stock client fingerprint did not return')
 return {'restored':True,'sha256':CLIENT_STOCK_SHA}
def load_av1():
 global active
 if not (RUN/'prepared.json').exists():raise RuntimeError('Prepare rollback first')
 if os.uname().release!=RELEASE:raise RuntimeError('Kernel changed; rebuild/review required')
 if not (video_path()/'driver').exists() or (video_path()/'driver').resolve().name!='qcom-iris':
  raise RuntimeError('Expected stock qcom-iris binding before trial')
 if opens():raise RuntimeError('Decoder is in use; stop stream first')
 if digest(RUN/'candidate.bin')!=HASHES['candidate']:raise RuntimeError('Staged module changed')
 active=True
 stopped=run(['rmmod','iris'])
 if stopped['code']:active=False;return {'stock_unload':stopped,'changed':False}
 loaded=run(['insmod',str(RUN/'candidate.bin'),'arm_av1_lab=1'])
 info=inventory()
 if loaded['code'] or info['current_driver']!='steam-frame-av1-lab':
  restored=rollback();return {'load':loaded,'candidate_inventory':info,'rollback':restored,'bound':False}
 return {'load':loaded,'inventory':info,'bound':True,'persistent_changes':False}
def formats():return run(['v4l2-ctl','--list-devices'])

def recover_orphan_fw():
 node=video_path()
 if os.uname().release!=RELEASE or (node/'driver').exists() or opens() or pathlib.Path('/sys/module/steam_frame_av1_lab').exists():
  raise RuntimeError('Recovery requires the exact unbound, idle Frame codec')
 if digest(BASE)!=HASHES['stock'] or digest(FW)!=HASHES['firmware'] or digest(CLIENT)!=CLIENT_STOCK_SHA:
  raise RuntimeError('Stock recovery fingerprints changed')
 fd=os.open(RECOVERY,os.O_RDONLY|os.O_NOFOLLOW)
 with os.fdopen(fd,'rb') as f:data=f.read()
 if hashlib.sha256(data).hexdigest()!=RECOVERY_SHA:raise RuntimeError('Recovery helper fingerprint changed')
 if not RUN.exists() or RUN.lstat().st_uid!=0:raise RuntimeError('Root recovery staging unavailable')
 target=RUN/'orphan-recovery.bin';target.write_bytes(data);target.chmod(0o600)
 logs=[]
 if pathlib.Path('/sys/module/iris').exists():
  result=run(['rmmod','iris']);logs.append(result)
  if result['code']:raise RuntimeError('Unbound stock module is busy')
 result=run(['insmod',str(target),'armed=1']);logs.append(result)
 if result['code']:
  logs.append(run(['modprobe','iris']));return {'recovered':False,'operations':logs,'inventory':inventory()}
 logs.append(run(['rmmod','recover_orphan_firmware']))
 logs.append(run(['modprobe','iris']))
 info=inventory()
 return {'recovered':info['current_driver']=='qcom-iris','operations':logs,'inventory':info,'persistent_changes':False}
def kernel_log():
 p=subprocess.run(['dmesg','--color=never'],capture_output=True,text=True,timeout=15)
 allowed=('iris','vcodec','venus','vpu','av1','firmware','video4linux','iommu')
 lines=[l for l in p.stdout.splitlines() if any(s in l.lower() for s in allowed)]
 return {'code':p.returncode,'lines':lines[-100:]}
def main():
 if os.geteuid()!=0:raise RuntimeError('Administrator authentication did not succeed')
 actions={'inventory':inventory,'prepare':prepare,'load-disabled':load_disabled,'load-av1':load_av1,
          'rollback':rollback,'formats':formats,'kernel-log':kernel_log,'client-av1':client_av1,'client-h264':client_h264,'client-stock':client_stock,
          'recover-orphan-fw':recover_orphan_fw}
 print(json.dumps({'root_ready':True,'uid':0}),flush=True)
 began=time.monotonic()
 try:
  while time.monotonic()-began<7200:
   ready,_,_=select.select([sys.stdin],[],[],60)
   if not ready:continue
   line=sys.stdin.readline()
   if not line:break
   if not line.strip():continue
   try:request=json.loads(line)
   except json.JSONDecodeError:
    print(json.dumps({'ok':False,'error':'Invalid action request'}),flush=True);continue
   action=request.get('action')
   if action=='close':break
   try:
    if action not in actions:raise ValueError('Unknown lab action')
    result={'ok':True,'result':actions[action]()}
   except Exception as e:result={'ok':False,'error':type(e).__name__+': '+str(e)}
   print(json.dumps(result),flush=True)
 finally:
  if active or client_active:
   try:rollback()
   except Exception:pass
if __name__=='__main__':main()
