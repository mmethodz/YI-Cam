import struct
import unittest
from unittest.mock import Mock

from yicam.protocol import Camera
from yicam.protocol_modes import LOCAL_PLAIN_PROTOCOL, LOCAL_PLAIN_MARKER


class PlainProtocolTests(unittest.TestCase):
    def test_explicit_mode_and_stock_default(self):
        with self.assertRaises(ValueError):
            Camera('127.0.0.1', '')
        with self.assertRaises(ValueError):
            Camera('127.0.0.1', 'AAAAAAAAAAAAAAA', protocol='unknown')
        c = Camera('127.0.0.1', '', protocol=LOCAL_PLAIN_PROTOCOL)
        self.assertIsNone(c._cipher)
        c.running = True; c._send = Mock()
        c.command(0x1300)
        packet = c._send.call_args.args[0]
        self.assertEqual(packet[24:56], b'OpenYI-LAN-v1'.ljust(32, b'\0'))
        self.assertEqual(packet[24:56], LOCAL_PLAIN_MARKER)

    def test_camera_media_is_unchanged(self):
        c = Camera('127.0.0.1', '', protocol=LOCAL_PLAIN_PROTOCOL)
        for audio in (True, False):
            payload = bytes(range(80))
            header = bytearray(24)
            struct.pack_into('>H', header, 0, 138 if audio else 78)
            struct.pack_into('>HHHI', header, 6, 123, 1280, 720, 567)
            struct.pack_into('>I', header, 20, 12345)
            c._message(1 if audio else 2, 2 if audio else 1, 0, bytes(header) + payload)
            frame = (c.audio_frames if audio else c.frames).get_nowait()
            self.assertEqual(frame.data, payload)
            self.assertEqual((frame.sequence, frame.seconds, frame.milliseconds), (123, 567, 12345))

    def test_talk_back_keeps_aac_and_reliable_channel(self):
        c = Camera('127.0.0.1', '', protocol=LOCAL_PLAIN_PROTOCOL)
        c.running = True; c._speaking = True; c._send = Mock()
        size = 37
        adts = bytes((255, 241, 0x60, 0x40, size >> 3, ((size << 5) | 31) & 255, 0xfc)) + b'x' * (size - 7)
        c.send_talk_audio(adts)
        packet = c._send.call_args.args[0]
        self.assertEqual(packet[4:8], bytes((0xd1, 1, 0, 0)))
        self.assertEqual(packet[40:], adts)
        self.assertIn((1, 0), c._unacked)


if __name__ == '__main__':
    unittest.main()
