# Local credential map — installed AK3918E / GC1084

This map applies to **6.0.24.10_202401091113 only**. Executable SHA-256 values
are pinned in [local01](../profiles/ak3918e-local01.json). Application addresses
are virtual addresses in `bin/anyka_ipc`; library addresses are relative to
`lib/libYiP2P.so`. Both use A32 on ARM926EJ-S, not Thumb-2.

## Which key?

| Value | Storage / address | Observed use |
| --- | --- | --- |
| Local 15-character password | `yi_cfg.ini:lastP2pPwd`; config `0x4b2004`; runtime `0x51b1dc` | Command HMAC and per-session media encryption. This is OpenYI's existing `password` field. |
| Last password refresh time | `lastUpdateP2pPwdTime`, `0x4b2000` | Stock login compares age with 7,200 seconds. It is not an expiry embedded in the password. |
| QR binding token | `yi.conf:bindkey`; runtime `0x51b1fc` | Region/setup parsing and vendor `/v5/ipc/qr_bind` request. Separate from the local password. |
| Factory device identity | `yi.conf:dev_id`, `0x51b21c` | Vendor identity / manufacturing data. Preserved. |
| Factory device key | `yi.conf:dev_key`, `0x51b23c` | Passed into the transport listener; not OpenYI's 15-character password. Preserved. |
| Factory TNP identity | `yi.conf:p2pid`, `0x51b25c` | Local discovery/connection identity. Preserved. |
| Server-returned transport settings | `0x51b440`, `0x51b460`, `0x51b480` | Cloud DID, six-character listen credential, and encoded server list. The candidate supplies local listen settings without fetching these. |

No actual owner key, device identity or Wi-Fi password is included here.

## Stock writers and lifetime

1. `yi_cfg_init` (`0x27ec0`) clears the cached password at `0x27ef4–0x27f00`,
   then loads `yi_cfg.ini` through a name/type/destination table. The entry for
   `lastP2pPwd` points to `0x4b2004`. This table-based write would be missed by
   searching only for literal references to the password address.
2. `webapi_do_login` (`0x434bc`) handles password selection. At `0x43640–0x4365c`
   it compares the last refresh time with 7,200 seconds and can call
   `gen_nonce(15, ...)`. An absent/short password also generates one at
   `0x43698–0x436b4`. Thus a changed key after power cycling does not establish
   that every power cycle inevitably changes it.
3. The cached path copies `0x4b2004` to runtime `0x51b1dc` at `0x43674`.
   The current runtime key is reused on the non-refresh path at `0x43690`.
4. The camera submits its selected password in the `/v6/ipc/on_line` request.
   It is not simply downloading a new signed local password from the vendor.
   On successful response, `0x43ce4` updates the refresh time; `0x43cec` and
   `0x43cfc` publish runtime/cached copies; `0x43d00` invokes `yi_save_cfg`.
   The failure fallback at `0x43db0` restores the cached key.
5. `yi_save_cfg` (`0x281c0`) iterates the same configuration table and writes
   `lastP2pPwd` back to `yi_cfg.ini`. Its unchecked stdio writes are why the new
   canonical file uses explicit short-I/O handling, `fsync` and checked close.

Located login call sites: startup `yi_cloud_init` at `0x466fc`, Wi-Fi change
callback at `0x2e71c`, and periodic registration worker at `0x47cd4`. The latter
is unreachable after the candidate's one-time local initialization/binding exit.
The Wi-Fi callback remains and reloads the same persistent key.

`GET_YI_DID` (`0x45f78`) loads factory identity/key/TNP fields from `yi.conf`;
it does **not** provide the 15-character password. Factory reset through
`yi_p2p_on_reset_device` (`0x30db8`) invokes `recover_cfg.sh`, which restores
`yi_cfg.ini` and Wi-Fi configuration. The inspected script does not delete the
new `openyi.key`. Physical reset and other recovery paths still need testing;
a configuration reset must not be advertised as credential revocation.

## Readers, alternate credentials and session copies

`yi_p2p_on_auth` (`0x2d3e4`) supplies three password pointers to the library:

| Argument | Stock source | Library behavior |
| --- | --- | --- |
| r2 | Runtime `0x51b1dc` | Primary HMAC password |
| r3 | `0x4b2314` = callback structure + `0xa8` | Alternate password, attempted after primary failure if populated and different |
| First stacked argument | `0x4b2334` = callback structure + `0xc8` | Credential used when the primary password is empty |

The two alternate buffers are in BSS. No independent population path was found
in the examined application references, but a literal scan is not proof that
indirect writes cannot occur. **The candidate does not rely on them staying
empty:** instructions at `0x2d400` and `0x2d410` pass the canonical runtime
pointer into both additional slots. The actual HMAC verifier remains intact.

In `yi_p2p_do_auth` (`0x10838`):

