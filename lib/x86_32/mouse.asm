; provides: OS_UpdateMouse OS_GetMouseX OS_GetMouseY OS_GetMouseDown OS_GetMouseUp
;
; PS/2 mouse, polled (turn it on with OS_SetupMouse, in mouse_setup.asm).
; OS_UpdateMouse reads any bytes the mouse has sent and moves the cursor; the
; GetMouse functions call it first. The cursor is kept inside the 320x200
; graphics screen, leaving room for a 10-pixel cursor.
MOUSE_CURSOR_WIDTH equ 10
MOUSE_MAX_X        equ 320 - MOUSE_CURSOR_WIDTH
MOUSE_MAX_Y        equ 200 - MOUSE_CURSOR_WIDTH

OS_UpdateMouse:
    in al, 0x64
    test al, 0x01           ; anything waiting?
    jz .done
    test al, 0x20           ; from the mouse (not the keyboard)?
    jz .done
    in al, 0x60

    mov bl, [mouse_packet_byte]
    cmp bl, 0
    je .byte1
    cmp bl, 1
    je .byte2
    ; third byte: y movement, and the packet is complete
    mov [mouse_byte_ymove], al
    mov byte [mouse_packet_byte], 0
    call mouse_process_packet
    jmp OS_UpdateMouse

.byte1:
    test al, 0x08           ; bit 3 is always set in the first byte
    jz OS_UpdateMouse       ; out of sync: wait for a real first byte
    mov [mouse_byte_states], al
    inc byte [mouse_packet_byte]
    jmp OS_UpdateMouse

.byte2:
    mov [mouse_byte_xmove], al
    inc byte [mouse_packet_byte]
    jmp OS_UpdateMouse

.done:
    ret

mouse_process_packet:
    mov bl, [mouse_byte_states]
    test bl, 0xC0           ; overflow: ignore the packet
    jnz .done

    mov al, [mouse_byte_xmove]
    xor ah, ah              ; movement is 9 bits: this byte plus a sign bit
    test bl, 0x10           ; x sign bit
    jz .x_positive
    or ah, 0xFF             ; negative: make the 9-bit value negative
.x_positive:
    add [mouse_cursor_x], ax
    cmp word [mouse_cursor_x], 0
    jge .x_not_negative
    mov word [mouse_cursor_x], 0
.x_not_negative:
    cmp word [mouse_cursor_x], MOUSE_MAX_X
    jle .y
    mov word [mouse_cursor_x], MOUSE_MAX_X

.y:
    mov al, [mouse_byte_ymove]
    xor ah, ah
    test bl, 0x20           ; y sign bit
    jz .y_positive
    or ah, 0xFF
.y_positive:
    sub [mouse_cursor_y], ax ; the mouse counts up, the screen counts down
    cmp word [mouse_cursor_y], 0
    jge .y_not_negative
    mov word [mouse_cursor_y], 0
.y_not_negative:
    cmp word [mouse_cursor_y], MOUSE_MAX_Y
    jle .done
    mov word [mouse_cursor_y], MOUSE_MAX_Y
.done:
    ret

OS_GetMouseX:
    call OS_UpdateMouse
    movsx eax, word [mouse_cursor_x]
    ret

OS_GetMouseY:
    call OS_UpdateMouse
    movsx eax, word [mouse_cursor_y]
    ret

OS_GetMouseDown:
    call OS_UpdateMouse
    movzx eax, byte [mouse_byte_states]
    and eax, 1              ; left button
    ret

OS_GetMouseUp:
    call OS_GetMouseDown
    xor eax, 1
    ret

mouse_packet_byte: db 0
mouse_byte_states: db 0
mouse_byte_xmove:  db 0
mouse_byte_ymove:  db 0
mouse_cursor_x:    dw 0
mouse_cursor_y:    dw 0
