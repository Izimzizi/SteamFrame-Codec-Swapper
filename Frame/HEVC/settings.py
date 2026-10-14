"""Small, read-back-verified HEVC trial controls using installed OpenVR."""
import argparse
import ctypes as c
import json
import os
import pathlib

RUNTIME = pathlib.Path(os.environ.get('STEAMVR_RUNTIME', r'C:\Program Files (x86)\Steam\steamapps\common\SteamVR'))


def apply(target=None, width=None, async_send=None, ten_bit=None):
    if target is not None and not 25 <= target <= 500:
        raise ValueError('Target must be 25..500 Mbps')
    if width is not None and (not 512 <= width <= 2048 or width % 32):
        raise ValueError('Use a multiple of 32 within 512..2048')
    loader = c.WinDLL(str(RUNTIME / 'bin/win64/openvr_api.dll'))
    error = c.c_int()
    loader.VR_InitInternal2.argtypes = [c.POINTER(c.c_int), c.c_int, c.c_char_p]
    loader.VR_InitInternal2.restype = c.c_uint
    loader.VR_InitInternal2(c.byref(error), 3, None)
    if error.value:
        raise RuntimeError('SteamVR settings connection failed')
    try:
        loader.VR_GetGenericInterface.argtypes = [c.c_char_p, c.POINTER(c.c_int)]
        loader.VR_GetGenericInterface.restype = c.c_void_p
        table = loader.VR_GetGenericInterface(b'FnTable:IVRSettings_003', c.byref(error))
        if not table or error.value:
            raise RuntimeError('SteamVR settings interface unavailable')
        def call(slot, returns, value_type, key, value=None):
            signature = [c.c_char_p, c.c_char_p] + ([value_type] if value is not None else []) + [c.POINTER(c.c_int)]
            fn = c.WINFUNCTYPE(returns, *signature)(c.c_void_p.from_address(table + slot * c.sizeof(c.c_void_p)).value)
            args = [b'driver_vrlink', key.encode()] + ([value] if value is not None else []) + [c.byref(error)]
            error.value = 0
            result = fn(*args)
            if error.value:
                raise RuntimeError('OpenVR setting failed: ' + key)
            return result
        before = {'target_mbps': call(7, c.c_float, None, 'targetBandwidth'),
                  'width': call(6, c.c_int, None, 'streamFormatWidth'),
                  'async_send': call(5, c.c_bool, None, 'asyncSend'),
                  'ten_bit': call(5, c.c_bool, None, '10bit')}
        if target is not None:
            call(3, None, c.c_float, 'targetBandwidth', target)
        if width is not None:
            call(2, None, c.c_int, 'streamFormatWidth', width)
        if async_send is not None:
            call(1, None, c.c_bool, 'asyncSend', async_send)
        if ten_bit is not None:
            call(1, None, c.c_bool, '10bit', ten_bit)
        after = {'target_mbps': call(7, c.c_float, None, 'targetBandwidth'),
                 'width': call(6, c.c_int, None, 'streamFormatWidth'),
                 'async_send': call(5, c.c_bool, None, 'asyncSend'),
                 'ten_bit': call(5, c.c_bool, None, '10bit')}
        if (target is not None and abs(after['target_mbps'] - target) > .01 or
                width is not None and after['width'] != width or
                async_send is not None and after['async_send'] != async_send or
                ten_bit is not None and after['ten_bit'] != ten_bit):
            raise RuntimeError('Requested setting did not read back correctly')
        return {'before': before, 'after': after}
    finally:
        loader.VR_ShutdownInternal()


if __name__ == '__main__':
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument('--target', type=int)
    parser.add_argument('--width', type=int)
    parser.add_argument('--async-send', action=argparse.BooleanOptionalAction, default=None,
                        help='Change sender scheduling; reconnect before comparing it')
    parser.add_argument('--ten-bit', action=argparse.BooleanOptionalAction, default=None,
                        help='Request HEVC Main10 or Main; verify accepted bit depth after reconnecting')
    args = parser.parse_args()
    print(json.dumps(apply(args.target, args.width, args.async_send, args.ten_bit)))
