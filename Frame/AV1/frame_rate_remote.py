"""Template sent through paired SSH; selects and verifies Frame display rate."""
import ctypes as c,json,time
fps=__FPS__
if fps not in (72,80,90,120):raise ValueError('Unsupported refresh rate')
lib=c.CDLL('/opt/steamvr/bin/linuxarm64/libopenvr_api.so')
lib.VR_InitInternal2.argtypes=[c.POINTER(c.c_int),c.c_int,c.c_char_p];lib.VR_InitInternal2.restype=c.c_uint32
lib.VR_GetGenericInterface.argtypes=[c.c_char_p,c.POINTER(c.c_int)];lib.VR_GetGenericInterface.restype=c.c_void_p
error=c.c_int();lib.VR_InitInternal2(c.byref(error),3,None)
if error.value:raise RuntimeError('Frame display session unavailable')
def table(name):
 error.value=0;p=lib.VR_GetGenericInterface(name,c.byref(error))
 if not p or error.value:raise RuntimeError('Frame display interface unavailable')
 return c.cast(p,c.POINTER(c.c_void_p))
try:
 settings=table(b'FnTable:IVRSettings_003');system=table(b'FnTable:IVRSystem_026')
 write=c.CFUNCTYPE(None,c.c_char_p,c.c_char_p,c.c_float,c.POINTER(c.c_int))(settings[3])
 read=c.CFUNCTYPE(c.c_float,c.c_uint32,c.c_int,c.POINTER(c.c_int))(system[23])
 write(b'steamvr',b'preferredRefreshRate',fps,c.byref(error))
 if error.value:raise RuntimeError('Frame display preference rejected')
 rate=0
 for i in range(20):
  time.sleep(.1);error.value=0;rate=read(0,2002,c.byref(error))
  if not error.value and abs(rate-fps)<.1:break
 print(json.dumps({'display_hz':rate,'error':error.value}))
finally:lib.VR_ShutdownInternal()
