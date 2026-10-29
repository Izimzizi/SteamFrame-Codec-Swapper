"""Paired dongle H.264 split-frame hardware experiment; not a SteamVR mode.

NVENC generates one second of high-entropy video offline. Independent TCP flows
replay that GOP at 90/120 Hz into separate V4L2 hardware decoder instances. This
isolates receiver/transport capacity from real-time rendering and encoding.
The total image area and requested aggregate bitrate stay constant for 1/2/4
vertical strips. Per-stream decoded pixels are checked against FFmpeg.
"""
import argparse
import base64
import concurrent.futures
import hashlib
import json
import os
import pathlib
import queue
import re
import socket
import struct
import subprocess
import threading
import time

ROOT = pathlib.Path(__file__).resolve().parent
REMOTE = '/tmp/steam-frame-av1-lab-20261011'
WINDOWS = pathlib.Path(os.environ.get('WINDIR', r'C:\Windows'))
LOCAL_DATA = pathlib.Path(os.environ.get('LOCALAPPDATA', pathlib.Path.home() / 'AppData/Local'))
SSH = str(WINDOWS / 'System32/OpenSSH/ssh.exe')
SCP = str(WINDOWS / 'System32/OpenSSH/scp.exe')
OPTIONS = ['-i', os.environ.get('FRAME_SSH_KEY', str(LOCAL_DATA / 'steamos-devkit/devkit_rsa')),
           '-o', 'IdentitiesOnly=yes', '-o', 'BatchMode=yes',
           '-o', 'StrictHostKeyChecking=yes', '-o',
           'UserKnownHostsFile=' + os.environ.get('FRAME_SSH_KNOWN_HOSTS', str(LOCAL_DATA / 'SteamFrameTray/known_hosts')),
           '-o', 'ConnectTimeout=3']
PEER = 'steamos@10.35.78.1'


def remote(script, timeout=15):
    result = subprocess.run([SSH, *OPTIONS, PEER, 'python3 -'], input=script,
                            text=True, capture_output=True, timeout=timeout)
    if result.returncode:
        raise RuntimeError('Frame command failed: ' + result.stderr[-1500:])
    return result.stdout.strip()


def cef(action):
    expression = (ROOT / (action + '_stream.js')).read_bytes()
    script = (ROOT / 'frame_cef_stdio.py').read_text().replace(
        '__EXPRESSION_BASE64__', base64.b64encode(expression).decode())
    return json.loads(remote(script, 20))


def run_tool(command, timeout=90):
    result = subprocess.run([str(x) for x in command], text=True,
                            capture_output=True, timeout=timeout)
    if result.returncode:
        raise RuntimeError(result.stderr[-2000:])
    return result.stdout


