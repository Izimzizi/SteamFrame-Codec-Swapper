"""Pure contract tests; never controls a real Frame or SteamVR process."""
import contextlib,json,os,pathlib,sys,tempfile,threading,unittest,uuid
from unittest.mock import MagicMock,Mock,patch
import codec_supervisor as service
class CodecProtocolTests(unittest.TestCase):
 def test_bitrate_bounds(self):
  for target in (25,300,500):service.validate_target(target)
  for target in (24,501,500.0,True,'300',None):
   with self.assertRaises(ValueError):service.validate_target(target)
 def test_request_id_and_version(self):
  request={'protocol_version':1,'id':str(uuid.uuid4()),'action':'rate','target_mbps':100}
  self.assertEqual(service.validate_request(request),(request['id'],'rate'))
  self.assertEqual(service.validate_request(dict(request,action='close')),(request['id'],'close'))
  for field,value in (('id','../escape'),('protocol_version',2),('action','exec'),('action','switch')):
   changed=dict(request);changed[field]=value
   with self.assertRaises((ValueError,KeyError)):service.validate_request(changed)
 def test_encoder_options_are_bounded(self):
  service.validate_encoder_options('vbr',2);service.validate_encoder_options('cbr',7)
  for values in (('abr',2),('vbr',0),('vbr',8),('vbr',2.0),('vbr',True),(None,2)):
   with self.assertRaises(ValueError):service.validate_encoder_options(*values)
 def test_failed_start_attempts_stock_recovery(self):
  instance=object.__new__(service.Supervisor)
  instance.pid=0;instance.watch=None;instance.authenticated=False;instance.root=None;instance.tray=None;instance.config={'target_mbps':100}
  instance.stop_heartbeat=threading.Event();instance.restart_on_close=True
  with patch.object(instance,'update'),patch.object(instance,'heartbeat'),patch.object(instance,'authenticate',side_effect=RuntimeError('authentication rejected')):
   with self.assertRaises(RuntimeError):instance.run(['secret-for-test'])
  self.assertTrue(instance.stop_heartbeat.is_set());self.assertIsNone(instance.password)
 def test_server_lookup_matches_only_this_steamvr_install(self):
  instance=object.__new__(service.Supervisor);instance.vr=pathlib.Path(r'C:\Steam\steamapps\common\SteamVR\bin\win64')
  with patch.object(service,'process_paths',return_value=[]):self.assertEqual(instance.server_pid(),0)
  with patch.object(service,'process_paths',return_value=[(7,r'D:\Other\SteamVR\bin\win64\vrserver.exe'),(9,r'c:\steam\steamapps\common\steamvr\bin\win64\vrserver.exe')]):
   self.assertEqual(instance.server_pid(),9)
 def test_encoder_failure_is_preserved_before_recovery(self):
  instance=object.__new__(service.Supervisor);instance.rate_control='vbr';instance.preset=2;instance.vbv=1
  with tempfile.TemporaryDirectory() as directory:
   instance.directory=pathlib.Path(directory)
   instance.check_encoder_errors({'errors':0},400,90)
   self.assertFalse((instance.directory/'encoder-failure.json').exists())
   state={'errors':1,'initialize_status':8,'width':1024,'height':4096,
    'source_fps_num':90,'source_fps_den':1,'initialize_error':'unsupported parameters'}
   with self.assertRaisesRegex(RuntimeError,'initialization status 8, source 1024x4096 at 90/1'):
    instance.check_encoder_errors(state,400,90)
   saved=json.loads((instance.directory/'encoder-failure.json').read_text())
   self.assertEqual(saved['encoder'],state)
   self.assertEqual((saved['codec'],saved['target_mbps'],saved['fps'],saved['rate_control'],saved['preset']),('H.264',400,90,'vbr',2))
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
 def test_process_inventory_and_exit_watch_use_windows_handles(self):
  # Real Windows calls against this test process; no shell is started.
  self.assertIn(os.getpid(),[pid for pid,_ in service.process_paths(pathlib.Path(sys.executable).name)])
  watch=service.ProcessWatch(os.getpid())
  try:self.assertTrue(watch.alive())
  finally:watch.close()
  self.assertFalse(watch.alive())

