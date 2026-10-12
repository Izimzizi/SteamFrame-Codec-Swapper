"""One paired PC's synthetic AV1/IVF test over the dongle, with explicit software decode.

This does not replace VRLink or claim headset hardware decode. The output is
decoded-frame hashes/accounting; presentation and motion-to-photon are untested.
"""
import hashlib,json,pathlib,socket,struct,subprocess,time

def receive_exact(sock,count):
    result=bytearray()
    while len(result)<count:
        part=sock.recv(count-len(result))
        if not part:raise EOFError('Truncated input')
        result.extend(part)
    return bytes(result)

def main():
    results=pathlib.Path('/tmp/steam-frame-av1-lab-20261011/dongle-result.json')
    with socket.socket(socket.AF_INET,socket.SOCK_STREAM) as listener:
        listener.setsockopt(socket.SOL_SOCKET,socket.SO_REUSEADDR,1)
        listener.bind(('10.35.78.1',50120));listener.listen(1);listener.settimeout(15)
        print('AV1_LAB_READY',flush=True)
        with listener.accept()[0] as connection:
            connection.settimeout(10)
            if connection.getpeername()[0]!='10.35.78.21':raise RuntimeError('Unexpected peer')
            header=receive_exact(connection,32)
            magic,version,length,codec,w,h,rate,scale,frames,_=struct.unpack('<4sHH4sHHIIII',header)
            if (magic,version,length,codec,rate,scale)!=(b'DKIF',0,32,b'AV01',120,1):raise ValueError('Expected 120-fps AV1 IVF')
            if w*h>1536*6144 or frames>72000:raise ValueError('Test limits exceeded')
            command=['ffmpeg','-hide_banner','-loglevel','error','-probesize','32','-analyzeduration','0','-c:v','libdav1d','-threads','8','-max_frame_delay','1','-f','ivf','-i','pipe:0','-f','framemd5','-']
            # Temp hashes avoid stdout/pipe backpressure during the network test.
            with pathlib.Path('/tmp/steam-frame-av1-lab-20261011/dongle-frames.md5').open('wb') as hashes, pathlib.Path('/tmp/steam-frame-av1-lab-20261011/dongle-decode.err').open('wb') as errors:
                process=subprocess.Popen(command,stdin=subprocess.PIPE,stdout=hashes,stderr=errors)
                began=time.monotonic();received=0;bytes_received=0;identity_errors=0;sequence_headers=0
                try:
                    process.stdin.write(header)
                    for i in range(frames):
                        packet_header=receive_exact(connection,12);size,timestamp=struct.unpack('<IQ',packet_header)
                        if size==0 or size>16000000:raise ValueError('Invalid frame size')
                        payload=receive_exact(connection,size)
                        if timestamp!=i:identity_errors+=1
                        process.stdin.write(packet_header);process.stdin.write(payload);process.stdin.flush()
                        received+=1;bytes_received+=size
                    process.stdin.close();status=process.wait(timeout=30)
                finally:
                    if process.poll() is None:process.kill();process.wait()
            elapsed=time.monotonic()-began
            lines=pathlib.Path('/tmp/steam-frame-av1-lab-20261011/dongle-frames.md5').read_text().splitlines()
            decoded=sum(bool(line) and not line.startswith('#') for line in lines)
            error=pathlib.Path('/tmp/steam-frame-av1-lab-20261011/dongle-decode.err').read_text()[-2000:]
            receipt={'transport':'dedicated dongle TCP lab','source_ip':'10.35.78.21','codec':'AV1','fps':120,'width':w,'height':h,'decoder':'libdav1d software','max_frame_delay':1,'received_frames':received,'decoded_frames':decoded,'identity_errors':identity_errors,'bytes':bytes_received,'wall_seconds':elapsed,'decode_exit_code':status,'stderr':error,'hardware_decode':False,'visible_on_headset':False,'steamvr_integration':False,'passed':status==0 and decoded==frames and identity_errors==0}
            results.write_text(json.dumps(receipt,indent=2));print(json.dumps(receipt),flush=True)

if __name__=='__main__':main()
