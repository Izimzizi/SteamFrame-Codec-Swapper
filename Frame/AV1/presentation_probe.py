"""Paired Frame compositor screenshot and temporary, restored wake lease."""
import argparse,json,pathlib,subprocess
parser=argparse.ArgumentParser();parser.add_argument('--hold',type=int,default=0);options=parser.parse_args()
if options.hold<0 or options.hold>900:raise ValueError('Wake lease must be within 0-900 seconds')

REMOTE=r'''
import ctypes as c,json,pathlib,time
lib=c.CDLL('/opt/steamvr/bin/linuxarm64/libopenvr_api.so')
lib.VR_InitInternal2.argtypes=[c.POINTER(c.c_int),c.c_int,c.c_char_p];lib.VR_InitInternal2.restype=c.c_uint32
lib.VR_GetGenericInterface.argtypes=[c.c_char_p,c.POINTER(c.c_int)];lib.VR_GetGenericInterface.restype=c.c_void_p
error=c.c_int();lib.VR_InitInternal2(c.byref(error),3,None)
if error.value:raise RuntimeError('Frame OpenVR init '+str(error.value))
def table(name):
 error.value=0;p=lib.VR_GetGenericInterface(name,c.byref(error))
 if error.value or not p:raise RuntimeError('OpenVR interface '+name.decode()+' '+str(error.value))
 return c.cast(p,c.POINTER(c.c_void_p))
def fn(t,i,ret,*args):return c.CFUNCTYPE(ret,*args)(t[i])
settings=table(b'FnTable:IVRSettings_003')
getbool=fn(settings,5,c.c_bool,c.c_char_p,c.c_char_p,c.POINTER(c.c_int))
getfloat=fn(settings,7,c.c_float,c.c_char_p,c.c_char_p,c.POINTER(c.c_int))
setbool=fn(settings,1,None,c.c_char_p,c.c_char_p,c.c_bool,c.POINTER(c.c_int))
setfloat=fn(settings,3,None,c.c_char_p,c.c_char_p,c.c_float,c.POINTER(c.c_int))
error.value=0;pause=getbool(b'power',b'pauseCompositorOnStandby',c.byref(error))
if error.value:raise RuntimeError('Read pause '+str(error.value))
timeout=getfloat(b'power',b'turnOffScreensTimeout',c.byref(error))
if error.value:raise RuntimeError('Read timeout '+str(error.value))
result={'original_power':{'pause':pause,'timeout':timeout},'capture_requested':False,'files':[]}
stem='/tmp/steam-frame-av1-lab-20261011/presentation-'+str(int(time.time()))
try:
 setbool(b'power',b'pauseCompositorOnStandby',False,c.byref(error))
 if error.value:raise RuntimeError('Set pause '+str(error.value))
 setfloat(b'power',b'turnOffScreensTimeout',3600,c.byref(error))
 if error.value:raise RuntimeError('Set timeout '+str(error.value))
 print(json.dumps({'wake_lease_active':True,'original_power':result['original_power']}),flush=True)
 until=time.monotonic()+__HOLD_SECONDS__
 while time.monotonic()<until:
  time.sleep(min(.5,max(0,until-time.monotonic())))
  setbool(b'power',b'pauseCompositorOnStandby',False,c.byref(error))
  setfloat(b'power',b'turnOffScreensTimeout',3600,c.byref(error))
 shots=table(b'FnTable:IVRScreenshots_001')
 request=fn(shots,0,c.c_int,c.POINTER(c.c_uint32),c.c_int,c.c_char_p,c.c_char_p)
 handle=c.c_uint32();code=request(c.byref(handle),1,(stem+'-preview').encode(),(stem+'-vr').encode())
 result.update(capture_requested=code==0,screenshot_error=code,screenshot_handle=handle.value)
 for i in range(20):
  files=list(pathlib.Path(stem).parent.glob(pathlib.Path(stem).name+'*.png'))
  if files:result['files']=[str(p) for p in files];break
  time.sleep(.25)
finally:
 setbool(b'power',b'pauseCompositorOnStandby',pause,c.byref(error))
 setfloat(b'power',b'turnOffScreensTimeout',timeout,c.byref(error))
 result['power_restored']=error.value==0
 lib.VR_ShutdownInternal()
print(json.dumps(result))
'''
args=[r'C:\Windows\System32\OpenSSH\ssh.exe','-i',r'C:\Users\ellio\AppData\Local\steamos-devkit\devkit_rsa',
 '-o','IdentitiesOnly=yes','-o','BatchMode=yes','-o','StrictHostKeyChecking=yes','-o',
 r'UserKnownHostsFile=C:\Users\ellio\AppData\Local\SteamLinkVRSettings\ssh-known-hosts',
 '-o','ConnectTimeout=3','steamos@10.35.78.1','python3 -']
hold=options.hold
print(json.dumps({'wake_lease_requested_seconds':hold}),flush=True)
p=subprocess.run(args,input=REMOTE.replace('__HOLD_SECONDS__',str(hold)),text=True,capture_output=True,timeout=hold+15)
if p.returncode:print(json.dumps({'ok':False,'error':p.stderr[-1200:]}));raise SystemExit(1)
print(p.stdout.strip())
