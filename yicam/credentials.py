"""Keep the camera's device key encrypted for the current Windows account."""
import ctypes
from ctypes import wintypes
import json
import os
from pathlib import Path
import sys

DEFAULT_PATH = Path(__file__).resolve().parents[1] / '.local' / 'device.dpapi'


class Blob(ctypes.Structure):
    _fields_ = [('size', wintypes.DWORD), ('data', ctypes.POINTER(ctypes.c_ubyte))]


def _crypt(data: bytes, protect: bool) -> bytes:
    crypt = ctypes.WinDLL('crypt32', use_last_error=True)
    kernel = ctypes.WinDLL('kernel32', use_last_error=True)
    kernel.LocalFree.argtypes = [ctypes.c_void_p]
    kernel.LocalFree.restype = ctypes.c_void_p
    buffer = (ctypes.c_ubyte * len(data)).from_buffer_copy(data)
    source = Blob(len(data), buffer)
    result = Blob()
    func = crypt.CryptProtectData if protect else crypt.CryptUnprotectData
    func.restype = wintypes.BOOL
    func.argtypes = [ctypes.POINTER(Blob), ctypes.c_void_p, ctypes.POINTER(Blob),
                    ctypes.c_void_p, ctypes.c_void_p, wintypes.DWORD, ctypes.POINTER(Blob)]
    if not func(ctypes.byref(source), None, None, None, None, 1, ctypes.byref(result)):
        raise ctypes.WinError(ctypes.get_last_error())
    try:
        return ctypes.string_at(result.data, result.size)
    finally:
        kernel.LocalFree(result.data)


def _keyring():
    try:
        if sys.platform == 'darwin':
            from keyring.backends.macOS import Keyring
            return Keyring()
        from keyring.backends.SecretService import Keyring
        return Keyring()
    except ImportError:
        raise RuntimeError('Install requirements-crossplatform.txt and enable your OS keychain.') from None


def save_device(device: dict, path: Path = DEFAULT_PATH):
    if os.name != 'nt':
        # Use the OS keychain explicitly; never fall back to a plain-text keyring.
        _keyring().set_password('YI Local', 'paired-camera', json.dumps(device))
        return
    path.parent.mkdir(parents=True, exist_ok=True)
    encrypted = _crypt(json.dumps(device).encode('utf-8'), True)
    temporary = path.with_suffix('.tmp')
    temporary.write_bytes(encrypted)
    temporary.replace(path)


def load_device(path: Path = DEFAULT_PATH) -> dict:
    if os.name != 'nt':
        value = _keyring().get_password('YI Local', 'paired-camera')
        if not value:
            raise FileNotFoundError('No paired camera is saved in the OS keychain.')
        return json.loads(value)
    return json.loads(_crypt(path.read_bytes(), False))
