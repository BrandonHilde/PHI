; provides: Bootloader_JumpToSectorTwo
; requires: phi_print
;
; Load the kernel (the OS classes) from the sectors after the boot sector and
; jump to it. The build defines PHI_KERNEL_SECTORS; phi_boot_drive is saved by
; the boot sector's startup code.
Bootloader_JumpToSectorTwo:
    mov ah, 0x42            ; extended read (LBA)
    mov dl, [phi_boot_drive]
    mov si, .packet
    int 0x13
    jc .failed
    jmp 0x0000:0x7E00
.failed:
    mov si, .message
    call phi_print
.halt:
    hlt
    jmp .halt
.packet:
    db 0x10, 0              ; packet size, reserved
    dw PHI_KERNEL_SECTORS   ; sectors to read
    dw 0x7E00, 0x0000       ; destination offset, segment
    dd 1, 0                 ; first sector (LBA 1, right after the boot sector)
.message:
    db 'PHI: could not read the OS from disk', 13, 10, 0