class RefreshRateTests(unittest.TestCase):
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
 def test_the_session_never_writes_the_refresh_rate(self):
  with tempfile.TemporaryDirectory() as directory:
   root=pathlib.Path(directory);(root/'config').mkdir()
   settings=root/'config/steamvr.vrsettings'
   settings.write_text(json.dumps({'driver_vrlink':{},'steamvr':{'preferredRefreshRate':72}}))
   instance=object.__new__(service.Supervisor);instance.steam=root;instance.directory=root/'session';instance.directory.mkdir()
   instance.host_path=instance.directory/'host-original.json';instance.load_host_state()
   with patch.object(instance,'server_pid',return_value=0):
    instance.save_host_settings(300)
    self.assertEqual(json.loads(settings.read_text())['steamvr']['preferredRefreshRate'],72)
    instance.save_host_settings(None)
    self.assertEqual(json.loads(settings.read_text())['steamvr']['preferredRefreshRate'],72)

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
 def test_session_writes_only_the_bitrate_and_width_then_restores_user_settings(self):
  self.instance.load_host_state()
  with patch.object(self.instance,'server_pid',return_value=0):
   self.instance.save_host_settings(300)
   driver=self.driver()
   self.assertEqual((driver['streamFormatWidth'],driver['automaticStreamFormatWidth'],driver['targetBandwidth'],driver['recommendedBandwidthMbit']),(1024,False,300.0,300))
   # Automatic bandwidth and multi-link belong to the user: never written.
   self.assertTrue(driver['automaticBandwidth']);self.assertTrue(driver['allowMultipleLinks'])
   self.instance.save_host_settings(None)
  # Keys absent before the session are removed again; unrelated keys are untouched.
  self.assertEqual(self.driver(),self.user)
 def test_the_session_never_names_multi_link_or_automatic_bandwidth(self):
  self.assertEqual(set(service.codec_host_values(300)),{'targetBandwidth','recommendedBandwidthMbit','automaticStreamFormatWidth','streamFormatWidth'})
  self.assertNotIn('allowMultipleLinks',service.HOST_KEYS);self.assertNotIn('automaticBandwidth',service.HOST_KEYS)
 def test_a_user_who_turned_multi_link_off_during_a_session_keeps_it_off(self):
  self.instance.load_host_state()
  with patch.object(self.instance,'server_pid',return_value=0):
   self.instance.save_host_settings(300)
   data=json.loads(self.settings.read_text(encoding='utf-8'));data['driver_vrlink']['allowMultipleLinks']=False
   self.settings.write_text(json.dumps(data),encoding='utf-8')
   self.instance.save_host_settings(None)
  self.assertFalse(self.driver()['allowMultipleLinks'])
 def test_interrupted_session_restores_the_originals_it_saved(self):
  self.instance.load_host_state()
  with patch.object(self.instance,'server_pid',return_value=0):self.instance.save_host_settings(400)
  # A new owner after a crash must not treat the leftover H.264 values as the user's settings.
  restarted=object.__new__(service.Supervisor);restarted.steam=self.instance.steam;restarted.directory=self.instance.directory
  restarted.host_path=self.instance.host_path;restarted.load_host_state()
  self.assertEqual(restarted.host_state['original'],service.host_values(self.user))
 def test_user_edits_after_an_interrupted_session_are_kept(self):
  self.instance.load_host_state()
  with patch.object(self.instance,'server_pid',return_value=0):self.instance.save_host_settings(400)
  data=json.loads(self.settings.read_text(encoding='utf-8'));data['driver_vrlink']['targetBandwidth']=200.0
  self.settings.write_text(json.dumps(data),encoding='utf-8')
  restarted=object.__new__(service.Supervisor);restarted.steam=self.instance.steam;restarted.directory=self.instance.directory
  restarted.host_path=self.instance.host_path;restarted.load_host_state()
  self.assertEqual(restarted.host_state['original']['targetBandwidth'],200.0)
 def test_automatic_bandwidth_is_reported_not_changed(self):
  self.instance.automatic_cache=(0.0,False)
  self.assertTrue(self.instance.automatic_bandwidth())
  data=json.loads(self.settings.read_text(encoding='utf-8'));data['driver_vrlink']['automaticBandwidth']=False
  self.settings.write_text(json.dumps(data),encoding='utf-8')
  self.instance.automatic_cache=(0.0,True)
  self.assertFalse(self.instance.automatic_bandwidth())

