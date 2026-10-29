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

class RefreshRateTests(unittest.TestCase):
 def test_user_rates_and_keep_current_are_accepted(self):
  for codec in service.MODES:
   for fps in (0,72,80,90,120):service.validate_mode(codec,100,fps)
  for fps in (60,91,144,-1,72.0,True):
   with self.assertRaises(ValueError):service.validate_mode('H.264',100,fps)
 def test_current_refresh_reads_the_steamvr_setting(self):
  with tempfile.TemporaryDirectory() as directory:
   root=pathlib.Path(directory);(root/'config').mkdir()
   instance=object.__new__(service.Supervisor);instance.steam=root
   for stored,expected in ((72,72),(72.0,72),(120,120)):
    (root/'config/steamvr.vrsettings').write_text(json.dumps({'steamvr':{'preferredRefreshRate':stored}}))
    self.assertEqual(instance.current_refresh(),expected)
   for stored in (None,60,'72'):
    (root/'config/steamvr.vrsettings').write_text(json.dumps({'steamvr':{'preferredRefreshRate':stored} if stored is not None else {}}))
    with self.assertRaisesRegex(RuntimeError,'refresh rate'):instance.current_refresh()
 def test_keep_current_never_writes_the_refresh_rate(self):
  with tempfile.TemporaryDirectory() as directory:
   root=pathlib.Path(directory);(root/'config').mkdir()
   settings=root/'config/steamvr.vrsettings'
   settings.write_text(json.dumps({'driver_vrlink':{},'steamvr':{'preferredRefreshRate':72}}))
   instance=object.__new__(service.Supervisor);instance.steam=root;instance.directory=root/'session';instance.directory.mkdir()
   instance.host_path=instance.directory/'host-original.json';instance.load_host_state()
   with patch.object(instance,'server_pid',return_value=0):
    instance.save_host_settings('H.264',300,None)
    self.assertEqual(json.loads(settings.read_text())['steamvr']['preferredRefreshRate'],72)
    instance.save_host_settings('HEVC',300,None)
    self.assertEqual(json.loads(settings.read_text())['steamvr']['preferredRefreshRate'],72)
    instance.save_host_settings('H.264',300,90)
    self.assertEqual(json.loads(settings.read_text())['steamvr']['preferredRefreshRate'],90)

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

