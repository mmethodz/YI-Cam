# Local01 first installation — 10 October 2026

The owner flashed local01 through the interactive workshop on the reference
AK3918E / GC1084 camera. The installer returned successfully after reboot and
readback. The owner heard “Upgrading software”, “Software upgraded” and the
reboot chime. Those observations corroborate the tool's result; the spoken
messages alone are not the integrity check.

Installed version: `6.0.24.10_202610100002`.

Exact `update.tar` SHA-256:

```text
1b79b956861ef8bf1776faf283e553cc1d8dd3f7248036245a4daae9a9c800bd
```

## Verified evidence

The private `installation.json` reports `flash_committed`,
`installed_files_verified` and `B_payload_readback_verified` as true, with no
recorded error. The installer requires the expected camera MAC, target version
and every patched executable/library digest before it can report this result.
Its final status was:

```text
Installed version, patch and full B payload verified; functional camera checks still required
```

A separate offline comparison of the saved post-flash dump established:

| Check | Result |
| --- | --- |
| Post-flash full NOR capture | Exactly 8,388,608 bytes; SHA-256 agrees with the installation audit. |
| Installed B payload | All 2,826,240 bytes equal the verified `usr.sqsh4` image. This covers the complete image, not a claim about the unused remainder of partition B. |
| Boot area, kernel, identity partitions and root filesystem | Byte-identical to the fresh pre-flash second backup, through offset `0x2b3000`. |
| Data partition D and trailing flash | Byte-identical to the fresh pre-flash second backup. |
| Persistent settings partition C | Changed; expected to contain runtime/settings writes. This comparison alone does not attribute those changes or verify the canonical key. |
| Network access after reboot | The workshop reached FTP and read the installed filesystem and complete NOR dump. |
| Existing OpenYI pairing and live video | The owner confirmed a successful connection and working live video with the vendor app closed, using the existing saved pairing. This is owner-observed functional evidence after the flash. |
| Separate cold power cycle | Following the test procedure (vendor apps closed, unplug/reconnect power, same saved pairing), the owner confirmed that OpenYI established a connection again. No vendor key refresh, QR pairing or key re-import was reported. |
| Canonical key and explicit export, after the cold cycle | A read-only FTP preflight checked `/usr/fw_version` and the patched `anyka_ipc` digest, then exported `/etc/jffs2/openyi.key` and the factory transport identity into a new Windows DPAPI profile. They matched the existing saved client profile. A new protocol session using only that export authenticated successfully; all connections were closed. |
| Firmware response versus installed version file | The authenticated protocol query `0x1300` still returned `6.0.24.10_202401091113`, while `/usr/fw_version` contained the verified local01 version above. Do not equate this protocol response with the application partition's installed version. The source of the response remains to be traced. |

The final audit was checked from PC files without opening another camera
connection. The later key-export preflight did contact the camera read-only.
Raw dumps, installer tokens, camera identifiers and key-bearing
configuration remain in ignored private storage and are not published.

## Qualification boundary and next checks

This is one successful Wi-Fi installation, system reboot, exact application
payload readback, existing-client live-video check and reconnection after one
separate cold power cycle on one unit, followed by direct canonical-key export
and authentication with that export. The canonical key read after the cold cycle
matches the saved client key; there is no pre-cycle canonical-file capture for
a file-to-file comparison. It is not full camera/application qualification,
a demonstrated recovery from an unbootable image, or a result for local02.

Still to verify on local01:

- local recording/playback, PTZ, night modes, tracking and two-way audio;
- repeated/extended power-cycle behavior and longer-term operation;
- operation without Internet, plus startup/idle traffic capture;
- first-key generation without a prior canonical key, and failed-boot recovery.

## Reset/pairing test preparation

The actual `/usr/sbin/recover_cfg.sh` was read and matched SHA-256
`47fa7ffdaacfbb472de6752ec824812f4d45f1cf12375cd3e6fbd8537cfdceea`.
It restores two settings files, removes other selected configuration files and
`/etc/jffs2/isp*.conf`, and does not delete `openyi.key`. This establishes the
inspected script's behavior, not every physical reset path.

Current settings and the canonical key were backed up under Windows DPAPI;
no `isp*.conf` files were present in that directory. A fresh local-marker QR for
the current Wi-Fi was saved and decoded back successfully without a vendor
token. The owner selected **Internet available** for the first test. See the
[test procedure](LOCAL_PAIRING.md#physical-reset-and-pairing-test).

## Physical reset and fresh native onboarding — 11 October 2026

The owner reset the camera and displayed the locally generated QR. They reported
the scan prompt, Wi-Fi connection, “pairing successful” and “you can start using
your camera now”, then clicked Connect in OpenYI and obtained a connection.
No vendor QR/token or vendor app was used for this flow.

A new post-reset read-only export passed the installed version/executable checks.
The canonical key and factory transport identity matched the pre-reset export.
An independent protocol session authenticated using only the fresh export.
All verification connections were closed. The owner key therefore survived this
reset; this reset is not a way to revoke previous clients' access.

The new native onboarding page was then exercised in the installed Windows app:

- camera Wi-Fi reuse read network details directly from the verified local01 unit;
- LAN broadcast discovery found the reference camera;
- Connect and save camera read its key directly, authenticated and saved the profile;
- the live page reported connected locally, 1280 × 720 and approximately 13.6 source fps;
- recording remained off and the alarm remained disarmed.

After the final app update, OpenYI was closed cleanly, restarted and connected
again using its saved imported profile. The live status reported 1280 × 720 at
approximately 15.0 source fps. The persisted key and identity were independently
compared with the post-reset camera export and matched. English and Finnish
setup layouts, including a synthetic QR preview, fit at the default window size.

This PC uses Ethernet: Native Wi-Fi returned no active Wi-Fi connection, and the
camera-based fallback worked. Windows Wi-Fi autofill parsing has automated tests,
but current-SSID/password detection on a physically connected Wi-Fi adapter still
needs hardware coverage. A separate test executable timed out at TNP session
establishment without the installed app's firewall rule; native onboarding passed
from the installed executable with its existing allowance. No firewall settings
were changed for these tests.

Internet was available throughout. These results establish vendor-app-free setup
on one already patched unit, not WAN-isolated first setup, support for other models,
initial random-key creation on hardware, or recovery from an unbootable image.

The existing saved pairing passed the first connection check without a factory
reset, new QR or key re-import. On another installation, try the existing pairing
first. If it is rejected, use the workshop's
explicit local01 key export and the app's verified pairing-file import; do not
infer from a failed stream that another flash is required.

The inspected build and its manifest are preserved as built. Their original
qualification text describes the pre-flash state; this dated evidence record
updates the hardware qualification without rewriting those artifacts.
