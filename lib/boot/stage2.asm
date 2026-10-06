; PHI loader for 32-bit kernels (stage 2).
;
; Runs in 16-bit real mode at 0x7E00, while the BIOS is still available:
;   1. loads the kernel to 0x10000
;   2. fills in BootInfo at 0x9000: the BIOS memory map (E820) and the cursor
;   3. enables the A20 line, loads a flat GDT and switches to 32-bit protected mode
;   4. jumps to the kernel with ebx = the BootInfo address
; Interrupts stay off: the kernel sets up its own interrupt table.
; See docs/memory-map.md for the layout.
bits 16
org 0x7E00

STAGE2_SIZE      equ 8 * 512                ; this file is padded to 8 sectors
KERNEL_LBA       equ 1 + STAGE2_SIZE / 512  ; the kernel follows us on disk
KERNEL_SEGMENT   equ 0x1000                 ; = linear 0x10000
KERNEL_ADDRESS   equ 0x10000
KERNEL_STACK     equ 0x9F000                ; grows down, below the BIOS's data at 0x9FC00
BOOT_INFO        equ 0x9000
MEMORY_MAP       equ 0x9040
MAX_REGIONS      equ 64                     ; 24 bytes each
READ_CHUNK       equ 64                     ; sectors per BIOS read (32 KB)

%ifndef PHI_KERNEL_SECTORS
%define PHI_KERNEL_SECTORS 1
%endif

; BootInfo fields (all u32)
INFO_MAGIC        equ BOOT_INFO + 0         ; 'PHI!'
INFO_DRIVE        equ BOOT_INFO + 4
INFO_REGION_COUNT equ BOOT_INFO + 8
INFO_REGIONS      equ BOOT_INFO + 12        ; address of the first MemoryRegion
INFO_CURSOR_ROW   equ BOOT_INFO + 16
INFO_CURSOR_COL   equ BOOT_INFO + 20
INFO_KERNEL_START equ BOOT_INFO + 24
INFO_KERNEL_SIZE  equ BOOT_INFO + 28

stage2:
    ; clear BootInfo and the memory map
    mov di, BOOT_INFO
    mov cx, (MEMORY_MAP - BOOT_INFO) + MAX_REGIONS * 24
    xor al, al
    rep stosb

    mov dword [INFO_MAGIC], 0x21494850
    movzx eax, dl
    mov [INFO_DRIVE], eax
    mov dword [INFO_REGIONS], MEMORY_MAP
    mov dword [INFO_KERNEL_START], KERNEL_ADDRESS
    mov dword [INFO_KERNEL_SIZE], PHI_KERNEL_SECTORS * 512

    ; ---- 1. load the kernel, in chunks the BIOS can handle
    mov word [remaining], PHI_KERNEL_SECTORS
    mov dword [packet.lba], KERNEL_LBA
    mov word [packet.segment], KERNEL_SEGMENT
.read:
    mov ax, [remaining]
    test ax, ax
    jz .loaded
    cmp ax, READ_CHUNK
    jbe .count
    mov ax, READ_CHUNK
.count:
    mov [packet.count], ax
    mov ah, 0x42
    mov dl, [INFO_DRIVE]
    mov si, packet
    int 0x13
    jc disk_error
    mov ax, [packet.count]
    sub [remaining], ax
    movzx eax, ax
    add [packet.lba], eax
    shl ax, 5               ; 512 bytes per sector = 32 paragraphs
    add [packet.segment], ax
    jmp .read
.loaded:

    ; ---- 2. the memory map (BIOS E820)
    xor ebx, ebx
    mov di, MEMORY_MAP
    xor bp, bp
.region:
    mov eax, 0xE820
    mov edx, 0x534D4150     ; 'SMAP'
    mov ecx, 24
    mov dword [di + 20], 1  ; "valid" in case the BIOS only writes 20 bytes
    int 0x15
    jc .regions_done
    cmp eax, 0x534D4150
    jne .regions_done
    jcxz .skip              ; empty entry
    inc bp
    add di, 24
.skip:
    test ebx, ebx           ; ebx = 0 after the last entry
    jz .regions_done
    cmp bp, MAX_REGIONS
    jb .region
.regions_done:
    movzx eax, bp
    mov [INFO_REGION_COUNT], eax

    ; the cursor, so the kernel can carry on below the BIOS's messages
    mov ah, 0x03
    xor bh, bh
    int 0x10
    movzx eax, dh
    mov [INFO_CURSOR_ROW], eax
    movzx eax, dl
    mov [INFO_CURSOR_COL], eax

    ; ---- 3. A20 (fast A20 through port 0x92), then protected mode
    in al, 0x92
    or al, 2
    and al, 0xFE            ; bit 0 would reset the machine
    out 0x92, al

    cli
    lgdt [gdt_descriptor]
    mov eax, cr0
    or eax, 1
    mov cr0, eax
    jmp 0x08:protected

disk_error:
    mov si, .message
.print:
    lodsb
    test al, al
    jz .halt
    mov ah, 0x0E
    mov bx, 0x0007
    int 0x10
    mov dx, 0x3F8
    out dx, al
    jmp .print
.halt:
    hlt
    jmp .halt
.message: db 'PHI: could not read the kernel from disk', 13, 10, 0

remaining: dw 0
packet:
    db 0x10, 0
.count:   dw 0
.offset:  dw 0
.segment: dw 0
.lba:     dd 0, 0

; flat segments covering all 4 GB: 0x08 is code, 0x10 is data
align 8
gdt:
    dq 0
    dw 0xFFFF, 0x0000
    db 0x00, 10011010b, 11001111b, 0x00     ; code: present, ring 0, executable, 4 KB granularity, 32-bit
    dw 0xFFFF, 0x0000
    db 0x00, 10010010b, 11001111b, 0x00     ; data: present, ring 0, writable
gdt_end:
gdt_descriptor:
    dw gdt_end - gdt - 1
    dd gdt

bits 32
protected:
    mov ax, 0x10
    mov ds, ax
    mov es, ax
    mov fs, ax
    mov gs, ax
    mov ss, ax
    mov esp, KERNEL_STACK
    mov ebx, BOOT_INFO
    mov eax, KERNEL_ADDRESS
    jmp eax

times STAGE2_SIZE - ($ - $$) db 0