class KeepModeTests(unittest.TestCase):
 def make(self,applied=('H.264',300,0,False)):
  instance=object.__new__(service.Supervisor)
  instance.keep=True;instance.password='pw';instance.applied=applied;instance.pid=0;instance.watch=None
  instance.status={'target_mbps':300,'fps':72};instance.stopping=False;instance.restart_on_close=True
  instance.root=MagicMock();instance.root.poll.return_value=None;instance.root_started=service.time.monotonic()
  instance.next_attempt=0;instance.phase='';instance.root_replies=MagicMock()
  return instance
 def test_keep_requires_h264_and_resume_requires_keep(self):
  base={'protocol_version':1,'ssh_host':'10.35.78.1','target_mbps':100,'fps':72}
  with tempfile.TemporaryDirectory() as directory:
   for config in (dict(base,initial_codec='AV1',keep=True),dict(base,initial_codec='H.264',resume=True)):
    with self.assertRaises(ValueError):service.Supervisor(pathlib.Path(directory),config)
 def test_authenticate_adopts_a_leftover_h264_mount_and_prepares_a_stock_client(self):
  for client,expected in ((service.CLIENT_H264_SHA,'adopt'),(service.CLIENT_STOCK_SHA,'prepare')):
   instance=self.make()
   inventory={'current_driver':'qcom-iris','client_sha256':client}
   with patch.object(instance,'start_root'),patch.object(instance,'action',side_effect=[inventory,{}]) as action:
    instance.authenticate('pw')
   self.assertEqual(action.call_args_list[1].args[0],expected)
   self.assertEqual(instance.frame_mounted,client==service.CLIENT_H264_SHA)
 def test_authenticate_refuses_an_unknown_or_av1_client(self):
  instance=self.make()
  with patch.object(instance,'start_root'),patch.object(instance,'action',return_value={'current_driver':'qcom-iris','client_sha256':'59231d5b'}):
   with self.assertRaisesRegex(RuntimeError,'Another codec trial'):instance.authenticate('pw')
 def test_steamvr_exit_keeps_h264_and_does_not_restore_the_frame(self):
  instance=self.make();instance.experimental=True;instance.watch=MagicMock();instance.pid=7
  with patch.object(instance,'update') as update,patch.object(instance,'save_host_settings') as save,patch.object(instance,'recover') as recover:
   instance.server_exited()
  recover.assert_not_called();save.assert_called_once_with('H.264',300,None)
  self.assertEqual(instance.pid,0);self.assertFalse(instance.restart_on_close)
  self.assertEqual(update.call_args.kwargs['state'],'waiting')
 def test_a_new_steamvr_triggers_reapply_only_when_h264_is_kept(self):
  for applied,calls in ((('H.264',300,0,False),1),(None,0)):
   instance=self.make(applied)
   with patch.object(instance,'server_pid',return_value=99),patch.object(instance,'reapply') as reapply:
    instance.keep_tick()
   self.assertEqual(reapply.call_count,calls)
 def test_reapply_hooks_before_touching_the_frame_and_uses_the_current_rate(self):
  instance=self.make(('H.264',300,0,True));order=[]
  with patch.object(instance,'update'),patch.object(instance,'current_refresh',return_value=72),\
       patch.object(instance,'start_server',side_effect=lambda *a,**k:order.append(('hook',a,k))),\
       patch.object(instance,'ensure_frame',side_effect=lambda:order.append('frame')),\
       patch.object(instance,'cef',side_effect=lambda w:order.append('cef-'+w)),\
       patch.object(instance,'verify_stream',side_effect=lambda *a:order.append(('verify',a))):
   instance.reapply()
  self.assertEqual(order[0],('hook',('H.264',300,72,True),{'launch':False}))
  self.assertEqual(order[1:3],['frame','cef-start']);self.assertEqual(order[3],('verify',('H.264',300,72)))
  self.assertFalse(instance.stopping)
 def test_failed_reapply_restores_stock_and_ends_keep_mode(self):
  instance=self.make()
  with patch.object(instance,'update'),patch.object(instance,'current_refresh',return_value=72),patch.object(service.time,'sleep'),\
       patch.object(instance,'start_server',side_effect=RuntimeError('no hook')),patch.object(instance,'recover') as recover:
   instance.reapply()
  recover.assert_called_once();self.assertTrue(instance.stopping);self.assertFalse(instance.keep);self.assertIsNone(instance.applied)
  self.assertIn('keep mode ended',instance.closing_message)
 def test_ensure_frame_mounts_the_client_after_a_frame_restart(self):
  instance=self.make();actions=[]
  def action(name):
   actions.append(name)
   return {'inventory':{'client_sha256':service.CLIENT_STOCK_SHA,'current_driver':'qcom-iris'},'client-h264':{'mounted':True}}[name]
  with patch.object(instance,'action',side_effect=action),patch.object(instance,'cef'):
   instance.ensure_frame()
  self.assertEqual(actions,['inventory','client-h264']);self.assertTrue(instance.frame_mounted)
 def test_ensure_frame_leaves_a_mounted_client_alone_and_refuses_unknown_clients(self):
  instance=self.make()
  with patch.object(instance,'action',return_value={'client_sha256':service.CLIENT_H264_SHA,'current_driver':'qcom-iris'}) as action:
   instance.ensure_frame()
  action.assert_called_once_with('inventory')
  with patch.object(instance,'action',return_value={'client_sha256':'other','current_driver':'qcom-iris'}):
   with self.assertRaisesRegex(RuntimeError,'client changed'):instance.ensure_frame()
 def test_renewal_hands_the_mount_to_a_new_session_then_detaches_the_old_one(self):
  instance=self.make();old=instance.root;calls=[]
  fresh=MagicMock()
  def start_root(password):instance.root=fresh;instance.root_started=123.0
  def action(name):
   calls.append(name);return {'client_sha256':service.CLIENT_H264_SHA} if name=='inventory' else {}
  with patch.object(instance,'start_root',side_effect=start_root),patch.object(instance,'action',side_effect=action):
   instance.swap_root()
  self.assertEqual(calls,['inventory','adopt']);self.assertIs(instance.root,fresh)
  self.assertIn('detach',old.stdin.write.call_args.args[0]);old.wait.assert_called()
 def test_failed_renewal_keeps_the_old_session(self):
  instance=self.make();old=instance.root;started=instance.root_started
  fresh=MagicMock()
  def start_root(password):instance.root=fresh
  with patch.object(instance,'start_root',side_effect=start_root),patch.object(instance,'action',side_effect=RuntimeError('rejected')):
   with self.assertRaises(RuntimeError):instance.swap_root()
  self.assertIs(instance.root,old);self.assertEqual(instance.root_started,started);fresh.stdin.close.assert_called()
  old.stdin.write.assert_not_called()
 def test_a_lost_frame_is_reconnected_and_a_hooked_stream_is_restarted(self):
  instance=self.make();instance.root=None;instance.pid=5
  with patch.object(instance,'authenticate') as authenticate,patch.object(instance,'server_alive',return_value=True),patch.object(instance,'restream') as restream:
   instance.keep_tick()
  authenticate.assert_called_once_with('pw');restream.assert_called_once()
  failing=self.make();failing.root=None
  with patch.object(failing,'authenticate',side_effect=RuntimeError('down')),patch.object(failing,'restream') as restream:
   failing.keep_tick()
   failing.keep_tick()  # rate limited: no second attempt inside the retry interval
  restream.assert_not_called();self.assertIsNone(failing.root)

