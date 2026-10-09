"""Small platform adapters; camera transport and recording have no OS dependency."""
import ctypes
import os
from pathlib import Path
import subprocess
import sys


def prevent_sleep(enabled):
    if os.name == 'nt':
        ctypes.windll.kernel32.SetThreadExecutionState(0x80000001 if enabled else 0x80000000)


def open_path(path):
    path = str(Path(path).resolve())
    if os.name == 'nt':
        os.startfile(path)
    else:
        subprocess.Popen(['open' if sys.platform == 'darwin' else 'xdg-open', path])


class WriterLock:
    """Exclude a second recorder from the same library; release on process exit."""
    def __init__(self, folder):
        self.handle = open(Path(folder) / '.yi-writer.lock', 'a+b')
        try:
            self.handle.seek(0, 2)
            if not self.handle.tell():
                self.handle.write(b'\0')
                self.handle.flush()
            self.handle.seek(0)
            if os.name == 'nt':
                import msvcrt
                msvcrt.locking(self.handle.fileno(), msvcrt.LK_NBLCK, 1)
            else:
                import fcntl
                fcntl.flock(self.handle, fcntl.LOCK_EX | fcntl.LOCK_NB)
        except BaseException:
            self.handle.close()
            raise

    def close(self):
        self.handle.close()
