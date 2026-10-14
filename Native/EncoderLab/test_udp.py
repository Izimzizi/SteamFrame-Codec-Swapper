"""Bounded probe checks: invalid arguments and a local-only UDP receiver."""
import json
from pathlib import Path
import socket
import struct
import subprocess
import threading
import time

ROOT = Path(__file__).resolve().parents[2]
EXE = ROOT / "artifacts/EncoderLab/udp_probe.exe"


def main():
    valid = ["127.0.0.1", "10", "256", "1", "1000"]
    rejected = [[], valid[:-1], valid + ["extra"]]
    for index, values in enumerate([
        ["invalid", "127.0.0.1suffix", "0.0.0.0", "255.255.255.255", "224.0.0.1"],
        ["9", "651", "10junk", "99999999999999999999"],
        ["255", "1401", "256.0"],
        ["0", "16", "-1"],
        ["99", "12001", "1000x"],
    ]):
        for value in values:
            args = valid.copy()
            args[index] = value
            rejected.append(args)
    # An exclusive bind both refuses an existing receiver and keeps all test
    # traffic local. It also lets argument tests verify zero transmitted packets.
    with socket.socket(socket.AF_INET, socket.SOCK_DGRAM) as receiver:
        receiver.setsockopt(socket.SOL_SOCKET, socket.SO_EXCLUSIVEADDRUSE, 1)
        receiver.setsockopt(socket.SOL_SOCKET, socket.SO_RCVBUF, 1024 * 1024)
        receiver.bind(("127.0.0.1", 39764))
        receiver.setblocking(False)
        for args in rejected:
            result = subprocess.run([str(EXE), *args], capture_output=True, text=True, timeout=3)
            assert result.returncode == 2, (args, result.returncode, result.stderr)
            try:
                receiver.recv(65536)
            except BlockingIOError:
                pass
            else:
                raise AssertionError("Rejected arguments sent a packet")

        receiver.settimeout(0.05)
        stop = threading.Event()
        packets = []
        errors = []

        def receive():
            while not stop.is_set():
                try:
                    payload, source = receiver.recvfrom(65536)
                except socket.timeout:
                    continue
                except OSError as error:
                    errors.append(str(error))
                    return
                if source[0] != "127.0.0.1" or len(payload) != 256:
                    errors.append("Unexpected loopback datagram")
                    return
                packets.append(struct.unpack_from("<Q", payload)[0])

        thread = threading.Thread(target=receive, daemon=True)
        thread.start()
        try:
            result = subprocess.run([str(EXE), *valid], capture_output=True, text=True, timeout=5)
            deadline = time.monotonic() + 0.25
            expected = 10_000_000 // (256 * 8)
            while len(packets) < expected and time.monotonic() < deadline:
                time.sleep(0.005)
        finally:
            stop.set()
            thread.join(timeout=1)
        assert not thread.is_alive() and not errors, errors
        assert result.returncode == 0, (result.returncode, result.stderr)
        output = json.loads(result.stdout)
        assert output["sent_packets"] == expected and output["send_errors"] == 0, output
        assert len(packets) == expected and set(packets) == set(range(expected)), len(packets)
        assert output["payload_bytes"] == expected * 256, output
        print(json.dumps({"argument_cases": len(rejected), "loopback_packets": expected,
                          "remote_traffic": False, "status": "passed"}))


if __name__ == "__main__":
    main()
