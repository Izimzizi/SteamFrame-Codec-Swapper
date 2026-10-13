"""Pure contract tests; never controls a real Frame or SteamVR process."""
import json,os,pathlib,sys,tempfile,threading,unittest,uuid
from unittest.mock import MagicMock,Mock,patch
import codec_supervisor as service
class CodecProtocolTests(unittest.TestCase):
 def test_supported_modes_and_rates(self):
  for codec in service.MODES:
   for fps in (90,120):service.validate_mode(codec,100,fps)
 def test_h264_high_budget_preserves_av1_bounds(self):
  for fps in (90,120):service.validate_mode('H.264',500,fps)
  for values in (('H.264',501,90),('H.264',500.0,90),('AV1',201,90),('HEVC',501,90)):
   with self.assertRaises(ValueError):service.validate_mode(*values)
 def test_unknown_mode_rate_and_unbounded_bitrate_rejected(self):
  for values in (('VP9',100,90),('AV1',201,90),('AV1',24,90),('AV1',100,91),('AV1',True,90)):
   with self.assertRaises(ValueError):service.validate_mode(*values)
 def test_request_id_and_version(self):
  request={'protocol_version':1,'id':str(uuid.uuid4()),'action':'switch','codec':'H.264','target_mbps':100,'fps':90}
  self.assertEqual(service.validate_request(request),(request['id'],'switch'))
  for field,value in (('id','../escape'),('protocol_version',2),('action','exec')):
   changed=dict(request);changed[field]=value
   with self.assertRaises((ValueError,KeyError)):service.validate_request(changed)
 def test_failed_codec_switch_attempts_stock_recovery(self):
  instance=object.__new__(service.Supervisor)
  instance.pid=0;instance.watch=None;instance.authenticated=False;instance.root=None;instance.config={'initial_codec':'AV1','target_mbps':100,'fps':90}
  instance.stop_heartbeat=threading.Event();instance.restart_on_close=True
  with patch.object(instance,'update'),patch.object(instance,'heartbeat'),patch.object(instance,'authenticate',side_effect=RuntimeError('authentication rejected')):
   with self.assertRaises(RuntimeError):instance.run(['secret-for-test'])
  self.assertTrue(instance.stop_heartbeat.is_set())
 def test_server_lookup_matches_only_this_steamvr_install(self):
  instance=object.__new__(service.Supervisor);instance.vr=pathlib.Path(r'C:\Steam\steamapps\common\SteamVR\bin\win64')
  with patch.object(service,'process_paths',return_value=[]):self.assertEqual(instance.server_pid(),0)
  with patch.object(service,'process_paths',return_value=[(7,r'D:\Other\SteamVR\bin\win64\vrserver.exe'),(9,r'c:\steam\steamapps\common\steamvr\bin\win64\vrserver.exe')]):
   self.assertEqual(instance.server_pid(),9)
 def test_encoder_failure_is_preserved_before_recovery(self):
  instance=object.__new__(service.Supervisor)
  with tempfile.TemporaryDirectory() as directory:
   instance.directory=pathlib.Path(directory)
   instance.check_encoder_errors({'errors':0},'H.264',400,90)
   self.assertFalse((instance.directory/'encoder-failure.json').exists())
   state={'errors':1,'initialize_status':8,'width':1024,'height':4096,
    'source_fps_num':90,'source_fps_den':1,'initialize_error':'unsupported parameters'}
   with self.assertRaisesRegex(RuntimeError,'initialization status 8, source 1024x4096 at 90/1'):
    instance.check_encoder_errors(state,'H.264',400,90)
   saved=json.loads((instance.directory/'encoder-failure.json').read_text())
   self.assertEqual(saved['encoder'],state)
   self.assertEqual((saved['codec'],saved['target_mbps'],saved['fps']),('H.264',400,90))
 def test_atomic_status_retries_transient_windows_reader(self):
  with tempfile.TemporaryDirectory() as directory:
   path=pathlib.Path(directory)/'status.json';path.write_text('{"old":true}')
   replace=service.os.replace
   attempts=0
   def transient_reader(source,target):
    nonlocal attempts
    attempts+=1
    if attempts==1:raise PermissionError()
    return replace(source,target)
   with patch.object(service.os,'replace',side_effect=transient_reader) as call,patch.object(service.time,'sleep'):
    service.atomic_json(path,{'ready':True})
    self.assertEqual(call.call_count,2)
   self.assertEqual(json.loads(path.read_text()),{'ready':True})
   self.assertEqual(list(path.parent.glob('*.tmp')),[])
 def test_atomic_status_failure_preserves_previous_and_cleans_temp(self):
  with tempfile.TemporaryDirectory() as directory:
   path=pathlib.Path(directory)/'status.json';path.write_text('{"old":true}')
   with patch.object(service.os,'replace',side_effect=PermissionError()),patch.object(service.time,'sleep'):
    with self.assertRaises(PermissionError):service.atomic_json(path,{'ready':True})
   self.assertEqual(json.loads(path.read_text()),{'old':True})
   self.assertEqual(list(path.parent.glob('*.tmp')),[])
 def test_heartbeat_survives_failed_status_write(self):
  instance=object.__new__(service.Supervisor)
  instance.stop_heartbeat=Mock();instance.stop_heartbeat.wait.side_effect=[False,False,True]
  with patch.object(instance,'update',side_effect=[PermissionError(),None]) as update:
   instance.heartbeat()
   self.assertEqual(update.call_count,2)
 def test_process_inventory_and_exit_watch_use_windows_handles(self):
  # Real Windows calls against this test process; no shell is started.
  self.assertIn(os.getpid(),[pid for pid,_ in service.process_paths(pathlib.Path(sys.executable).name)])
  watch=service.ProcessWatch(os.getpid())
  try:self.assertTrue(watch.alive())
  finally:watch.close()
  self.assertFalse(watch.alive())

