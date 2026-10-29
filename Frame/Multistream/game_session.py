"""Bounded Home test with a temporary low-bitrate Valve tracking/audio stream.

Uses an already-authenticated app codec session. Restores its previous codec,
bitrate and refresh rate on exit; does not overwrite a newer app operation.
"""
import argparse
import ctypes as c
import json
import os
import pathlib
import subprocess
import sys
import time
import uuid

ROOT = pathlib.Path(__file__).resolve().parents[2]
SESSION = pathlib.Path(os.environ.get('LOCALAPPDATA', '.')) / 'SteamFrameTray/CodecSession'


def owner_created(pid):
    win = c.WinDLL('kernel32', use_last_error=True)
    win.OpenProcess.argtypes = [c.c_uint, c.c_int, c.c_uint]
    win.OpenProcess.restype = c.c_void_p
    win.GetProcessTimes.argtypes = [c.c_void_p] * 5
    win.CloseHandle.argtypes = [c.c_void_p]
    handle = win.OpenProcess(0x1000, False, pid)
    if not handle:
        raise RuntimeError('Codec session owner is unavailable')
    try:
        times = [c.c_uint64() for _ in range(4)]
        if not win.GetProcessTimes(handle, *[c.byref(t) for t in times]):
            raise RuntimeError('Cannot verify codec session owner')
        return times[0].value / 10000000 - 11644473600
    finally:
        win.CloseHandle(handle)


def status():
    value = json.loads((SESSION / 'status.json').read_text())
    if (value.get('protocol_version') != 1 or abs(time.time() - value['updated_epoch']) > 6
            or abs(owner_created(value['owner_pid']) - value['owner_created_epoch']) > 2):
        raise RuntimeError('An authenticated, responsive app codec session is required')
    return value


def same_owner(a, b):
    return a['owner_pid'] == b['owner_pid'] and a['owner_created_epoch'] == b['owner_created_epoch']


def owns_control(current, previous, operation):
    return (same_owner(current, previous) and current['operation_id'] == operation
            and current['state'] == 'ready' and current['active_codec'] == 'H.264'
            and current['target_mbps'] == 25 and current['fps'] == 90)


def wait_for_operation(identifier, owner):
    for _ in range(240):
        time.sleep(.5)
        current = status()
        if not same_owner(current, owner):
            raise RuntimeError('Codec session owner changed during the switch')
        if current['operation_id'] != identifier:
            continue
        if current['state'] == 'error':
            raise RuntimeError('Codec service could not apply the requested mode')
        if current['state'] == 'ready':
            if not current.get('last_result', {}).get('ok'):
                raise RuntimeError('Codec service reported an unsuccessful switch')
            return identifier
    raise RuntimeError('Codec operation did not finish within two minutes')


def switch(codec, mbps, fps, owner, identifier=None, expected_operation=None, retry=False):
    current = status()
    if (current['state'] not in (('ready', 'error') if retry else ('ready',))
            or not same_owner(current, owner)
            or current['operation_id'] != (expected_operation or owner['operation_id'])):
        raise RuntimeError('Codec session changed before the requested switch')
    identifier = identifier or str(uuid.uuid4())
    request = dict(protocol_version=1, id=identifier, action='switch', codec=codec,
                   target_mbps=mbps, fps=fps)
    path = SESSION / 'requests' / ('request-' + identifier + '.json')
    temporary = path.with_suffix('.tmp')
    temporary.write_text(json.dumps(request))
    temporary.replace(path)
    return wait_for_operation(identifier, owner)


def restore(previous, operation, report):
    current = status()
    if same_owner(current, previous) and current['operation_id'] == operation and current['state'] not in ('ready', 'error'):
        try:
            wait_for_operation(operation, previous)
        except RuntimeError:
            pass
        current = status()
    failed_ours = same_owner(current, previous) and current['operation_id'] == operation and current['state'] == 'error'
    if not owns_control(current, previous, operation) and not failed_ours:
        report['restore_skipped'] = 'A newer app operation owns the settings, or our switch is still pending.'
        return
    print('Restoring the previous app stream settings.', flush=True)
    expected = operation
    report['restore_attempts'] = []
    for attempt in range(2):
        identifier = str(uuid.uuid4())
        try:
            switch(previous['active_codec'], previous['target_mbps'], previous['fps'], previous,
                   identifier=identifier, expected_operation=expected, retry=failed_ours or attempt > 0)
            report['restored'] = True
            break
        except Exception as error:
            report['restore_attempts'].append(str(error))
            current = status()
            # Retry only our own completed failure. Never take over a user switch.
            if not (same_owner(current, previous) and current['operation_id'] == identifier and current['state'] == 'error'):
                break
            expected = identifier
            # Recovery starts the stock client asynchronously. Let it settle
            # before another codec transition, checking ownership again below.
            time.sleep(5)
    if not report['restored']:
        raise RuntimeError('Previous app settings could not be restored; see session.json')


