; provides: OS_SetupInteruptTimer
; hooks: OS_TimerEvent
;
; Program the PIT for 60 interrupts a second and call OS_TimerEvent on each.
; This replaces the BIOS timer handler.
PIT_COMMAND   equ 0x43
PIT_CHANNEL_0 equ 0x40
PIT_DIVISOR   equ 1193182 / 60

OS_SetupInteruptTimer:
    cli
    mov al, 00110100b       ; channel 0, low then high byte, rate generator
    out PIT_COMMAND, al
    mov ax, PIT_DIVISOR
    out PIT_CHANNEL_0, al
    mov al, ah
    out PIT_CHANNEL_0, al
    mov word [0x08 * 4], .interrupt
    mov word [0x08 * 4 + 2], 0
    sti
    ret

.interrupt:
    pushad
    push ds
    push es
    xor ax, ax
    mov ds, ax
    mov es, ax
    cld
    call OS_TimerEvent
    mov al, 0x20            ; end of interrupt
    out 0x20, al
    pop es
    pop ds
    popad
    iret
