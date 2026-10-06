; provides: OS_SetupKeyboardInterupt OS_GetKey OS_IsKeyDown OS_ReadKey OS_KeyAvailable Bootloader_WaitForKeyPress phi_read_char phi_take_key
; requires: phi_irq_register
; hooks: OS_KeyboardEvent
;
; PS/2 keyboard on IRQ 1 (32-bit kernels). Every press and release updates
; which keys are down and calls OS_KeyboardEvent; presses of keys with a
; character also go into a 64-character buffer for OS_ReadKey (and `ask`).
; Shift gives capitals and symbols.
KEYBOARD_IRQ    equ 1
KEYBOARD_BUFFER equ 64

OS_SetupKeyboardInterupt:
    cmp byte [phi_keyboard_ready], 0
    jne .done
    mov byte [phi_keyboard_ready], 1
    mov eax, KEYBOARD_IRQ
    mov ebx, phi_keyboard_irq
    call phi_irq_register
.done:
    ret

; called for IRQ 1 (end-of-interrupt is sent by the caller)
phi_keyboard_irq:
    in al, 0x60             ; scan code; bit 7 set means released
    movzx ebx, al
    and bl, 0x7F
    test al, 0x80
    setz cl                 ; cl = 1 for a press, 0 for a release

    cmp bl, 0x2A            ; left shift
    je .shift
    cmp bl, 0x36            ; right shift
    je .shift

    movzx edx, byte [phi_scan_to_char + ebx]
    mov [phi_key_down + edx], cl      ; indexed by the unshifted character
    cmp byte [phi_shift], 0
    je .char
    movzx edx, byte [phi_scan_to_shifted + ebx]
.char:
    mov [phi_last_key], dl

    ; a press with a character goes into the buffer, unless it's full
    test cl, cl
    jz .event
    test dl, dl
    jz .event
    mov eax, [phi_keyboard_head]
    inc eax
    and eax, KEYBOARD_BUFFER - 1
    cmp eax, [phi_keyboard_tail]
    je .event
    mov ebx, [phi_keyboard_head]
    mov [phi_keyboard_buffer + ebx], dl
    mov [phi_keyboard_head], eax
    jmp .event

.shift:
    mov [phi_shift], cl
.event:
    call OS_KeyboardEvent
    ret

; eax = character of the last key pressed or released
OS_GetKey:
    movzx eax, byte [phi_last_key]
    ret

; arg: key -> eax = 1 if it's held down
OS_IsKeyDown:
    push ebp
    mov ebp, esp
    movzx ebx, byte [ebp + 8]
    movzx eax, byte [phi_key_down + ebx]
    pop ebp
    ret

; eax = 1 if OS_ReadKey has a character waiting
OS_KeyAvailable:
    call OS_SetupKeyboardInterupt
    mov eax, [phi_keyboard_head]
    cmp eax, [phi_keyboard_tail]
    setne al
    movzx eax, al
    ret

; wait for a key press and return its character
OS_ReadKey:
Bootloader_WaitForKeyPress:
phi_read_char:
    call OS_SetupKeyboardInterupt
.wait:
    cli
    mov ebx, [phi_keyboard_tail]
    cmp ebx, [phi_keyboard_head]
    jne .take
    sti
    hlt                     ; sti; hlt can't miss an interrupt in between
    jmp .wait
.take:
    movzx eax, byte [phi_keyboard_buffer + ebx]
    inc ebx
    and ebx, KEYBOARD_BUFFER - 1
    mov [phi_keyboard_tail], ebx
    sti
    ret

; for code that runs with interrupts off (the scheduler): eax = the next
; buffered character, or 0 if there is none
phi_take_key:
    xor eax, eax
    mov ebx, [phi_keyboard_tail]
    cmp ebx, [phi_keyboard_head]
    je .none
    movzx eax, byte [phi_keyboard_buffer + ebx]
    inc ebx
    and ebx, KEYBOARD_BUFFER - 1
    mov [phi_keyboard_tail], ebx
.none:
    ret

phi_keyboard_ready: db 0
phi_shift:          db 0
phi_last_key:       db 0
phi_keyboard_head:  dd 0
phi_keyboard_tail:  dd 0
phi_keyboard_buffer: times KEYBOARD_BUFFER db 0
phi_key_down:       times 256 db 0

; scan code set 1 to character, for codes 0x00-0x7F
phi_scan_to_char:
    db 0, 27, '1', '2', '3', '4', '5', '6', '7', '8', '9', '0', '-', '=', 8, 9
    db 'q', 'w', 'e', 'r', 't', 'y', 'u', 'i', 'o', 'p', '[', ']', 13, 0, 'a', 's'
    db 'd', 'f', 'g', 'h', 'j', 'k', 'l', ';', 39, '`', 0, '\', 'z', 'x', 'c', 'v'
    db 'b', 'n', 'm', ',', '.', '/', 0, '*', 0, ' '
    times 128 - ($ - phi_scan_to_char) db 0

phi_scan_to_shifted:
    db 0, 27, '!', '@', '#', '$', '%', '^', '&', '*', '(', ')', '_', '+', 8, 9
    db 'Q', 'W', 'E', 'R', 'T', 'Y', 'U', 'I', 'O', 'P', '{', '}', 13, 0, 'A', 'S'
    db 'D', 'F', 'G', 'H', 'J', 'K', 'L', ':', '"', '~', 0, '|', 'Z', 'X', 'C', 'V'
    db 'B', 'N', 'M', '<', '>', '?', 0, '*', 0, ' '
    times 128 - ($ - phi_scan_to_shifted) db 0
