"""Build and run the bounded, synthetic VD-architecture prototype on this pair.

No proprietary binaries are copied, and no installed client/driver is patched.
The framing and CAVLC selection are prototype choices, not claims about
Virtual Desktop's exact wire protocol or entropy mode. Decoder DMA buffers are
imported by EGL; --cpu-upload retains the slower copy path for comparison.
"""
import argparse
import hashlib
import json
import pathlib
import queue
import subprocess
import sys
import threading
import time

ROOT = pathlib.Path(__file__).resolve().parents[2]
sys.path.insert(0, str(ROOT / 'Frame' / 'AV1'))
import h264_multistream_trial as paired

REMOTE = '/tmp/steam-frame-multistream-20261013'


def stage():
    paired.remote(f"import pathlib; p=pathlib.Path({REMOTE!r}); (p/'Frame/Multistream').mkdir(parents=True,exist_ok=True); (p/'Frame/AV1').mkdir(exist_ok=True)")
    files = ['Frame/AV1/hardware_decode.cpp', 'Frame/AV1/hardware_decode_test.cpp']
    files += ['Frame/Multistream/' + name for name in (
        'protocol.h', 'projection_math.h', 'datagrams.h', 'frame_pairer.h', 'contract_test.cpp', 'presentation.h', 'receiver.cpp')]
    for name in files:
        paired.run_tool([paired.SCP, *paired.OPTIONS, ROOT / name, paired.PEER + ':' + REMOTE + '/' + name], 20)
    script = """
import subprocess,json,pathlib
root=pathlib.Path(%r)
base=['g++','-std=c++17','-O2','-Wall','-Wextra','-pthread']
targets=[('Frame/AV1/hardware_decode_test.cpp','decode_test',[]),
 ('Frame/Multistream/contract_test.cpp','contract_test',[]),
 ('Frame/Multistream/receiver.cpp','receiver',['-I/opt/steamvr/tools/hellovr_vulkan_linux/src/openvr/headers','-L/opt/steamvr/bin/linuxarm64','-Wl,-rpath,/opt/steamvr/bin/linuxarm64','-lopenvr_api','-lEGL','-lGL'])]
results=[]
for source,target,extra in targets:
 p=subprocess.run(base+[str(root/source),'-o',str(root/target)]+extra,capture_output=True,text=True,timeout=50)
 results.append({'target':target,'exit_code':p.returncode,'diagnostics':p.stderr})
 if p.returncode:print(json.dumps(results));raise SystemExit(1)
for target in ('decode_test','contract_test'):
 p=subprocess.run([str(root/target)],capture_output=True,text=True,timeout=5)
 if p.returncode:raise RuntimeError(p.stderr)
 results.append({'test':target,'result':json.loads(p.stdout)})
print(json.dumps(results))
""" % REMOTE
    return json.loads(paired.remote(script, 100))


