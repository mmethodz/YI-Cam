"""Offline setup QR composition for local01/local02, with no vendor token service."""
from __future__ import annotations

import io
import os
from pathlib import Path
import secrets
import string
import sys

from workshop import HOME, require

sys.path.insert(0, str(HOME.parent))
from yicam.provisioning import encode_password, encode_text, parse, serialize

REGIONS = ('EU', 'US', 'CN')


def setup_payload(ssid, password, region='EU'):
    """Use the retained scanner syntax; the marker is not an access credential.

    The camera scanner XORs decoded password bytes, whereas the mobile app
    transforms UTF-16 characters. Restrict passwords to their common ASCII
    subset until the other paths are deliberately implemented and qualified.
    """
    require(region in REGIONS, 'Choose region EU, US or CN')
    require(1 <= len(ssid.encode('utf-8')) <= 32 and
            all(ord(c) >= 32 and ord(c) != 127 for c in ssid),
            'Use a Wi-Fi name of 1–32 UTF-8 bytes, without control characters')
    require(password == '' or (8 <= len(password) <= 63 and
            all(32 <= ord(c) <= 126 for c in password)),
            'Use an 8–63 character ASCII Wi-Fi password, or empty for an open network')
    # The scanner has 31 bytes, but the app's saved bindkey reader has only 20.
    marker = region + ''.join(secrets.choice(string.ascii_letters + string.digits) for _ in range(18))
    return serialize({'b': marker, 's': encode_text(ssid), 'p': encode_password(password)})


def save_setup_qr(payload, path):
    """Save and decode-check a new PNG; never print its network credentials."""
    fields = parse(payload)
    require(list(fields) == ['b', 's', 'p'] and len(fields['b']) == 20 and
            fields['b'][:2] in REGIONS and fields['b'].isascii() and fields['b'].isalnum(),
            'Expected a local01/local02 setup payload')
    path = Path(path)
    require(path.suffix.lower() == '.png', 'Save the setup QR as a .png file')
    require(not path.exists(), 'Choose a new QR image filename')
    try:
        import zxingcpp
        from PIL import Image
    except ImportError as error:
        raise ValueError('QR export needs the optional packages in firmware/requirements-qr.txt in this Python environment') from error
    encoded = zxingcpp.write_barcode_to_image(
        zxingcpp.create_barcode(payload, zxingcpp.BarcodeFormat.QRCode), scale=12)
    image = Image.frombytes('L', (encoded.shape[1], encoded.shape[0]), bytes(encoded))
    buffer = io.BytesIO()
    image.save(buffer, format='PNG')
    path.parent.mkdir(parents=True, exist_ok=True)
    with path.open('xb') as stream:
        stream.write(buffer.getvalue())
        stream.flush()
        os.fsync(stream.fileno())
    with Image.open(path) as saved:
        codes = zxingcpp.read_barcodes(saved)
        require(len(codes) == 1 and codes[0].format == zxingcpp.BarcodeFormat.QRCode and
                codes[0].text == payload, 'Saved setup QR failed decode/readback verification')
