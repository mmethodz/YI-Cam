"""Copy original H.264 packets into bounded, independently playable MP4 files."""
from __future__ import annotations

from datetime import datetime
from fractions import Fraction
import io
from pathlib import Path
import re
import shutil
import time
import uuid

import av
from .storage import Library, StoragePolicy
from .platform_support import WriterLock

START_CODE = re.compile(b'\x00\x00\x00\x01|\x00\x00\x01')


def nal_units(data: bytes) -> list[tuple[int, bytes]]:
    starts = list(START_CODE.finditer(data))
    result = []
    for index, match in enumerate(starts):
        end = starts[index + 1].start() if index + 1 < len(starts) else len(data)
        if end > match.end():
            result.append((data[match.end()] & 31, data[match.start():end]))
    return result


class SegmentRecorder:
    """One camera, video only. Caller supplies ordered source timestamps.

    Start/rotate on an IDR with SPS/PPS. MP4 fragments are flushed at keyframes,
    keeping completed fragments readable if the process is interrupted.
    """
    def __init__(self, folder: Path, segment_seconds=600, minimum_free=2 * 1024**3,
                 policy: StoragePolicy | None = None):
        self.folder = Path(folder).resolve()
        self.folder.mkdir(parents=True, exist_ok=True)
        self.segment_seconds = segment_seconds
        self.minimum_free = minimum_free
        self.policy = policy or StoragePolicy(segment_minutes=segment_seconds/60,
                                               minimum_free_gib=minimum_free/1024**3)
        self.policy.validate()
        self.segment_seconds = self.policy.segment_minutes * 60
        self.writer_lock = WriterLock(self.folder)
        try:
            self.library = Library(self.folder)
        except BaseException:
            self.writer_lock.close()
            raise
        self.output = None
        self.stream = None
        self.source = None
        self.parameters = {}
        self.active_parameters = None
        self.decoder = None
        self.started = None
        self.last_pts = -1
        self.last_packet_time = None
        self.last_check = 0.0
        self.frames = 0
        self.total_bytes = 0
        self.files = []
        self.current_path = None
        self.last_duration = 67
        self.recycled = 0

    def check_storage(self):
        deleted = self.library.enforce(self.policy,
            self.current_path.name if self.output and self.current_path else None)
        self.recycled += len(deleted)

    def _open(self, keyframe: bytes, timestamp: float):
        self.check_storage()
        self.source = av.open(io.BytesIO(keyframe), format='h264')
        source_stream = self.source.streams.video[0]
        width, height = source_stream.width, source_stream.height
        if not width or not height:
            self.source.close()
            self.source = None
            raise ValueError('A complete H.264 keyframe is required to start recording.')
        stamp = datetime.now().strftime('%Y-%m-%d_%H-%M-%S')
        self.current_path = self.folder / f'YI_{stamp}_{width}x{height}_{uuid.uuid4().hex[:6]}.mp4'
        self.output = av.open(str(self.current_path), 'w', format='mp4',
            options={'movflags':'+frag_keyframe+empty_moov+default_base_moof'})
        self.stream = self.output.add_stream_from_template(source_stream)
        self.library.register(self.current_path, width, height)
        self.started = timestamp
        self.last_pts = -1
        self.active_parameters = dict(self.parameters)
        self.files.append(self.current_path)

    def write(self, data: bytes, timestamp: float, decoder: str = 'default'):
        if self.decoder is None:
            self.decoder = decoder
        elif decoder != self.decoder:
            # The YI client creates a fresh decoder when resolution changes.
            self.close_segment()
            self.parameters.clear()
            self.active_parameters = None
            self.decoder = decoder
        now = time.monotonic()
        if now - self.last_check > 2:
            self.check_storage()
            self.last_check = now
        units = nal_units(data)
        types = {kind for kind, unit in units}
        for kind, unit in units:
            if kind in (7, 8):
                self.parameters[kind] = unit
        is_key = 5 in types
        has_picture = bool(types & {1, 5})
        if not has_picture:
            return
        # A decoder may reconnect after a gap using new SPS/PPS or a new size.
        changed = self.output and self.active_parameters != self.parameters
        gap = self.last_packet_time is not None and timestamp - self.last_packet_time > 10
        rotate = self.output and is_key and (timestamp - self.started >= self.segment_seconds or gap)
        if changed or rotate:
            self.close_segment()
        self.last_packet_time = timestamp
        if self.output is None:
            if not is_key or not all(kind in self.parameters for kind in (7, 8)):
                return
            prefix = b''.join(self.parameters[kind] for kind in (7, 8) if kind not in types)
            data = prefix + data
            self._open(data, timestamp)
        packet = av.Packet(data)
        packet.stream = self.stream
        packet.time_base = Fraction(1, 1000)
        # Preserve camera timing; a raw H.264 demuxer otherwise guesses 25 fps.
        pts = max(self.last_pts + 1, round((timestamp - self.started) * 1000))
        if self.last_pts >= 0:
            self.last_duration = min(1000, max(1, pts - self.last_pts))
        packet.pts = packet.dts = pts
        packet.duration = self.last_duration
        packet.is_keyframe = is_key
        self.output.mux(packet)
        self.last_pts = pts
        self.frames += 1
        self.total_bytes += len(data)

    def close_segment(self):
        try:
            if self.output is not None:
                self.output.close()
                self.library.finish(self.current_path, max(0, self.last_pts + self.last_duration) / 1000)
        finally:
            self.output = self.stream = None
            if self.source is not None:
                self.source.close()
                self.source = None

    def close(self):
        try:
            self.close_segment()
        finally:
            self.library.close()
            self.writer_lock.close()
