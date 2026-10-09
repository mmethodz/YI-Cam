"""Inspect or prepare setup QR files offline; never sends anything to a camera."""
from __future__ import annotations

import argparse
import json
from pathlib import Path
import sys

sys.path.insert(0, str(Path(__file__).resolve().parents[1]))
from yicam.provisioning import decode_password, decode_text, parse, token_cases


def read_payload(path: Path) -> str:
    if path.suffix.lower() == ".txt":
        return path.read_text(encoding="utf-8").strip()
    import zxingcpp
    from PIL import Image
    with Image.open(path) as image:
        codes = [code for code in zxingcpp.read_barcodes(image) if code.format == zxingcpp.BarcodeFormat.QRCode]
    if len(codes) != 1:
        raise ValueError("Expected exactly one readable QR code.")
    return codes[0].text


def main() -> None:
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("operation", choices=("inspect", "cases"))
    parser.add_argument("input", type=Path, help="QR image or UTF-8 .txt payload; contains network secrets")
    parser.add_argument("--reveal", action="store_true", help="Explicitly print decoded network credentials and token")
    parser.add_argument("--output", type=Path, default=Path(".local/qr-cases"), help="New private directory for generated experiments")
    args = parser.parse_args()
    payload = read_payload(args.input)
    fields = parse(payload)
    if args.operation == "inspect":
        result = {"fields": list(fields), "ssid_utf8_bytes": len(decode_text(fields["s"]).encode("utf-8")),
                  "password_characters": len(decode_password(fields["p"])), "token_characters": len(fields.get("b", "")),
                  "camera_acceptance": "not tested"}
        if args.reveal:
            result.update(ssid=decode_text(fields["s"]), password=decode_password(fields["p"]), fields_decoded=fields)
        print(json.dumps(result, indent=2, ensure_ascii=False))
        return
    cases = token_cases(payload)
    import zxingcpp
    from PIL import Image
    # Refuse to overwrite previous evidence. No network or device operations follow.
    args.output.mkdir(parents=True, exist_ok=False)
    for name, value in cases.items():
        image = zxingcpp.write_barcode_to_image(zxingcpp.create_barcode(value, zxingcpp.BarcodeFormat.QRCode), scale=12)
        pil = Image.frombytes("L", (image.shape[1], image.shape[0]), bytes(image))
        if zxingcpp.read_barcode(pil).text != value:
            raise ValueError("Generated QR failed independent decode.")
        pil.save(args.output / f"{name}.png")
    (args.output / "observations.json").write_text(json.dumps({
        "warning": "Images contain recoverable Wi-Fi credentials. Keep private. No camera tests have run.",
        "expiry": "No expiry duration established. An old token is not proof of an expired token.",
        "cases": {name: {"status": "not run", "qr_recognized": None, "wifi_joined": None,
                         "spoken_prompt": None, "local_auth_works": None, "cloud_result": None} for name in cases}
    }, indent=2), encoding="utf-8")
    print(f"Prepared {len(cases)} private QR files. Nothing was sent to the camera.")


if __name__ == "__main__":
    try:
        main()
    except (ValueError, OSError) as error:
        # Parsing exceptions should never echo the sensitive source payload.
        raise SystemExit(type(error).__name__ + ": " + str(error))