def home():
    runtime = pathlib.Path(os.environ.get('STEAMVR_RUNTIME', r'C:\Program Files (x86)\Steam\steamapps\common\SteamVR'))
    loader = c.WinDLL(str(runtime / 'bin/win64/openvr_api.dll'))
    error = c.c_int()
    loader.VR_InitInternal2.argtypes = [c.POINTER(c.c_int), c.c_int, c.c_char_p]
    loader.VR_InitInternal2.restype = c.c_uint
    loader.VR_InitInternal2(c.byref(error), 3, None)
    if error.value:
        raise RuntimeError('SteamVR Home runtime initialization failed')
    try:
        loader.VR_GetGenericInterface.argtypes = [c.c_char_p, c.POINTER(c.c_int)]
        loader.VR_GetGenericInterface.restype = c.c_void_p
        table = loader.VR_GetGenericInterface(b'FnTable:IVRApplications_008', c.byref(error))
        if not table or error.value:
            raise RuntimeError('SteamVR application interface unavailable')
        launch = c.WINFUNCTYPE(c.c_int, c.c_char_p)(c.c_void_p.from_address(table + 6 * c.sizeof(c.c_void_p)).value)
        if launch(b'openvr.tool.steamvr_environments') not in (0, 106, 110):
            raise RuntimeError('SteamVR Home could not start')
    finally:
        loader.VR_ShutdownInternal()


def stop_home():
    runtime = pathlib.Path(os.environ.get('STEAMVR_RUNTIME', r'C:\Program Files (x86)\Steam\steamapps\common\SteamVR'))
    executable = str(runtime / 'tools/steamvr_environments/game/bin/win64/steamtours.exe').replace("'", "''")
    script = ("Get-Process steamtours -ErrorAction SilentlyContinue | Where-Object { $_.Path -eq '" + executable
              + "' } | ForEach-Object { Stop-Process -Id $_.Id -ErrorAction Stop }; exit 0")
    subprocess.run(['powershell.exe', '-NoProfile', '-Command', script], check=True,
                   creationflags=subprocess.CREATE_NO_WINDOW, timeout=10)


def main():
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument('--mbps', type=int, default=550)
    parser.add_argument('--seconds', type=int, default=30)
    parser.add_argument('--minimum-mbps', type=int, default=400)
    parser.add_argument('--results', type=pathlib.Path, required=True)
    parser.add_argument('--udp', action='store_true')
    parser.add_argument('--overlay', action='store_true', help='Display the experimental projection layers in the headset')
    args = parser.parse_args()
    if not 25 <= args.mbps <= 600 or not 10 <= args.seconds <= 60 or not 1 <= args.minimum_mbps <= args.mbps:
        parser.error('Invalid bounded test parameters')
    args.results = args.results.resolve()
    args.results.mkdir(parents=True, exist_ok=True)
    previous = status()
    report = {'previous': previous, 'restored': False}
    operation = str(uuid.uuid4())
    try:
        print('Starting temporary 25 Mbps tracking/audio connection.', flush=True)
        switch('H.264', 25, 90, previous, identifier=operation)
        home()
        time.sleep(4)
        command = [sys.executable, str(ROOT / 'Frame/Multistream/run_trial.py'), '--game', '--streams', '4',
                   '--mbps', str(args.mbps), '--minimum-mbps', str(args.minimum_mbps), '--seconds', str(args.seconds),
                   '--skip-build', '--results', str(args.results)]
        if args.udp:
            command.append('--udp')
        if args.overlay:
            command.append('--overlay')
        return subprocess.run(command, cwd=ROOT).returncode
    finally:
        try:
            current = status()
            if same_owner(current, previous) and current['operation_id'] == operation:
                stop_home()
            restore(previous, operation, report)
            if report['restored']:
                home()
        except Exception as error:
            report['cleanup_error'] = str(error)
            raise
        finally:
            (args.results / 'session.json').write_text(json.dumps(report, indent=2))


if __name__ == '__main__':
    raise SystemExit(main())
