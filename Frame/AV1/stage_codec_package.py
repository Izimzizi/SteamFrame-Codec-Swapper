"""Stage the supervised codec companion and isolated CPython runtime.

All runtime helper sources live in the repository. The pinned decoder binary
must already have passed the exact-kernel acceptance tests; this script never
builds, loads or installs a module. No Valve executable or firmware is bundled.
"""
import argparse
import hashlib
import json
import pathlib
import shutil
import subprocess
import sys
import zipfile

ROOT = pathlib.Path(__file__).resolve().parents[2]
SOURCE = ROOT / 'Frame/AV1'
OUT = ROOT / 'artifacts/CodecLab'
DECODER_SHA = 'c83ea764512488af7e9c3a357d78c21a4f32d4d193c39de01bb075226d440339'
# Remote CEF helpers execute using the Frame's Python. Only the extensions
# needed by the local supervisor are shipped with the PC interpreter.
EXTENSIONS = ('_ctypes.pyd', 'libffi-8.dll')
RUNTIME_FILES = ('python.exe', 'python3.dll', 'python313.dll', 'vcruntime140.dll',
                 'vcruntime140_1.dll', 'zlib1.dll', 'LICENSE.txt')
HELPERS = ('codec_supervisor.py', 'root_session.py', 'frame_rate_remote.py',
           'frame_cef_stdio.py', 'start_stream.js', 'stop_stream.js')


def copy_required(source, destination):
    if not source.is_file() or source.is_symlink():
        raise RuntimeError('Required regular staging input is unavailable: ' + str(source))
    shutil.copy2(source, destination)


