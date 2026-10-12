"""Pin public upstream Iris sources for an isolated 6.18 compile experiment."""
import argparse,concurrent.futures,hashlib,json,pathlib,urllib.request
root=pathlib.Path(__file__).resolve().parent
args=argparse.ArgumentParser();args.add_argument('--ref',default='v7.0');args.add_argument('--directory',default='iris-upstream-pinned');a=args.parse_args()
def get(url):
 with urllib.request.urlopen(urllib.request.Request(url,headers={'User-Agent':'SteamFrameAV1Lab'}),timeout=20) as response:return response.read()
commit=json.loads(get('https://api.github.com/repos/torvalds/linux/commits/'+a.ref))['sha']
path='drivers/media/platform/qcom/iris'
listing=json.loads(get('https://api.github.com/repos/torvalds/linux/contents/'+path+'?ref='+commit))
directory=root/a.directory;directory.mkdir(exist_ok=True)
def fetch(item):
 data=get(item['download_url']);(directory/item['name']).write_bytes(data)
 return {'file':item['name'],'sha256':hashlib.sha256(data).hexdigest(),'git_blob_sha':item['sha']}
with concurrent.futures.ThreadPoolExecutor(max_workers=6) as pool:files=list(pool.map(fetch,[i for i in listing if i['type']=='file']))
dependency='https://raw.githubusercontent.com/torvalds/linux/v6.18/drivers/media/v4l2-core/v4l2-mem2mem.c'
data=get(dependency);(directory/'v4l2-mem2mem.c').write_bytes(data)
(directory/'provenance.json').write_text(json.dumps({'repository':'https://github.com/torvalds/linux','commit':commit,'path':path,'files':files,'dependency':{'url':dependency,'sha256':hashlib.sha256(data).hexdigest()}},indent=2))
print(json.dumps({'commit':commit,'files':len(files),'source_matches_valve_commit':False}))
