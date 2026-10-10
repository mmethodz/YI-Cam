// Retired WAN network detection returns an error.
mvn r0, #0
bx lr
// Local egress guard. Non-IP local IPC remains available. IPv4 is
// limited to RFC1918, loopback, link-local and limited broadcast; DNS ports
// are denied. IPv6/public destinations and absent sockaddr are rejected.
push {r4, r5, r6, lr}
ldr r4, [sp, #16]
ldr r5, [sp, #20]
cmp r4, #0
beq deny
cmp r5, #2
blo deny
ldrh r6, [r4]
cmp r6, #1
beq allow
cmp r6, #16
beq allow
cmp r6, #2
bne deny
cmp r5, #16
blo deny
ldrb r6, [r4, #2]
ldrb ip, [r4, #3]
cmp r6, #0
cmpeq ip, #53
beq deny
cmp r6, #3
cmpeq ip, #85
beq deny
ldrb r6, [r4, #4]
cmp r6, #10
beq allow
cmp r6, #127
beq allow
ldrb ip, [r4, #5]
cmp r6, #192
cmpeq ip, #168
beq allow
cmp r6, #169
cmpeq ip, #254
beq allow
cmp r6, #172
bne broadcast
and ip, ip, #240
cmp ip, #16
beq allow
b deny
broadcast:
ldr r6, [r4, #4]
cmn r6, #1
bne deny
allow:
pop {r4, r5, r6, lr}
ldr ip, slot_delta
slot_base:
add ip, pc, ip
ldr pc, [ip]
deny:
bl 0xb414
mov r1, #13
str r1, [r0]
mvn r0, #0
pop {r4, r5, r6, pc}
// GOT 0x4dd4c minus runtime PC (0x28018 + load base).
slot_delta: .word 0x25d34
