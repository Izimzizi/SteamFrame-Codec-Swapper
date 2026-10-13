"""Observe the already-running HEVC monitor and paired headset; no setting changes."""
import argparse
import collections
import datetime
import json
import os
import pathlib
import subprocess
import sys
import time

sys.path.insert(0, str(pathlib.Path(__file__).resolve().parents[1] / 'AV1'))
import h264_multistream_trial as paired

VR_CMD = pathlib.Path(os.environ.get('STEAMVR_RUNTIME', r'C:\Program Files (x86)\Steam\steamapps\common\SteamVR')) / 'bin/win64/vrcmd.exe'


def pc_compositor():
    values = json.loads(subprocess.run([str(VR_CMD), '--stats'], capture_output=True, text=True,
                                      check=True, timeout=5, creationflags=subprocess.CREATE_NO_WINDOW).stdout)
    return {'applications': [x for x in values if x.get('operation') == 'compositor_stats'],
            'home': next((x for x in values if x.get('key') == 'openvr.tool.steamvr_environments'), None),
            'status': next((x for x in values if x.get('operation') == 'status'), None)}

REMOTE = r'''
import json,pathlib,subprocess,time
values=json.loads(subprocess.run(['/opt/steamvr/bin/linuxarm64/vrcmd','--stats'],capture_output=True,text=True,check=True,timeout=5).stdout)
base=pathlib.Path('/sys/class/net/wlanap/statistics')
print(json.dumps({'monotonic':time.monotonic(),'compositor':next((x for x in values if x.get('key')=='vrlink.client'),None),
 'interface':{k:int((base/k).read_text()) for k in ('rx_bytes','rx_errors','rx_dropped')}}))
'''


def snapshot(path):
    if time.time() - path.stat().st_mtime > 4:
        raise RuntimeError('App encoder monitor is stale; keep its live monitor running')
    value = json.loads(path.read_text())
    if value['codec'] != 'HEVC' or not value['active'] or not value['installed']:
        raise RuntimeError('Fresh HEVC output is required')
    return value


def probe_snapshot(executable):
    observed = json.loads(subprocess.run([str(executable.resolve())], capture_output=True, text=True,
                                        check=True, timeout=10, creationflags=subprocess.CREATE_NO_WINDOW).stdout)['Snapshot']
    policy, timing = observed['EncodingPolicy'], observed['CallbackTiming']
    value = {key: observed[source] for key, source in {
        'generation': 'Generation', 'timestamp_seconds': 'TimestampSeconds', 'encoded_bytes': 'EncodedBytes',
        'encoded_frames': 'EncodedFrames', 'encode_errors': 'EncodeErrors', 'lock_errors': 'LockErrors',
        'bit_depth': 'BitDepth', 'quantizer_samples': 'QuantizerSamples', 'quantizer_sum': 'QuantizerSum'}.items()}
    value.update({key: policy[source] for key, source in {
        'encode_width': 'Width', 'encode_height': 'Height', 'average_bitrate': 'AverageBitrate',
        'max_bitrate': 'MaxBitrate', 'filler': 'Filler'}.items()})
    value['timing'] = {}
    for key in ('completion', 'encode', 'reconfigure'):
        for suffix, managed in (('_calls', 'Calls'), ('_total_us', 'TotalUs')):
            value['timing'][key + suffix] = timing[key.capitalize() + managed]
    value['managed_snapshot'] = observed
    return value


