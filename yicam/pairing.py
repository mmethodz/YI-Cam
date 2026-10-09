"""Import an existing device key without replacing a working profile on failure."""
from .credentials import save_device
from .protocol import Camera


def device_key_from_response(response):
    # Frida serializes the RPC result as a dictionary, not a bare password.
    if not isinstance(response, dict) or not isinstance(response.get('password'), str):
        raise ValueError('The YI IOT client returned an invalid pairing response. The saved pairing was not changed.')
    key = response['password']
    if len(key.encode('utf-8')) != 15:
        raise ValueError('The imported device key has an unsupported format. The saved pairing was not changed.')
    return key


def import_from_client(ip, name, *, bridge_factory=None, camera_factory=Camera, persist=save_device):
    if bridge_factory is None:
        try:
            from .bridge import ClientBridge
        except ModuleNotFoundError as exc:
            if exc.name == 'frida':
                raise RuntimeError('Install requirements-import.txt, then restart the Python app to use the optional importer.') from None
            raise
        bridge_factory = ClientBridge
    bridge = bridge_factory(lambda *_: None)
    try:
        bridge.connect()
        key = device_key_from_response(bridge.script.exports_sync.pairing())
    finally:
        bridge.close()
    # Detach first. Save only after this exact key authenticates to the LAN camera.
    with camera_factory(ip, key) as camera:
        camera.firmware()
        device = {'ip': ip, 'password': key, 'name': name or 'YI Camera', 'uid': camera.uid}
    persist(device)
    return device