def main():
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument('--candidate', type=pathlib.Path,
                        default=ROOT / 'artifacts/AV1Lab/iris-7.0-frame-candidate')
    parser.add_argument('--backend', type=pathlib.Path, default=OUT / 'Backend',
                        help='Directory containing the final, tested PC backend/controller')
    parser.add_argument('--decoder-module', type=pathlib.Path,
                        help='Pinned decoder module, when stored separately from its source')
    args = parser.parse_args()
    candidate = args.candidate.resolve()
    module = args.decoder_module or candidate / 'steam-frame-av1-lab.ko'
    if args.decoder_module is None and not module.is_file():
        module = OUT / 'Assets/steam-frame-av1-lab.ko'
    if not module.is_file() or hashlib.sha256(module.read_bytes()).hexdigest() != DECODER_SHA:
        raise RuntimeError('The pinned, previously tested Frame decoder candidate is required')
    for required in ('Makefile', 'provenance.json', 'frame-port.patch',
                     'frame_av1_compat.h', 'frame_firmware_no_tz.c', 'v4l2-mem2mem.c'):
        if not (candidate / required).is_file():
            raise RuntimeError('Corresponding decoder source is incomplete: ' + required)
    if sys.version_info[:2] != (3, 13):
        raise RuntimeError('This staging recipe requires CPython 3.13')
    OUT.mkdir(parents=True, exist_ok=True)
    runtime = OUT / 'runtime'
    dlls = runtime / 'DLLs'
    assets = OUT / 'Assets'
    backend = OUT / 'Backend'
    for directory in (OUT, runtime, dlls, assets, backend):
        if directory.is_symlink():
            raise RuntimeError('Staging directories must not be symlinks')
        directory.mkdir(exist_ok=True)
    base = pathlib.Path(sys.base_prefix)
    for name in RUNTIME_FILES:
        location = base / name
        if name == 'zlib1.dll':
            location = base / 'DLLs' / name
        copy_required(location, runtime / name)
    # Every cleanup target is a direct child of the fixed artifact/DLLs directory.
    for path in dlls.iterdir():
        if path.is_file() and path.suffix.lower() in ('.pyd', '.dll') and path.name not in EXTENSIONS:
            path.unlink()
    for name in EXTENSIONS:
        copy_required(base / 'DLLs' / name, dlls / name)
    with zipfile.ZipFile(runtime / 'python313.zip', 'w',
                         compression=zipfile.ZIP_DEFLATED, compresslevel=6) as archive:
        for path in sorted((base / 'Lib').rglob('*.py')):
            relative = path.relative_to(base / 'Lib')
            if any(part in ('site-packages', 'test', 'tests', '__pycache__') for part in relative.parts):
                continue
            archive.write(path, relative.as_posix())
    (runtime / 'python313._pth').write_text('python313.zip\n.\nDLLs\n..\n', encoding='ascii')
    for name in HELPERS:
        copy_required(SOURCE / name, OUT / name)
    for name in ('SteamFrameAV1Backend.dll', 'trial_control.exe'):
        source_binary = args.backend.resolve() / name
        if not source_binary.is_file() or source_binary.is_symlink():
            raise RuntimeError('Final PC codec backend is unavailable: ' + name)
        if source_binary.resolve() != (backend / name).resolve():
            copy_required(source_binary, backend / name)
    if module.resolve() != (assets / 'steam-frame-av1-lab.ko').resolve():
        copy_required(module, assets / 'steam-frame-av1-lab.ko')
    copy_required(SOURCE / 'GPL-2.0-only.txt', assets / 'GPL-2.0-only.txt')
    copy_required(SOURCE / 'decoder-build.md', assets / 'decoder-build.md')
    with zipfile.ZipFile(assets / 'decoder-source.zip', 'w',
                         compression=zipfile.ZIP_DEFLATED) as archive:
        for path in sorted(candidate.iterdir()):
            if path.is_file() and (path.suffix in ('.c', '.h') or path.name in
                                  ('Makefile', 'Kbuild', 'Kconfig', 'provenance.json', 'frame-port.patch')):
                archive.write(path, path.name)
        archive.write(SOURCE / 'GPL-2.0-only.txt', 'COPYING')
        archive.write(SOURCE / 'decoder-build.md', 'BUILDING.md')
        for name in ('fetch_upstream_iris.py', 'prepare_iris_candidate.py',
                     'frame_av1_compat.h', 'frame_firmware_no_tz.c', 'frame_firmware_no_tz.h'):
            archive.write(SOURCE / name, 'tools/' + name)
    (OUT / 'README.txt').write_text(
        'Temporary paired codecs for Frame kernel 6.18.0-ge66bc2ca6f8c.\n'
        'Frame administrator authentication and trusted developer SSH are required.\n'
        'AV1 uses the bundled GPL-2.0-only experimental Iris module.\n'
        'Corresponding source, provenance, port patch, license and build instructions: Assets/decoder-source.zip.\n'
        'Client patches are generated on the Frame from its exact installed build; no Valve client or firmware is distributed.\n'
        'Returning to HEVC restores the PC stream settings from before the session. Codec changes restart SteamVR.\n'
        'Quitting SteamVR ends the session without relaunching it. After 90 minutes HEVC is restored when SteamVR exits,\n'
        'or automatically before the two-hour Frame administrator session limit.\n'
        'H.264 uses a 1024x4096 packed layout; AV1 uses up to 1536x6144.\n'
        'Bundled CPython license and included third-party notices: runtime/LICENSE.txt.\n',
        encoding='utf-8')
    # Imports/IPC setup only: never authenticate, start SteamVR or open a decoder.
    check = subprocess.run([str(runtime / 'python.exe'), '-I', '-c',
                            "import codec_supervisor as s; import ctypes,uuid,msvcrt,subprocess; "
                            "s.validate_mode('AV1',100,90); "
                            "s.validate_request({'protocol_version':1,'id':str(uuid.uuid4()),'action':'close'}); "
                            "assert s.creation_epoch()>0; print('isolated codec runtime imports passed')"],
                           capture_output=True, text=True, timeout=20)
    if check.returncode:
        raise RuntimeError('Isolated codec runtime import validation failed: ' + check.stderr[-1000:])
    print(json.dumps({'staged': str(OUT), 'python': sys.version.split()[0],
                      'runtime_extensions': EXTENSIONS,
                      'decoder_sha256': DECODER_SHA,
                      'isolated_runtime_imports': 'passed',
                      'original_valve_binaries_bundled': False}))


if __name__ == '__main__':
    main()
