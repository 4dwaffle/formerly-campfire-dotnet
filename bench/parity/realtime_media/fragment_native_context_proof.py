"""Independent native ABI/reset/failure and gzip decoder checks; no HTTP timing."""
import argparse, ctypes, gzip, hashlib, json, pathlib, struct, zlib

parser = argparse.ArgumentParser()
parser.add_argument('--library', required=True)
parser.add_argument('--encoded', type=pathlib.Path, required=True)
parser.add_argument('--capture', type=pathlib.Path, required=True)
parser.add_argument('--report', type=pathlib.Path, required=True)
args = parser.parse_args()
library = ctypes.CDLL(args.library)
create = library.campfire_deflate_create
create.argtypes = [ctypes.c_int]; create.restype = ctypes.c_void_p
destroy = library.campfire_deflate_destroy
destroy.argtypes = [ctypes.c_void_p]; destroy.restype = None
run = library.campfire_deflate_run
run.argtypes = [ctypes.c_void_p, ctypes.c_void_p, ctypes.c_uint32, ctypes.c_void_p,
               ctypes.c_uint32, ctypes.c_void_p, ctypes.c_uint32,
               ctypes.POINTER(ctypes.c_uint32), ctypes.POINTER(ctypes.c_uint32)]
run.restype = ctypes.c_int

def compress(context, value, dictionary=b'', capacity=None):
    output = ctypes.create_string_buffer(capacity or (len(value)+len(value)//8+128))
    written, checksum = ctypes.c_uint32(), ctypes.c_uint32()
    result = run(context, value, len(value), dictionary, len(dictionary), output, len(output),
                 ctypes.byref(written), ctypes.byref(checksum))
    return result, output.raw[:written.value], checksum.value

context = create(6)
assert context
controls = 0
try:
    # Force an output-capacity error. Production rejects/disposes this lease.
    error, encoded, checksum = compress(context, bytes(range(256))*16, capacity=1)
    assert error != 0 and encoded == b'' and checksum == 0
    controls += 1
finally:
    destroy(context)
context = create(6)
assert context
try:
    cases = [b'', b'xyz', 'Unicode 雪 😀'.encode()*500, bytes(range(256))*256,
             b'fresh data after a larger window'*1000]
    for index, value in enumerate(cases*3):
        dictionary = bytes(range(256))*128 if index%2 else b''
        result, encoded, checksum = compress(context, value, dictionary)
        assert result == 0 and checksum == zlib.crc32(value)
        decoder = zlib.decompressobj(wbits=-15, zdict=dictionary)
        assert decoder.decompress(encoded+b'\x03\x00')+decoder.flush() == value
        assert decoder.eof and not decoder.unused_data
        controls += 1
finally:
    destroy(context)

encoded = args.encoded.read_bytes()
body = args.capture.read_bytes()
assert gzip.decompress(encoded) == body
assert zlib.decompress(encoded, wbits=31) == body
crc, size = struct.unpack('<II', encoded[-8:])
assert crc == zlib.crc32(body) and size == len(body)
controls += 3
report = {'kind':'independent native reset/fault ABI and whole gzip validation; no timing',
          'controls_passed':controls, 'native_error_dropped_before_fresh_context':True,
          'python_gzip_exact':True, 'python_zlib_exact':True, 'crc_isize_exact':True,
          'decoded_bytes':len(body), 'gzip_bytes':len(encoded),
          'decoded_sha256':hashlib.sha256(body).hexdigest(),
          'gzip_sha256':hashlib.sha256(encoded).hexdigest()}
args.report.write_text(json.dumps(report,indent=2)+'\n')
print(json.dumps(report))
