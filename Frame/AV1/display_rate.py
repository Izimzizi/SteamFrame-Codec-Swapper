"""Explicit preferred-rate setter and hardware property readback on PC/Frame."""
import argparse,ctypes as c,json,subprocess
parser=argparse.ArgumentParser();parser.add_argument('--hz',type=int,choices=(90,120));a=parser.parse_args()
SCRIPT=r'''
import ctypes as c,json,time
lib=c.CDLL(__LIBRARY__)
lib.VR_InitInternal2.argtypes=[c.POINTER(c.c_int),c.c_int,c.c_char_p];lib.VR_InitInternal2.restype=c.c_uint32
lib.VR_GetGenericInterface.argtypes=[c.c_char_p,c.POINTER(c.c_int)];lib.VR_GetGenericInterface.restype=c.c_void_p
error=c.c_int();lib.VR_InitInternal2(c.byref(error),3,None)
if error.value:raise RuntimeError('OpenVR rate session '+str(error.value))
def table(name):
 error.value=0;p=lib.VR_GetGenericInterface(name,c.byref(error))
 if not p or error.value:raise RuntimeError('Rate interface unavailable '+name.decode())
 return c.cast(p,c.POINTER(c.c_void_p))
def fn(t,i,ret,*args):return c.CFUNCTYPE(ret,*args)(t[i])
try:
 settings=table(b'FnTable:IVRSettings_003');system=table(b'FnTable:IVRSystem_026')
 read=fn(settings,7,c.c_float,c.c_char_p,c.c_char_p,c.POINTER(c.c_int))
 write=fn(settings,3,None,c.c_char_p,c.c_char_p,c.c_float,c.POINTER(c.c_int))
 result={'before_preference':read(b'steamvr',b'preferredRefreshRate',c.byref(error)),'before_error':error.value}
 if __HZ__:
  error.value=0;write(b'steamvr',b'preferredRefreshRate',__HZ__,c.byref(error))
  if error.value:raise RuntimeError('Display preference rejected '+str(error.value))
  time.sleep(.5)
 result['preferred_hz']=read(b'steamvr',b'preferredRefreshRate',c.byref(error));result['preference_error']=error.value
 property=fn(system,23,c.c_float,c.c_uint32,c.c_int,c.POINTER(c.c_int))
 error.value=0;result['display_hz']=property(0,2002,c.byref(error));result['display_error']=error.value
 print(json.dumps(result))
finally:lib.VR_ShutdownInternal()
'''
local=SCRIPT.replace('__LIBRARY__',repr(r'C:\Program Files (x86)\Steam\steamapps\common\SteamVR\bin\win64\openvr_api.dll')).replace('__HZ__',str(a.hz or 0))
exec(compile(local,'pc-display-rate','exec'))
remote=SCRIPT.replace('__LIBRARY__',repr('/opt/steamvr/bin/linuxarm64/libopenvr_api.so')).replace('__HZ__',str(a.hz or 0))
options=['-i',r'C:\Users\ellio\AppData\Local\steamos-devkit\devkit_rsa','-o','IdentitiesOnly=yes','-o','BatchMode=yes','-o','StrictHostKeyChecking=yes','-o',r'UserKnownHostsFile=C:\Users\ellio\AppData\Local\SteamLinkVRSettings\ssh-known-hosts','-o','ConnectTimeout=3']
p=subprocess.run([r'C:\Windows\System32\OpenSSH\ssh.exe',*options,'steamos@10.35.78.1','python3 -'],input=remote,text=True,capture_output=True,timeout=8)
if p.returncode:raise RuntimeError('Frame rate command failed: '+p.stderr[-300:])
print(p.stdout.strip())
