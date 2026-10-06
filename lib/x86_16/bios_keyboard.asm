; provides: Bootloader_WaitForKeyPress
;
; Wait for a key through the BIOS. eax = its character.
; Stops working after OS.SetupKeyboardInterupt replaces the BIOS keyboard handler.
Bootloader_WaitForKeyPress:
    xor ah, ah
    int 0x16
    movzx eax, al
    ret
