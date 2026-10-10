// A32, installed anyka_ipc VA 0x47000. Replaces webapi_do_tnp_on_line.
// Publish readiness only after local key initialization, with no WAN server
// initialization string. The six-byte license is an unused LAN placeholder;
// normal device-key authentication in yi_p2p_do_auth is preserved.
push {r4, lr}
ldr r4, =0x51b0f4
ldrb r0, [r4, #232]
cmp r0, #0
beq failed
ldr r0, =0x51b460
adr r1, license
bl 0x1c868
ldr r0, =0x51b480
mov r1, #0
mov r2, #128
bl 0x1c1c0
mov r0, #1
strb r0, [r4, #1092]
pop {r4, pc}
failed:
mov r0, #0
strb r0, [r4, #1092]
pop {r4, pc}
.ltorg
license: .asciz "LOCAL0"
.balign 4
