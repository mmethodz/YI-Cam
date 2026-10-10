import json
import os
from pathlib import Path
import sys
import tempfile
import unittest
from unittest.mock import patch

sys.path.insert(0, str(Path(__file__).resolve().parents[1] / 'tools'))
import workshop as w
import local_pairing as pairing
import local_setup as setup


class AssemblyTests(unittest.TestCase):
    def test_every_declared_patch_reassembles_exactly(self):
        from verify_local import assembly_checks
        for name in ('ak3918e-local01', 'ak3918e-local02'):
            with self.subTest(profile=name):
                profile = w.read_profile(name)
                self.assertEqual(assembly_checks(profile), sum(len(b['patches']) for b in profile['binaries']))

    def test_different_assembly_bytes_rejected(self):
        from verify_local import assembly_checks
        profile = w.read_profile('ak3918e-local01')
        profile['binaries'][0]['patches'][0]['after'] = '00000000'
        with self.assertRaisesRegex(ValueError, 'Reassembled patch differs'):
            assembly_checks(profile)

    def test_modified_assembly_source_digest_rejected(self):
        from verify_local import assembly_checks
        profile = w.read_profile('ak3918e-local01')
        profile['binaries'][0]['patches'][0]['assembly_sha256'] = '0' * 64
        with self.assertRaisesRegex(ValueError, 'source SHA'):
            assembly_checks(profile)

    def test_unknown_execution_suite_cannot_be_skipped(self):
        with self.assertRaisesRegex(ValueError, 'Unknown mandatory'):
            w.execution_checks(Path('.'), {'offline_checks': 'typo'})


class KeyExportTests(unittest.TestCase):
    def test_exact_canonical_key_only(self):
        key = b'Owner_TestKey15'
        self.assertEqual(pairing.parse_key(key + b'\n'), key.decode())
        for data in (key, key + b'\r\n', key + b'\nextra', b'\xff' * 15 + b'\n', b'x' * 14 + b' \n'):
            with self.subTest(length=len(data)), self.assertRaises(ValueError):
                pairing.parse_key(data)

    def test_transport_identity_matches_native_format(self):
        self.assertEqual(pairing.parse_uid(b'p2pid = TNPABC-000123-XYZ\n'),
                         (b'TNPABC\0\0' + b'\0\0\0{' + b'XYZ\0\0\0\0\0').hex())
        for data in (b'p2pid = TNPABC-123-XYZ', b'p2pid = TNPABC-4294967296-XYZ',
                     b'p2pid = TNPABC-000123-XYZ\np2pid = TNPABC-000124-XYZ', b'none=1'):
            with self.subTest(data=data), self.assertRaises(ValueError):
                pairing.parse_uid(data)

    def test_wrong_firmware_stops_before_key_read(self):
        seen = []
        def read(_, path, *args):
            seen.append(path)
            return b'stock-firmware\n'
        with patch.object(pairing, 'get', side_effect=read), self.assertRaisesRegex(ValueError, 'local01'):
            pairing.read_owner_profile(object(), '192.168.1.23', 'Door')
        self.assertEqual(seen, ['/usr/fw_version'])

    def test_binary_identity_required_before_reading_owner_key(self):
        profile = w.read_profile('ak3918e-local01')
        seen = []
        def read(_, path, *args):
            seen.append(path)
            return profile['target_version'].encode() if path.endswith('fw_version') else b'wrong binary'
        with patch.object(pairing, 'get', side_effect=read), self.assertRaisesRegex(ValueError, 'differs'):
            pairing.read_owner_profile(object(), '192.168.1.23', 'Door')
        self.assertNotIn('/etc/jffs2/openyi.key', seen)

    @unittest.skipUnless(os.name == 'nt', 'DPAPI is Windows-specific')
    def test_export_is_native_dpapi_and_never_overwrites(self):
        sys.path.insert(0, str(w.HOME.parent))
        from yicam.credentials import _crypt
        data = {'ip': '192.168.1.23', 'name': 'Door', 'uid': '00' * 20, 'password': 'Owner_TestKey15'}
        with tempfile.TemporaryDirectory() as directory:
            target = Path(directory) / 'camera.dpapi'
            pairing.save_windows_profile(data, target)
            encoded = target.read_bytes()
            self.assertNotIn(data['password'].encode(), encoded)
            self.assertEqual(json.loads(_crypt(encoded, False)), data)
            with self.assertRaisesRegex(ValueError, 'new pairing'):
                pairing.save_windows_profile(data, target)
            self.assertEqual(target.read_bytes(), encoded)


class LocalSetupTests(unittest.TestCase):
    def test_local_marker_and_network_roundtrip(self):
        from yicam.provisioning import parse, decode_text, decode_password
        payload = setup.setup_payload('Keittiö', '89JFSjo8', 'EU')
        fields = parse(payload)
        self.assertEqual(list(fields), ['b', 's', 'p'])
        self.assertEqual(len(fields['b']), 20)
        self.assertTrue(fields['b'].startswith('EU') and fields['b'].isalnum())
        self.assertEqual(decode_text(fields['s']), 'Keittiö')
        self.assertEqual(decode_password(fields['p']), '89JFSjo8')

    def test_camera_supported_limits(self):
        for ssid, password, region in [('', 'OnlyATest123', 'EU'), ('ä' * 17, 'OnlyATest123', 'EU'),
                                      ('bad\nname', 'OnlyATest123', 'EU'), ('Test', 'short', 'EU'),
                                      ('Test', 'p' * 64, 'EU'), ('Test', 'salasanaä', 'EU'),
                                      ('Test', 'OnlyATest123', 'XX')]:
            with self.subTest(region=region, lengths=(len(ssid), len(password))), self.assertRaises(ValueError):
                setup.setup_payload(ssid, password, region)
        self.assertTrue(setup.setup_payload('ä' * 16, 'p' * 63))
        self.assertTrue(setup.setup_payload('Open network', ''))

    def test_png_roundtrip_and_no_overwrite(self):
        import zxingcpp
        from PIL import Image
        payload = setup.setup_payload('OpenYI Test', 'OnlyATest123')
        with tempfile.TemporaryDirectory() as directory:
            target = Path(directory) / 'setup.png'
            setup.save_setup_qr(payload, target)
            with Image.open(target) as image:
                self.assertEqual(zxingcpp.read_barcode(image).text, payload)
            original = target.read_bytes()
            with self.assertRaisesRegex(ValueError, 'new QR'):
                setup.save_setup_qr(payload, target)
            self.assertEqual(target.read_bytes(), original)


if __name__ == '__main__':
    unittest.main()