class HostSettingsTests(unittest.TestCase):
 def setUp(self):
  self.temporary=tempfile.TemporaryDirectory();root=pathlib.Path(self.temporary.name)
  (root/'config').mkdir();self.settings=root/'config/steamvr.vrsettings'
  self.user={'targetBandwidth':150.0,'automaticBandwidth':True,'allowMultipleLinks':True,'streamFormatWidth':1280,'enableNAT':True}
  self.settings.write_text(json.dumps({'driver_vrlink':dict(self.user),'steamvr':{'preferredRefreshRate':90}}),encoding='utf-8')
  self.instance=object.__new__(service.Supervisor);self.instance.steam=root
  self.instance.directory=root/'session';self.instance.directory.mkdir();self.instance.host_path=self.instance.directory/'host-original.json'
 def tearDown(self):self.temporary.cleanup()
 def driver(self):return json.loads(self.settings.read_text(encoding='utf-8'))['driver_vrlink']
 def test_codec_session_writes_codec_values_then_hevc_restores_user_settings(self):
  self.instance.load_host_state()
  with patch.object(self.instance,'server_pid',return_value=0):
   self.instance.save_host_settings('H.264',300,120)
   self.assertEqual(self.driver()['streamFormatWidth'],1024);self.assertFalse(self.driver()['automaticBandwidth'])
   self.assertEqual(self.driver()['targetBandwidth'],300.0)
   self.instance.save_host_settings('HEVC',300,120)
  # Keys absent before the session are removed again; unrelated keys are untouched.
  self.assertEqual(self.driver(),self.user)
 def test_av1_keeps_the_users_width_within_its_packed_bound(self):
  self.assertEqual(service.codec_host_values('AV1',100,{'streamFormatWidth':1280})['streamFormatWidth'],1280)
  self.assertEqual(service.codec_host_values('AV1',100,{'streamFormatWidth':2048})['streamFormatWidth'],1536)
  self.assertEqual(service.codec_host_values('AV1',100,{})['streamFormatWidth'],1536)
 def test_interrupted_session_restores_the_originals_it_saved(self):
  self.instance.load_host_state()
  with patch.object(self.instance,'server_pid',return_value=0):self.instance.save_host_settings('H.264',400,90)
  # A new owner after a crash must not treat the leftover H.264 values as the user's settings.
  restarted=object.__new__(service.Supervisor);restarted.steam=self.instance.steam;restarted.directory=self.instance.directory
  restarted.host_path=self.instance.host_path;restarted.load_host_state()
  self.assertEqual(restarted.host_state['original'],service.host_values(self.user))
 def test_user_edits_after_an_interrupted_session_are_kept(self):
  self.instance.load_host_state()
  with patch.object(self.instance,'server_pid',return_value=0):self.instance.save_host_settings('H.264',400,90)
  data=json.loads(self.settings.read_text(encoding='utf-8'));data['driver_vrlink']['targetBandwidth']=200.0
  self.settings.write_text(json.dumps(data),encoding='utf-8')
  restarted=object.__new__(service.Supervisor);restarted.steam=self.instance.steam;restarted.directory=self.instance.directory
  restarted.host_path=self.instance.host_path;restarted.load_host_state()
  self.assertEqual(restarted.host_state['original']['targetBandwidth'],200.0)

class SessionLifecycleTests(unittest.TestCase):
 def make(self):
  instance=object.__new__(service.Supervisor)
  instance.status={'target_mbps':300,'fps':90};instance.pid=0;instance.watch=None
  return instance
 def test_codec_budget_is_not_fixed_so_steamvr_can_lower_it(self):
  instance=self.make();instance.steam=pathlib.Path(r'C:\Steam');instance.backend=pathlib.Path(r'C:\CodecLab\Backend')
  with patch.object(service.subprocess,'Popen'),patch.object(service.subprocess,'run',return_value=MagicMock(returncode=0)) as run,\
       patch.object(instance,'server_pid',return_value=42),patch.object(service,'ProcessWatch'):
   instance.start_server('H.264',300,90)
  self.assertEqual(run.call_args.args[0][3:9],['install','300000000','300000000','0','90','2'])
  self.assertEqual(instance.pid,42)
 def test_restore_after_steamvr_exit_does_not_relaunch_it(self):
  instance=self.make();instance.experimental=True
  inventory={'inventory':{'current_driver':'qcom-iris','client_trial_active':False}}
  with patch.object(instance,'stop_server') as stop,patch.object(instance,'cef'),patch.object(instance,'action',return_value=inventory),\
       patch.object(instance,'save_host_settings') as save,patch.object(instance,'start_server') as start:
   instance.recover(restart=False)
  stop.assert_called_once();save.assert_called_once_with('HEVC',300,90);start.assert_not_called()
  self.assertFalse(instance.experimental)
 def test_restore_without_an_active_experiment_leaves_steamvr_running(self):
  instance=self.make();instance.experimental=False
  inventory={'inventory':{'current_driver':'qcom-iris','client_trial_active':False}}
  with patch.object(instance,'stop_server') as stop,patch.object(instance,'action',return_value=inventory),\
       patch.object(instance,'save_host_settings') as save,patch.object(instance,'start_server') as start:
   instance.recover()
  stop.assert_not_called();save.assert_not_called();start.assert_not_called()
if __name__=='__main__':unittest.main()
