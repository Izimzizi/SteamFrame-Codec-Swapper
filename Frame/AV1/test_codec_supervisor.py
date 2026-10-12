"""Pure contract tests; never controls a real Frame or SteamVR process."""
import pathlib,tempfile,unittest,uuid
from unittest.mock import patch
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
if __name__=='__main__':unittest.main()
