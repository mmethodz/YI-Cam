"""Background camera session, preview decoder, and recording controller."""
from collections import deque
import ctypes
import logging
import queue
import threading
import time

import av
from PIL import Image

from .credentials import load_device, save_device
from .protocol import Camera, AuthenticationError
from .recording import SegmentRecorder
from .storage import StoragePolicy
from .video import FrameClock, FrameOrder
from .platform_support import prevent_sleep


class CameraWorker(threading.Thread):
    def __init__(self, events):
        super().__init__(daemon=True, name='YI camera worker')
        self.events = events
        self.commands = queue.Queue()
        self.camera = None
        self.recorder = None
        self.wanted = False
        self.quitting = False
        self.reconnect_at = 0
        self.resolution = 1
        self.order = FrameOrder()
        self.clock = FrameClock()
        self.decoder = None
        self.decoder_id = None
        self.session_id = 0
        self.times = deque(maxlen=120)
        self.dimensions = (0, 0)
        self.last_frame = self.last_preview = self.last_stats = 0

    def emit(self, kind, **values):
        if kind in ('status', 'error', 'connected', 'recording'):
            logging.info('%s: %s', kind, values)
        self.events.put({'kind': kind, **values})

    def info(self):
        data = self.camera.device_info()
        if len(data) < 92:
            raise ValueError('This firmware returned an unsupported settings layout.')
        self.emit('settings', infrared=data[91], tracking=data[68], hardware=data[8])
        return data

    def connect(self):
        self.emit('status', message='Connecting directly to the camera on your LAN…')
        device = load_device()
        camera = Camera(device['ip'], device['password'], device.get('uid'))
        try:
            camera.connect()
            version = camera.firmware()
            self.camera = camera
            self.info()
            camera.start_video(self.resolution)
            if not device.get('uid'):
                device['uid'] = camera.uid
                save_device(device)
        except Exception:
            camera.close()
            self.camera = None
            raise
        self.order, self.clock = FrameOrder(), FrameClock()
        self.session_id += 1
        self.decoder = self.decoder_id = None
        self.times.clear()
        self.last_frame = time.monotonic()
        self.emit('connected', active=True)
        self.emit('firmware', version=version)
        self.emit('status', message='Connected directly. No vendor app or account login is needed.')

    def drop_connection(self):
        if self.recorder:
            self.recorder.close_segment()
        camera, self.camera = self.camera, None
        if camera:
            camera.close()
        self.emit('connected', active=False)

    def stop_recording(self):
        recorder, self.recorder = self.recorder, None
        if recorder:
            try:
                recorder.close()
            finally:
                prevent_sleep(False)
                self.emit('recording', active=False,
                    message=f'Saved {len(recorder.files)} clip(s), {recorder.frames:,} frames.')

    def command(self, name, value):
        if name == 'connect':
            self.wanted = True
            self.reconnect_at = 0
        elif name == 'disconnect':
            self.wanted = False
            self.stop_recording()
            self.drop_connection()
            self.emit('status', message='Disconnected.')
        elif name == 'quit':
            self.quitting = True
        elif name == 'record':
            if self.recorder:
                return
            if not self.camera or time.monotonic() - self.last_frame > 5:
                raise RuntimeError('Connect to a live camera before recording.')
            folder, policy_values = value
            policy = StoragePolicy(**policy_values).validate()
            recorder = SegmentRecorder(folder, policy=policy)
            try:
                recorder.check_storage()
            except Exception:
                recorder.close()
                raise
            self.recorder = recorder
            prevent_sleep(True)
            self.emit('recording', active=True, message='Recording; the next keyframe starts a clip.')
        elif name == 'stop':
            self.stop_recording()
        elif name == 'import':
            # Frida is optional and is loaded only for this one-time local import.
            from .bridge import ClientBridge
            ip, camera_name = value
            bridge = ClientBridge(lambda *_: None)
            try:
                bridge.connect()
                key = bridge.script.exports_sync.pairing()['password']
                # Validate against the actual LAN camera before saving it.
                with Camera(ip, key) as camera:
                    camera.firmware()
                    save_device({'ip': ip, 'password': key, 'name': camera_name, 'uid': camera.uid})
            finally:
                bridge.close()
            self.emit('paired', message='Pairing key imported and verified. You can close YI IOT.')
        else:
            if not self.camera:
                raise RuntimeError('Connect to the camera first.')
            if name == 'quality':
                self.camera.set_resolution(value)
                self.resolution = value
                self.emit('status', message='Stream quality requested. Live dimensions show the received resolution.')
            elif name == 'infrared':
                self.camera.set_night_vision(value)
                result = self.info()[91]
                if result != value:
                    raise RuntimeError('The camera did not retain the requested IR setting.')
                self.emit('status', message={0: 'Infrared mode selected. IR activates automatically in darkness.',
                    1: 'Colour night vision selected. The extra lighting is on.',
                    2: 'Automatic lighting mode selected.'}[value])
            elif name == 'tracking':
                self.camera.set_tracking(value)
                result = self.info()[68]
                if result != int(value):
                    raise RuntimeError('The camera did not retain the requested tracking setting.')
                self.emit('status', message='Motion tracking ' + ('on.' if value else 'off.'))
            elif name == 'move':
                self.camera.move(value, 0.12)
            elif name == 'halt':
                self.camera.stop_moving()
            elif name == 'refresh_settings':
                self.info()

    def process(self, frame):
        if frame.codec != 0x4e:
            raise ValueError('The camera sent a codec other than H.264.')
        stamp = self.clock.time(frame)
        now = time.monotonic()
        self.last_frame = now
        self.times.append(stamp)
        self.dimensions = frame.width, frame.height
        stream_id = (self.session_id, self.order.epoch, frame.generation)
        if self.recorder:
            try:
                self.recorder.write(frame.data, stamp, stream_id)
            except Exception:
                self.stop_recording()
                raise
        if stream_id != self.decoder_id:
            if not frame.keyframe:
                return
            self.decoder = av.CodecContext.create('h264', 'r')
            self.decoder_id = stream_id
        if self.decoder is None:
            return
        try:
            frames = self.decoder.decode(av.Packet(frame.data))
        except av.FFmpegError:
            self.decoder = self.decoder_id = None
            return
        if frames and now - self.last_preview >= 0.04:
            preview = frames[-1].to_image()
            preview.thumbnail((1100, 619), Image.Resampling.BILINEAR)
            self.emit('preview', image=preview)
            self.last_preview = now

    def run(self):
        try:
            while not self.quitting:
                try:
                    name, value = self.commands.get_nowait()
                    self.command(name, value)
                except queue.Empty:
                    pass
                except Exception as exc:
                    self.emit('error', message=str(exc))
                if self.quitting:
                    break
                if self.wanted and self.camera is None and time.monotonic() >= self.reconnect_at:
                    try:
                        self.connect()
                    except (AuthenticationError, FileNotFoundError, ValueError) as exc:
                        self.wanted = False
                        self.emit('error', message=str(exc) if not isinstance(exc, FileNotFoundError)
                                  else 'Add the camera pairing key in Camera setup first.')
                        self.emit('connected', active=False)
                    except Exception as exc:
                        self.emit('status', message=f'{exc} Retrying in 5 seconds…')
                        self.reconnect_at = time.monotonic() + 5
                camera = self.camera
                if camera:
                    try:
                        if not camera.running:
                            raise camera.error or ConnectionError('Camera disconnected.')
                        frame = camera.frames.get(timeout=0.04)
                        for frame in self.order.feed(frame):
                            self.process(frame)
                    except queue.Empty:
                        pass
                    except OSError as exc:
                        # Disk/retention failures stop recording but leave the preview usable.
                        if self.recorder:
                            self.stop_recording()
                        self.emit('error', message=str(exc))
                        if not camera.running:
                            self.drop_connection()
                            self.reconnect_at = time.monotonic() + 3
                    except Exception as exc:
                        self.emit('error', message=str(exc))
                        self.drop_connection()
                        self.reconnect_at = time.monotonic() + 3
                    if self.camera and time.monotonic() - self.last_frame > 8:
                        self.emit('status', message='Video stalled. Reconnecting…')
                        self.drop_connection()
                        self.reconnect_at = time.monotonic() + 3
                else:
                    time.sleep(0.04)
                now = time.monotonic()
                if now - self.last_stats > 1:
                    self.last_stats = now
                    span = self.times[-1] - self.times[0] if len(self.times) > 1 else 0
                    fps = (len(self.times) - 1) / span if span > 0 else 0
                    recorder = self.recorder
                    self.emit('stats', width=self.dimensions[0], height=self.dimensions[1], fps=fps,
                        frames=recorder.frames if recorder else 0,
                        mb=recorder.total_bytes / 1024**2 if recorder else 0,
                        file=recorder.current_path.name if recorder and recorder.current_path else '',
                        recycled=recorder.recycled if recorder else 0)
        finally:
            try:
                self.stop_recording()
                self.drop_connection()
            finally:
                self.emit('closed')