def summary(samples):
    a, b = samples[0], samples[-1]
    pc0, pc1 = a['pc'], b['pc']
    if pc0['generation'] != pc1['generation']:
        raise RuntimeError('Encoder generation changed during measurement')
    duration = pc1['timestamp_seconds'] - pc0['timestamp_seconds']
    frame_duration = b['frame']['monotonic'] - a['frame']['monotonic']
    result = {'seconds': duration, 'encoded_mbps': (pc1['encoded_bytes'] - pc0['encoded_bytes']) * 8 / duration / 1e6,
              'encoded_fps': (pc1['encoded_frames'] - pc0['encoded_frames']) / duration,
              'encode_errors': pc1['encode_errors'] - pc0['encode_errors'],
              'lock_errors': pc1['lock_errors'] - pc0['lock_errors'],
              'width': pc1['encode_width'], 'height': pc1['encode_height'], 'bit_depth': pc1['bit_depth'],
              'requested_encoder_mbps': pc1['average_bitrate'] / 1e6,
              'accepted_peak_mbps': pc1['max_bitrate'] / 1e6, 'filler': pc1['filler'],
              'frame_delta': {}, 'callback_mean_ms': {}}
    for key in ('frame_submits', 'frame_presents', 'dropped_frames', 'reprojected_frames', 'frame_presents_timed_out'):
        result['frame_delta'][key] = b['frame']['compositor'][key] - a['frame']['compositor'][key]
    if a.get('pc_compositor', {}).get('home') and b.get('pc_compositor', {}).get('home'):
        result['home_delta'] = {key: b['pc_compositor']['home'][key] - a['pc_compositor']['home'][key]
                                for key in result['frame_delta']}
    applications = collections.defaultdict(list)
    for sample in samples:
        for app in sample.get('pc_compositor', {}).get('applications', []):
            applications[app['key']].append((sample['pc']['timestamp_seconds'], app))
    result['application_deltas'] = {}
    for key, rows in applications.items():
        if len(rows) < 2:
            continue
        first, last = rows[0], rows[-1]
        delta = {field: last[1][field] - first[1][field] for field in result['frame_delta']}
        delta['seconds_observed'] = last[0] - first[0]
        delta['counters_reset'] = any(delta[field] < 0 for field in result['frame_delta'])
        result['application_deltas'][key] = delta
    for key in ('completion', 'encode', 'reconfigure'):
        calls = pc1['timing'][key + '_calls'] - pc0['timing'][key + '_calls']
        total = pc1['timing'][key + '_total_us'] - pc0['timing'][key + '_total_us']
        result['callback_mean_ms'][key] = total / calls / 1000 if calls else None
    qp_samples = pc1['quantizer_samples'] - pc0['quantizer_samples']
    result['whole_frame_qp_mean'] = ((pc1['quantizer_sum'] - pc0['quantizer_sum']) / qp_samples
                                     if qp_samples else None)
    result['network_mbps'] = (b['frame']['interface']['rx_bytes'] - a['frame']['interface']['rx_bytes']) * 8 / frame_duration / 1e6
    result['network_errors'] = b['frame']['interface']['rx_errors'] - a['frame']['interface']['rx_errors']
    result['network_drops'] = b['frame']['interface']['rx_dropped'] - a['frame']['interface']['rx_dropped']
    return result


def main():
    parser = argparse.ArgumentParser(description=__doc__)
    source = parser.add_mutually_exclusive_group(required=True)
    source.add_argument('--status', type=pathlib.Path)
    source.add_argument('--probe', type=pathlib.Path)
    parser.add_argument('--output', type=pathlib.Path, required=True)
    parser.add_argument('--seconds', type=int, default=60)
    args = parser.parse_args()
    if not 10 <= args.seconds <= 300 or args.seconds % 10:
        parser.error('Use a multiple of ten seconds from 10 to 300')
    args.output.parent.mkdir(parents=True, exist_ok=True)
    report = {'started': datetime.datetime.now().astimezone().isoformat(), 'completed': False, 'samples': []}
    diagnostic_dir = pathlib.Path(os.environ['LOCALAPPDATA']) / 'SteamLinkVRSettings/Diagnostics'
    offsets = {p: p.stat().st_size for p in diagnostic_dir.glob('*health.jsonl')}
    try:
        began = time.monotonic()
        for index in range(args.seconds // 10 + 1):
            time.sleep(max(0, began + index * 10 - time.monotonic()))
            sample = {'pc': snapshot(args.status) if args.status else probe_snapshot(args.probe),
                      'frame': json.loads(paired.remote(REMOTE)), 'pc_compositor': pc_compositor()}
            report['samples'].append(sample)
            if index:
                report['summary'] = summary(report['samples'])
                print(json.dumps(report['summary']), flush=True)
            args.output.write_text(json.dumps(report, indent=2))
        report['completed'] = True
    except Exception as error:
        report['error'] = str(error)
        raise
    finally:
        events = collections.Counter()
        for path, offset in offsets.items():
            with path.open() as reader:
                reader.seek(offset if path.stat().st_size >= offset else 0)
                for line in reader:
                    try:
                        event = json.loads(line)
                        events[path.name + ':' + event['category']] += 1
                    except (ValueError, KeyError):
                        pass
        report['new_diagnostic_events'] = dict(events)
        args.output.write_text(json.dumps(report, indent=2))


if __name__ == '__main__':
    main()
