"""Live PC NVENC -> paired dongle TCP -> Frame V4L2 AV1; no VRLink/display.

Requires the separately authenticated, temporarily armed decoder candidate.
Only the SSH receiver/process started here is stopped on an error.
"""
import argparse
import hashlib
import json
import pathlib
import queue
import subprocess
import threading
import time

parser = argparse.ArgumentParser()
parser.add_argument('backend', type=pathlib.Path)
parser.add_argument('results', type=pathlib.Path)
parser.add_argument('--frames', type=int, default=1200)
parser.add_argument('--mbps', type=int, default=100)
args = parser.parse_args()
if not 1200 <= args.frames <= 7200 or not 1 <= args.mbps <= 200:
    raise ValueError('Use 1200..7200 frames and 1..200 Mbps')
backend = args.backend.resolve()
args.results.mkdir(parents=True, exist_ok=True)
options = ['-i', r'C:\Users\ellio\AppData\Local\steamos-devkit\devkit_rsa',
           '-o', 'IdentitiesOnly=yes', '-o', 'BatchMode=yes', '-o', 'StrictHostKeyChecking=yes',
           '-o', r'UserKnownHostsFile=C:\Users\ellio\AppData\Local\SteamLinkVRSettings\ssh-known-hosts',
           '-o', 'ConnectTimeout=3']
ssh = r'C:\Windows\System32\OpenSSH\ssh.exe'
remote = '/tmp/steam-frame-av1-lab-20261011/hardware_decode /dev/video22 tcp paced'
receiver = subprocess.Popen([ssh, *options, 'steamos@10.35.78.1', remote],
                            stdout=subprocess.PIPE, stderr=subprocess.PIPE, text=True)
signals = queue.Queue()
diagnostics = []

def read_errors():
    for line in receiver.stderr:
        diagnostics.append(line.strip())
        if '"dongle_listener_ready":true' in line:
            signals.put(True)
    signals.put(False)

threading.Thread(target=read_errors, daemon=True).start()
result = {'hardware_path': True, 'steamvr_integration': False, 'display_tested': False}
try:
    if not signals.get(timeout=15):
        raise RuntimeError('Frame hardware listener unavailable')
    started = time.monotonic()
    source = subprocess.run([str(backend), '1536', '6144', str(args.frames), 'dongle', '8',
                             str(args.mbps), '17', '0', '0'], cwd=args.results,
                            capture_output=True, text=True, timeout=args.frames / 120 + 25)
    receiver.wait(timeout=10)
    output = receiver.stdout.read()
    result['wall_seconds'] = time.monotonic() - started
    result['host_exit_code'] = source.returncode
    result['frame_exit_code'] = receiver.returncode
    result['host'] = json.loads(source.stdout) if source.returncode == 0 else {'error': source.stderr[-1000:]}
    result['frame'] = json.loads(output) if receiver.returncode == 0 else {'error': '; '.join(diagnostics[-8:])}
    result['functional_pass'] = source.returncode == receiver.returncode == 0 and result['frame'].get('functional_pass') is True
    result['120fps_pass'] = result['functional_pass'] and result['host'].get('deadline_misses') == 0 and result['frame'].get('120fps_pass') is True
    if result['functional_pass']:
        reference = args.results / 'source-firstframe.nv12'
        decode = subprocess.run([r'C:\Users\ellio\ffmpeg\ffmpeg-8.1-essentials_build\bin\ffmpeg.exe',
                                 '-hide_banner', '-loglevel', 'error', '-i', str(args.results / 'backend-test.ivf'),
                                 '-frames:v', '1', '-f', 'rawvideo', '-pix_fmt', 'nv12', '-y', str(reference)],
                                capture_output=True, text=True, timeout=20)
        if decode.returncode:
            raise RuntimeError('Independent first-frame reference decode failed')
        proof = subprocess.run([ssh, *options, 'steamos@10.35.78.1',
                                'sha256sum /tmp/steam-frame-av1-lab-20261011/hardware-live.nv12'],
                               capture_output=True, text=True, timeout=5)
        expected = hashlib.sha256(reference.read_bytes()).hexdigest()
        actual = proof.stdout.split()[0] if proof.returncode == 0 and proof.stdout.split() else None
        result['pixel_verification'] = {'source_sha256': expected, 'hardware_sha256': actual, 'exact_match': actual == expected}
        result['functional_pass'] = result['functional_pass'] and actual == expected
        result['120fps_pass'] = result['120fps_pass'] and actual == expected
except Exception as error:
    result.update(error=str(error), functional_pass=False, **{'120fps_pass': False})
finally:
    if receiver.poll() is None:
        receiver.terminate()
        try:
            receiver.wait(timeout=5)
        except subprocess.TimeoutExpired:
            receiver.kill()
            receiver.wait()
    (args.results / 'hardware-dongle.json').write_text(json.dumps(result, indent=2), encoding='utf-8')
print(json.dumps(result))
raise SystemExit(0 if result.get('functional_pass') else 1)