def prepare(args):
    """Produce real coded data, without bitrate padding, for equal-area trials."""
    folder = args.results / 'source'
    folder.mkdir(parents=True, exist_ok=True)
    clip = folder / 'stream.h264'
    width, height = 1024, 4096 // args.streams
    bitrate = args.mbps * 1000000 // args.streams
    command = [args.ffmpeg, '-hide_banner', '-loglevel', 'error', '-f', 'lavfi',
               '-i', f'nullsrc=s={width}x{height}:r={args.fps},noise=alls=100:allf=t+u:all_seed=41',
               '-frames:v', str(args.fps), '-c:v', 'h264_nvenc', '-preset', 'p2',
               '-tune', 'ull', '-profile:v', 'high', '-rc', 'vbr', '-b:v', str(bitrate),
               '-maxrate', str(bitrate), '-bufsize', str(bitrate // args.fps),
               '-rc-lookahead', '0', '-bf', '0', '-g', str(args.fps), '-refs', '3',
               '-zerolatency', '1', '-aud', '1', '-coder', args.entropy, '-y', clip]
    # VBR can undershoot its requested budget even on noise. Calibrate the
    # source, then report actual coded bytes; do not add filler to reach a label.
    for attempt in range(4):
        run_tool(command)
        actual = clip.stat().st_size * 8 * args.streams / 1e6
        if actual >= args.mbps * .99:
            break
        bitrate = min(650000000 // args.streams, int(bitrate * args.mbps / actual * 1.01))
        for option, value in (('-b:v', bitrate), ('-maxrate', bitrate), ('-bufsize', bitrate // args.fps)):
            command[command.index(option) + 1] = str(value)
    else:
        raise RuntimeError('Could not generate enough real coded video for the requested trial')
    probe = json.loads(run_tool([args.ffprobe, '-v', 'error', '-show_packets',
                                '-show_streams', '-of', 'json', clip]))
    info = probe['streams'][0]
    if (info['codec_name'], info['width'], info['height']) != ('h264', width, height):
        raise RuntimeError('Encoder returned an unexpected codec or layout')
    raw = clip.read_bytes()
    if any(raw[m.end()] & 31 == 12 for m in re.finditer(b'\x00\x00(?:\x00)?\x01', raw)):
        raise RuntimeError('Bitrate filler is not accepted as decoder throughput evidence')
    packets = probe['packets']
    if len(packets) != args.fps or 'K' not in packets[0]['flags']:
        raise RuntimeError('Expected exactly one independently decodable GOP')
    frames = []
    position = 0
    for packet in packets:
        length = int(packet['size'])
        if int(packet['pos']) != position or length <= 0 or position + length > len(raw):
            raise RuntimeError('Non-contiguous or truncated compressed packets')
        frames.append(raw[position:position + length])
        position += length
    if position != len(raw):
        raise RuntimeError('Unaccounted compressed bytes')
    reference = folder / 'first.nv12'
    run_tool([args.ffmpeg, '-hide_banner', '-loglevel', 'error', '-i', clip,
              '-frames:v', '1', '-f', 'rawvideo', '-pix_fmt', 'nv12', '-y', reference])
    expected_size = width * height * 3 // 2
    if reference.stat().st_size != expected_size:
        raise RuntimeError('Unexpected reference image size')
    result = {'width': width, 'height': height, 'fps': args.fps,
              'streams': args.streams, 'requested_aggregate_mbps': args.mbps,
              'coded_aggregate_mbps': len(raw) * 8 * args.streams / 1e6,
              'encoder_aggregate_budget_mbps': bitrate * args.streams / 1e6,
              'entropy': args.entropy,
              'profile': info['profile'], 'level': info['level'],
              'clip_sha256': hashlib.sha256(raw).hexdigest(),
              'reference_sha256': hashlib.sha256(reference.read_bytes()).hexdigest(),
              'encoder_command': [str(x) for x in command], 'padding_requested': False,
              'filler_nals': 0}
    (folder / 'manifest.json').write_text(json.dumps(result, indent=2))
    return frames, result


def stage():
    remote("import pathlib; pathlib.Path(" + repr(REMOTE) + ").mkdir(exist_ok=True)")
    for name in ('hardware_decode.cpp', 'hardware_decode_test.cpp'):
        run_tool([SCP, *OPTIONS, ROOT / name, PEER + ':' + REMOTE + '/' + name], 20)
    script = """
import pathlib,subprocess,json
root=pathlib.Path(%r)
results=[]
for source,target in [('hardware_decode.cpp','hardware_decode_multistream'),('hardware_decode_test.cpp','hardware_decode_test')]:
 p=subprocess.run(['g++','-std=c++17','-O2','-Wall','-Wextra','-pthread',str(root/source),'-o',str(root/target)],capture_output=True,text=True,timeout=40)
 if p.returncode:raise RuntimeError(p.stderr[-2000:])
 results.append({'source':source,'warnings':p.stderr})
p=subprocess.run([str(root/'hardware_decode_test')],capture_output=True,text=True,timeout=5)
if p.returncode:raise RuntimeError('Decoder layout regression test failed')
print(json.dumps({'builds':results,'layout_test':json.loads(p.stdout)}))
""" % REMOTE
    return json.loads(remote(script, 85))


def snapshot():
    return json.loads(remote("""
import json,pathlib,time
root=pathlib.Path('/sys/class/net/wlanap/statistics')
print(json.dumps({'monotonic':time.monotonic(),**{n:int((root/n).read_text()) for n in ('rx_bytes','rx_errors','rx_dropped')}}))
"""))


def offline_trial(args, frames, manifest):
    """Run the same hardware workload from local RAM, excluding wireless/TCP."""
    clip = args.results / 'source' / 'stream.lab'
    with clip.open('wb') as output:
        output.write(struct.pack('<4sHH4sHHIIII', b'DKIF', 0, 32, b'H264',
                     manifest['width'], manifest['height'], args.fps, 1, len(frames), 0))
        for index, payload in enumerate(frames):
            output.write(struct.pack('<IQ', len(payload), index) + payload)
    run_tool([SCP, *OPTIONS, clip, PEER + ':' + REMOTE + '/multistream-source.lab'], 30)
    result = {'steamvr_integration': False, 'display_tested': False,
              'realtime_encoder_tested': False, 'wireless_tested': False,
              'source': manifest, 'functional_pass': False, 'throughput_pass': False}
    stopped = False
    script = """
import json,pathlib,subprocess,hashlib,os
root=pathlib.Path(%r)
processes=[]
try:
 for index in range(%d):
  path=root/('multistream-%%d.lab'%%index)
  if path.exists():path.unlink()
  os.link(root/'multistream-source.lab',path)
  proc=subprocess.Popen([str(root/'hardware_decode_multistream'),'/dev/video22',str(path),'paced','repeat=%d'],stdout=subprocess.PIPE,stderr=subprocess.PIPE,text=True)
  processes.append((proc,path))
 rows=[]
 for proc,path in processes:
  output,error=proc.communicate(timeout=%d)
  row={'exit_code':proc.returncode,'diagnostics':error}
  if proc.returncode==0:
   row['decoder']=json.loads(output)
   row['first_frame_sha256']=hashlib.sha256(path.with_suffix('.lab.nv12').read_bytes()).hexdigest()
  rows.append(row)
 print(json.dumps(rows))
finally:
 for proc,path in processes:
  if proc.poll() is None:proc.terminate()
 for proc,path in processes:
  try:proc.wait(timeout=3)
  except subprocess.TimeoutExpired:proc.kill();proc.wait()
""" % (REMOTE, args.streams, args.seconds, args.seconds + 15)
    try:
        if args.pause_steamvr:
            result['stop_stream'] = cef('stop')
            stopped = True
            time.sleep(2)
        result['streams'] = json.loads(remote(script, args.seconds + 25))
        for row in result['streams']:
            row['pixel_match'] = row.get('first_frame_sha256') == manifest['reference_sha256']
        result['functional_pass'] = all(row.get('decoder', {}).get('functional_pass') and row['pixel_match'] for row in result['streams'])
        if result['functional_pass']:
            duration = max(row['decoder']['elapsed_seconds'] for row in result['streams'])
            result['decoded_aggregate_mbps'] = manifest['coded_aggregate_mbps'] * args.seconds / duration
            result['throughput_pass'] = duration <= args.seconds + .1 and result['decoded_aggregate_mbps'] >= args.mbps * .95
            result['latency_pass'] = result['throughput_pass'] and all(row['decoder']['p99_ms'] <= 1000 / args.fps for row in result['streams'])
    except Exception as error:
        result['error'] = str(error)
    finally:
        if stopped:
            try:
                result['restart_stream'] = cef('start')
            except Exception as error:
                result['restart_error'] = str(error)
        (args.results / 'offline-result.json').write_text(json.dumps(result, indent=2))
    return result


def trial(args, frames, manifest):
    result = {'steamvr_integration': False, 'display_tested': False,
              'realtime_encoder_tested': False, 'source': manifest,
              'streams': [], 'functional_pass': False, 'throughput_pass': False}
    processes, sockets, errors, signals = [], [], [], []
    stopped_stream = False
    count = args.seconds * args.fps
    try:
        if args.pause_steamvr:
            result['stop_stream'] = cef('stop')
            stopped_stream = True
            time.sleep(2)
        # Separate, ordinary user descriptors only; no module or firmware swap.
        for index in range(args.streams):
            port = 50120 + index
            process = subprocess.Popen([SSH, *OPTIONS, PEER,
                f'timeout --kill-after=2s {args.seconds + 25}s {REMOTE}/hardware_decode_multistream /dev/video22 tcp:{port}'],
                stdout=subprocess.PIPE, stderr=subprocess.PIPE, text=True)
            processes.append(process)
            messages, ready = [], queue.Queue()
            errors.append(messages)
            signals.append(ready)

            def read_errors(proc=process, output=messages, channel=ready):
                for line in proc.stderr:
                    output.append(line.strip())
                    try:
                        value = json.loads(line)
                        if value.get('dongle_listener_ready'):
                            channel.put('listening')
                        if value.get('hardware_ready'):
                            channel.put('decoding')
                    except json.JSONDecodeError:
                        pass
                channel.put('exited')

            threading.Thread(target=read_errors, daemon=True).start()
            if ready.get(timeout=10) != 'listening':
                raise RuntimeError('Hardware listener failed: ' + '; '.join(messages[-4:]))
            connection = socket.socket(socket.AF_INET, socket.SOCK_STREAM)
            sockets.append(connection)
            connection.settimeout(3)
            connection.setsockopt(socket.IPPROTO_TCP, socket.TCP_NODELAY, 1)
            connection.setsockopt(socket.SOL_SOCKET, socket.SO_SNDBUF, 1024 * 1024)
            connection.bind(('10.35.78.21', 0))
            connection.connect(('10.35.78.1', port))
            header = struct.pack('<4sHH4sHHIIII', b'DKIF', 0, 32, b'H264',
                                 manifest['width'], manifest['height'], args.fps, 1, count, 0)
            connection.sendall(header + struct.pack('<IQ', len(frames[0]), 0) + frames[0])
            if ready.get(timeout=10) != 'decoding':
                raise RuntimeError('Hardware initialization failed: ' + '; '.join(messages[-4:]))
        before = snapshot()
        barrier = threading.Barrier(args.streams)
        started = time.monotonic()

        def send(index):
            connection = sockets[index]
            barrier.wait(timeout=5)
            beginning = time.monotonic()
            sent_bytes, late, worst_send_ms = len(frames[0]), 0, 0.0
            for frame_id in range(1, count):
                due = beginning + frame_id / args.fps
                remaining = due - time.monotonic()
                if remaining > 0:
                    time.sleep(remaining)
                packet = frames[frame_id % len(frames)]
                send_start = time.monotonic()
                connection.sendall(struct.pack('<IQ', len(packet), frame_id) + packet)
                now = time.monotonic()
                sent_bytes += len(packet)
                worst_send_ms = max(worst_send_ms, (now - send_start) * 1000)
                late += now > due + 1 / args.fps
            elapsed = time.monotonic() - beginning
            connection.shutdown(socket.SHUT_WR)
            return {'encoded_bytes': sent_bytes, 'elapsed_seconds': elapsed,
                    'send_deadline_misses': late, 'max_send_ms': worst_send_ms}

        with concurrent.futures.ThreadPoolExecutor(max_workers=args.streams) as pool:
            futures = [pool.submit(send, index) for index in range(args.streams)]
            senders = [future.result() for future in futures]
        result['send_elapsed_seconds'] = time.monotonic() - started
        for index, process in enumerate(processes):
            process.wait(timeout=10)
            output = process.stdout.read()
            if process.returncode:
                raise RuntimeError(f'Decoder {index} failed: ' + '; '.join(errors[index][-6:]))
            decoder = json.loads(output)
            result['streams'].append({'sender': senders[index], 'decoder': decoder})
        after = snapshot()
        elapsed = after['monotonic'] - before['monotonic']
        result['dongle'] = {'rx_mbps': (after['rx_bytes'] - before['rx_bytes']) * 8 / elapsed / 1e6,
                            'rx_errors': after['rx_errors'] - before['rx_errors'],
                            'rx_dropped': after['rx_dropped'] - before['rx_dropped']}
        result['encoded_mbps'] = sum(s['encoded_bytes'] for s in senders) * 8 / result['send_elapsed_seconds'] / 1e6
        hashes = json.loads(remote("""
import hashlib,json,pathlib
root=pathlib.Path(%r)
print(json.dumps([hashlib.sha256((root/('hardware-live-%%d.nv12'%%(50120+i))).read_bytes()).hexdigest() for i in range(%d)]))
""" % (REMOTE, args.streams)))
        for row, actual in zip(result['streams'], hashes):
            row['first_frame_sha256'] = actual
            row['pixel_match'] = actual == manifest['reference_sha256']
        result['functional_pass'] = all(row['decoder']['functional_pass'] and row['pixel_match'] for row in result['streams'])
        result['throughput_pass'] = (result['functional_pass']
            and result['encoded_mbps'] >= args.mbps * .95
            and result['send_elapsed_seconds'] <= args.seconds + .1
            and all(row['decoder']['elapsed_seconds'] <= args.seconds + .5 for row in result['streams']))
        result['latency_pass'] = result['throughput_pass'] and all(row['decoder']['p99_ms'] <= 1000 / args.fps for row in result['streams'])
    except Exception as error:
        result['error'] = str(error)
    finally:
        for connection in sockets:
            connection.close()
        for process in processes:
            if process.poll() is None:
                try:
                    process.wait(timeout=8)
                except subprocess.TimeoutExpired:
                    process.terminate()
                    process.wait(timeout=5)
        result['receiver_diagnostics'] = errors
        if stopped_stream:
            try:
                result['restart_stream'] = cef('start')
            except Exception as error:
                result['restart_error'] = str(error)
        (args.results / 'result.json').write_text(json.dumps(result, indent=2))
    return result


def main():
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument('results', type=pathlib.Path)
    parser.add_argument('--streams', type=int, choices=(1, 2, 4), required=True)
    parser.add_argument('--mbps', type=int, default=400)
    parser.add_argument('--fps', type=int, choices=(90, 120), default=90)
    parser.add_argument('--entropy', choices=('cabac', 'cavlc'), default='cabac')
    parser.add_argument('--seconds', type=int, default=10)
    parser.add_argument('--prepare-only', action='store_true')
    parser.add_argument('--pause-steamvr', action='store_true')
    parser.add_argument('--offline', action='store_true', help='Decode local clips to isolate hardware from transport')
    parser.add_argument('--ffmpeg', type=pathlib.Path, default=pathlib.Path(r'C:\Program Files\Virtual Desktop Streamer\ffmpeg.exe'))
    parser.add_argument('--ffprobe', type=pathlib.Path, default=pathlib.Path(r'C:\Program Files\Virtual Desktop Streamer\ffprobe.exe'))
    args = parser.parse_args()
    if not 25 <= args.mbps <= 500 or not 10 <= args.seconds <= 60:
        parser.error('Use 25..500 aggregate Mbps and 10..60 seconds')
    args.results = args.results.resolve()
    args.results.mkdir(parents=True, exist_ok=True)
    frames, manifest = prepare(args)
    print(json.dumps({'prepared': manifest}), flush=True)
    if args.prepare_only:
        return 0
    build = stage()
    (args.results / 'build.json').write_text(json.dumps(build, indent=2))
    result = (offline_trial if args.offline else trial)(args, frames, manifest)
    print(json.dumps(result), flush=True)
    return 0 if result.get('throughput_pass') else 1


if __name__ == '__main__':
    raise SystemExit(main())
