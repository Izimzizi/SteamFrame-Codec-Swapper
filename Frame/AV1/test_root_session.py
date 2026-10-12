"""Host-side regression checks; never loads/unloads a real kernel module."""
import pathlib
import subprocess
import tempfile
import types
import unittest
from unittest.mock import patch
import root_session as session


class RootSessionTests(unittest.TestCase):
    def test_recent_kernel_lines_survive_large_boot_log(self):
        old = ('unrelated boot line\n' * 2500) + 'iris: current AV1 session error\n'
        result = subprocess.CompletedProcess([], 0, stdout=old, stderr='')
        with patch.object(session.subprocess, 'run', return_value=result):
            self.assertEqual(session.kernel_log()['lines'], ['iris: current AV1 session error'])

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

    def test_busy_decoder_refuses_stock_unload(self):
        with tempfile.TemporaryDirectory() as temporary:
            staged = pathlib.Path(temporary)
            (staged / 'prepared.json').write_text('{}')
            (staged / 'driver').mkdir()
            with patch.object(session, 'RUN', staged), \
                 patch.object(session.os, 'uname', return_value=types.SimpleNamespace(release=session.RELEASE), create=True), \
                 patch.object(session, 'video_path', return_value=staged), \
                 patch.object(session.pathlib.Path, 'resolve', return_value=pathlib.Path('/qcom-iris')), \
                 patch.object(session, 'opens', return_value=[{'pid': 123}]), \
                 patch.object(session, 'run') as run:
                with self.assertRaisesRegex(RuntimeError, 'Decoder is in use'):
                    session.load_av1()
                run.assert_not_called()


if __name__ == '__main__':
    unittest.main()
