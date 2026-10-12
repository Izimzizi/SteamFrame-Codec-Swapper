import argparse,json,pathlib,time,uuid
parser=argparse.ArgumentParser();parser.add_argument('session',type=pathlib.Path);parser.add_argument('action',choices=['inventory','prepare','load-disabled','load-av1','rollback','formats','kernel-log','client-av1','client-stock','recover-orphan-fw','close']);a=parser.parse_args()
status=json.loads((a.session/'status.json').read_text())
if status.get('state')!='authenticated':raise RuntimeError('Private authentication has not completed')
id=uuid.uuid4().hex
request=a.session/('request-'+id+'.json');reply=a.session/('reply-'+id+'.json')
request.write_text(json.dumps({'action':a.action}))
if a.action=='close':print('{"close_requested":true}');raise SystemExit(0)
began=time.monotonic()
while time.monotonic()-began<50:
 if reply.exists():print(reply.read_text());raise SystemExit(0)
 time.sleep(.1)
raise TimeoutError('Privileged action timed out')
