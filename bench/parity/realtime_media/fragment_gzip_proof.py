"""Size/correctness experiment only, never a server timing benchmark.

Reconstruct the real cached-message/single-request CSRF segmentation from an
existing captured HTML file. Secrets exist only in this process: the report
contains hashes, lengths and sizes. Python zlib independently decodes all bytes.
"""
import argparse
import gzip
import hashlib
import json
import re
import struct
import zlib
from pathlib import Path

LENGTH_BASE = [3,4,5,6,7,8,9,10,11,13,15,17,19,23,27,31,35,43,51,59,67,83,99,115,131,163,195,227,258]
LENGTH_EXTRA = [0,0,0,0,0,0,0,0,1,1,1,1,2,2,2,2,3,3,3,3,4,4,4,4,5,5,5,5,0]
DISTANCE_BASE = [1,2,3,4,5,7,9,13,17,25,33,49,65,97,129,193,257,385,513,769,1025,1537,2049,3073,4097,6145,8193,12289,16385,24577]
DISTANCE_EXTRA = [0,0,0,0,1,1,2,2,3,3,4,4,5,5,6,6,7,7,8,8,9,9,10,10,11,11,12,12,13,13]

class Bits:
    def __init__(self): self.data=bytearray(); self.word=0; self.count=0
    def put(self, value, count):
        self.word |= value << self.count; self.count += count
        while self.count >= 8:
            self.data.append(self.word & 255); self.word >>= 8; self.count -= 8
    def reverse(self, value, count):
        reversed_value=0
        for _ in range(count): reversed_value=(reversed_value<<1)|(value&1); value >>= 1
        self.put(reversed_value,count)
    def symbol(self, symbol):
        if symbol<=143: self.reverse(0x30+symbol,8)
        elif symbol<=255: self.reverse(0x190+symbol-144,9)
        elif symbol<=279: self.reverse(symbol-256,7)
        else: self.reverse(0xc0+symbol-280,8)
    def aligned(self):
        if self.count: self.put(0,8-self.count)

def match_fragment(length, distance):
    assert 3<=length<=distance<=32768
    writer=Bits(); writer.put(2,3) # BFINAL=0, fixed Huffman BTYPE=01.
    while length:
        count=min(length,258)
        if length-count in (1,2): count-=3
        index=max(i for i,base in enumerate(LENGTH_BASE) if base<=count)
        writer.symbol(257+index)
        writer.put(count-LENGTH_BASE[index],LENGTH_EXTRA[index])
        index=max(i for i,base in enumerate(DISTANCE_BASE) if base<=distance)
        writer.reverse(index,5)
        writer.put(distance-DISTANCE_BASE[index],DISTANCE_EXTRA[index])
        length-=count
    writer.symbol(256)
    writer.put(0,3) # Empty stored block (non-final), padded before LEN/NLEN.
    writer.aligned(); writer.data.extend(b"\x00\x00\xff\xff")
    return bytes(writer.data)

def raw(data, dictionary=b"", level=6):
    compressor=zlib.compressobj(level,zlib.DEFLATED,-15,zdict=dictionary) if dictionary else zlib.compressobj(level,zlib.DEFLATED,-15)
    return compressor.compress(data)+compressor.flush(zlib.Z_SYNC_FLUSH)

def encode(segments, repeats=True, dictionaries=True, full_history=False):
    parts=[]; previous=b""; gap=0; position=0; local={}; hits=0; known_history=b""
    for data, cacheable in segments:
        if not data: continue
        dictionary=(previous[-(32768-gap):]+b"\0"*gap) if dictionaries and previous and gap<32768 and b"\0" not in data else b""
        if full_history: dictionary=known_history if b"\0" not in data else b""
        prior=local.get(id(data)) if not cacheable else None
        if repeats and prior is not None and 3<=len(data)<=position-prior<=32768:
            parts.append(match_fragment(len(data),position-prior)); hits+=1
        else: parts.append(raw(data,dictionary,6 if cacheable else 1))
        if not cacheable: local[id(data)]=position
        position+=len(data)
        if cacheable: previous=data; gap=0
        else: gap=min(32768,gap+len(data))
        known_history=(known_history+(data if cacheable else b'\0'*len(data)))[-32768:]
    decoded=b"".join(data for data,_ in segments)
    encoded=b"\x1f\x8b\x08\0\0\0\0\0\0\x03"+b"".join(parts)+b"\x03\x00"+struct.pack("<II",zlib.crc32(decoded),len(decoded)&0xffffffff)
    assert gzip.decompress(encoded)==decoded
    assert zlib.decompress(encoded,31)==decoded
    return len(encoded),hits

