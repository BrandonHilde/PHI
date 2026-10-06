; provides: OS_DrawRectangle OS_DrawPixel
;
; Drawing in VGA mode 13h (see Bootloader_EnableVideoMode). Shapes are clipped
; to the 320x200 screen.
SCREEN_WIDTH  equ 320
SCREEN_HEIGHT equ 200

; args: x, y, width, height, color
OS_DrawRectangle:
    push bp
    mov bp, sp
    push es
    mov ax, 0xA000
    mov es, ax

    ; x range [x0, x1) clipped to the screen
    mov esi, [bp + 4]
    mov edi, esi
    add edi, [bp + 12]
    cmp esi, 0
    jge .x0_ok
    xor esi, esi
.x0_ok:
    cmp edi, SCREEN_WIDTH
    jle .x1_ok
    mov edi, SCREEN_WIDTH
.x1_ok:
    cmp esi, edi
    jge .done

    ; y range [y0, y1)
    mov ebx, [bp + 8]
    mov edx, ebx
    add edx, [bp + 16]
    cmp ebx, 0
    jge .y0_ok
    xor ebx, ebx
.y0_ok:
    cmp edx, SCREEN_HEIGHT
    jle .y1_ok
    mov edx, SCREEN_HEIGHT
.y1_ok:
    mov al, [bp + 20]
    sub edi, esi            ; edi = width of each row
.row:
    cmp ebx, edx
    jge .done
    push edi
    imul cx, bx, SCREEN_WIDTH
    add cx, si
    mov di, cx              ; di = offset of the row's first pixel
    pop ecx                 ; cx = pixels in the row
    push ecx
    rep stosb
    pop edi
    inc ebx
    jmp .row

.done:
    pop es
    pop bp
    ret

; args: x, y, color
OS_DrawPixel:
    push bp
    mov bp, sp
    mov eax, [bp + 4]
    cmp eax, SCREEN_WIDTH
    jae .done               ; unsigned: also rejects negative values
    mov ebx, [bp + 8]
    cmp ebx, SCREEN_HEIGHT
    jae .done
    push es
    mov cx, 0xA000
    mov es, cx
    imul bx, bx, SCREEN_WIDTH
    add bx, ax
    mov al, [bp + 12]
    mov [es:bx], al
    pop es
.done:
    pop bp
    ret
