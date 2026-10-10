// A32 anyka_ipc 0x47000. No key prerequisite, no WAN server list.
push {r4, lr}
ldr r4, =0x51b0f4
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
.ltorg
license: .asciz "LOCAL0"
.balign 4
