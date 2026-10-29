"""Host-side regression checks; never touches a real Frame."""
import pathlib
import subprocess
import tempfile
import types
import unittest
from unittest.mock import patch
import root_session as session


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
        (staged / 'prepared.json').write_text('{}')
        (staged / 'client-h264.bin').write_bytes(b'h264')
        (staged / 'client-stock.bin').write_bytes(b'stock')
        shas = {'client-h264.bin': session.CLIENT_H264_SHA, 'client-stock.bin': session.CLIENT_STOCK_SHA}
        def digest(path):
            return shas.get(pathlib.Path(path).name, session.CLIENT_H264_SHA)
        return digest

    def test_adopt_takes_over_a_mounted_h264_client(self):
        with tempfile.TemporaryDirectory() as temporary:
            staged = pathlib.Path(temporary)
            digest = self.adoptable(staged)
            with patch.object(session, 'RUN', staged), patch.object(session, 'digest', digest),                  patch.object(session, 'require_supported_kernel'), patch.object(session, 'bind_mounted', return_value=True),                  patch.object(session, 'video_path', return_value=staged),                  patch.object(session.pathlib.Path, 'resolve', return_value=pathlib.Path('/qcom-iris')),                  patch.object(session.os, 'getuid', create=True),                  patch.object(session.pathlib.Path, 'lstat', return_value=types.SimpleNamespace(st_uid=0)),                  patch.object(session, 'client_active', False), patch.object(session, 'client_codec', 'HEVC'):
                self.assertTrue(session.adopt()['adopted'])
                self.assertTrue(session.client_active)
                self.assertEqual(session.client_codec, 'H.264')

    def test_adopt_refuses_without_a_real_h264_mount(self):
        with tempfile.TemporaryDirectory() as temporary:
            staged = pathlib.Path(temporary)
            digest = self.adoptable(staged)
            common = [patch.object(session, 'RUN', staged), patch.object(session, 'digest', digest),
                      patch.object(session, 'require_supported_kernel'),
                      patch.object(session.pathlib.Path, 'lstat', return_value=types.SimpleNamespace(st_uid=0)),
                      patch.object(session, 'client_active', False)]
            for mounted, client in ((False, session.CLIENT_H264_SHA), (True, session.CLIENT_STOCK_SHA)):
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
