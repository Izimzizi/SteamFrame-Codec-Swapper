"""Host-side regression checks; never touches a real Frame."""
import hashlib
import json
import pathlib
import struct
import subprocess
import tempfile
import types
import unittest
from unittest.mock import patch
import root_session as session


STOCK_SHA = 'a' * 64
H264_SHA = 'b' * 64


def elf_with(*words, executable=True):
    """A minimal AArch64 ELF64 file whose one loadable segment holds the given instruction words."""
    code = b''.join(struct.pack('<I', word) for word in words)
    header = bytearray(64)
    header[0:6] = b'\x7fELF\x02\x01'
    struct.pack_into('<H', header, 18, 183)
    struct.pack_into('<Q', header, 32, 64)
    struct.pack_into('<HH', header, 54, 56, 1)
    program = bytearray(56)
    struct.pack_into('<II', program, 0, 1, 5 if executable else 4)
    struct.pack_into('<QQQQQ', program, 8, 120, 0x1000, 0x1000, len(code), len(code))
    return bytes(header + program + code)


def movz(register, part):
    return 0x52800000 | (part << 5) | register


def movk(register, part):
    return 0x72A00000 | (part << 5) | register


HEVC = (movz(3, 0x4548), 0xD503201F, movk(3, 0x4356))
FILLER = (0xD503201F, 0xD65F03C0)


class CodecPatchTests(unittest.TestCase):
    def test_one_hevc_name_load_is_a_stock_client_and_is_patched_to_h264(self):
        original = elf_with(*FILLER, *HEVC, *FILLER)
        self.assertEqual(session.client_kind(original), 'stock')
        patched = session.make_client(original, 'H.264')
        self.assertEqual(session.client_kind(bytes(patched)), 'h264')
        self.assertEqual(len(patched), len(original))
        changed = [i for i in range(len(original)) if original[i] != patched[i]]
        self.assertTrue(changed and all(128 <= i < 132 or 136 <= i < 140 for i in changed))   # only the two instructions

    def test_the_register_and_a_gap_between_the_two_instructions_do_not_matter(self):
        original = elf_with(movz(9, 0x4548), *FILLER, *FILLER, *FILLER, movk(9, 0x4356))
        self.assertEqual(session.client_kind(original), 'stock')
        self.assertEqual(session.client_kind(bytes(session.make_client(original, 'H.264'))), 'h264')

    def test_the_second_half_must_use_the_same_register_and_be_close(self):
        self.assertEqual(session.client_kind(elf_with(movz(3, 0x4548), movk(4, 0x4356))), 'unknown')
        self.assertEqual(session.client_kind(elf_with(movz(3, 0x4548), *FILLER * 5, movk(3, 0x4356))), 'unknown')

    def test_no_match_or_several_matches_are_refused(self):
        for code in ((*FILLER,), (*HEVC, *FILLER, *HEVC)):
            original = elf_with(*code)
            self.assertEqual(session.client_kind(original), 'unknown')
            with self.assertRaisesRegex(RuntimeError, 'not recognised'):
                session.make_client(original, 'H.264')

    def test_a_client_that_loads_both_names_is_not_recognised(self):
        both = elf_with(*HEVC, movz(5, 0x3248), movk(5, 0x3436))
        self.assertEqual(session.client_kind(both), 'unknown')

    def test_data_segments_are_not_scanned(self):
        self.assertEqual(session.client_kind(elf_with(*HEVC, executable=False)), 'unknown')

    def test_only_aarch64_elf_files_are_accepted(self):
        with self.assertRaisesRegex(RuntimeError, 'AArch64'):
            session.client_kind(b'not an elf' * 20)


class RootSessionTests(unittest.TestCase):
    def test_open_guard_follows_decoder_number(self):
        with tempfile.TemporaryDirectory() as temporary:
            process = pathlib.Path(temporary) / '123'
            (process / 'fd').mkdir(parents=True)
            (process / 'fd' / '8').touch()
            (process / 'comm').write_text('hardware_decode\n')
            real_path = pathlib.Path
            def path(value):
                return real_path(temporary) if str(value) == '/proc' else real_path(value)
            with patch.object(session, 'pathlib', types.SimpleNamespace(Path=path)), \
                 patch.object(session, 'decoder_nodes', return_value=['/dev/video0']), \
                 patch.object(session.os, 'readlink', return_value='/dev/video0'):
                self.assertEqual(session.opens(), [{'pid': 123, 'comm': 'hardware_decode', 'device': '/dev/video0'}])


