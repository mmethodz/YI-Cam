"""Original implementation of the YI TNP camera's authenticated LAN protocol.

No vendor binary, cloud service, account login, or running vendor app is used.
Only an already paired device key and the camera's LAN address are required.
"""
from __future__ import annotations

import base64
from collections import Counter
from dataclasses import dataclass
import hashlib
import hmac
import ipaddress
import queue
import secrets
import socket
import struct
import threading
import time

from Crypto.Cipher import AES


class ProtocolError(RuntimeError):
    pass


class AuthenticationError(ProtocolError):
    pass


def envelope(kind: int, body: bytes = b'') -> bytes:
    return struct.pack('>BBH', 0xF1, kind, len(body)) + body


def authentication(key: str, nonce: str) -> bytes:
    signature = base64.b64encode(hmac.new(key.encode(),
        ('user=xiaoyiuser&nonce=' + nonce).encode(), hashlib.sha1).digest())[:15]
    return (nonce.encode() + b',' + signature).ljust(32, b'\0')


class Channel:
    """Reassemble a reliable ordered byte stream from 16-bit UDP sequences."""
    def __init__(self):
        self.expected = 0
        self.pending = {}
        self.buffer = bytearray()

    def feed(self, sequence: int, data: bytes):
        distance = (sequence - self.expected) & 0xffff
        if distance >= 0x8000:
            return []  # Already acknowledged duplicate.
        if distance > 4096:
            raise ProtocolError('Camera packet sequence exceeds the receive window.')
        self.pending.setdefault(sequence, data)
        while self.expected in self.pending:
            self.buffer.extend(self.pending.pop(self.expected))
            self.expected = (self.expected + 1) & 0xffff
        messages = []
        while len(self.buffer) >= 8:
            version, kind, ability, reserved, size = struct.unpack_from('>BBBBI', self.buffer)
            if version not in (1, 2, 3) or kind not in (1, 2, 3) or size > 8 * 1024**2:
                raise ProtocolError('Invalid TNP stream header.')
            if len(self.buffer) < 8 + size:
                break
            messages.append((kind, ability, bytes(self.buffer[8:8 + size])))
            del self.buffer[:8 + size]
        return messages


@dataclass
class Reply:
    command: int
    number: int
    result: int
    data: bytes
    unsupported: int = 0


@dataclass
class VideoFrame:
    data: bytes
    sequence: int
    width: int
    height: int
    timestamp: float
    keyframe: bool
    generation: int
    codec: int
    day: int
    channel: int
    flags: int
    seconds: int = 0
    milliseconds: int = 0


@dataclass
class AudioFrame:
    data: bytes
    sequence: int
    seconds: int
    milliseconds: int
    codec: int
    flags: int


