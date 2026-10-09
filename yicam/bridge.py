"""Version-checked adapter to the user's already authenticated PC client."""
from __future__ import annotations

import hashlib
import json
from pathlib import Path
import subprocess
import threading
import time

import frida

CLIENT_SHA256 = 'f6f9c422fa046f66d032a919e85444c195084c019efc959971a1213f46f32c46'
CREATE_NO_WINDOW = 0x08000000


def client_pids() -> list[int]:
    result = subprocess.run(
        ['powershell.exe', '-NoProfile', '-NonInteractive', '-Command',
         'Get-Process -Name YIIOTHomePCClientIntl -ErrorAction SilentlyContinue | Select-Object -ExpandProperty Id'],
        capture_output=True, text=True, timeout=15, creationflags=CREATE_NO_WINDOW,
    )
    return [int(line) for line in result.stdout.split() if line.isdigit()]


class ClientBridge:
    def __init__(self, callback):
        self.callback = callback
        self.session = None
        self.script = None
        self.pid = None
        self.closed = False

    def connect(self):
        pids = client_pids()
        if not pids:
            raise RuntimeError('Open YI IOT and its live camera view first.')
        errors = []
        for pid in pids:
            if self.closed:
                break
            session = None
            try:
                session = frida.attach(pid)
                metadata = session.create_script(
                    'rpc.exports = { info() { return {path:Process.mainModule.path, arch:Process.arch}; } };'
                )
                metadata.load()
                info = metadata.exports_sync.info()
                metadata.unload()
                digest = hashlib.sha256(Path(info['path']).read_bytes()).hexdigest()
                if info['arch'] != 'ia32' or digest != CLIENT_SHA256:
                    raise RuntimeError('This YI client build is not supported. No camera commands were sent.')
                script = session.create_script(Path(__file__).with_name('bridge.js').read_text())
                alive = threading.Event()
                def receive(message, data):
                    if message['type'] == 'error':
                        self.callback({'kind':'error','message':message.get('description','Bridge error')}, None)
                        return
                    payload = message.get('payload', {})
                    if payload.get('kind') == 'video':
                        alive.set()
                    self.callback(payload, data)
                script.on('message', receive)
                script.load()
                if not alive.wait(2.5):
                    script.exports_sync.stop()
                    script.unload()
                    session.detach()
                    continue
                self.session, self.script, self.pid = session, script, pid
                for _ in range(30):
                    try:
                        script.exports_sync.state()
                        break
                    except frida.RPCException:
                        time.sleep(0.1)
                session.on('detached', lambda reason, crash=None:
                    self.callback({'kind':'disconnected','message':reason}, None))
                return pid
            except Exception as exc:
                errors.append(str(exc))
                if session is not None:
                    session.detach()
        if errors:
            raise RuntimeError(errors[-1])
        raise RuntimeError('YI IOT is running but no live video arrived. Open one camera and reconnect.')

    def state(self):
        if not self.script:
            raise RuntimeError('Connect to the live camera first.')
        return self.script.exports_sync.state()

    def firmware(self):
        if not self.script:
            raise RuntimeError('Connect to the live camera first.')
        return self.script.exports_sync.firmware()

    def resolution(self, mode: int):
        if not self.script:
            raise RuntimeError('Connect to the live camera first.')
        return self.script.exports_sync.resolution(mode)

    def close(self):
        self.closed = True
        script, session = self.script, self.session
        self.script = self.session = None
        try:
            if script:
                script.exports_sync.stop()
                script.unload()
        finally:
            if session:
                session.detach()
