import base64
import hashlib
import hmac
import struct
import unittest

from yicam.protocol import Camera, Channel, ProtocolError, VideoFrame, authentication
from Crypto.Cipher import AES
from yicam.video import FrameClock, FrameOrder


def frame(sequence, key=False, milliseconds=10000):
    return VideoFrame(b'', sequence, 1280, 720, 0, key, 1, 78, 0, 2 if key else 3, int(key), 1000, milliseconds)


class TransportTests(unittest.TestCase):
    def test_audio_decrypts_complete_blocks_and_keeps_tail(self):
        camera = Camera('127.0.0.1', '123456789012345')
        clear = bytes(range(37))
        encrypted = AES.new(b'1234567890123450', AES.MODE_ECB).encrypt(clear[:32]) + clear[32:]
        header = bytearray(24)
        struct.pack_into('>H', header, 0, 138)
        header[2] = 27
        struct.pack_into('>H', header, 6, 65535)
        struct.pack_into('>I', header, 12, 123)
        struct.pack_into('>I', header, 20, 100000)
        camera._message(1, 2, 0, bytes(header) + encrypted)
        audio = camera.audio_frames.get_nowait()
        self.assertEqual(audio.data, clear)
        self.assertEqual((audio.codec, audio.flags, audio.sequence, audio.seconds, audio.milliseconds), (138, 27, 65535, 123, 100000))

    def test_fragment_reordering_duplicates_and_wrap(self):
        channel = Channel()
        channel.expected = 65535
        body = b'camera command response'
        data = struct.pack('>BBBBI', 2, 3, 0, 0, len(body)) + body
        self.assertEqual(channel.feed(0, data[5:]), [])
        self.assertEqual(channel.feed(0, data[5:]), [])
        self.assertEqual(channel.feed(65535, data[:5]), [(3, 0, body)])
        self.assertEqual(channel.feed(65535, data[:5]), [])
        self.assertEqual(channel.expected, 1)

    def test_reject_invalid_size_and_receive_window(self):
        with self.assertRaises(ProtocolError):
            Channel().feed(0, struct.pack('>BBBBI', 2, 1, 0, 0, 9*1024**2))
        with self.assertRaises(ProtocolError):
            Channel().feed(5000, b'')

    def test_authentication_format_without_real_credentials(self):
        key, nonce = 'test-device-key', '0123456789abcde'
        result = authentication(key, nonce)
        digest = hmac.digest(key.encode(), b'user=xiaoyiuser&nonce='+nonce.encode(), 'sha1')
        self.assertEqual(result, nonce.encode()+b','+base64.b64encode(digest)[:15]+b'\0')
        self.assertEqual(len(result), 32)

    def test_merge_keyframes_arriving_after_pframes(self):
        order = FrameOrder()
        self.assertEqual(order.feed(frame(2)), [])
        self.assertEqual(order.feed(frame(3)), [])
        self.assertEqual([f.sequence for f in order.feed(frame(1, True))], [1, 2, 3])
        self.assertEqual(order.feed(frame(2)), [])

    def test_lost_frame_resumes_at_next_keyframe_only(self):
        order = FrameOrder(maximum_wait=1)
        order.feed(frame(65534, True), now=0)
        self.assertEqual(order.feed(frame(0), now=0), [])
        self.assertEqual(order.feed(frame(1), now=.5), [])
        self.assertEqual([f.sequence for f in order.feed(frame(2, True), now=2)], [2])
        self.assertEqual(order.resets, 1)
        self.assertEqual([f.sequence for f in order.feed(frame(3), now=2)], [3])

    def test_timestamp_uptime_wrap(self):
        clock = FrameClock()
        self.assertEqual(clock.time(frame(1, True, 0xffffffc0)), 0)
        self.assertAlmostEqual(clock.time(frame(2, False, 2)), .066)
        self.assertAlmostEqual(clock.time(frame(3, False, 69)), .133)

    def test_fraction_timestamps(self):
        clock = FrameClock()
        first, second = frame(1, True, 960), frame(2, False, 27)
        second.seconds += 1
        self.assertEqual(clock.time(first), 0)
        self.assertAlmostEqual(clock.time(second), .067)


if __name__ == '__main__':
    unittest.main()
