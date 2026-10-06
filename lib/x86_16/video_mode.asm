; provides: Bootloader_EnableVideoMode
;
; Switch to VGA mode 13h: 320x200, 256 colors, one byte per pixel at A000:0000.
Bootloader_EnableVideoMode:
    mov ax, 0x13
    int 0x10
    ret
