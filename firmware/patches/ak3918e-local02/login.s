// A32 anyka_ipc 0x434bc. Keyless LAN bootstrap, no key file or entropy request.
push {r4, lr}
ldr r0, =0x51b1dc
mov r1, #0
mov r2, #16
bl 0x1c1c0
ldr r0, =0x4b2004
mov r1, #0
mov r2, #16
bl 0x1c1c0
ldr r1, =0x4b1f90
mov r0, #0
str r0, [r1, #28]
str r0, [r1, #32]
mov r0, #1
pop {r4, pc}
.ltorg
