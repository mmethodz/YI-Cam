// A32 libYiP2P.so 0x10838. Public protocol marker, not authentication.
// This is intentionally keyless: any LAN client can send this published marker.
// Reject old HMAC framing so an encrypted client cannot silently use plain media.
ldr ip, [sp, #4]
cmp ip, #0
movne r3, #0
strbne r3, [ip]
cmp r1, #0
moveq r0, #1
bxeq lr
push {r4, lr}
adr r0, marker
mov r2, #32
bl 0xa4f0
cmp r0, #0
movne r0, #1
pop {r4, pc}
marker: .ascii "OpenYI-LAN-v1"
.zero 19
.balign 4
