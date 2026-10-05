"""Small parser/selection checks; no HTTP requests or app workloads."""
import struct
import unittest
import zlib

import upload


class UploadClientTests(unittest.TestCase):
    def test_selects_new_attachment_and_skips_first_avatar(self):
        parser = upload.AttachmentParser("new-id")
        parser.feed('<div id="message_old-id"><img class="message__attachment" src="/rails/active_storage/representations/redirect/old/x/a.jpg"></div><div id="message_new-id"><a class="avatar"><img src="/users/token/avatar"></a><img class="message__attachment" src="/rails/active_storage/representations/redirect/new/x/a.jpg?x=1&amp;y=2"></div>')
        self.assertEqual(parser.attachment()["src"], "/rails/active_storage/representations/redirect/new/x/a.jpg?x=1&y=2")

    def test_no_attachment_fails_even_when_avatar_exists(self):
        parser = upload.AttachmentParser("id")
        parser.feed('<div id="message_id"><img src="/users/token/avatar"></div>')
        with self.assertRaises(upload.ValidationError):
            parser.attachment()

    def test_dimensions_and_magic(self):
        ihdr = b"IHDR" + struct.pack(">IIBBBBB", 1200, 800, 8, 2, 0, 0, 0)
        png = b"\x89PNG\r\n\x1a\n" + struct.pack(">I", 13) + ihdr + struct.pack(">I", zlib.crc32(ihdr))
        jpeg = b"\xff\xd8\xff\xc0" + struct.pack(">H", 11) + b"\x08" + struct.pack(">HH", 800, 1200) + b"\x01\x01\x11\x00\xff\xda\x00\x08\x01\x01\x00\x00\x3f\x00\x01\xff\xd9"
        webp_chunk = b"\x2f" + ((1200 - 1) | ((800 - 1) << 14)).to_bytes(4, "little")
        webp = b"RIFF" + struct.pack("<I", 18) + b"WEBPVP8L" + struct.pack("<I", 5) + webp_chunk + b"\x00"
        for data in (png, jpeg, webp):
            self.assertEqual((upload.image_info(data)["width"], upload.image_info(data)["height"]), (1200, 800))
        with self.assertRaises(upload.ValidationError):
            upload.image_info(b"<html>not an image</html>")
        with self.assertRaises(upload.ValidationError):
            upload.image_info(png[:20])

    def test_multipart_matches_original_fields(self):
        boundary, body = upload.multipart("csrf", "new-id", "photo.jpg", "image/jpeg", b"actual bytes")
        for field in (b'name="authenticity_token"', b'name="message[client_message_id]"', b'name="message[attachment]"; filename="photo.jpg"'):
            self.assertIn(field, body)
        self.assertTrue(body.endswith(("\r\n--" + boundary + "--\r\n").encode()))
        self.assertIn(b"actual bytes", body)


if __name__ == "__main__":
    unittest.main()