class KeepModeTests(unittest.TestCase):
    def adoptable(self, staged):
        (staged / 'prepared.json').write_text(json.dumps({'client_stock_sha256': STOCK_SHA, 'client_h264_sha256': H264_SHA}))
        (staged / 'client-h264.bin').write_bytes(b'h264')
        (staged / 'client-stock.bin').write_bytes(b'stock')
        shas = {'client-h264.bin': H264_SHA, 'client-stock.bin': STOCK_SHA}
        def digest(path):
            return shas.get(pathlib.Path(path).name, H264_SHA)
        return digest

    def test_adopt_takes_over_a_mounted_h264_client(self):
        with tempfile.TemporaryDirectory() as temporary:
            staged = pathlib.Path(temporary)
            digest = self.adoptable(staged)
            with patch.object(session, 'RUN', staged), patch.object(session, 'digest', digest),                  patch.object(session, 'bind_mounted', return_value=True),                  patch.object(session, 'video_path', return_value=staged),                  patch.object(session.pathlib.Path, 'resolve', return_value=pathlib.Path('/qcom-iris')),                  patch.object(session.os, 'getuid', create=True),                  patch.object(session.pathlib.Path, 'lstat', return_value=types.SimpleNamespace(st_uid=0)),                  patch.object(session, 'client_active', False), patch.object(session, 'client_codec', 'HEVC'):
                self.assertTrue(session.adopt()['adopted'])
                self.assertTrue(session.client_active)
                self.assertEqual(session.client_codec, 'H.264')

    def test_adopt_refuses_without_a_real_h264_mount(self):
        with tempfile.TemporaryDirectory() as temporary:
            staged = pathlib.Path(temporary)
            digest = self.adoptable(staged)
            common = [patch.object(session, 'RUN', staged), patch.object(session, 'digest', digest),
                      patch.object(session.pathlib.Path, 'lstat', return_value=types.SimpleNamespace(st_uid=0)),
                      patch.object(session, 'client_active', False)]
            for mounted, client in ((False, H264_SHA), (True, STOCK_SHA)):
                def digest_for(path, client=client):
                    return client if pathlib.Path(path) == session.CLIENT else digest(path)
                with patch.object(session, 'bind_mounted', return_value=mounted), patch.object(session, 'digest', digest_for):
                    for context in common[:1] + common[2:]:
                        context.start()
                    try:
                        with self.assertRaisesRegex(RuntimeError, 'No H.264 client mount'):
                            session.adopt()
                    finally:
                        patch.stopall()

    def test_detach_only_for_a_mounted_h264_client(self):
        with patch.object(session, 'detached', False):
            with patch.object(session, 'client_active', False):
                with self.assertRaises(RuntimeError):
                    session.detach()
            with patch.object(session, 'client_active', True), patch.object(session, 'client_codec', 'HEVC'):
                with self.assertRaises(RuntimeError):
                    session.detach()
            with patch.object(session, 'client_active', True), patch.object(session, 'client_codec', 'H.264'):
                self.assertTrue(session.detach()['detached'])
                self.assertTrue(session.detached)

    def test_staging_leaves_a_matching_file_untouched(self):
        with tempfile.TemporaryDirectory() as temporary:
            target = pathlib.Path(temporary) / 'client-h264.bin'
            session.stage(target, b'payload', 0o755)
            first = target.stat().st_ino, target.stat().st_mtime_ns
            with patch.object(pathlib.Path, 'write_bytes', side_effect=AssertionError('rewritten')):
                session.stage(target, b'payload', 0o755)
            self.assertEqual((target.stat().st_ino, target.stat().st_mtime_ns), first)
            session.stage(target, b'other', 0o755)
            self.assertEqual(target.read_bytes(), b'other')


if __name__ == '__main__':
    unittest.main()