class Camera:
    def __init__(self, ip: str, password: str, uid: str | None = None):
        # A literal LAN address prevents accidentally sending pairing credentials
        # to a DNS name or a public endpoint entered as the camera address.
        address = ipaddress.ip_address(ip)
        if address.version != 4 or not address.is_private or address.is_unspecified or address.is_multicast:
            raise ValueError('Enter the camera\'s private IPv4 address.')
        if len(password.encode()) != 15:
            raise ValueError('This camera protocol requires a 15-byte pairing key.')
        self.ip = ip
        self._key = password
        self.expected_uid = uid
        self.uid = None
        self.peer = None
        self.socket = None
        self.thread = None
        self.running = False
        self.error = None
        self.frames = queue.Queue(maxsize=300)
        self.audio_frames = queue.Queue(maxsize=128)
        self.replies = queue.Queue(maxsize=100)
        self.stats = Counter()
        self.channels = {i: Channel() for i in range(6)}
        self._send_lock = threading.RLock()
        self._request_lock = threading.Lock()
        self._waiters = {}
        self._unacked = {}
        self._sequence = 0
        self._number = 0
        self.generation = 0
        self._nonce_prefix = secrets.token_hex(4)[:7]
        self._cipher = AES.new((password + '0').encode(), AES.MODE_ECB)
        self._last_received = time.monotonic()

    def connect(self, timeout=5):
        self.socket = socket.socket(socket.AF_INET, socket.SOCK_DGRAM)
        self.socket.setsockopt(socket.SOL_SOCKET, socket.SO_RCVBUF, 4 * 1024**2)
        self.socket.bind(('', 0))
        self.socket.settimeout(0.15)
        deadline, last_send = time.monotonic() + timeout, 0
        try:
            while time.monotonic() < deadline:
                if time.monotonic() - last_send > 0.5:
                    self.socket.sendto(envelope(0x30), (self.ip, 32108))
                    last_send = time.monotonic()
                try:
                    packet, peer = self.socket.recvfrom(65535)
                except socket.timeout:
                    continue
                if peer[0] != self.ip or len(packet) < 24 or packet[:2] != b'\xf1\x41':
                    continue
                uid = packet[4:24]
                if self.expected_uid and uid.hex() != self.expected_uid:
                    raise ProtocolError('A different camera answered at this address.')
                self.uid = uid.hex()
                self.peer = peer
                self.socket.sendto(envelope(0x41, uid), peer)
                while time.monotonic() < deadline:
                    try:
                        answer, source = self.socket.recvfrom(65535)
                    except socket.timeout:
                        self.socket.sendto(envelope(0x41, uid), peer)
                        continue
                    if source == peer and answer[:2] == b'\xf1\x42':
                        self.running = True
                        self._last_received = time.monotonic()
                        self.thread = threading.Thread(target=self._receive, daemon=True, name='YI LAN')
                        self.thread.start()
                        return self
                break
            raise TimeoutError('The camera did not answer LAN discovery.')
        except BaseException:
            self.close()
            raise

    def _send(self, packet: bytes):
        with self._send_lock:
            if self.socket is None or self.peer is None:
                raise ConnectionError('Camera is disconnected.')
            self.socket.sendto(packet, self.peer)

    def command(self, command: int, data=b'', response: int | None = None, timeout=4) -> Reply | None:
        if not self.running:
            raise ConnectionError(str(self.error or 'Camera is disconnected.'))
        with self._request_lock:
            self._number = (self._number + 1) & 0xffff
            number = self._number
            nonce = self._nonce_prefix + secrets.token_hex(4)
            auth = authentication(self._key, nonce)
            body = struct.pack('>HHHH', command, number, 0, len(data)) + auth + data
            message = struct.pack('>BBBBI', 2, 3, 0, 0, len(body)) + body
            waiter = queue.Queue(maxsize=1)
            if response is not None:
                self._waiters[number] = (response, waiter)
            try:
                with self._send_lock:
                    seq = self._sequence
                    self._sequence = (seq + 1) & 0xffff
                    packet = envelope(0xD0, struct.pack('>BBH', 0xD1, 0, seq) + message)
                    self._unacked[seq] = (packet, time.monotonic(), 0)
                    self._send(packet)
                if response is None:
                    return None
                try:
                    reply = waiter.get(timeout=timeout)
                except queue.Empty:
                    raise TimeoutError(f'Camera command 0x{command:04x} timed out.') from None
                if isinstance(reply, Exception):
                    raise reply
                if reply.result:
                    raise AuthenticationError(f'Camera rejected command authentication ({reply.result}).')
                if reply.unsupported:
                    raise ProtocolError(f'Camera does not support command 0x{command:04x}.')
                return reply
            finally:
                self._waiters.pop(number, None)

    def _receive(self):
        try:
            while self.running:
                try:
                    packet, peer = self.socket.recvfrom(65535)
                except socket.timeout:
                    packet, peer = b'', None
                if peer == self.peer and len(packet) >= 4:
                    magic, kind, size = struct.unpack_from('>BBH', packet)
                    if magic != 0xF1 or size != len(packet) - 4:
                        continue
                    self._last_received = time.monotonic()
                    self.stats[f'packet_{kind:02x}'] += 1
                    if kind == 0xE0:
                        self._send(envelope(0xE1))
                    elif kind == 0xF0:
                        raise ConnectionError('Camera ended the session.')
                    elif kind == 0xD1 and size >= 4:
                        marker, channel, count = struct.unpack_from('>BBH', packet, 4)
                        if marker == 0xD1 and channel == 0 and size == 4 + 2 * count:
                            with self._send_lock:
                                for offset in range(8, len(packet), 2):
                                    self._unacked.pop(struct.unpack_from('>H', packet, offset)[0], None)
                    elif kind == 0xD0 and size >= 4:
                        marker, channel, seq = struct.unpack_from('>BBH', packet, 4)
                        if marker == 0xD1 and channel in self.channels:
                            self._send(envelope(0xD1, struct.pack('>BBHH', 0xD1, channel, 1, seq)))
                            for message_kind, ability, body in self.channels[channel].feed(seq, packet[8:]):
                                self._message(channel, message_kind, ability, body)
                now = time.monotonic()
                if now - self._last_received > 10:
                    raise TimeoutError('Camera connection was lost.')
                with self._send_lock:
                    for seq, (packet, sent, attempts) in list(self._unacked.items()):
                        if now - sent > 0.3:
                            if attempts >= 12:
                                raise TimeoutError('Camera did not acknowledge a command.')
                            self._send(packet)
                            self._unacked[seq] = (packet, now, attempts + 1)
        except Exception as exc:
            if self.running:
                self.error = exc
        finally:
            self.running = False
            for response, waiter in list(self._waiters.values()):
                try:
                    waiter.put_nowait(self.error or ConnectionError('Camera disconnected.'))
                except queue.Full:
                    pass

    def _message(self, channel, kind, ability, body):
        self.stats[f'message_{kind}'] += 1
        if kind == 3 and len(body) >= 40:
            command, number, extra, size, result = struct.unpack_from('>HHHHI', body)
            if 40 + extra + size > len(body):
                raise ProtocolError('Truncated camera command response.')
            reply = Reply(command, number, result, body[40 + extra:40 + extra + size], ability)
            pair = self._waiters.get(number)
            if pair and (pair[0] == command or result):
                try:
                    pair[1].put_nowait(reply)
                except queue.Full:
                    pass
            if not self.replies.full():
                self.replies.put_nowait(reply)
        elif kind == 2 and channel == 1 and len(body) >= 24:
            data = body[24:]
            encrypted = len(data) // 16 * 16
            data = self._cipher.decrypt(data[:encrypted]) + data[encrypted:]
            frame = AudioFrame(data, struct.unpack_from('>H', body, 6)[0], struct.unpack_from('>I', body, 12)[0],
                               struct.unpack_from('>I', body, 20)[0], struct.unpack_from('>H', body)[0], body[2])
            try:
                self.audio_frames.put_nowait(frame)
            except queue.Full:
                raise ProtocolError('Audio processing fell behind; reconnect to recover safely.')
        elif kind == 1 and len(body) >= 24:
            codec, flags, live, online, generation, sequence, width, height, seconds = struct.unpack_from('>HBBBBHHHI', body)
            milliseconds = struct.unpack_from('>I', body, 20)[0]
            data = body[24:]
            keyframe = channel in (2, 4)
            if keyframe and len(data) >= 36:
                data = data[:4] + self._cipher.decrypt(data[4:36]) + data[36:]
            frame = VideoFrame(data, sequence, width, height, seconds + milliseconds / 1000,
                keyframe, generation, codec, body[16], channel, flags, seconds, milliseconds)
            try:
                self.frames.put_nowait(frame)
            except queue.Full:
                raise ProtocolError('Video processing fell behind; reconnect to recover safely.')

    def firmware(self):
        reply = self.command(0x1300, response=0x1301)
        return reply.data.rstrip(b'\0').decode('ascii', errors='replace')

    def device_info(self):
        return self.command(0x0330, b'\0' * 4, response=0x0331).data

    def start_video(self, resolution=1):
        self.generation = (self.generation + 1) & 0xff
        self.command(0x2345, bytes((self.generation, resolution, 1, 0)))

    def start_audio(self):
        self.command(0x0300, bytes(8))

    def stop_audio(self):
        self.command(0x0301, bytes(8))

    def set_resolution(self, mode):
        if mode not in (0, 1, 2, 3):
            raise ValueError('Unsupported resolution mode.')
        self.generation = (self.generation + 1) & 0xff
        return self.command(0x1311, struct.pack('>II', mode, self.generation), response=0x1312)

    def set_infrared(self, mode):
        if mode not in (1, 2, 3):
            raise ValueError('Infrared mode must be Auto, Day, or Night.')
        epoch = struct.pack('>HBBBBBB', 1970, 1, 1, 5, 0, 0, 0)
        return self.command(0x1321, struct.pack('>I', mode) + epoch * 2 + bytes(12), response=0x1322)

    def set_tracking(self, enabled):
        return self.command(0x400B, struct.pack('>I', int(bool(enabled))), response=0x400C)

    def set_night_vision(self, mode):
        if mode not in (0, 1, 2):
            raise ValueError('Choose infrared, colour, or automatic lighting.')
        return self.command(0x1380, struct.pack('>I', mode), response=0x1381)

    def move(self, direction, duration=0.25):
        if direction not in (1, 2, 3, 4) or not 0 < duration <= 0.5:
            raise ValueError('Use a valid direction and a movement of at most half a second.')
        try:
            self.command(0x4012, struct.pack('>II', direction, 0))
            time.sleep(duration)
        finally:
            self.stop_moving()

    def stop_moving(self):
        self.command(0x4013, bytes(4))

    def close(self):
        self.running = False
        if self.socket:
            try:
                if self.peer:
                    self._send(envelope(0xF0))
            except OSError:
                pass
            if self.thread and self.thread is not threading.current_thread():
                self.thread.join(timeout=1)
            self.socket.close()
            self.socket = None

    def __enter__(self):
        return self.connect()

    def __exit__(self, *args):
        self.close()
