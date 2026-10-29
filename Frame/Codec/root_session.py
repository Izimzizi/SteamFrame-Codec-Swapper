"""Ephemeral sudo/SSH session that mounts the H.264 Steam Link client; fixed actions, no persistent install.

Password is consumed by sudo before this process starts. This program never
receives, records or reports it. EOF/time limit unmounts the patched client if one is
mounted. Boot-volume module and firmware files are only read, never changed.
"""
import hashlib,json,os,pathlib,select,shutil,signal,stat,struct,subprocess,sys,time

LAB=pathlib.Path('/tmp/steam-frame-av1-lab-20261011')
RUN=pathlib.Path('/run/steam-frame-av1-lab-20261012')
# Frame kernel builds the session recognises, with the stock decoder module's fingerprint.
BUILDS={
 '6.18.0-ge66bc2ca6f8c':{'stock':'7a5647a009d60068965a476a887c2c73a51bedcec22727d71f010e9165d64a86'},
 '6.18.0-g83913f38113c':{'stock':'bdf95ec2ebb52b600aeeb2f8be5aa278abc4f14b25b45cb4e6b96907b732757d'}}
FIRMWARE_SHA='7b829fc1c8ce7cca836d10e898b99c5bcbd86e22073b690147168c9d0a5de378'
_running=getattr(os,'uname',lambda:None)()
RELEASE=_running.release if _running and _running.release in BUILDS else '6.18.0-ge66bc2ca6f8c'
BASE=pathlib.Path('/lib/modules')/RELEASE/'kernel/drivers/media/platform/qcom/vcodec/iris/iris.ko'
FW=pathlib.Path('/lib/firmware/qcom/vpu/vpu33_4v.mbn')
HASHES={'stock':BUILDS[RELEASE]['stock'],'firmware':FIRMWARE_SHA}
client_active=False
detached=False
client_codec='HEVC'
CLIENT=pathlib.Path('/opt/steamvr/tools/vrlink/bin/linuxarm64/vrlink')
CLIENT_STOCK_SHA='31addef0c9553d3770b5570487699e96d70271d4073cd6c03b080fdee6ae2153'
CLIENT_H264_SHA='12a19ed7744ddb79e871bbb235ceb04b6d2c163eda06fbadd319c69f2294d831'
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
def require_supported_kernel():
 if os.uname().release not in BUILDS:
  raise RuntimeError('Unsupported Frame kernel '+os.uname().release+'; supported: '+', '.join(BUILDS))
