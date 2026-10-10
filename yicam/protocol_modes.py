"""Explicit wire modes. Missing configuration always means the stock protocol."""
STOCK_PROTOCOL = 'yi-stock'
LOCAL_PLAIN_PROTOCOL = 'openyi-lan-plain-v1'
LOCAL_PLAIN_FIRMWARE = '6.0.24.10_202610100003'
# Public framing/version discriminator, deliberately not an access secret.
LOCAL_PLAIN_MARKER = b'OpenYI-LAN-v1'.ljust(32, b'\0')


def is_plain(protocol):
    if protocol not in (STOCK_PROTOCOL, LOCAL_PLAIN_PROTOCOL):
        raise ValueError('Unknown camera protocol; select stock or OpenYI local02 explicitly.')
    return protocol == LOCAL_PLAIN_PROTOCOL
