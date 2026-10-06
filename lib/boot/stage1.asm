; PHI boot sector for 32-bit kernels (stage 1).
;
; The BIOS loads this sector at 0x7C00. It loads the loader (stage 2) from the
; sectors right after it to 0x7E00 and runs it. See docs/memory-map.md.
bits 16
org 0x7C00

%ifndef PHI_STAGE2_SECTORS
%define PHI_STAGE2_SECTORS 8
%endif

    jmp 0x0000:start        ; some BIOSes start us at 07C0:0000
start:
    cli
    xor ax, ax
    mov ds, ax
    mov es, ax
    mov ss, ax
    mov sp, 0x7C00
    sti
    cld
    mov [boot_drive], dl

    mov ah, 0x42            ; extended read (LBA)
    mov si, packet
    int 0x13
    jc failed

    mov dl, [boot_drive]    ; stage 2 gets the boot drive in dl too
    jmp 0x0000:0x7E00

failed:
    mov si, message
.print:
    lodsb
    test al, al
    jz .halt
    mov ah, 0x0E
    mov bx, 0x0007
    int 0x10
    jmp .print
.halt:
    hlt
    jmp .halt

packet:
    db 0x10, 0
    dw PHI_STAGE2_SECTORS
    dw 0x7E00, 0x0000
    dd 1, 0                 ; stage 2 starts at sector 1
boot_drive: db 0
message: db 'PHI: could not read the loader from disk', 13, 10, 0

times 446 - ($ - $$) db 0   ; the code must end here: the partition table follows
times 64 db 0               ; four partition entries (the build fills in the first)
dw 0xAA55