def inventory():
 require_supported_kernel()
 return {'uid':os.geteuid(),'release':os.uname().release,'stock_sha256':digest(BASE),
  'firmware_sha256':digest(FW),  'open_decoder_users':opens(),'decoder_nodes':decoder_nodes(),'platform_device':video_path().name,
  'current_driver':(video_path()/'driver').resolve().name if (video_path()/'driver').exists() else None,
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
 if codec!='H.264':raise RuntimeError('Unknown codec patch')
 value=int.from_bytes(b'H264','little');modified=bytearray(original)
 for va,before,after in ((0x32e7c,0x52800003|(0x4548<<5),0x52800003|((value&0xffff)<<5)),
                         (0x32e88,0x72a00003|(0x4356<<5),0x72a00003|((value>>16)<<5))):
  ranges=[offset+va-start for offset,start,size in segments if start<=va and va+4<=start+size]
  if len(ranges)!=1 or struct.unpack_from('<I',original,ranges[0])[0]!=before:raise RuntimeError('Client instruction layout changed')
  struct.pack_into('<I',modified,ranges[0],after)
 expected=CLIENT_H264_SHA
 if hashlib.sha256(modified).hexdigest()!=expected:raise RuntimeError('Generated client fingerprint differs')
 return modified
def stage(out,data,mode):
 # A bind-mounted client executes from its staged file, so a matching file is left untouched.
 if out.exists() and hashlib.sha256(out.read_bytes()).hexdigest()==hashlib.sha256(data).hexdigest():return
 out.write_bytes(data);out.chmod(mode)
def prepare():
 require_supported_kernel()
 if os.uname().release!=RELEASE:raise RuntimeError('Kernel changed; rebuild/review required')
 if RUN.exists():
  info=RUN.lstat()
  if not stat.S_ISDIR(info.st_mode) or info.st_uid!=0:raise RuntimeError('Invalid root staging directory')
 else:RUN.mkdir(mode=0o700)
 RUN.chmod(0o700)
 for name,path in [('stock',BASE),('firmware',FW)]:
  fd=os.open(path,os.O_RDONLY|os.O_NOFOLLOW)
  with os.fdopen(fd,'rb') as f:data=f.read()
  if hashlib.sha256(data).hexdigest()!=HASHES[name]:raise RuntimeError(name+' fingerprint changed')
  stage(RUN/(name+'.bin'),data,0o600)
 (RUN/'prepared.json').write_text(json.dumps({'release':RELEASE,'hashes':HASHES,'boot_changes':False,'prepared_utc':time.time()}))
 original=CLIENT.read_bytes()
 if hashlib.sha256(original).hexdigest()!=CLIENT_STOCK_SHA:raise RuntimeError('Client fingerprint changed')
 for name,data in [('client-stock',original),('client-h264',make_client(original,'H.264'))]:
  stage(RUN/(name+'.bin'),data,0o755)
 return {'prepared':True,'files':2,'persistent_changes':False,'rollback':'stock module and firmware fingerprint verified; reboot uses stock boot/module files'}
def rollback():
 client_stock()
 logs=[];node=video_path()
 if not pathlib.Path('/sys/module/iris').exists():logs.append(run(['modprobe','iris']))
 # The original module's probe attaches automatically after reload.
 stock_driver=pathlib.Path('/sys/bus/platform/drivers/qcom-iris')
 if not (node/'driver').exists() and stock_driver.exists():(stock_driver/'bind').write_text(node.name)
 return {'operations':logs,'inventory':inventory()}

def client_h264():
 global client_active,client_codec
 if (video_path()/'driver').resolve().name!='qcom-iris':raise RuntimeError('Matching hardware decoder must be active')
 busy=opens()
 if busy:raise RuntimeError('Stop stream before client trial (decoder held by '+', '.join(sorted({u['comm'] for u in busy}))+')')
 source=RUN/'client-h264.bin'
 if digest(CLIENT)!=CLIENT_STOCK_SHA or digest(source)!=CLIENT_H264_SHA:raise RuntimeError('Client fingerprint changed')
 result=run(['mount','--bind',str(source),str(CLIENT)])
 if result['code']:return result
 client_active=True
 client_codec='H.264'
 if digest(CLIENT)!=CLIENT_H264_SHA:raise RuntimeError('Client bind did not take effect')
 return {'mounted':True,'codec':'H.264','sha256':CLIENT_H264_SHA,'persistent_changes':False}
def bind_mounted(path):
 with open('/proc/self/mountinfo') as f:
  return any(len(line.split())>4 and line.split()[4]==str(path) for line in f)
def adopt():
 """Take over an H.264 client bind mount left by an earlier administrator session (keep mode)."""
 global client_active,client_codec
 if client_active:return {'adopted':True,'already':True}
 require_supported_kernel()
 if not RUN.exists() or RUN.lstat().st_uid!=0 or not (RUN/'prepared.json').exists():raise RuntimeError('No staged session to adopt')
 if digest(CLIENT)!=CLIENT_H264_SHA or not bind_mounted(CLIENT):raise RuntimeError('No H.264 client mount to adopt')
 if digest(RUN/'client-h264.bin')!=CLIENT_H264_SHA or digest(RUN/'client-stock.bin')!=CLIENT_STOCK_SHA:raise RuntimeError('Staged client files changed')
 if (video_path()/'driver').resolve().name!='qcom-iris':raise RuntimeError('Expected stock qcom-iris binding')
 client_active=True;client_codec='H.264'
 return {'adopted':True,'sha256':CLIENT_H264_SHA}
def detach():
 """Let the H.264 client mount outlive this session; a newer session adopts or removes it."""
 global detached
 if not client_active or client_codec!='H.264':raise RuntimeError('Only a mounted H.264 client can outlive its session')
 detached=True
 return {'detached':True}
def client_stock():
 global client_active,client_codec
 if not client_active:return {'already_stock':True}
 # Stop only this patched VRLink executable if orderly stream stop was missed.
 stopped=[]
 for process in pathlib.Path('/proc').glob('[0-9]*'):
  try:
   executable=process/'exe'
   if (process/'comm').read_text().strip() in ('vrlink','vrlinkrunthread') and digest(executable)==CLIENT_H264_SHA:
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
def main():
 if os.geteuid()!=0:raise RuntimeError('Administrator authentication did not succeed')
 actions={'inventory':inventory,'prepare':prepare,'rollback':rollback,'client-h264':client_h264,'client-stock':client_stock,
          'adopt':adopt,'detach':detach}
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
   if detached:break
 finally:
  if client_active and not detached:
   try:rollback()
   except Exception:pass
if __name__=='__main__':main()
