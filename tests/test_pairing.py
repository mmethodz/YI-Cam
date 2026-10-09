import types
import unittest
from unittest.mock import Mock

from yicam.pairing import device_key_from_response, import_from_client
from yicam.protocol import Camera

KEY = '123456789012345'  # Synthetic test key.


class PairingTests(unittest.TestCase):
    def bridge(self, result):
        bridge = Mock()
        bridge.script = types.SimpleNamespace(exports_sync=types.SimpleNamespace(pairing=lambda: result))
        return bridge

    def test_rpc_dictionary_is_unwrapped_before_camera_constructor(self):
        bridge = self.bridge({'password': KEY})
        received = []
        def factory(ip, key):
            # Exercise the actual constructor that previously failed on dict.encode().
            camera = Camera(ip, key)
            received.append((ip, key))
            camera.connect = lambda: camera
            camera.firmware = lambda: 'test-firmware'
            camera.uid = '00' * 20
            return camera
        persist = Mock()
        device = import_from_client('192.168.0.2', 'Test camera', bridge_factory=lambda cb: bridge,
                                    camera_factory=factory, persist=persist)
        self.assertEqual(received, [('192.168.0.2', KEY)])
        self.assertEqual(device['password'], KEY)
        bridge.close.assert_called_once()
        persist.assert_called_once_with(device)

    def test_malformed_responses_never_echo_values_or_replace_saved_profile(self):
        for result in (None, KEY, {'password': {}}, {'password': 'private-invalid-value'}):
            with self.subTest(response_type=type(result).__name__):
                bridge, persist, camera = self.bridge(result), Mock(), Mock()
                with self.assertRaises(ValueError) as error:
                    import_from_client('192.168.0.2', 'Test', bridge_factory=lambda cb: bridge,
                                       camera_factory=camera, persist=persist)
                self.assertNotIn('private-invalid-value', str(error.exception))
                persist.assert_not_called(); camera.assert_not_called(); bridge.close.assert_called_once()

    def test_authentication_failure_preserves_existing_pairing(self):
        bridge = self.bridge({'password': KEY})
        camera = Mock()
        camera.__enter__ = Mock(return_value=camera)
        camera.__exit__ = Mock(return_value=False)
        camera.firmware.side_effect = RuntimeError('Device key rejected')
        persist = Mock()
        with self.assertRaisesRegex(RuntimeError, 'rejected'):
            import_from_client('192.168.0.2', 'Test', bridge_factory=lambda cb: bridge,
                               camera_factory=lambda *args: camera, persist=persist)
        persist.assert_not_called(); bridge.close.assert_called_once()

    def test_bridge_failure_detaches_and_keeps_saved_pairing(self):
        bridge, persist = self.bridge({'password': KEY}), Mock()
        bridge.connect.side_effect = RuntimeError('Open live view first')
        with self.assertRaisesRegex(RuntimeError, 'live view'):
            import_from_client('192.168.0.2', 'Test', bridge_factory=lambda cb: bridge, persist=persist)
        persist.assert_not_called(); bridge.close.assert_called_once()


if __name__ == '__main__':
    unittest.main()