class KeepModeTests(unittest.TestCase):
 def make(self,applied=300):
  instance=object.__new__(service.Supervisor)
  instance.password='pw';instance.applied=applied;instance.pid=0;instance.watch=None
  instance.status={'target_mbps':300,'fps':72};instance.stopping=False;instance.restart_on_close=True
  instance.root=MagicMock();instance.root.poll.return_value=None;instance.root_started=service.time.monotonic()
  instance.next_attempt=0;instance.phase='';instance.root_replies=MagicMock()
  instance.rate_control='vbr';instance.preset=2;instance.vbv=1;instance.tray=None;instance.tray_checked=0.0
  return instance
 def test_resume_requires_an_in_range_target_and_valid_encoder_options(self):
  base={'protocol_version':1,'ssh_host':'10.35.78.1','target_mbps':100}
  with tempfile.TemporaryDirectory() as directory:
   for config in (dict(base,target_mbps=501),dict(base,rate_control='abr'),dict(base,preset=9)):
    with self.assertRaises(ValueError):service.Supervisor(pathlib.Path(directory),config)
 def test_authenticate_adopts_a_leftover_h264_mount_and_prepares_a_stock_client(self):
  for client,expected in ((service.CLIENT_H264_SHA,'adopt'),(service.CLIENT_STOCK_SHA,'prepare')):
   instance=self.make()
   inventory={'current_driver':'qcom-iris','client_sha256':client}
   with patch.object(instance,'start_root'),patch.object(instance,'action',side_effect=[inventory,{}]) as action:
    instance.authenticate('pw')
   self.assertEqual(action.call_args_list[1].args[0],expected)
   self.assertEqual(instance.frame_mounted,client==service.CLIENT_H264_SHA)
 def test_authenticate_refuses_an_unknown_client(self):
  instance=self.make()
  with patch.object(instance,'start_root'),patch.object(instance,'action',return_value={'current_driver':'qcom-iris','client_sha256':'59231d5b'}):
   with self.assertRaisesRegex(RuntimeError,'Another codec trial'):instance.authenticate('pw')
 def test_steamvr_exit_keeps_h264_and_does_not_restore_the_frame(self):
  instance=self.make();instance.experimental=True;instance.watch=MagicMock();instance.pid=7
  with patch.object(instance,'update') as update,patch.object(instance,'save_host_settings') as save,patch.object(instance,'recover') as recover:
   instance.server_exited()
  recover.assert_not_called();save.assert_called_once_with(300)
  self.assertEqual(instance.pid,0);self.assertFalse(instance.restart_on_close)
  self.assertEqual(update.call_args.kwargs['state'],'waiting')
 def test_a_new_steamvr_triggers_reapply_only_when_h264_is_applied(self):
  for applied,calls in ((300,1),(None,0)):
   instance=self.make(applied)
   with patch.object(instance,'server_pid',return_value=99),patch.object(instance,'reapply') as reapply:
    instance.keep_tick()
   self.assertEqual(reapply.call_count,calls)
 def test_reapply_hooks_before_touching_the_frame_and_uses_the_current_rate(self):
  instance=self.make(300);order=[]
  with patch.object(instance,'update'),patch.object(instance,'current_refresh',return_value=72),\
       patch.object(instance,'start_server',side_effect=lambda *a,**k:order.append(('hook',a,k))),\
       patch.object(instance,'ensure_frame',side_effect=lambda:order.append('frame')),\
       patch.object(instance,'cef',side_effect=lambda w:order.append('cef-'+w)),\
       patch.object(instance,'verify_stream',side_effect=lambda *a:order.append(('verify',a))):
   instance.reapply()
  self.assertEqual(order[0],('hook',(72,),{'launch':False}))
  self.assertEqual(order[1:3],['frame','cef-start']);self.assertEqual(order[3],('verify',(300,72)))
  self.assertFalse(instance.stopping)
 def test_failed_reapply_restores_stock_and_ends_the_session(self):
  instance=self.make();instance.directory=pathlib.Path(tempfile.mkdtemp())
  with patch.object(instance,'update'),patch.object(instance,'current_refresh',return_value=72),patch.object(service.time,'sleep'),       patch.object(instance,'start_server',side_effect=RuntimeError('no hook')) as start,patch.object(instance,'recover') as recover:
   instance.reapply()
  recover.assert_called_once();self.assertTrue(instance.stopping);self.assertIsNone(instance.applied)
  self.assertEqual(start.call_count,3)  # SteamVR may still be starting, so install is tried again before giving up
  self.assertIn('session ended',instance.closing_message);self.assertIn('no hook',instance.closing_message)
  self.assertEqual(json.loads((instance.directory/'reapply-failure.json').read_text())['error'],'no hook')
 def test_ensure_frame_mounts_the_client_after_a_frame_restart(self):
  instance=self.make();actions=[]
  def action(name):
   actions.append(name)
   return {'inventory':{'client_sha256':service.CLIENT_STOCK_SHA,'current_driver':'qcom-iris'},'client-h264':{'mounted':True}}[name]
  with patch.object(instance,'action',side_effect=action),patch.object(instance,'cef'):
   instance.ensure_frame()
  self.assertEqual(actions,['inventory','inventory','client-h264']);self.assertTrue(instance.frame_mounted)
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
 def test_arming_with_steamvr_closed_waits_instead_of_launching_it(self):
  instance=self.make(None);instance.config={'target_mbps':350}
  with patch.object(instance,'server_pid',return_value=0),patch.object(instance,'save_host_settings') as save,patch.object(instance,'update') as update,\
       patch.object(instance,'switch') as switch,patch.object(instance,'start_server') as start:
   instance.frame_mounted=False;instance.resume_session()
  save.assert_called_once_with(350);switch.assert_not_called();start.assert_not_called()
  self.assertEqual(update.call_args.kwargs['state'],'waiting');self.assertEqual(instance.applied,350)

 def test_steamvr_started_while_arming_is_restarted_once_so_it_reads_the_settings(self):
  instance=self.make(None);instance.config={'target_mbps':350}
  with patch.object(instance,'server_pid',return_value=77),patch.object(instance,'save_host_settings') as save,       patch.object(instance,'switch') as switch:
   instance.frame_mounted=False;instance.resume_session()
  switch.assert_called_once_with(350);save.assert_not_called()

