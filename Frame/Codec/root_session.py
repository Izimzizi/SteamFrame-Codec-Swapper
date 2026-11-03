"""Ephemeral sudo/SSH session that mounts the H.264 Steam Link client; fixed actions, no persistent install.

Password is consumed by sudo before this process starts. This program never
receives, records or reports it. EOF/time limit unmounts the patched client if one is
mounted. Nothing on the boot volume is read or changed; the Steam Link client is patched in a copy that is bind-mounted over it.
"""
import hashlib,json,os,pathlib,select,shutil,signal,stat,struct,subprocess,sys,time

LAB=pathlib.Path('/tmp/steam-frame-av1-lab-20261011')
RUN=pathlib.Path('/run/steam-frame-av1-lab-20261012')
client_active=False
detached=False
client_codec='HEVC'
CLIENT=pathlib.Path('/opt/steamvr/tools/vrlink/bin/linuxarm64/vrlink')
stock_sha=None   # fingerprints of the stock client and its patched copy, learned when the copy is staged or adopted
h264_sha=None
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
def client_users(users):
 """Only the Steam Link client itself (vrlink, vrlinkrunthread) has to be off the decoder before the client file is swapped or the stream is judged;
 another program that happens to hold a decoder or encoder node (a screen recorder on the Frame) does not stop the swap."""
 return [u for u in users if u['comm'].startswith('vrlink')]
def code_segments(data):
 """The executable loadable segments of an AArch64 ELF64 file as (file offset, virtual address, size)."""
 if data[:6]!=b'\x7fELF\x02\x01' or struct.unpack_from('<H',data,18)[0]!=183:raise RuntimeError('Expected AArch64 ELF64 client')
 phoff=struct.unpack_from('<Q',data,32)[0];entry,count=struct.unpack_from('<HH',data,54)
 if entry!=56 or phoff+entry*count>len(data):raise RuntimeError('Invalid ELF program headers')
 segments=[]
 for i in range(count):
  position=phoff+i*entry
  kind,flags=struct.unpack_from('<II',data,position)
  if kind==1 and flags&1:
   offset,va=struct.unpack_from('<QQ',data,position+8);size=struct.unpack_from('<Q',data,position+32)[0]
   if offset+size>len(data):raise RuntimeError('Invalid ELF load range')
   segments.append((offset,va,size))
 return segments