def captured_segments(body):
    # Exact matching div boundaries avoid treating the viewer-specific shell as
    # globally cacheable. Each immutable message owner excludes CSRF inputs.
    tokens=re.compile(rb'<input[^>]*name="authenticity_token"[^>]*>')
    roots=[]; depth=0; root=None
    for tag in re.finditer(rb'</?div\b[^>]*>',body):
        if tag.group().startswith(b'</'):
            depth-=1
            if root and depth==root[1]: roots.append((root[0],tag.end())); root=None
        else:
            if re.search(rb'\bid="message_[^$][^"]*"',tag.group()): root=(tag.start(),depth)
            depth+=1
    output=[]; position=0; request_values={}
    for start,end in roots:
        if start>position: output.append((body[position:start],False))
        message=body[start:end]; offset=0
        for token in tokens.finditer(message):
            output.append((message[offset:token.start()],True))
            dynamic=request_values.setdefault(token.group(),token.group())
            output.append((dynamic,False)); offset=token.end()
        output.append((message[offset:],True)); position=end
    if position<len(body): output.append((body[position:],False))
    assert b"".join(data for data,_ in output)==body
    assert roots, "capture contains no real message fragments"
    return output,len(roots)

def boundary_proof():
    checks=0
    for length in [3,4,10,11,18,19,34,35,130,131,257,258,259,260,261,516,517,518,519,1000,32768]:
        for distance in [length, min(32768,length+31),32768]:
            token=bytes((i*17)%255+1 for i in range(length)); pad=b'p'*(distance-length)
            history=raw(token+pad,level=1)
            actual=zlib.decompress(history+match_fragment(length,distance)+b'\x03\x00',-15)
            assert actual==token+pad+token; checks+=1
    for stable_length in [1,2,50,32767,32768,32769,65536]:
        for gap_length in [0,1,31,32767,32768,32769]:
            stable=(b'<p>\xe2\x98\x83 utf8 \xf0\x9f\x98\x80</p>'*4096)[:stable_length]
            for secret in [b'x'*gap_length,b'y'*gap_length,bytes([0])*gap_length]:
                target=stable[-min(len(stable),900):]+b'<input new-value="nonzero">'
                encode([(stable,True),(secret,False),(target,True)])
                encode([(stable,True),(secret,False),(target+b'\0suffix',True)])
                checks+=2
    return checks

def main():
    parser=argparse.ArgumentParser(); parser.add_argument('--capture',type=Path,required=True); parser.add_argument('--output',type=Path,required=True); args=parser.parse_args()
    body=args.capture.read_bytes(); segments,messages=captured_segments(body)
    optimized,hits=encode(segments)
    masked_no_repeats,_=encode(segments,repeats=False)
    independent,_=encode(segments,repeats=False,dictionaries=False)
    full_masked,_=encode(segments,full_history=True)
    report={"capture":str(args.capture).replace('\\','/'),"sha256":hashlib.sha256(body).hexdigest(),"decoded_bytes":len(body),"messages":messages,"segments":len(segments),"whole_gzip_level1":len(gzip.compress(body,compresslevel=1,mtime=0)),"whole_gzip_level6":len(gzip.compress(body,compresslevel=6,mtime=0)),"independent_fragments":independent,"masked_dictionary_fragments":masked_no_repeats,"masked_with_request_local_references":optimized,"masked_full_history_with_request_local_references":full_masked,"local_reference_hits":hits,"boundary_independent_decoder_checks":boundary_proof(),"exact_decoded_bytes_and_gzip_crc":True,"timing_benchmark":False}
    args.output.parent.mkdir(parents=True,exist_ok=True); args.output.write_text(json.dumps(report,indent=2)+'\n'); print(json.dumps(report))

if __name__=='__main__': main()