def main():
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument('--streams', type=int, choices=(2, 4), default=2)
    parser.add_argument('--mbps', type=int, default=400)
    parser.add_argument('--minimum-mbps', type=float, help='Measured video throughput acceptance threshold; default 95%% of encoder budget')
    parser.add_argument('--seconds', type=int, default=10)
    parser.add_argument('--results', type=pathlib.Path, required=True)
    parser.add_argument('--sender', type=pathlib.Path, default=ROOT / 'artifacts/Multistream/bin/sender.exe')
    parser.add_argument('--ffmpeg', type=pathlib.Path, default=pathlib.Path(r'C:\Program Files\Virtual Desktop Streamer\ffmpeg.exe'))
    parser.add_argument('--cabac', action='store_true')
    parser.add_argument('--udp', action='store_true', help='Use paced UDP with one-packet-per-block XOR recovery')
    parser.add_argument('--overlay', action='store_true')
    parser.add_argument('--game', action='store_true', help='Capture live SteamVR eyes and show pose-correct projection overlays; keep the Valve stream connected')
    parser.add_argument('--cpu-upload', action='store_true')
    parser.add_argument('--pause-steamvr', action='store_true')
    parser.add_argument('--skip-build', action='store_true')
    args = parser.parse_args()
    if args.game and args.pause_steamvr:
        parser.error('Game mode requires an active Valve tracking connection; omit --pause-steamvr')
    if not 25 <= args.mbps <= 600 or not 10 <= args.seconds <= 60:
        parser.error('Use 25..600 Mbps and 10..60 seconds')
    if args.minimum_mbps is None:
        args.minimum_mbps = args.mbps * .95
    if not 1 <= args.minimum_mbps <= args.mbps:
        parser.error('Minimum measured Mbps must be positive and no higher than the encoder budget')
    args.results = args.results.resolve()
    args.results.mkdir(parents=True, exist_ok=True)
    result = {'requested': {'streams': args.streams, 'mbps': args.mbps, 'seconds': args.seconds,
                           'minimum_measured_mbps': args.minimum_mbps,
                           'overlay': args.overlay, 'cpu_upload': args.cpu_upload,
                           'game': args.game,
                           'udp': args.udp,
                           'entropy': 'CABAC' if args.cabac else 'CAVLC'},
              'steamvr_game_capture': args.game, 'vd_wire_compatible': False,
              'functional_pass': False, 'throughput_pass': False,
              'presentation_latency_pass': False}
    stopped = False
    receiver = None
    diagnostics = []
    try:
        if not args.sender.is_file() or not args.ffmpeg.is_file():
            raise RuntimeError('Build Native/Multistream/build.ps1 and provide an existing FFmpeg executable')
        if not args.skip_build:
            build = stage()
            (args.results / 'build.json').write_text(json.dumps(build, indent=2))
        if args.pause_steamvr:
            result['stop_stream'] = paired.cef('stop')
            stopped = True
            time.sleep(2)
        ready = queue.Queue()
        command = (f'cd {REMOTE} && XDG_RUNTIME_DIR=/run/user/1000 '
                   f'timeout --kill-after=2s {args.seconds + 35}s ./receiver' + (' --overlay' if args.overlay else '')
                   + (' --cpu-upload' if args.cpu_upload else ''))
        receiver = subprocess.Popen([paired.SSH, *paired.OPTIONS, paired.PEER, command],
                                    stdout=subprocess.PIPE, stderr=subprocess.PIPE, text=True)

        def reader():
            for line in receiver.stderr:
                diagnostics.append(line.strip())
                if '"prototype_listening":true' in line:
                    ready.put(True)
            ready.put(False)

        thread = threading.Thread(target=reader, daemon=True)
        thread.start()
        if not ready.get(timeout=15):
            raise RuntimeError('Receiver failed to listen: ' + '\n'.join(diagnostics[-15:]))
        before = paired.snapshot()
        sender = subprocess.run([str(args.sender.resolve()), str(args.streams), str(args.mbps),
                                 str(args.seconds), *(['cabac'] if args.cabac else []), *(['game'] if args.game else []), *(['udp'] if args.udp else [])],
                                cwd=args.results, capture_output=True, text=True, timeout=args.seconds + 25)
        result['sender_exit_code'] = sender.returncode
        result['sender_diagnostics'] = sender.stderr
        if sender.stdout.strip():
            result['sender'] = json.loads(sender.stdout)
        output = receiver.stdout.read()
        receiver.wait(timeout=15)
        thread.join(timeout=2)
        result['receiver_exit_code'] = receiver.returncode
        result['receiver_diagnostics'] = diagnostics
        if output.strip():
            # OpenVR can print unstructured startup diagnostics before our report.
            # The report contains nested pretty line breaks from hardware_decode.
            start = output.find('{"streams"')
            if start >= 0:
                result['receiver'] = json.loads(output[start:])
            else:
                result['receiver_output'] = output[-4000:]
        after = paired.snapshot()
        result['dongle_rx_mbps'] = (after['rx_bytes'] - before['rx_bytes']) * 8 / (after['monotonic'] - before['monotonic']) / 1e6
        result['dongle_rx_errors'] = after['rx_errors'] - before['rx_errors']
        result['dongle_rx_dropped'] = after['rx_dropped'] - before['rx_dropped']
        if sender.returncode or receiver.returncode:
            raise RuntimeError('Sender or receiver failed; inspect diagnostics')
        pixels = []
        for index in range(args.streams):
            decoded = args.results / f'decoded-{index}-first.nv12'
            reference = args.results / f'reference-{index}-first.nv12'
            paired.run_tool([paired.SCP, *paired.OPTIONS, paired.PEER + ':' + REMOTE + '/' + decoded.name, decoded], 20)
            paired.run_tool([args.ffmpeg, '-hide_banner', '-loglevel', 'error', '-i',
                             args.results / f'stream-{index}-first.h264', '-frames:v', '1',
                             '-pix_fmt', 'nv12', '-f', 'rawvideo', '-y', reference])
            observed = hashlib.sha256(decoded.read_bytes()).hexdigest()
            expected = hashlib.sha256(reference.read_bytes()).hexdigest()
            pixels.append({'stream': index, 'actual_sha256': observed, 'expected_sha256': expected,
                           'match': observed == expected})
        result['pixels'] = pixels
        paired.run_tool([paired.SCP, *paired.OPTIONS, paired.PEER + ':' + REMOTE + '/composed-last.ppm', args.results / 'composed-last.ppm'], 20)
        r, s = result['receiver'], result['sender']
        result['functional_pass'] = r['functional_pass'] and all(p['match'] for p in pixels)
        # Real coded data and actual composed cadence, not selected target Mbps.
        duration = max(s['elapsed_seconds'], r['elapsed_seconds']) + 1 / 90
        result['delivered_video_mbps'] = r['received_video_bytes'] * 8 / duration / 1e6
        result['throughput_pass'] = (result['functional_pass'] and result['delivered_video_mbps'] >= args.minimum_mbps
                                     and s['elapsed_seconds'] <= args.seconds + .1
                                     and r['rendered_fps'] >= 89 and r['rendered'] == args.seconds * 90 - 1
                                     and r['presentation_queue_drops'] == 0 and r['clock_resets'] == 0)
        result['presentation_latency_pass'] = (result['throughput_pass'] and r['presentation_interval_p99_ms'] <= 1000 / 90 * 1.25)
    except Exception as error:
        result['error'] = str(error)
    finally:
        if receiver is not None and receiver.poll() is None:
            receiver.terminate()
            try:
                receiver.wait(timeout=3)
            except subprocess.TimeoutExpired:
                receiver.kill()
                receiver.wait()
        result['receiver_diagnostics'] = diagnostics
        if stopped:
            try:
                result['restart_stream'] = paired.cef('start')
            except Exception as error:
                result['restart_error'] = str(error)
        (args.results / 'result.json').write_text(json.dumps(result, indent=2))
    print(json.dumps(result), flush=True)
    return 0 if result['throughput_pass'] else 1


if __name__ == '__main__':
    raise SystemExit(main())
