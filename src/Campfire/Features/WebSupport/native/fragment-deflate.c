/* Independent raw DEFLATE fragments, ending at a Z_SYNC_FLUSH boundary.
 * Callers append one final empty block and a gzip CRC/length trailer.
 * Pool leases are exclusive, reset before reuse and retain no caller pointers.
 * The original one-shot export still owns and ends its stream per call. */
#include <stdint.h>
#include <string.h>
#include <stdlib.h>
#include <zlib.h>

#if defined(_WIN32)
#define EXPORT __declspec(dllexport)
#else
#define EXPORT __attribute__((visibility("default")))
#endif

EXPORT uint32_t campfire_crc32(const unsigned char *input, uint32_t length)
{
    return (uint32_t)crc32(0, input, length);
}

/* Managed SafeHandle leases are exclusive. Only immutable token-free input is
 * compressed in these pooled contexts. Reset clears matching history before
 * every call; callers dispose a context on any error instead of reusing it. */
EXPORT void *campfire_deflate_create(int level)
{
    z_stream *stream = calloc(1, sizeof(z_stream));
    if (stream == NULL) return NULL;
    if (deflateInit2(stream, level, Z_DEFLATED, -15, 8, Z_DEFAULT_STRATEGY) != Z_OK) {
        free(stream);
        return NULL;
    }
    return stream;
}

EXPORT void campfire_deflate_destroy(void *context)
{
    z_stream *stream = context;
    if (stream == NULL) return;
    (void)deflateEnd(stream);
    free(stream);
}

EXPORT int campfire_deflate_run(void *context, const unsigned char *input, uint32_t input_length,
    const unsigned char *dictionary, uint32_t dictionary_length, unsigned char *output,
    uint32_t output_capacity, uint32_t *output_length, uint32_t *checksum)
{
    z_stream *stream = context;
    *output_length = 0;
    *checksum = 0;
    if (stream == NULL) return Z_STREAM_ERROR;
    int result = deflateReset(stream);
    if (result != Z_OK) return result;
    if (dictionary_length != 0) {
        result = deflateSetDictionary(stream, dictionary, dictionary_length);
        if (result != Z_OK) return result;
    }
    stream->next_in = (Bytef *)input;
    stream->avail_in = input_length;
    stream->next_out = output;
    stream->avail_out = output_capacity;
    result = deflate(stream, Z_SYNC_FLUSH);
    if (result == Z_OK && stream->avail_in == 0 && stream->avail_out != 0) {
        *output_length = output_capacity - stream->avail_out;
        *checksum = (uint32_t)crc32(0, input, input_length);
    } else if (result == Z_OK) result = Z_BUF_ERROR;
    /* Input/output belong to the caller, so never retain their pointers. */
    stream->next_in = NULL;
    stream->next_out = NULL;
    stream->avail_in = 0;
    stream->avail_out = 0;
    return result;
}

EXPORT int campfire_deflate_fragment(const unsigned char *input, uint32_t input_length,
    const unsigned char *dictionary, uint32_t dictionary_length, int level,
    unsigned char *output, uint32_t output_capacity, uint32_t *output_length,
    uint32_t *checksum)
{
    z_stream stream;
    memset(&stream, 0, sizeof(stream));
    *output_length = 0;
    *checksum = 0;
    int result = deflateInit2(&stream, level, Z_DEFLATED, -15, 8, Z_DEFAULT_STRATEGY);
    if (result != Z_OK) return result;
    if (dictionary_length != 0) {
        result = deflateSetDictionary(&stream, dictionary, dictionary_length);
        if (result != Z_OK) {
            (void)deflateEnd(&stream);
            return result;
        }
    }
    stream.next_in = (Bytef *)input;
    stream.avail_in = input_length;
    stream.next_out = output;
    stream.avail_out = output_capacity;
    result = deflate(&stream, Z_SYNC_FLUSH);
    if (result == Z_OK && stream.avail_in == 0 && stream.avail_out != 0) {
        *output_length = output_capacity - stream.avail_out;
        *checksum = (uint32_t)crc32(0, input, input_length);
    } else if (result == Z_OK) {
        result = Z_BUF_ERROR;
    }
    /* Z_DATA_ERROR here means an intentionally unfinished raw stream. */
    (void)deflateEnd(&stream);
    return result;
}
