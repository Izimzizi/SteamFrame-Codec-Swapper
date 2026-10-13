"""Compare 1/2/4 stock HEVC Main10 decoders at equal image area and coded bitrate.

Local preencoded clips isolate the decoder. This is not a renderer, wireless,
end-to-end latency or image-quality test. Compressed QC10C surfaces are checked
for frame identity/errors but are not independently pixel-verified.
"""
import argparse
import json
import pathlib
import re
import struct
import sys
import time

sys.path.insert(0, str(pathlib.Path(__file__).resolve().parents[1] / 'AV1'))
import h264_multistream_trial as paired


def prepare(folder, streams, mbps, width, total_height, ffmpeg, ffprobe):
    folder.mkdir(parents=True, exist_ok=True)
    height = total_height // streams
    path = folder / 'source.hevc'
    budget = mbps * 1000000 // streams
    command = [ffmpeg, '-hide_banner', '-loglevel', 'error', '-f', 'lavfi', '-i',
               f'testsrc2=s={width}x{height}:r=90,noise=alls=16:allf=t+u:all_seed=41,format=p010le',
               '-frames:v', '90', '-c:v', 'hevc_nvenc', '-preset', 'p2', '-tune', 'ull', '-profile:v', 'main10',
               '-rc', 'vbr', '-b:v', str(budget), '-maxrate', str(budget), '-bufsize', str(budget // 90),
               '-rc-lookahead', '0', '-bf', '0', '-g', '90', '-refs', '3', '-zerolatency', '1', '-aud', '1', '-y', path]
    for _ in range(4):
        paired.run_tool(command, 180)
        actual = path.stat().st_size * 8 * streams / 1e6
        print(json.dumps({'streams': streams, 'budget_mbps': budget * streams / 1e6, 'coded_mbps': actual}), flush=True)
        if abs(actual / mbps - 1) <= .03:
            break
        budget = min(650000000 // streams, int(budget * mbps / actual))
        for flag, value in (('-b:v', budget), ('-maxrate', budget), ('-bufsize', budget // 90)):
            command[command.index(flag) + 1] = str(value)
    else:
        raise RuntimeError('Could not match aggregate coded bitrate within 3%')
    probe = json.loads(paired.run_tool([ffprobe, '-v', 'error', '-show_packets', '-show_streams', '-of', 'json', path]))
    stream = probe['streams'][0]
    if (stream['codec_name'], stream['profile'], stream['width'], stream['height'], stream['pix_fmt']) != ('hevc', 'Main 10', width, height, 'yuv420p10le'):
        raise RuntimeError('Unexpected codec, geometry or bit depth')
    raw = path.read_bytes()
    if any((raw[m.end()] >> 1) & 63 == 38 for m in re.finditer(b'\x00\x00(?:\x00)?\x01', raw)):
        raise RuntimeError('HEVC filler data is not allowed')
    packets = probe['packets']
    if len(packets) != 90 or 'K' not in packets[0]['flags']:
        raise RuntimeError('Expected one independently decodable 90-frame GOP')
    clip = folder / 'source.lab'
    with clip.open('wb') as output:
        output.write(struct.pack('<4sHH4sHHIIII', b'DKIF', 0, 32, b'HEVC', width, height, 90, 1, 90, 0))
        position = 0
        for index, packet in enumerate(packets):
            length = int(packet['size'])
            if int(packet['pos']) != position or not 0 < length <= 16000000:
                raise RuntimeError('Invalid access-unit bounds')
            output.write(struct.pack('<IQ', length, index) + raw[position:position + length])
            position += length
        if position != len(raw):
            raise RuntimeError('Unaccounted bytes in source')
    # Independently validate that the Main10 elementary stream fully decodes.
    paired.run_tool([ffmpeg, '-v', 'error', '-xerror', '-i', path, '-f', 'null', '-'], 180)
    manifest = {'streams': streams, 'width': width, 'height': height, 'fps': 90, 'bit_depth': 10,
                'actual_aggregate_mbps': actual, 'requested_aggregate_mbps': mbps,
                'filler_nals': 0, 'software_decode_verified': True,
                'encoder_budget_mbps': budget * streams / 1e6}
    (folder / 'source.json').write_text(json.dumps(manifest, indent=2))
    return manifest


def run(folder, streams, seconds):
    manifest = json.loads((folder / 'source.json').read_text())
    remote_clip = paired.REMOTE + f'/hevc-{streams}-source.lab'
    paired.run_tool([paired.SCP, *paired.OPTIONS, folder / 'source.lab', paired.PEER + ':' + remote_clip], 30)
    script = '''
import json,pathlib,subprocess,os
root=pathlib.Path(%r);processes=[];rows=[]
try:
 for index in range(%d):
  path=root/('hevc-%%d-%%d.lab'%%(%d,index))
  path.unlink(missing_ok=True);os.link(%r,path)
  process=subprocess.Popen([str(root/'hardware_decode_multistream'),'/dev/video22',str(path),'paced','ubwc10','repeat=%d'],stdout=subprocess.PIPE,stderr=subprocess.PIPE,text=True)
  processes.append(process)
 for process in processes:
  output,error=process.communicate(timeout=%d)
  rows.append({'exit_code':process.returncode,'diagnostics':error,'decoder':json.loads(output) if output.strip() else None})
 print(json.dumps(rows))
finally:
 for process in processes:
  if process.poll() is None:process.terminate()
 for process in processes:
  try:process.wait(timeout=3)
  except subprocess.TimeoutExpired:process.kill();process.wait()
''' % (paired.REMOTE, streams, streams, remote_clip, seconds, seconds + 15)
    result = {'source': manifest, 'wireless_tested': False, 'display_tested': False, 'pixel_verified': False}
    try:
        result['decoders'] = json.loads(paired.remote(script, seconds + 30))
        result['capacity_pass'] = all(row['exit_code'] == 0 and row['decoder']['pacing_pass'] for row in result['decoders'])
        result['latency_pass'] = result['capacity_pass'] and all(row['decoder']['p99_ms'] < 1000 / 90 for row in result['decoders'])
    except Exception as error:
        result['error'] = str(error)
    (folder / 'result.json').write_text(json.dumps(result, indent=2))
    return result


def main():
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument('--results', type=pathlib.Path, required=True)
    parser.add_argument('--mbps', type=int, default=300)
    parser.add_argument('--width', type=int, default=2048)
    parser.add_argument('--height', type=int, default=8192)
    parser.add_argument('--seconds', type=int, default=10)
    parser.add_argument('--prepare-only', action='store_true')
    parser.add_argument('--run-prepared', action='store_true')
    parser.add_argument('--ffmpeg', type=pathlib.Path, default=pathlib.Path(r'C:\Program Files\Virtual Desktop Streamer\ffmpeg.exe'))
    parser.add_argument('--ffprobe', type=pathlib.Path, default=pathlib.Path(r'C:\Program Files\Virtual Desktop Streamer\ffprobe.exe'))
    args = parser.parse_args()
    if not 25 <= args.mbps <= 500 or not 10 <= args.seconds <= 30 or args.width not in (1536, 1792, 2048) or args.height != args.width * 4:
        parser.error('Use a valid packed-image geometry and bounded bitrate/duration')
    args.results = args.results.resolve()
    if not args.run_prepared:
        for streams in (1, 2, 4):
            manifest = prepare(args.results / str(streams), streams, args.mbps, args.width, args.height,
                               args.ffmpeg, args.ffprobe)
            print(json.dumps({'prepared': manifest}), flush=True)
    if args.prepare_only:
        return
    report = {'results': []}
    try:
        report['stop'] = paired.cef('stop')
        time.sleep(2)
        for streams in (1, 2, 4):
            result = run(args.results / str(streams), streams, args.seconds)
            report['results'].append(result)
            print(json.dumps(result), flush=True)
    finally:
        try:
            report['restart'] = paired.cef('start')
        except Exception as error:
            report['restart_error'] = str(error)
        (args.results / 'comparison.json').write_text(json.dumps(report, indent=2))


if __name__ == '__main__':
    main()