def find_codec_sites(data,fourcc):
 """File offsets of the instruction pairs that load a four-character codec name into one register: a move of its low half, then within eight instructions a move-keep of its high half."""
 low=int.from_bytes(fourcc[:2],'little');high=int.from_bytes(fourcc[2:],'little');sites=[]
 for start,_,size in code_segments(data):
  words=struct.unpack_from('<%dI'%(size//4),data,start)
  for i,word in enumerate(words):
   if word&0xFFE00000!=0x52800000 or (word>>5)&0xFFFF!=low:continue
   register=word&31
   for k in range(1,9):
    if i+k>=len(words):break
    follow=words[i+k]
    if follow&0xFFE00000==0x72A00000 and (follow>>5)&0xFFFF==high and follow&31==register:
     sites.append((start+4*i,start+4*(i+k)));break
 return sites
def client_kind(data):
 """'stock' (exactly one place loads the HEVC name), 'h264' (exactly one loads H264 and none HEVC) or 'unknown'."""
 hevc,h264=find_codec_sites(data,b'HEVC'),find_codec_sites(data,b'H264')
 if len(hevc)==1 and not h264:return 'stock'
 if len(h264)==1 and not hevc:return 'h264'
 return 'unknown'
def inventory():
 data=CLIENT.read_bytes()
 try:
  node=video_path();driver=(node/'driver').resolve().name if (node/'driver').exists() else None;nodes=decoder_nodes()
 except RuntimeError:driver,nodes=None,[]   # an unfamiliar kernel: decoder users cannot be listed, the client patch does not need them
 users=opens() if nodes else []
 return {'uid':os.geteuid(),'release':os.uname().release,'open_decoder_users':client_users(users),'other_decoder_users':[u for u in users if u not in client_users(users)],'decoder_nodes':nodes,
  'current_driver':driver,'client_trial_active':client_active,
  'client_sha256':hashlib.sha256(data).hexdigest(),'client_kind':client_kind(data),'client_codec':client_codec}

def make_client(original,codec):
 """The stock client with its one HEVC name load changed to H264; refuses unless there is exactly one such place."""
 if codec!='H.264':raise RuntimeError('Unknown codec patch')
 if client_kind(original)!='stock':raise RuntimeError('Client layout not recognised: expected exactly one HEVC codec setting')
 (low_at,high_at),=find_codec_sites(original,b'HEVC')
 value=int.from_bytes(b'H264','little');modified=bytearray(original)
 for position,part in ((low_at,value&0xFFFF),(high_at,value>>16)):
  word=struct.unpack_from('<I',original,position)[0]
  struct.pack_into('<I',modified,position,(word&~(0xFFFF<<5))|(part<<5))
 if client_kind(bytes(modified))!='h264':raise RuntimeError('Patched client did not verify')
 return modified
def stage(out,data,mode):
 # A bind-mounted client executes from its staged file, so a matching file is left untouched.
 if out.exists() and hashlib.sha256(out.read_bytes()).hexdigest()==hashlib.sha256(data).hexdigest():return
 out.write_bytes(data);out.chmod(mode)
def prepare():
 global stock_sha,h264_sha
 if RUN.exists():
  info=RUN.lstat()
  if not stat.S_ISDIR(info.st_mode) or info.st_uid!=0:raise RuntimeError('Invalid root staging directory')
 else:RUN.mkdir(mode=0o700)
 RUN.chmod(0o700)
 original=CLIENT.read_bytes()
 patched=bytes(make_client(original,'H.264'))
 for name,data in [('client-stock',original),('client-h264',patched)]:
  stage(RUN/(name+'.bin'),data,0o755)
 stock_sha,h264_sha=hashlib.sha256(original).hexdigest(),hashlib.sha256(patched).hexdigest()
 (RUN/'prepared.json').write_text(json.dumps({'client_stock_sha256':stock_sha,'client_h264_sha256':h264_sha,'boot_changes':False,'prepared_utc':time.time()}))
 return {'prepared':True,'files':2,'persistent_changes':False,'client_stock_sha256':stock_sha,'client_h264_sha256':h264_sha}
def rollback():
 client_stock()
 return {'inventory':inventory()}

def client_h264():
 global client_active,client_codec
 busy=client_users(opens())
 if busy:raise RuntimeError('Stop stream before client trial (decoder held by '+', '.join(sorted({u['comm'] for u in busy}))+')')
 source=RUN/'client-h264.bin'
 if not stock_sha or digest(CLIENT)!=stock_sha or digest(source)!=h264_sha:raise RuntimeError('Client fingerprint changed')
 result=run(['mount','--bind',str(source),str(CLIENT)])
 if result['code']:return result
 client_active=True
 client_codec='H.264'
 if digest(CLIENT)!=h264_sha:raise RuntimeError('Client bind did not take effect')
 return {'mounted':True,'codec':'H.264','sha256':h264_sha,'persistent_changes':False}
def bind_mounted(path):
 with open('/proc/self/mountinfo') as f:
  return any(len(line.split())>4 and line.split()[4]==str(path) for line in f)
def adopt():
 """Take over an H.264 client bind mount left by an earlier administrator session (keep mode)."""
 global client_active,client_codec,stock_sha,h264_sha
 if client_active:return {'adopted':True,'already':True}
 if not RUN.exists() or RUN.lstat().st_uid!=0 or not (RUN/'prepared.json').exists():raise RuntimeError('No staged session to adopt')
 try:recorded=json.loads((RUN/'prepared.json').read_text());stock,h264=recorded['client_stock_sha256'],recorded['client_h264_sha256']
 except (ValueError,KeyError,TypeError):raise RuntimeError('Staged session record unreadable')
 if digest(CLIENT)!=h264 or not bind_mounted(CLIENT):raise RuntimeError('No H.264 client mount to adopt')
 if digest(RUN/'client-h264.bin')!=h264 or digest(RUN/'client-stock.bin')!=stock:raise RuntimeError('Staged client files changed')
 stock_sha,h264_sha=stock,h264
 client_active=True;client_codec='H.264'
 return {'adopted':True,'sha256':h264_sha}
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
   if (process/'comm').read_text().strip() in ('vrlink','vrlinkrunthread') and digest(executable)==h264_sha:
    os.kill(int(process.name),signal.SIGTERM)
    stopped.append(int(process.name))
  except (OSError,ProcessLookupError):pass
 started=time.monotonic()
 while (client_users(opens()) or any(pathlib.Path('/proc',str(pid)).exists() for pid in stopped)) and time.monotonic()-started<5:time.sleep(.1)
 result=run(['umount',str(CLIENT)])
 if result['code']:raise RuntimeError('Temporary client unmount failed')
 client_active=False
 client_codec='HEVC'
 if digest(CLIENT)!=stock_sha:raise RuntimeError('Stock client fingerprint did not return')
 return {'restored':True,'sha256':stock_sha}
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
