; provides: OS_SetupKeyboardInterupt OS_GetKey OS_IsKeyDown
; hooks: OS_KeyboardEvent
;
; Replace the BIOS keyboard handler. Every press and release updates a table
; of which keys are down (indexed by character) and calls OS_KeyboardEvent.
OS_SetupKeyboardInterupt:
    cli
    mov word [0x09 * 4], .interrupt
    mov word [0x09 * 4 + 2], 0
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
    in al, 0x60             ; scan code; bit 7 set means released
    movzx bx, al
    and bl, 0x7F
    mov ah, [phi_scan_to_char + bx]
    test al, 0x80
    setz al                 ; al = 1 for a press, 0 for a release
    movzx bx, ah
    mov [phi_key_down + bx], al
    mov [phi_last_key], ah
    call OS_KeyboardEvent
    mov al, 0x20            ; end of interrupt
    out 0x20, al
    pop es
    pop ds
    popad
    iret

; eax = character of the last key pressed or released
OS_GetKey:
    movzx eax, byte [phi_last_key]
    ret

; eax = 1 if the key given as the first argument is down
OS_IsKeyDown:
    push bp
    mov bp, sp
    movzx bx, byte [bp + 4]
    movzx eax, byte [phi_key_down + bx]
    pop bp
    ret

phi_last_key: db 0
phi_key_down: times 256 db 0

; scan code set 1 to character, for codes 0x00-0x7F
phi_scan_to_char:
    db 0, 27, '1', '2', '3', '4', '5', '6', '7', '8', '9', '0', '-', '=', 8, 9
    db 'q', 'w', 'e', 'r', 't', 'y', 'u', 'i', 'o', 'p', '[', ']', 13, 0, 'a', 's'
    db 'd', 'f', 'g', 'h', 'j', 'k', 'l', ';', 39, '`', 0, '\', 'z', 'x', 'c', 'v'
    db 'b', 'n', 'm', ',', '.', '/', 0, '*', 0, ' '
    times 128 - ($ - phi_scan_to_char) db 0