class SessionLifecycleTests(unittest.TestCase):
 def make(self):
  instance=object.__new__(service.Supervisor)
  instance.status={'target_mbps':300,'fps':90};instance.pid=0;instance.watch=None
  instance.rate_control='vbr';instance.preset=2;instance.vbv=1
  return instance
 def install_arguments(self,rate_control='vbr',preset=2,vbv=1):
  instance=self.make();instance.rate_control=rate_control;instance.preset=preset;instance.vbv=vbv
  instance.steam=pathlib.Path(r'C:\Steam');instance.backend=pathlib.Path(r'C:\CodecLab\Backend')
  with patch.object(service.subprocess,'Popen'),patch.object(service.subprocess,'run',return_value=MagicMock(returncode=0)) as run,\
       patch.object(instance,'server_pid',return_value=42),patch.object(service,'ProcessWatch'):
   instance.start_server(90)
  self.assertEqual(instance.pid,42)
  return run.call_args.args[0][3:],instance
 def test_the_hook_is_installed_uncapped_so_steamvr_drives_the_bitrate_live(self):
  arguments,instance=self.install_arguments()
  self.assertEqual(arguments,['install','500000000','500000000','0','90','2','vbr','2','1']);self.assertTrue(instance.live_rate)
 def test_encoder_experiment_options_reach_the_hook(self):
  arguments,_=self.install_arguments('cbr',4,3)
  self.assertEqual(arguments[-3:],['cbr','4','3'])
 def test_restore_after_steamvr_exit_does_not_relaunch_it(self):
  instance=self.make();instance.experimental=True
  inventory={'inventory':{'current_driver':'qcom-iris','client_trial_active':False}}
  with patch.object(instance,'stop_server') as stop,patch.object(instance,'cef'),patch.object(instance,'action',return_value=inventory),\
       patch.object(instance,'save_host_settings') as save,patch.object(instance,'start_server') as start:
   instance.recover(restart=False)
  stop.assert_called_once();save.assert_called_once_with(None);start.assert_not_called()
  self.assertFalse(instance.experimental)
 def test_restore_without_an_active_experiment_leaves_steamvr_running(self):
  instance=self.make();instance.experimental=False
  inventory={'inventory':{'current_driver':'qcom-iris','client_trial_active':False}}
  with patch.object(instance,'stop_server') as stop,patch.object(instance,'action',return_value=inventory),\
       patch.object(instance,'save_host_settings') as save,patch.object(instance,'start_server') as start:
   instance.recover()
  stop.assert_not_called();save.assert_not_called();start.assert_not_called()

