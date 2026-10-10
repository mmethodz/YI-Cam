// A32, installed anyka_ipc VA 0x434bc. Replaces webapi_do_login.
// Canonical owner key: /etc/jffs2/openyi.key (15 ASCII bytes + LF).
// Preserve an existing 15-byte device password on first migration. Otherwise
// use 90 bits from /dev/urandom. Never fall back to a common/default password.
// No cloud calls, clock expiry, vendor token or account data are needed.
push {r4, r5, r6, r7, r8, lr}
sub sp, sp, #32
ldr r4, =0x51b1dc
mov r0, r4
mov r1, #0
mov r2, #32
bl 0x1c1c0
ldr r0, =key_path
mov r1, #0
bl 0x1b5d8
cmp r0, #0
blt missing
mov r5, r0
mov r6, #0
read_key:
mov r0, r5
add r1, sp, r6
rsb r2, r6, #17
bl 0x1bb24
cmp r0, #0
blt close_fail
beq key_read
add r6, r6, r0
cmp r6, #17
blt read_key
b close_fail
key_read:
mov r0, r5
bl 0x1bf38
cmp r0, #0
bne fail
cmp r6, #16
bne fail
ldrb r0, [sp, #15]
cmp r0, #10
bne fail
mov r6, #0
validate_file:
ldrb r0, [sp, r6]
cmp r0, #33
blo fail
cmp r0, #126
bhi fail
add r6, r6, #1
cmp r6, #15
blt validate_file
b publish
missing:
bl 0x1b584
ldr r0, [r0]
cmp r0, #2
bne fail
ldr r8, =0x4b2004
mov r6, #0
validate_cached:
ldrb r0, [r8, r6]
cmp r0, #33
blo random_key
cmp r0, #126
bhi random_key
strb r0, [sp, r6]
add r6, r6, #1
cmp r6, #15
blt validate_cached
ldrb r0, [r8, #15]
cmp r0, #0
beq create_key
random_key:
ldr r0, =random_path
mov r1, #0
bl 0x1b5d8
cmp r0, #0
blt fail
mov r5, r0
mov r6, #0
random_read:
mov r0, r5
add r1, sp, r6
rsb r2, r6, #15
bl 0x1bb24
cmp r0, #0
ble close_fail
add r6, r6, r0
cmp r6, #15
blt random_read
mov r0, r5
bl 0x1bf38
cmp r0, #0
bne fail
ldr r7, =alphabet
mov r6, #0
encode:
ldrb r0, [sp, r6]
and r0, r0, #63
ldrb r0, [r7, r0]
strb r0, [sp, r6]
add r6, r6, #1
cmp r6, #15
blt encode
create_key:
mov r0, #10
strb r0, [sp, #15]
ldr r0, =key_path
mov r1, #193
mov r2, #384
bl 0x1b5d8
cmp r0, #0
blt fail
mov r5, r0
mov r6, #0
write_key:
mov r0, r5
add r1, sp, r6
rsb r2, r6, #16
bl 0x1be60
cmp r0, #0
ble close_fail
add r6, r6, r0
cmp r6, #16
blt write_key
mov r0, r5
bl 0x1c6dc
cmp r0, #0
bne close_fail
mov r0, r5
bl 0x1bf38
cmp r0, #0
bne fail
// Successful fsync and close are required before exposing the new key.
publish:
mov r0, #0
strb r0, [sp, #15]
mov r0, r4
mov r1, sp
mov r2, #16
bl 0x1b6bc
ldr r0, =0x4b2004
mov r1, sp
mov r2, #16
bl 0x1b6bc
ldr r1, =0x4b1f90
mov r0, #0
str r0, [r1, #28]
str r0, [r1, #32]
mov r0, #1
b done
close_fail:
mov r0, r5
bl 0x1bf38
fail:
mov r0, #0
done:
add sp, sp, #32
pop {r4, r5, r6, r7, r8, pc}
.ltorg
key_path: .asciz "/etc/jffs2/openyi.key"
random_path: .asciz "/dev/urandom"
alphabet: .ascii "ABCDEFGHIJKLMNOPQRSTUVWXYZabcdefghijklmnopqrstuvwxyz0123456789-_"
.balign 4
