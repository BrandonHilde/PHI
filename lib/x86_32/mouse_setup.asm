; provides: OS_SetupMouse
; requires: OS_UpdateMouse
;
; Turn on the PS/2 mouse: enable the controller's mouse port and tell the
; mouse to start reporting movement. Polled, so no interrupt is enabled.
OS_SetupMouse:
    call .wait_input
    mov al, 0xA8            ; enable the auxiliary (mouse) port
    out 0x64, al

    call .wait_input
    mov al, 0x20            ; read the controller configuration
    out 0x64, al
    call .wait_output
    in al, 0x60
    and al, 11011101b       ; enable the mouse clock, no mouse interrupt (we poll)
    mov ah, al
    call .wait_input
    mov al, 0x60            ; write the controller configuration
    out 0x64, al
    call .wait_input
    mov al, ah
    out 0x60, al

    mov al, 0xF6            ; mouse: use default settings
    call .command
    mov al, 0xF4            ; mouse: start sending movement
    call .command

    mov byte [mouse_packet_byte], 0
    ret

; send al to the mouse and read its acknowledgement
.command:
    mov ah, al
    call .wait_input
    mov al, 0xD4            ; the next data byte goes to the mouse
    out 0x64, al
    call .wait_input
    mov al, ah
    out 0x60, al
    call .wait_output
    in al, 0x60             ; 0xFA
    ret

; wait until the controller can accept a byte (gives up after a while)
.wait_input:
    mov ecx, 0xFFFF
.wait_input_loop:
    in al, 0x64
    test al, 2
    loopnz .wait_input_loop
    ret

; wait until the controller has a byte for us (gives up after a while)
.wait_output:
    mov ecx, 0xFFFF
.wait_output_loop:
    in al, 0x64
    test al, 1
    loopz .wait_output_loop
    ret