class ExitTests(unittest.TestCase):
 """The service never outlives the tray's job: it ends when the tray does."""
 def make(self):
  instance=object.__new__(service.Supervisor)
  instance.tray=MagicMock();instance.tray_checked=0.0;instance.password='pw'
  return instance
 def test_tray_exit_is_detected_within_the_check_interval(self):
  instance=self.make();instance.tray.alive.return_value=False
  with patch.object(service.time,'monotonic',return_value=100.0):self.assertTrue(instance.tray_gone())
  instance.tray.alive.reset_mock()
  with patch.object(service.time,'monotonic',return_value=101.0):self.assertFalse(instance.tray_gone())   # too soon to look again
  instance.tray.alive.assert_not_called()
  with patch.object(service.time,'monotonic',return_value=106.0):self.assertTrue(instance.tray_gone())
 def test_a_running_tray_or_no_tray_never_ends_the_session(self):
  instance=self.make();instance.tray.alive.return_value=True
  with patch.object(service.time,'monotonic',return_value=100.0):self.assertFalse(instance.tray_gone())
  instance.tray=None
  self.assertFalse(instance.tray_gone())
 def test_an_unreachable_frame_is_retried_for_a_bounded_time_then_given_up(self):
  instance=self.make();instance.root=None
  clock=iter([0.0,1.0,2.0,service.RESTORE_RETRY_SECONDS+1])
  with patch.object(instance,'update'),patch.object(instance,'start_root',side_effect=RuntimeError('down')) as start,\
       patch.object(service.time,'sleep'),patch.object(service.time,'monotonic',side_effect=lambda:next(clock)):
   instance.retry_frame_restore()
  self.assertEqual(start.call_count,2)
 def test_the_frame_returning_gets_its_stock_client_back(self):
  instance=self.make();instance.root=None
  with patch.object(instance,'update'),patch.object(instance,'start_root'),patch.object(instance,'action') as action,patch.object(service.time,'sleep'):
   instance.retry_frame_restore()
  action.assert_called_once_with('rollback');self.assertTrue(instance.authenticated)
 def test_no_password_means_no_retry(self):
  instance=self.make();instance.password=None
  with patch.object(instance,'start_root') as start:instance.retry_frame_restore()
  start.assert_not_called()
 def test_a_close_request_restores_and_a_bad_request_file_is_dropped(self):
  with tempfile.TemporaryDirectory() as directory:
   instance=self.make();instance.requests=pathlib.Path(directory);instance.stopping=False;instance.restart_on_close=True
   identifier=str(uuid.uuid4())
   (instance.requests/('request-'+identifier+'.json')).write_text(json.dumps({'protocol_version':1,'id':identifier,'action':'close'}))
   (instance.requests/'request-!bad.json').write_text('not json')
   with patch.object(instance,'server_pid',return_value=0),patch.object(instance,'update'):
    instance.handle_requests()
   self.assertTrue(instance.stopping);self.assertFalse(instance.restart_on_close)
   self.assertEqual(list(instance.requests.iterdir()),[])