class SessionLifecycleTests(unittest.TestCase):
 def make(self):
  instance=object.__new__(service.Supervisor)
  instance.status={'target_mbps':300,'fps':90};instance.pid=0;instance.watch=None
  return instance
 def install_arguments(self,codec,target,fixed):
  instance=self.make();instance.steam=pathlib.Path(r'C:\Steam');instance.backend=pathlib.Path(r'C:\CodecLab\Backend')
  with patch.object(service.subprocess,'Popen'),patch.object(service.subprocess,'run',return_value=MagicMock(returncode=0)) as run,\
       patch.object(instance,'server_pid',return_value=42),patch.object(service,'ProcessWatch'):
   instance.start_server(codec,target,90,fixed)
  self.assertEqual(instance.pid,42)
  return run.call_args.args[0][3:9],instance
 def test_h264_hook_is_installed_uncapped_so_steamvr_drives_the_bitrate_live(self):
  arguments,instance=self.install_arguments('H.264',300,False)
  self.assertEqual(arguments,['install','500000000','500000000','0','90','2']);self.assertTrue(instance.live_rate)
 def test_fixed_and_av1_hooks_keep_the_requested_budget(self):
  arguments,instance=self.install_arguments('H.264',300,True)
  self.assertEqual(arguments,['install','300000000','300000000','1','90','2']);self.assertFalse(instance.live_rate)
  arguments,instance=self.install_arguments('AV1',150,False)
  self.assertEqual(arguments,['install','150000000','150000000','0','90','1']);self.assertFalse(instance.live_rate)
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
class FrameAddressTests(unittest.TestCase):
 def test_hostnames_and_ipv4_addresses_are_accepted(self):
  for host in ('frame','10.35.78.1','192.168.1.20','steam-frame.local','Frame2'):self.assertTrue(service.valid_host(host),host)
 def test_anything_ssh_could_read_as_an_option_or_a_port_is_refused(self):
  for host in ('','-oProxyCommand=calc','a b','frame:22','fe80::1','frame/../x','user@frame','frame;ls','a..b','.frame','frame.',None,22,'x'*300):
   self.assertFalse(service.valid_host(host),repr(host))
 def test_the_supervisor_logs_in_to_whatever_validated_address_it_was_given(self):
  with tempfile.TemporaryDirectory() as directory:
   base={'protocol_version':1,'target_mbps':100,'fps':72,'initial_codec':'H.264'}
   for host in ('-oProxyCommand=calc','host name'):
    with self.assertRaises(ValueError):service.Supervisor(pathlib.Path(directory),dict(base,ssh_host=host))

