# Account-free local provisioning: candidate and evidence

Applies to the pinned AK3918E / GC1084 application filesystem
`6.0.24.10_202401091113`, patched as [local01](LOCAL01.md) or
[local02](LOCAL02.md). Local01's [first installation and reboot](LOCAL01_INSTALLATION.md)
succeeded; local02 has not been flashed. On **11 October 2026**, a physical reset
and local-marker QR scan succeeded on local01: the owner heard recognition,
Wi-Fi connection and pairing success, then connected with OpenYI. A fresh export
authenticated, with the key and factory identity unchanged. Native direct import
and live video subsequently passed. Internet was available during these tests;
WAN-isolated provisioning remains unqualified.

## The owner key and the setup marker are different

Local01 is the recommended path. It retains the normal camera protocol and gives
the owner control over the 15-character device password used by OpenYI:

| Stage | Local01 behavior |
| --- | --- |
| First patched boot, valid existing client key | Migrate `lastP2pPwd` into `/etc/jffs2/openyi.key`; preserve the client's existing key. |
| No existing key | Generate a local random key, write and sync it before publishing runtime/config copies. |
| Subsequent start or Wi-Fi change | Load the same canonical key. No vendor registration or timed rotation. |
| Client onboarding | The native app reads the local key, verifies camera authentication and saves a Windows-encrypted profile. The workshop's separate export/import route is also available. |
| Malformed/unreadable key file | Fail closed; do not silently replace the owner's key. |

Factory device/TNP identity remains unchanged. The QR's `b` field is a setup
marker, not this password. The patch removes the identified cloud binding call;
the marker satisfies the retained parser and selects a region. There is no
local marker signature or expiry added by OpenYI.

Export uses existing owner/root FTP access and checks the installed version and
patched executable digest before reading the canonical file. FTP is an
unencrypted LAN maintenance interface; the `.dpapi` output is encrypted for the
current Windows user. No key is printed, broadcast or embedded in the firmware.
The client authenticates before replacing its saved pairing. See the
[key reader/writer map](LOCAL_KEY_MAP.md) for session behavior and reset limits.

Local02 deliberately has no device-password authentication or media encryption.
Its explicit client mode is an experimental alternative, not a prerequisite for
account-free setup or remote viewing. Dashboard authentication and HTTPS are
separate from the camera protocol and are outside this firmware work.

## Exact fresh-setup payload

```text
b=<region-and-local-marker>&s=<base64-ssid>&p=<base64-masked-password>
```

Field order is `b`, `s`, `p`. Fields use literal `&` and `=` delimiters, without
URL/form encoding; Base64 `+` must not become a space.

- `b`: 20 ASCII alphanumeric characters. The generator uses `EU`, `US` or `CN`
  followed by 18 random alphanumeric characters. The scanner can copy 31
  characters, but the application's saved bind-key reader only accommodates 20.
- `s`: standard padded Base64 of the SSID's UTF-8 bytes. The generator permits
  1–32 bytes and rejects control characters.
- `p`: XOR each password byte with the corresponding repeating mask byte. If
  the XOR result is zero, retain the original byte. Standard padded Base64
  encodes the result. Empty `p=` represents an open network.

The public mask, embedded in the stock format, is:

```text
89JFSjo8HUbhou5776NJOMp9i90ghg7Y78G78t68899y79HY7g7y87y9ED45Ew30O0jkkl
```

This is reversible obfuscation. The saved PNG contains recoverable Wi-Fi
credentials. The camera parser operates on decoded bytes, while the inspected
mobile generator operates on UTF-16 characters before UTF-8 encoding. The local
generator therefore permits printable ASCII passwords of 8–63 characters, or
empty for an open network. Non-ASCII passwords and 64-digit raw PSKs are not
qualified by this flow. UTF-8 SSIDs are covered separately.

The existing stock Wi-Fi-change format (`t=1&...&d=...`) and cellular/APN variants
are separate; the account-free generator emits only the fresh-setup format.

## What the actual installed parser does

Unchanged `lib/libscanner.so` SHA-256:

```text
baf862649c0a8ffe26861328fdda1a7e19bab9a1409f46f498ec7ec3aee1e288
```

Library-relative A32 sites are field extraction `0x312c`, Base64 decoding
`0x329c` and `yi_get_scan_result` at `0x3438`. The latter requires a nonempty
marker and decoded SSID; a missing/empty password is allowed. Output offsets
`0`, `0x40`, `0x80` hold SSID, password and marker respectively.

The application's `judge_bindkey` at `0x234dc` maps the tested prefixes to
region values: EU → 16, US → 17, CN → 1. Changing the tail does not change that
selection. This function is region parsing, not evidence of token authentication.
The stock binding worker calls the cloud and checks its result before the
spoken-success path. Local01/local02 replace that cloud dependency while
retaining the local Wi-Fi persistence path.

Mandatory verification executes 16 provisioning cases: all three prefixes,
UTF-8 SSID, maximum supported lengths, XOR-zero fallback, ASCII punctuation,
open network, missing/empty marker and SSID, and region selection with an altered
tail. The optical ZBar boundary supplies synthetic decoded QR text; the actual
field, Base64, XOR and region ARM instructions execute. No camera or host network
is used. Startup tests separately execute the patched worker with voice and
configuration operations observed through modeled boundaries.

These tests establish acceptance of local-marker text in the patched paths.
They do not establish stock-cloud token reuse, expiry or signature semantics.
The later physical local01 result is recorded
separately above; local02 still has no hardware qualification.

## Save an image and onboard the client

The Windows app defaults to **Camera pairing → OpenYI firmware setup**. A new
installation with no saved primary camera opens this page automatically.