class FrameAddressTests(unittest.TestCase):
 def test_hostnames_and_ipv4_addresses_are_accepted(self):
  for host in ('frame','10.35.78.1','192.168.1.20','steam-frame.local','Frame2'):self.assertTrue(service.valid_host(host),host)
 def test_anything_ssh_could_read_as_an_option_or_a_port_is_refused(self):
  for host in ('','-oProxyCommand=calc','a b','frame:22','fe80::1','frame/../x','user@frame','frame;ls','a..b','.frame','frame.',None,22,'x'*300):
   self.assertFalse(service.valid_host(host),repr(host))
 def test_the_supervisor_logs_in_to_whatever_validated_address_it_was_given(self):
  with tempfile.TemporaryDirectory() as directory:
   base={'protocol_version':1,'target_mbps':100}
   for host in ('-oProxyCommand=calc','host name'):
    with self.assertRaises(ValueError):service.Supervisor(pathlib.Path(directory),dict(base,ssh_host=host))

class LiveRateTests(unittest.TestCase):
 def make(self,pid=7,live=True,applied=300,codec='H.264'):
  instance=object.__new__(service.Supervisor)
  instance.applied=applied;instance.pid=pid;instance.live_rate=live;instance.watch=MagicMock()
  instance.status={'target_mbps':300,'fps':72,'active_codec':codec}
  original={'targetBandwidth':100.0,'recommendedBandwidthMbit':100,'automaticBandwidth':True}
  instance.directory=pathlib.Path(tempfile.mkdtemp());instance.host_path=instance.directory/'host-original.json'
  instance.host_state={'protocol_version':1,'original':original,'applied':service.codec_host_values(300)}
  return instance
 def test_rate_is_available_only_when_a_restart_is_not_needed(self):
  with patch.object(service.Supervisor,'server_alive',return_value=True):
   self.assertTrue(self.make().rate_available())
   self.assertFalse(self.make(live=False).rate_available())          # hook installed with a fixed budget
   self.assertFalse(self.make(codec='HEVC').rate_available())
  with patch.object(service.Supervisor,'server_alive',return_value=False):
   self.assertFalse(self.make().rate_available())                    # hooked SteamVR went away
  self.assertTrue(self.make(pid=0).rate_available())                 # armed and waiting for SteamVR
  self.assertFalse(self.make(pid=0,applied=None).rate_available())
 def test_running_steamvr_gets_new_values_recorded_without_touching_the_settings_file(self):
  instance=self.make();before=dict(instance.host_state['applied'])
  with patch.object(service.Supervisor,'server_alive',return_value=True),patch.object(instance,'update') as update,patch.object(instance,'save_host_settings') as save:
   instance.set_rate(450,'op')
  save.assert_not_called()
  self.assertEqual(instance.host_state['applied']['targetBandwidth'],450.0);self.assertEqual(instance.host_state['applied']['recommendedBandwidthMbit'],450)
  self.assertEqual(instance.host_state['previous'],before);self.assertEqual(instance.applied,450)
  self.assertEqual(json.loads(instance.host_path.read_text())['applied']['targetBandwidth'],450.0)
  self.assertEqual(update.call_args.kwargs,{'target_mbps':450,'operation_id':'op','last_result':{'ok':True}})
 def test_a_bitrate_chosen_during_the_session_is_kept_when_settings_are_restored(self):
  instance=self.make()
  with patch.object(service.Supervisor,'server_alive',return_value=True),patch.object(instance,'update'):
   instance.set_rate(200,'op')
  original=instance.host_state['original']
  self.assertEqual((original['targetBandwidth'],original['recommendedBandwidthMbit']),(200.0,200))
  self.assertTrue(original['automaticBandwidth'])  # everything else still goes back to the user's own value
  self.assertEqual(json.loads(instance.host_path.read_text())['original']['targetBandwidth'],200.0)
 def test_closed_steamvr_gets_the_values_written_for_the_next_start(self):
  instance=self.make(pid=0)
  with patch.object(instance,'update'),patch.object(instance,'save_host_settings') as save:
   instance.set_rate(250,'op')
  save.assert_called_once_with(250);self.assertEqual(instance.applied,250)
 def test_unavailable_or_invalid_rate_changes_are_refused_without_state_changes(self):
  hookless=self.make(live=False)
  with patch.object(service.Supervisor,'server_alive',return_value=True),patch.object(hookless,'update') as update:
   with self.assertRaisesRegex(RuntimeError,'not available'):hookless.set_rate(400)
   for bad in (24,501,300.0,True,'300'):
    with self.assertRaises(ValueError):self.make().set_rate(bad)
  update.assert_not_called();self.assertEqual(hookless.applied,300)
 def test_original_is_still_recognised_after_a_crash_between_recording_and_the_live_write(self):
  original={'targetBandwidth':100.0,'recommendedBandwidthMbit':100}
  old=service.codec_host_values(300);new=service.codec_host_values(450)
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


