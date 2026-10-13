import unittest
from unittest.mock import patch
from game_session import owns_control, restore


class RestoreOwnership(unittest.TestCase):
    def test_restore_only_our_unchanged_temporary_connection(self):
        before = dict(owner_pid=123, owner_created_epoch=100.)
        current = dict(before, operation_id='ours', state='ready', active_codec='H.264', target_mbps=25, fps=90)
        self.assertTrue(owns_control(current, before, 'ours'))
        for key, replacement in dict(owner_pid=124, owner_created_epoch=101., operation_id='user-switch',
                                     state='switching', active_codec='HEVC', target_mbps=400, fps=120).items():
            with self.subTest(key=key):
                changed = dict(current, **{key: replacement})
                self.assertFalse(owns_control(changed, before, 'ours'))

    def test_restore_retries_own_failure_once(self):
        before = dict(owner_pid=123, owner_created_epoch=100., active_codec='H.264', target_mbps=400, fps=90)
        current = dict(before, operation_id='ours', state='ready', target_mbps=25)
        report = {'restored': False}
        def attempt(*args, **kwargs):
            if current['operation_id'] == 'ours':
                current.update(operation_id=kwargs['identifier'], state='error')
                raise RuntimeError('Failed once')
            self.assertEqual(kwargs['expected_operation'], current['operation_id'])
            self.assertTrue(kwargs['retry'])
        with patch('game_session.status', side_effect=lambda: dict(current)), patch('game_session.switch', side_effect=attempt) as switch:
            restore(before, 'ours', report)
        self.assertTrue(report['restored'])
        self.assertEqual(switch.call_count, 2)

    def test_restore_does_not_retry_over_user_switch(self):
        before = dict(owner_pid=123, owner_created_epoch=100., active_codec='H.264', target_mbps=400, fps=90)
        current = dict(before, operation_id='ours', state='ready', target_mbps=25)
        report = {'restored': False}
        def attempt(*args, **kwargs):
            current.update(operation_id='user', state='ready')
            raise RuntimeError('A newer operation arrived')
        with patch('game_session.status', side_effect=lambda: dict(current)), patch('game_session.switch', side_effect=attempt) as switch:
            with self.assertRaises(RuntimeError):
                restore(before, 'ours', report)
        self.assertFalse(report['restored'])
        self.assertEqual(switch.call_count, 1)


if __name__ == '__main__':
    unittest.main()