1. **Use this PC's Wi-Fi** reads connected and saved Windows networks, even when
   the PC uses Ethernet, and their passwords when Windows permits. A single
   network fills automatically; with several saved networks, choose one from
   the Wi-Fi name list. An existing matching selection or the sole connected
   network is preferred. Detection runs when the page first opens. The app calls
   the [Native Wi-Fi query API](https://learn.microsoft.com/en-us/windows/win32/api/wlanapi/nf-wlanapi-wlanqueryinterface)
   and [saved-profile list API](https://learn.microsoft.com/en-us/windows/win32/api/wlanapi/nf-wlanapi-wlangetprofilelist)
   and [profile API](https://learn.microsoft.com/en-us/windows/win32/api/wlanapi/nf-wlanapi-wlangetprofile);
   it does not export plaintext profiles or parse localized command output.
   If no networks are saved, enter the network or choose **Use saved camera's
   Wi-Fi** to read it from an existing, verified local01 camera. SSID/password can
   be edited. Windows may withhold passwords independently of SSID access;
   location permissions can also affect detection of the current connection.
   An unreadable password is not treated as an open network: that choice requires
   the explicit **Network has no password** checkbox.
2. **Show QR code** previews the locally generated marker; **Save QR image** saves
   an ordinary PNG for phone display. Editing Wi-Fi fields invalidates the old
   preview. The region defaults to EU and can be changed in Advanced settings.
3. After the camera completes QR/Wi-Fi setup, choose **Find cameras**. Three short
   TNP broadcasts locate local devices without passwords or a port sweep. Pick
   the intended camera; a manual LAN address remains available when broadcasts
   are blocked. No pairing ID or vendor binding token is requested.
4. **Connect and save camera** verifies the installed version file and exact
   key-handling binary, reads the canonical key and UID, authenticates, then saves
   under Windows DPAPI. Discovery's UID must match the imported identity. The
   first camera becomes primary; a known identity refreshes its own entry and a
   new additional identity gets a separate grid entry. Existing profiles survive
   failed reads/authentication. Camera names, source quality and direction choices
   for a matching identity are retained.

The maintenance password has the reference camera's default, with an override
under **Advanced settings**. It is transient and is not saved in preferences.
The read-only FTP transport remains unencrypted; normal authenticated HMAC/AES
camera sessions use the newly saved key. The native flow requires local01 and
fails closed on a stock version or an unrecognized key-handling binary.
Stock QR tools remain on **Original firmware · advanced**.

### Workshop/reference route

Run `python firmware/openyi_fw.py` and choose **Save local setup QR**. The optional
PNG dependencies are listed in `firmware/requirements-qr.txt`; install them in
the Python environment running the menu. The tool asks for SSID, hidden Wi-Fi
password, region and a new `.png` path. It saves the image and decodes the saved
file to verify an exact payload round trip. It never contacts or resets a camera.

Save the PNG and transfer it privately to a phone for display in front of the camera.

After the owner has separately installed and inspected the intended candidate,
physical setup testing requires that camera's QR setup mode. A reset is not
performed by the generator; the one successful physical reset is not evidence
of recovery from a failed firmware image. Test with vendor connectivity blocked and record
QR recognition, association, spoken prompt and working local commands separately.

For local01, use the workshop's **Export local01 camera key for OpenYI** and the
app's **Import encrypted pairing profile**. Existing migrated pairings may need
no replacement. For local02, explicitly select **keyless, unencrypted LAN** in
camera setup and verify/save the camera's LAN address; no key export is needed.
The native client checks the exact local02 firmware version before allowing that
mode. This version/UID check detects mode mismatches, not cryptographic identity.

## Physical reset and pairing test

This procedure targets the already installed **local01** image. It requires no
new firmware build or flash. One run passed on 11 October with Internet available;
repeat with WAN isolation as a separate qualification case.

1. Export the current canonical key and retain the working client profile as a
   backup. Preserve current settings privately before resetting. The inspected
   reset script restores settings and removes `/etc/jffs2/isp*.conf`; it does not
   remove `openyi.key`. The physical button may have additional behavior, so this
   is an expectation to test, not a guarantee or credential-revocation feature.
2. Generate/save the local setup QR, confirm the saved image decodes correctly,
   and display it on a phone. Stop recording, disarm the optional alarm, disconnect
   OpenYI and close vendor apps. Keep power connected during setup.
3. Use the camera's physical reset procedure to enter QR setup, then show the
   local QR to its lens. Record QR recognition, Wi-Fi association and spoken
   success separately. Do not treat a spoken prompt alone as proof of pairing.
4. Find the camera's LAN address after it joins; DHCP may assign a different one.
   Use the workshop's **Export local01 camera key for OpenYI** to make a new
   encrypted pairing file directly from the camera. This action checks the
   installed version file and patched executable hash. It does not rely on the
   protocol firmware string, which still reported the stock version on local01.
5. Authenticate using only that new export, then use **Import encrypted pairing
   profile** in OpenYI and confirm live video. Retain the old profile separately
   so this test demonstrates fresh onboarding rather than an old session.
6. Record whether the factory transport identity and canonical key survived,
   whether the camera reconnects after another power cycle, and whether Internet
   was available. Vendor-app-free operation and WAN-isolated operation are
   separate observations; the first planned test has Internet available.

Never publish the QR, exported profile or raw settings as test evidence. Record
non-secret pass/fail observations and firmware/build hashes in the installation
report. The current DPAPI file belongs to the Windows account that exported it;
on a customer's computer, perform that export under the account running OpenYI.
