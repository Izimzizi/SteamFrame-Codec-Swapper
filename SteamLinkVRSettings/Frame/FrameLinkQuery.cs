namespace SteamLinkVRSettings.Frame;

internal static class FrameLinkQuery
{
    // Fixed read-only query runs on the headset through paired SSH, without a tunnel.
    public const string Script = """"
"""Run on the paired Frame through foreground SSH; no forwarded port or helper daemon."""
import base64
import hashlib
import json
import os
import socket
import struct
import sys
import urllib.request
import urllib.parse

expression = '({wifi:SystemNetworkStore.isWifiEnabled,links:streamingStore.vrLinkStatus})'
with urllib.request.urlopen('http://127.0.0.1:8080/json/list', timeout=2) as response:
    targets = json.loads(response.read(65537))
target = next(t for t in targets if t.get('title') == 'SharedJSContext')
path = urllib.parse.urlparse(target['webSocketDebuggerUrl']).path

def exact(stream, count):
    chunks = bytearray()
    while len(chunks) < count:
        chunk = stream.read(count - len(chunks))
        if not chunk:
            raise RuntimeError('Debugger closed the connection')
        chunks.extend(chunk)
    return bytes(chunks)

def send_frame(connection, opcode, payload):
    length = len(payload)
    prefix = bytes([0x80 | opcode, 0x80 | length]) if length < 126 else bytes([0x80 | opcode, 0x80 | 126]) + struct.pack('!H', length)
    mask = os.urandom(4)
    connection.sendall(prefix + mask + bytes(b ^ mask[i % 4] for i, b in enumerate(payload)))

with socket.create_connection(('127.0.0.1', 8080), timeout=2) as connection:
    key = base64.b64encode(os.urandom(16)).decode('ascii')
    request = f'GET {path} HTTP/1.1\r\nHost: 127.0.0.1:8080\r\nUpgrade: websocket\r\nConnection: Upgrade\r\nSec-WebSocket-Key: {key}\r\nSec-WebSocket-Version: 13\r\n\r\n'
    connection.sendall(request.encode('ascii'))
    with connection.makefile('rb') as stream:
        status = stream.readline().decode('ascii').strip()
        if ' 101 ' not in status:
            raise RuntimeError('Debugger upgrade failed: ' + status)
        headers = {}
        while True:
            line = stream.readline().decode('ascii').strip()
            if not line:
                break
            name, value = line.split(':', 1)
            headers[name.lower()] = value.strip()
        expected = base64.b64encode(hashlib.sha1((key + '258EAFA5-E914-47DA-95CA-C5AB0DC85B11').encode('ascii')).digest()).decode('ascii')
        if headers.get('sec-websocket-accept') != expected:
            raise RuntimeError('Debugger handshake was invalid')
        message = json.dumps({'id': 1, 'method': 'Runtime.evaluate', 'params': {'expression': expression, 'returnByValue': True, 'awaitPromise': True}}).encode('utf-8')
        send_frame(connection, 1, message)
        assembled = bytearray()
        while True:
            first, second = exact(stream, 2)
            opcode = first & 15
            length = second & 127
            if length == 126:
                length = struct.unpack('!H', exact(stream, 2))[0]
            elif length == 127:
                length = struct.unpack('!Q', exact(stream, 8))[0]
            if length > 1048576:
                raise RuntimeError('Debugger response exceeds the metadata limit')
            mask = exact(stream, 4) if second & 128 else None
            payload = exact(stream, length)
            if mask:
                payload = bytes(b ^ mask[i % 4] for i, b in enumerate(payload))
            if opcode == 8:
                raise RuntimeError('Debugger closed the connection')
            if opcode == 9:
                send_frame(connection, 10, payload)
                continue
            if opcode not in (0, 1):
                continue
            if len(assembled) + len(payload) > 1048576: raise RuntimeError('Metadata exceeds the limit')
            assembled.extend(payload)
            if not first & 128:
                continue
            result = json.loads(assembled)
            assembled.clear()
            if result.get('id') != 1:
                continue
            details = result.get('result', {})
            if 'exceptionDetails' in details:
                raise RuntimeError(details['exceptionDetails'].get('text', 'Evaluation failed'))
            print(json.dumps(details.get('result', {}).get('value')))
            break

"""";
}
