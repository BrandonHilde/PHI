; provides: OS_SetupInteruptTimer OS_GetTicks OS_Sleep
; requires: phi_irq_register
; hooks: OS_TimerEvent
;
; The PIT on IRQ 0, at 1000 interrupts a second (32-bit kernels). OS_GetTicks
; counts milliseconds since the timer started; OS_TimerEvent runs 60 times a
; second, as in 16-bit programs. GetTicks and Sleep start the timer if needed.
TIMER_IRQ      equ 0
PIT_COMMAND    equ 0x43
PIT_CHANNEL_0  equ 0x40
PIT_HZ         equ 1000
PIT_DIVISOR    equ 1193182 / PIT_HZ
EVENTS_PER_SECOND equ 60

OS_SetupInteruptTimer:
    cmp byte [phi_timer_ready], 0
    jne .done
    mov byte [phi_timer_ready], 1
    mov al, 00110100b       ; channel 0, low then high byte, rate generator
    out PIT_COMMAND, al
    mov ax, PIT_DIVISOR
    out PIT_CHANNEL_0, al
    mov al, ah
    out PIT_CHANNEL_0, al
    mov eax, TIMER_IRQ
    mov ebx, phi_timer_irq
    call phi_irq_register
.done:
    ret

; called for IRQ 0 (end-of-interrupt is sent by the caller)
phi_timer_irq:
    inc dword [phi_timer_ticks]
    ; every 1000/60 ms, on average, run the timer event
    add dword [phi_timer_events], EVENTS_PER_SECOND
    cmp dword [phi_timer_events], PIT_HZ
    jb .done
    sub dword [phi_timer_events], PIT_HZ
    call OS_TimerEvent
.done:
    ret

; eax = milliseconds since the timer started
OS_GetTicks:
    call OS_SetupInteruptTimer
    mov eax, [phi_timer_ticks]
    ret

; arg: milliseconds to wait
OS_Sleep:
    push ebp
    mov ebp, esp
    call OS_SetupInteruptTimer
    mov ebx, [phi_timer_ticks]
.wait:
    mov eax, [phi_timer_ticks]
    sub eax, ebx            ; elapsed, correct even when the counter wraps
    cmp eax, [ebp + 8]
    jae .done
    sti
    hlt
    jmp .wait
.done:
    pop ebp
    ret

phi_timer_ready:  db 0
phi_timer_ticks:  dd 0
phi_timer_events: dd 0