- `0x10c1c` computes the primary HMAC; `0x10c2c` compares its first 15 Base64
  characters. The message is `user=xiaoyiuser&nonce=<nonce>`.
- `0x10c68` is the alternate-key attempt; `0x10d18` is the empty-primary fallback.
- `0x10da4`, `0x10c90`, and `0x10d40` copy an accepted password into the session.
  The session table starts at library-relative `0x4e054`, stride `0xcd0`;
  the password is at entry + `0x49` (the verifier uses a base shifted by `0x18`,
  hence its displayed offset `0x31`).
- Later commands authenticate against that session copy at `0x10b38`. Nonces
  are tracked in a 100-entry ring. Version-2 sessions reject repeated nonces.
- SDK AP mode returns success before normal authentication. **Local01 does
  not enable AP mode to bypass account dependency.** Normal authentication is
  retained. AP-mode behavior outside the normal configured path is not qualified.

An existing session retains its accepted key until session release. The
candidate does not add live key rotation or force-disconnect behavior.

## Media touches

`yi_p2p_send_frame_data` (`0xfce8`) reads the session password: copies 15 bytes
at `0xfd84` (audio) or `0x10000` (video), then appends ASCII `0`, creating the
16-byte AES-128 key already used by OpenYI.

- Audio sender: `AesSetKeyDirect` at `0x10400`; encrypts complete 16-byte blocks.
- Video sender: `AesSetKey` at `0x10140` / `0x10184`, encrypts the selected
  blocks with a reset IV. Frame selection and timestamps remain unchanged.
- Speaker receive: `yi_p2p_recv_audio_data` (`0x10578`), copies the session
  password at `0x1073c`, appends ASCII `0` at `0x10760`, calls key setup at
  `0x10764`, and decrypts complete blocks at `0x107a0`.

These media functions are byte-for-byte unchanged by local01. Their expected
key material is supplied by normal successful local authentication.

## Candidate ownership and retrieval

[login.s](../patches/ak3918e-local01/login.s) replaces the cloud login entry:

- Read `/etc/jffs2/openyi.key`: exactly 15 non-space printable ASCII bytes and LF.
- If absent, migrate a valid existing `lastP2pPwd`. Existing OpenYI clients
  should therefore keep their key on first installation.
- If neither exists, use 15 random bytes from `/dev/urandom`, encode their
  lower six bits with a 64-character alphabet: a 90-bit key space. Entropy
  quality still depends on the device's OS random source.
- Create with `O_EXCL`, mode `0600`; require successful write, `fsync`, close.
  No common password, vendor clock/refresh timer, or account token is used.
- Publish identical runtime/config copies only after success. Malformed,
  unreadable or incompletely written canonical files fail closed, without
  silently generating another key. The worker exits without invoking vendor
  binding-failure recovery when no local key is ready.

The workshop's **Export local camera key** action explicitly reads the candidate
over the existing owner/root FTP connection and saves a Windows DPAPI profile.
It checks the installed version and key-handling binary hash, never prints the
key, and never overwrites the client's existing profile. OpenYI's existing
**Import encrypted pairing profile** performs its usual live verification.
The FTP transport itself is the stock, unencrypted LAN maintenance interface;
this is not a newly hardened credential exchange protocol.

## What was verified

The mandatory [ARM checks](../tools/verify_local.py) load actual patched ELF
bytes into an ARM926 Unicorn instance. Synthetic keys only; filesystem, logging,
time, socket and device/persistence boundaries are modeled. Unexpected imports
or syscalls fail. No simulated call reaches a camera or host network.

Checks execute migration, random generation, reload after simulated restart,
partial I/O, malformed files, read/write/fsync/close/entropy failures; the real
HMAC/SHA1/Base64 implementation against Python; correct/wrong/alternate keys;
session reuse/replay rejection; canonical callback pointers; real AES against
a FIPS-197 vector; video/audio key use; and speaker decryption. Local binding's
control flow is executed with persistence/voice callbacks observed, not hardware.

This is a bounded, address-specific static/dataflow map supplemented by actual
instruction execution. It is **not** a proof covering every possible indirect
write, concurrency race, reset path, compromised process, power-loss scenario,
or firmware version. We found ordinary HMAC-SHA1/AES use in the tested local
paths, not evidence of a vendor signature or secure-element dependency there.
Local01's [first installation and reboot](LOCAL01_INSTALLATION.md) succeeded
with exact image readback. The owner also confirmed that the existing saved key
was accepted after a separate cold power cycle, without a vendor-app refresh.
A subsequent read-only preflight exported the canonical key, confirmed that it
matches the saved client key, and authenticated a new session using the exported
profile. On 11 October, a physical reset/local-QR test passed; a new post-reset
export confirmed that the canonical key and factory identity matched the
pre-reset export. The installed native app also imported directly and displayed
live video. WAN isolation, initial generation with no prior canonical key and
repeated/extended power-cycle behavior remain to be measured.
