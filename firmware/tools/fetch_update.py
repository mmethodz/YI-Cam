"""Fetch vendor-offered AKOpen EU metadata/image without contacting the camera.

This reproduces the inspected app's metadata GET, not its install command.
Only the EU AKOpen route is currently evidenced. No account token is used.
"""
from __future__ import annotations

import argparse
import hashlib
import json
from pathlib import Path
import re
import tarfile
import urllib.error
import urllib.parse
import urllib.request

from inspect_update import MAX_IMAGE, inspect_bytes

METADATA = "https://plt-api-de.xiaoyi.com/vmanager/ipc/firmware/upgrade/app"
DOWNLOAD_HOST = "iot-firmware-eu.oss-eu-central-1.aliyuncs.com"


class NoRedirect(urllib.request.HTTPRedirectHandler):
    def redirect_request(self, req, fp, code, msg, headers, newurl):
        raise ValueError("Redirect refused; review the changed vendor origin first.")


def read_url(url: str, limit: int) -> bytes:
    opener = urllib.request.build_opener(NoRedirect())
    request = urllib.request.Request(url, headers={"User-Agent": "OpenYI firmware research"})
    with opener.open(request, timeout=30) as response:
        content = response.read(limit + 1)
        if len(content) > limit:
            raise ValueError("Vendor response exceeded the download bound.")
        return content


def main() -> None:
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("--device-id", required=True, help="Your own camera's A-prefixed DID, not its LAN key/UID")
    parser.add_argument("--version", required=True, help="Installed version read from this camera")
    parser.add_argument("--output", type=Path, default=Path(".local/stock"))
    args = parser.parse_args()
    if not re.fullmatch(r"A[A-Za-z0-9]{8,63}", args.device_id):
        raise ValueError("Unexpected DID format.")
    if not re.fullmatch(r"[A-Za-z0-9_.-]{1,96}", args.version):
        raise ValueError("Unexpected installed version format.")
    params = {"sname": "AKOpen", "version": args.version, "did": args.device_id}
    body = read_url(METADATA + "?" + urllib.parse.urlencode(params), 1024 * 1024)
    metadata = json.loads(body)
    if not isinstance(metadata, dict) or metadata.get("code") != 1:
        raise ValueError("Vendor did not return successful metadata.")
    result = metadata.get("result")
    if not isinstance(result, dict):
        raise ValueError("Vendor metadata has no result object.")
    if str(result.get("needUpdate", "")).lower() != "true":
        print("The vendor did not offer an update for that request. No image downloaded.")
        return
    version = result.get("version")
    if not isinstance(version, str) or not re.fullmatch(r"[A-Za-z0-9][A-Za-z0-9_.-]{0,95}", version):
        raise ValueError("Unexpected offered version format.")
    path, filename = result.get("downloadPath"), result.get("fileName")
    if not isinstance(path, str) or not isinstance(filename, str):
        raise ValueError("Missing download fields.")
    # The inspected A-prefixed-DID app path concatenates these fields verbatim.
    url = urllib.parse.urlsplit(path + filename)
    if (url.scheme not in ("http", "https") or url.hostname != DOWNLOAD_HOST
            or url.username or url.password or url.port not in (None, 443) or url.fragment
            or not url.path.startswith("/iotfirmware/AKOpen/")):
        raise ValueError("Unexpected download origin/path; inspect before accepting it.")
    tls_url = urllib.parse.urlunsplit(("https", url.netloc, url.path, url.query, ""))
    expected_md5 = result.get("md5Code")
    if not isinstance(expected_md5, str) or not re.fullmatch(r"[A-Fa-f0-9]{32}", expected_md5):
        raise ValueError("Vendor did not supply the expected package checksum.")
    data = read_url(tls_url, MAX_IMAGE)
    if hashlib.md5(data).hexdigest() != expected_md5.lower():
        raise ValueError("Package checksum differs from vendor metadata.")
    report, _ = inspect_bytes(data)
    if report["version"] != version:
        raise ValueError("Embedded and offered versions differ.")
    destination = args.output / version
    # A version label is not a content identity: never overwrite a previous acquisition.
    destination.mkdir(parents=True, exist_ok=False)
    (destination / "vendor-update.bin").write_bytes(data)
    (destination / "metadata.json").write_bytes(body)
    (destination / "inspection.json").write_text(json.dumps(report, indent=2) + "\n", encoding="utf-8")
    provenance = {"source_url": tls_url, "version": version,
                  "installed_version": args.version,
                  "requested_device_id_sha256": hashlib.sha256(args.device_id.encode()).hexdigest(),
                  "sha256": report["sha256"], "vendor_md5_matches": True,
                  "camera_contacted": False, "camera_updated": False,
                  "device_backup": False}
    (destination / "provenance.json").write_text(json.dumps(provenance, indent=2) + "\n", encoding="utf-8")
    print(json.dumps({"version": version, "bytes": len(data), "sha256": report["sha256"],
                      "saved_to": str(destination), "camera_updated": False}, indent=2))


if __name__ == "__main__":
    try:
        main()
    except urllib.error.HTTPError as error:
        raise SystemExit(f"Vendor HTTP request failed ({error.code}); request URL withheld.") from None
    except urllib.error.URLError:
        raise SystemExit("Vendor connection failed; request URL withheld.") from None
    except (ValueError, OSError, UnicodeError, tarfile.TarError) as error:
        raise SystemExit(f"Download refused: {error}") from None
