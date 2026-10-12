"""Create an OFFLINE paired-trial AV1 client. Never changes an installed file.

Only changes OutputSetup's HEVC FourCC to AV01 in the IDA-verified Frame build.
This is not general codec negotiation. Do not launch without a proven AV1
hardware decoder and a matching host backend; neither gate has passed yet.
"""
import argparse,hashlib,json,pathlib,struct
from elftools.elf.elffile import ELFFile

parser=argparse.ArgumentParser();parser.add_argument('original',type=pathlib.Path);parser.add_argument('output',type=pathlib.Path);parser.add_argument('--codec',choices=('AV1','H.264'),default='AV1');a=parser.parse_args()
original=a.original.read_bytes()
sha=hashlib.sha256(original).hexdigest()
expected='31addef0c9553d3770b5570487699e96d70271d4073cd6c03b080fdee6ae2153'
if sha!=expected:raise ValueError('Unsupported Frame client build; no output written')
if a.original.resolve()==a.output.resolve():raise ValueError('In-place patching is forbidden')
if a.output.exists():raise ValueError('Output already exists; preserve previous candidate')
with a.original.open('rb') as f:
 elf=ELFFile(f);segments=[s.header for s in elf.iter_segments() if s['p_type']=='PT_LOAD']
def offset(va):
 for s in segments:
  if s.p_vaddr<=va<s.p_vaddr+s.p_filesz:return s.p_offset+va-s.p_vaddr
 raise ValueError('Patch address not file-backed')
fourcc=int.from_bytes(b'AV01' if a.codec=='AV1' else b'H264','little')
patches=[(0x32e7c,0x52800003|(0x4548<<5),0x52800003|((fourcc&0xffff)<<5)),
         (0x32e88,0x72a00003|(0x4356<<5),0x72a00003|((fourcc>>16)<<5))]
modified=bytearray(original)
for va,before,after in patches:
 pos=offset(va)
 if struct.unpack_from('<I',original,pos)[0]!=before:raise ValueError('Unexpected instruction; no output written')
 struct.pack_into('<I',modified,pos,after)
a.output.parent.mkdir(parents=True,exist_ok=True);a.output.write_bytes(modified)
receipt={'original_sha256':sha,'candidate_sha256':hashlib.sha256(modified).hexdigest(),
 'original_codec':'HEVC','candidate_codec':a.codec,'instructions':2,'installed':False,
 'hardware_decode_passed':False,'host_bridge_passed':False,'negotiated_codec':False}
a.output.with_suffix('.receipt.json').write_text(json.dumps(receipt,indent=2));print(json.dumps(receipt))