class LiveRateTests(unittest.TestCase):
 def make(self,pid=7,live=True,applied=('H.264',300,0,False),keep=True,fixed=False,codec='H.264'):
  instance=object.__new__(service.Supervisor)
  instance.keep=keep;instance.applied=applied;instance.pid=pid;instance.live_rate=live;instance.watch=MagicMock()
  instance.status={'target_mbps':300,'fps':72,'fixed_bitrate':fixed,'active_codec':codec}
  original={'targetBandwidth':100.0,'recommendedBandwidthMbit':100,'automaticBandwidth':True}
  instance.directory=pathlib.Path(tempfile.mkdtemp());instance.host_path=instance.directory/'host-original.json'
  instance.host_state={'protocol_version':1,'original':original,'applied':service.codec_host_values('H.264',300,original)}
  return instance
 def test_rate_is_available_only_when_a_restart_is_not_needed(self):
  with patch.object(service.Supervisor,'server_alive',return_value=True):
   self.assertTrue(self.make().rate_available())
   self.assertFalse(self.make(live=False).rate_available())          # hook installed with a fixed budget
   self.assertFalse(self.make(fixed=True).rate_available())
   self.assertFalse(self.make(codec='HEVC').rate_available())
  with patch.object(service.Supervisor,'server_alive',return_value=False):
   self.assertFalse(self.make().rate_available())                    # hooked SteamVR went away
  self.assertTrue(self.make(pid=0).rate_available())                 # kept session waiting for SteamVR
  self.assertFalse(self.make(pid=0,keep=False,applied=None).rate_available())
  self.assertFalse(self.make(pid=0,applied=('H.264',300,0,True)).rate_available())
 def test_running_steamvr_gets_new_values_recorded_without_touching_the_settings_file(self):
  instance=self.make();before=dict(instance.host_state['applied'])
  with patch.object(service.Supervisor,'server_alive',return_value=True),patch.object(instance,'update') as update,patch.object(instance,'save_host_settings') as save:
   instance.set_rate(450,'op')
  save.assert_not_called()
  self.assertEqual(instance.host_state['applied']['targetBandwidth'],450.0);self.assertEqual(instance.host_state['applied']['recommendedBandwidthMbit'],450)
  self.assertEqual(instance.host_state['previous'],before);self.assertEqual(instance.applied,('H.264',450,0,False))
  self.assertEqual(json.loads(instance.host_path.read_text())['applied']['targetBandwidth'],450.0)
  self.assertEqual(update.call_args.kwargs,{'target_mbps':450,'operation_id':'op','last_result':{'ok':True}})
 def test_closed_steamvr_gets_the_values_written_for_the_next_start(self):
  instance=self.make(pid=0,applied=('H.264',300,72,False))
  with patch.object(instance,'update'),patch.object(instance,'save_host_settings') as save:
   instance.set_rate(250,'op')
  save.assert_called_once_with('H.264',250,72);self.assertEqual(instance.applied,('H.264',250,72,False))
  keep_rate=self.make(pid=0)
  with patch.object(keep_rate,'update'),patch.object(keep_rate,'save_host_settings') as save:
   keep_rate.set_rate(250,'op')
  save.assert_called_once_with('H.264',250,None)
 def test_unavailable_or_invalid_rate_changes_are_refused_without_state_changes(self):
  fixed=self.make(fixed=True)
  with patch.object(service.Supervisor,'server_alive',return_value=True),patch.object(fixed,'update') as update:
   with self.assertRaisesRegex(RuntimeError,'not available'):fixed.set_rate(400)
   for bad in (24,501,300.0,True,'300'):
    with self.assertRaises(ValueError):self.make().set_rate(bad)
  update.assert_not_called();self.assertEqual(fixed.applied,('H.264',300,0,False))
 def test_original_is_still_recognised_after_a_crash_between_recording_and_the_live_write(self):
  original={'targetBandwidth':100.0,'recommendedBandwidthMbit':100}
  old=service.codec_host_values('H.264',300,original);new=service.codec_host_values('H.264',450,original)
  saved={'protocol_version':1,'original':original,'applied':new,'previous':old}
  self.assertEqual(service.host_original(old,saved),original)    # file still holds the pre-change values
  self.assertEqual(service.host_original(new,saved),original)    # file already holds the new ones
  other=dict(old,targetBandwidth=321.0)
  self.assertEqual(service.host_original(other,saved),other)     # unrelated values are the user's own
 def test_rate_requests_are_validated(self):
  request={'protocol_version':1,'id':str(uuid.uuid4()),'action':'rate','target_mbps':350}
  self.assertEqual(service.validate_request(request),(request['id'],'rate'))
  for target in (24,501,350.0,None,'350'):
   with self.assertRaises(ValueError):service.validate_request(dict(request,target_mbps=target))
if __name__=='__main__':unittest.main()
