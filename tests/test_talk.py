import struct
import unittest
from unittest.mock import Mock, call

from Crypto.Cipher import AES
from yicam.protocol import Camera, ProtocolError


def packet(size=37):
    return bytes((255, 241, 0x60, 0x40 | (size >> 11), (size >> 3) & 255,
                  ((size << 5) | 31) & 255, 0xfc)) + bytes([0x5a]) * (size - 7)


class TalkTests(unittest.TestCase):
    def camera(self):
        camera = Camera('127.0.0.1', 'AAAAAAAAAAAAAAA')
        camera.running = True
        camera._send = Mock()
        camera.command = Mock()
        camera.frames.put(object())
        return camera

    def test_stream_initialization_speaker_mode_and_stop(self):
        c = self.camera()
        with self.assertRaises(ConnectionError):
            c.send_talk_audio(packet())
        c.start_talk(2)
        self.assertEqual(c.command.call_args_list, [call(0x2345, bytes((1, 2, 1, 0))),
                                                   call(0x02ff, bytes(8)), call(0x350, bytes(4))])
        c.send_talk_audio(packet())
        c.stop_talk()
        c.command.assert_called_with(0x351, bytes(8))
        self.assertFalse(c._unacked)
        with self.assertRaises(ConnectionError):
            c.send_talk_audio(packet())

    def test_audio_wire_encryption_and_independent_sequences(self):
        c = self.camera()
        c.start_talk()
        c._sequence = 51
        for seq in range(16):
            c.send_talk_audio(packet())
            data = c._send.call_args.args[0]
            self.assertEqual(data[4:8], struct.pack('>BBH', 0xd1, 1, seq))
            self.assertEqual(data[8:16], struct.pack('>BBBBI', 2, 2, 0, 0, 61))
            self.assertEqual(data[16:20], bytes((0, 138, 2, 0)))
            self.assertEqual(struct.unpack_from('>I', data, 28)[0], (seq + 1) * 20)
            self.assertEqual(AES.new(b'AAAAAAAAAAAAAAA0', AES.MODE_ECB).decrypt(data[40:72]) + data[72:], packet())
        self.assertEqual(c._sequence, 51)
        with self.assertRaises(ProtocolError):
            c.send_talk_audio(packet())
        self.assertEqual(len(c._unacked), 16)

    def test_bad_audio_rejected(self):
        c = self.camera()
        c.start_talk()
        bad_rate = bytearray(packet()); bad_rate[2] = 0x50
        stereo = bytearray(packet()); stereo[3] = 0x80
        for data in (b'', packet()[:-1], packet(1025), bytes(bad_rate), bytes(stereo)):
            with self.assertRaises(ValueError):
                c.send_talk_audio(data)
        c._send.assert_not_called()


if __name__ == '__main__':
    unittest.main()
