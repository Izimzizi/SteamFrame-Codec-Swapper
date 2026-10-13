"""Pure contract tests; never controls a real Frame or SteamVR process."""
import json,pathlib,tempfile,unittest,uuid
from unittest.mock import Mock,patch
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
  instance.pid=0;instance.authenticated=False;instance.root=None;instance.config={'initial_codec':'AV1','target_mbps':100,'fps':90}
  instance.stop_heartbeat=__import__('threading').Event()
  with patch.object(instance,'update'),patch.object(instance,'heartbeat'),patch.object(instance,'authenticate',side_effect=RuntimeError('authentication rejected')):
   with self.assertRaises(RuntimeError):instance.run(['secret-for-test'])
  self.assertTrue(instance.stop_heartbeat.is_set())
 def test_absent_server_is_successful_empty_inventory(self):
  instance=object.__new__(service.Supervisor);instance.vr=pathlib.Path(r'C:\Steam\steamapps\common\SteamVR\bin\win64')
  with patch.object(instance,'powershell',return_value='') as call:
   self.assertEqual(instance.server_pid(),0)
   self.assertIn('exit 0',call.call_args.args[0])
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
if __name__=='__main__':unittest.main()
