"""Offline YI IoT QR research. No network access or camera operations."""
from __future__ import annotations

import base64
import secrets
import string

PASSWORD_MASK = "89JFSjo8HUbhou5776NJOMp9i90ghg7Y78G78t68899y79HY7g7y87y9ED45Ew30O0jkkl"


def _password_transform(value: str) -> str:
    # Java charAt operates on UTF-16 code units, not Python Unicode code points.
    encoded = value.encode("utf-16-le")
    transformed = bytearray()
    for index in range(0, len(encoded), 2):
        char = int.from_bytes(encoded[index:index + 2], "little")
        mask = ord(PASSWORD_MASK[(index // 2) % len(PASSWORD_MASK)])
        transformed.extend((char ^ mask or char).to_bytes(2, "little"))
    return transformed.decode("utf-16-le")


def encode_text(value: str) -> str:
    return base64.b64encode(value.encode("utf-8")).decode("ascii")


def encode_password(value: str) -> str:
    if "\0" in value:
        raise ValueError("Passwords containing NUL cannot be represented unambiguously.")
    return encode_text(_password_transform(value))


def decode_text(value: str) -> str:
    return base64.b64decode(value, validate=True).decode("utf-8")


def decode_password(value: str) -> str:
    # XOR is symmetric, including the observed zero-result fallback.
    return _password_transform(decode_text(value))


def parse(payload: str) -> dict[str, str]:
    """Do not use URL form decoding: '+' in Base64 is a literal plus."""
    fields: dict[str, str] = {}
    if len(payload) > 4096:
        raise ValueError("QR payload is too large.")
    for part in payload.split("&"):
        key, separator, value = part.partition("=")
        if not separator or key not in {"b", "s", "p", "t", "d", "a"} or key in fields:
            raise ValueError("Unknown, duplicate or malformed QR field.")
        fields[key] = value
    if not {"s", "p"} <= fields.keys():
        raise ValueError("QR is missing its network fields.")
    decode_text(fields["s"])
    decode_password(fields["p"])
    if "a" in fields:
        decode_text(fields["a"])
    return fields


def serialize(fields: dict[str, str]) -> str:
    if any("&" in value or "\0" in value for value in fields.values()):
        raise ValueError("Field separator or NUL in QR value.")
    payload = "&".join(f"{key}={value}" for key, value in fields.items())
    parse(payload)
    return payload


def token_cases(payload: str) -> dict[str, str]:
    """Prepared experiments only; none is asserted to be camera-acceptable."""
    fields = parse(payload)
    token = fields.get("b", "")
    if "t" in fields or "a" in fields or "d" in fields or len(token) < 3:
        raise ValueError("Use a genuine fresh Wi-Fi QR with a binding token of at least three characters.")
    if not all(char in string.ascii_letters + string.digits + "._~-" for char in token):
        raise ValueError("Unrecognized token alphabet; inspect it before generating test cases.")
    altered = token[:-1] + ("A" if token[-1] != "A" else "B")
    random_tail = "".join(secrets.choice(string.ascii_letters + string.digits) for _ in token[2:])
    if random_tail == token[2:]:
        random_tail = altered[2:]
    def variant(value: str | None) -> str:
        changed = fields.copy()
        if value is None:
            del changed["b"]
        else:
            changed["b"] = value
        return serialize(changed)
    return {
        "issued-reference": serialize(fields),
        "altered-same-region": variant(altered),
        "missing-field": variant(None),
        "empty-field": variant(""),
        "random-same-region": variant(token[:2] + random_tail),
    }
