; provides: OS_SetupInteruptTimer OS_GetTicks OS_Sleep
; requires: phi_irq_register phi_kernel_halt
; hooks: OS_TimerEvent
;
; The PIT on IRQ 0, at 1000 interrupts a second (32-bit kernels). OS_GetTicks
; gives milliseconds since the timer started, by the CPU's timestamp counter,
; whose rate is measured against the PIT's channel 2 when the timer starts.
; Counting the interrupts instead would lose time whenever one comes before the
; one before it was taken (code that keeps interrupts off, or a virtual machine
; whose host is busy: QEMU on Windows loses about a third of them, even idle);
; if the rate can't be measured, the interrupts are counted after all. The
; interrupts wake sleeping tasks and switch between them. OS_TimerEvent runs 60
; times a second, as in 16-bit programs. GetTicks and Sleep start the timer if
; needed.
TIMER_IRQ      equ 0
PIT_COMMAND    equ 0x43
PIT_CHANNEL_0  equ 0x40
PIT_CHANNEL_2  equ 0x42
PIT_GATE       equ 0x61         ; bit 0: channel 2 counts; bit 1: the speaker; bit 5: channel 2's output
PIT_HZ         equ 1000
PIT_DIVISOR    equ 1193182 / PIT_HZ
EVENTS_PER_SECOND equ 60
CALIBRATE_MS   equ 10

OS_SetupInteruptTimer:
    cmp byte [phi_timer_ready], 0
    jne .done
    mov byte [phi_timer_ready], 1
    pushad
    call phi_calibrate_tsc
    mov al, 00110100b       ; channel 0, low then high byte, rate generator
    out PIT_COMMAND, al
    mov ax, PIT_DIVISOR
    out PIT_CHANNEL_0, al
    mov al, ah
    out PIT_CHANNEL_0, al
    mov eax, TIMER_IRQ
    mov ebx, phi_timer_irq
    call phi_irq_register
    popad
.done:
    ret

; phi_tsc_per_ms = the timestamp counter's counts in a millisecond, from PIT channel 2
; counting down CALIBRATE_MS ms (looked at, not interrupting); phi_tsc_start = now.
; Left at 0 if channel 2 never finishes (it gives up after 2^32 counts).
phi_calibrate_tsc:
    in al, PIT_GATE
    and al, 11111100b       ; channel 2 stopped, the speaker off
    out PIT_GATE, al
    mov al, 10110000b       ; channel 2, low then high byte, mode 0: its output goes high at 0
    out PIT_COMMAND, al
    mov ax, 1193182 * CALIBRATE_MS / 1000
    out PIT_CHANNEL_2, al
    mov al, ah
    out PIT_CHANNEL_2, al
    in al, PIT_GATE
    or al, 1                ; count
    out PIT_GATE, al
    rdtsc
    mov esi, eax
    mov edi, edx
.wait:
    in al, PIT_GATE
    test al, 0x20
    jnz .counted
    rdtsc
    sub edx, edi
    cmp edx, 2
    jb .wait
    jmp .stop
.counted:
    rdtsc
    sub eax, esi
    sbb edx, edi
    mov ecx, CALIBRATE_MS
    div ecx                 ; (10 ms of counts: the result fits)
    mov [phi_tsc_per_ms], eax
    rdtsc
    mov [phi_tsc_start], eax
    mov [phi_tsc_start + 4], edx
.stop:
    in al, PIT_GATE
    and al, 11111110b
    out PIT_GATE, al
    ret

; called for IRQ 0 (end-of-interrupt is sent by the caller)
phi_timer_irq:
    inc dword [phi_timer_ticks]
    ; the timer event, as many times as are due by now at EVENTS_PER_SECOND (by the
    ; clock, not by counting interrupts, so ones that came late don't slow it)
    call OS_GetTicks
    mov ecx, EVENTS_PER_SECOND
    mul ecx                 ; edx:eax = ms * 60, which / 1000 always fits
    mov ecx, 1000
    div ecx
    mov [phi_events_due], eax
.event:
    mov eax, [phi_events_due]
    cmp eax, [phi_timer_events]
    je .done
    inc dword [phi_timer_events]
    call OS_TimerEvent
    jmp .event
.done:
    ret

; eax = milliseconds since the timer started (only eax changes)
OS_GetTicks:
    call OS_SetupInteruptTimer
    push ebx
    push ecx
    push edx
    mov ecx, [phi_tsc_per_ms]
    test ecx, ecx
    jz .counted
    rdtsc
    sub eax, [phi_tsc_start]
    sbb edx, [phi_tsc_start + 4]
    ; the counts since then over ecx, keeping the low 32 bits of the result (so it wraps
    ; round after 49 days, as a count would): the high half first, so neither can overflow
    mov ebx, eax
    mov eax, edx
    xor edx, edx
    div ecx
    mov eax, ebx
    div ecx
    jmp .done
.counted:
    mov eax, [phi_timer_ticks]
.done:
    pop edx
    pop ecx
    pop ebx
    ret

; arg: milliseconds to wait
OS_Sleep:
    push ebp
    mov ebp, esp
    push ebx
    call OS_GetTicks
    mov ebx, eax
.wait:
    call OS_GetTicks
    sub eax, ebx            ; elapsed, correct even when the counter wraps
    cmp eax, [ebp + 8]
    jae .done
    call phi_kernel_halt    ; sti; hlt (letting other cores into the kernel meanwhile)
    jmp .wait
.done:
    pop ebx
    pop ebp
    ret

phi_tsc_per_ms:   dd 0
phi_tsc_start:    dd 0, 0
phi_timer_ready:  db 0
phi_timer_ticks:  dd 0
phi_timer_events: dd 0          ; timer events run so far
phi_events_due:   dd 0          ; how many there should have been by now
