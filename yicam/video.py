"""Merge the independent I/P-frame channels and retain the camera's timing."""
from __future__ import annotations

import time

from .protocol import VideoFrame


class FrameOrder:
    def __init__(self, maximum_wait=2.0):
        self.pending = {}
        self.expected = None
        self.wait_since = None
        self.maximum_wait = maximum_wait
        self.epoch = 0
        self.resets = 0

    def feed(self, frame: VideoFrame, now=None) -> list[VideoFrame]:
        now = time.monotonic() if now is None else now
        if self.expected is not None and (frame.sequence - self.expected) & 0xffff >= 0x8000:
            return []
        self.pending.setdefault(frame.sequence, frame)
        if self.expected is None and frame.keyframe:
            self.expected = frame.sequence
            self.pending = {seq: f for seq, f in self.pending.items()
                            if (seq - self.expected) & 0xffff < 0x8000}
        if len(self.pending) > 300:
            self.pending.clear()
            self.expected = None
            self.epoch += 1
            self.resets += 1
            return []
        result = []
        while self.expected in self.pending:
            result.append(self.pending.pop(self.expected))
            self.expected = (self.expected + 1) & 0xffff
            self.wait_since = None
        if self.pending:
            if self.wait_since is None:
                self.wait_since = now
            if now - self.wait_since > self.maximum_wait:
                keys = [seq for seq, f in self.pending.items() if f.keyframe]
                if keys:
                    base = self.expected if self.expected is not None else keys[0]
                    self.expected = min(keys, key=lambda seq: (seq - base) & 0xffff)
                    self.pending = {seq: f for seq, f in self.pending.items()
                                    if (seq - self.expected) & 0xffff < 0x8000}
                    self.epoch += 1
                    self.resets += 1
                    # Never send an undecodable P frame across a missing frame.
                    while self.expected in self.pending:
                        result.append(self.pending.pop(self.expected))
                        self.expected = (self.expected + 1) & 0xffff
                    self.wait_since = now if self.pending else None
        return result


class FrameClock:
    """This Anyka firmware sends a wrapping uptime in timestamp_ms.

    Other firmware uses a millisecond fraction; support both without assuming
    the device's wall clock is synchronized with the PC.
    """
    def __init__(self):
        self.previous = None
        self.elapsed = 0.0
        self.uptime = None

    def time(self, frame: VideoFrame):
        if self.uptime is None:
            self.uptime = frame.milliseconds > 999
        value = frame.milliseconds if self.uptime else frame.seconds * 1000 + frame.milliseconds
        if self.previous is not None:
            delta = ((value - self.previous) & 0xffffffff) if self.uptime else value - self.previous
            if not 0 <= delta <= 30000:
                raise ValueError('Camera timestamps jumped; reconnecting is required.')
            self.elapsed += max(delta, 1) / 1000
        self.previous = value
        return self.elapsed