class HistoryLogTests(unittest.TestCase):
 def test_state_changes_are_kept_in_a_short_history_and_repeats_are_not(self):
  instance=object.__new__(service.Supervisor)
  instance.directory=pathlib.Path(tempfile.mkdtemp());instance.phase='verifying hardware stream'
  instance.log('re-apply attempt 1 failed (verifying hardware stream): no decoder users')
  instance.log_last=None
  instance.status={'state':'waiting','active_codec':'Unknown','message':'SteamVR closed.'}
  with instance_mutex(instance),patch.object(service.Supervisor,'rate_available',return_value=False),patch.object(service.Supervisor,'automatic_bandwidth',return_value=False):
   instance.update();instance.update()
  lines=(instance.directory/'supervisor.log').read_text().splitlines()
  self.assertEqual(len(lines),2);self.assertIn('no decoder users',lines[0]);self.assertIn('state waiting / Unknown: SteamVR closed.',lines[1])


def instance_mutex(instance):
 instance.mutex=threading.RLock()
 return contextlib.nullcontext()


class LiveEncoderTests(LiveRateTests):
 def make(self,pid=7,live=True,applied=300,codec='H.264'):
  instance=super().make(pid,live,applied,codec)
  instance.backend=pathlib.Path('backend');instance.rate_control='vbr';instance.preset=2;instance.vbv=2
  return instance
 def test_encoder_requests_are_validated(self):
  request={'protocol_version':1,'id':str(uuid.uuid4()),'action':'encoder','rate_control':'cbr','preset':4,'vbv_frames':2}
  self.assertEqual(service.validate_request(request),(request['id'],'encoder'))
  for key,bad in (('rate_control','abr'),('preset',8),('preset',True),('vbv_frames',0),('vbv_frames',17),('rate_control',None)):
   with self.assertRaises(ValueError):service.validate_request(dict(request,**{key:bad}))
 def test_new_options_are_queued_on_the_resident_hook_with_the_current_rate(self):
  instance=self.make()
  done=Mock(returncode=0,stdout='{"code":0}',stderr='')
  with patch.object(service.Supervisor,'server_alive',return_value=True),patch.object(instance,'update') as update,patch.object(instance,'log'),patch.object(instance,'hook_identity',side_effect=['vbr, P2, VBV 2','cbr, P4, VBV 2']),patch.object(service.time,'sleep'),patch.object(service.subprocess,'run',return_value=done) as run:
   instance.set_encoder('cbr',4,2,'op')
  args=run.call_args.args[0]
  self.assertEqual(args[1],'7');self.assertEqual(args[3:],['configure','500000000','500000000','0','72','2','cbr','4','2'])
  self.assertEqual((instance.rate_control,instance.preset,instance.vbv),('cbr',4,2))
  # Applied only once the hook reports the new options (SteamVR's next encoder update adopted them).
  self.assertEqual(update.call_args.kwargs,{'operation_id':'op','last_result':{'ok':True}})
 def test_a_refused_or_unavailable_change_leaves_the_options_alone(self):
  hookless=self.make(live=False)
  with patch.object(service.Supervisor,'server_alive',return_value=True),patch.object(hookless,'update'),patch.object(service.subprocess,'run') as run:
   with self.assertRaisesRegex(RuntimeError,'not available'):hookless.set_encoder('cbr',4,2)
   run.assert_not_called()
  failing=self.make();refused=Mock(returncode=1,stdout='',stderr='no')
  with patch.object(service.Supervisor,'server_alive',return_value=True),patch.object(failing,'update'),patch.object(service.subprocess,'run',return_value=refused):
   with self.assertRaisesRegex(RuntimeError,'refused'):failing.set_encoder('cbr',4,2)
  self.assertEqual((failing.rate_control,failing.preset,failing.vbv),('vbr',2,2))
 def test_restarting_the_stream_stops_it_first_and_reports_the_operation(self):
  instance=self.make();instance.verify_seconds=20;calls=[]
  instance.cef=lambda which:calls.append('cef '+which)
  with patch.object(instance,'update') as update,patch.object(instance,'ensure_frame',side_effect=lambda:calls.append('frame')),patch.object(instance,'verify_stream',side_effect=lambda *a:calls.append('verify')),patch.object(service.time,'sleep'):
   instance.restream('msg','done','op',stop=True)
  self.assertEqual(calls,['cef stop','frame','cef start','verify'])
  self.assertEqual(update.call_args.kwargs['last_result'],{'ok':True})
  with patch.object(instance,'update') as update,patch.object(instance,'ensure_frame',side_effect=RuntimeError('boom')),patch.object(instance,'log'),patch.object(service.time,'sleep'):
   instance.restream('msg','done','op2',stop=True)
  self.assertFalse(update.call_args.kwargs['last_result']['ok']);self.assertEqual(update.call_args.kwargs['state'],'waiting')
 def test_options_the_hook_never_adopts_are_reported(self):
  instance=self.make();done=Mock(returncode=0,stdout='{"code":0}',stderr='');clock=iter([0,1,2,3,4,5,6,7,8,9,10,11,12,13])
  with patch.object(service.Supervisor,'server_alive',return_value=True),patch.object(instance,'update'),patch.object(instance,'log'),patch.object(instance,'hook_identity',return_value='vbr, P2, VBV 2'),patch.object(service.time,'sleep'),patch.object(service.time,'monotonic',side_effect=lambda:next(clock)),patch.object(service.subprocess,'run',return_value=done):
   with self.assertRaisesRegex(RuntimeError,'has not applied'):instance.set_encoder('cbr',4,2,'op')
