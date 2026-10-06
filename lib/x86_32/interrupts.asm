; provides: phi_interrupts_init phi_irq_register OS_SetInterruptHandler OS_SetIrqHandler OS_EndOfInterrupt OS_EnableInterrupts OS_DisableInterrupts OS_UnmaskIrq OS_MaskIrq
; requires: phi_panic
; init: phi_interrupts_init
;
; Protected-mode interrupts for 32-bit kernels. Every kernel gets this:
;   - the interrupt controllers (8259 PICs) are remapped so IRQ 0-15 are
;     vectors 32-47 instead of colliding with CPU exceptions 0-31
;   - a 256-entry IDT: exceptions go to the panic screen, IRQs to the handler
;     registered for them (then end-of-interrupt is sent automatically)
;   - interrupts are enabled, with every IRQ masked until a driver unmasks it
IRQ_BASE   equ 32
PIC1_CMD   equ 0x20
PIC1_DATA  equ 0x21
PIC2_CMD   equ 0xA0
PIC2_DATA  equ 0xA1
KERNEL_CS  equ 0x08

phi_interrupts_init:
    ; remap the PICs (ICW1-ICW4)
    mov al, 0x11            ; start initialization, expect ICW4
    out PIC1_CMD, al
    out PIC2_CMD, al
    mov al, IRQ_BASE        ; master: IRQ 0-7 -> vectors 32-39
    out PIC1_DATA, al
    mov al, IRQ_BASE + 8    ; slave: IRQ 8-15 -> vectors 40-47
    out PIC2_DATA, al
    mov al, 4               ; the slave is on the master's IRQ 2
    out PIC1_DATA, al
    mov al, 2
    out PIC2_DATA, al
    mov al, 1               ; 8086 mode
    out PIC1_DATA, al
    out PIC2_DATA, al
    mov al, 11111011b       ; mask everything except the cascade (IRQ 2)
    out PIC1_DATA, al
    mov al, 0xFF
    out PIC2_DATA, al

    ; point every vector at a stub
    xor ecx, ecx
.gate:
    mov eax, phi_isr_default
    cmp ecx, 48
    jae .set
    mov eax, [phi_isr_table + ecx * 4]
.set:
    call phi_set_gate
    inc ecx
    cmp ecx, 256
    jb .gate

    lidt [phi_idt_descriptor]
    sti
    ret

; ecx = vector, eax = handler address
phi_set_gate:
    push eax
    push edx
    lea edx, [phi_idt + ecx * 8]
    mov [edx], ax           ; handler, low half
    mov word [edx + 2], KERNEL_CS
    mov byte [edx + 4], 0
    mov byte [edx + 5], 10001110b   ; present, ring 0, 32-bit interrupt gate
    shr eax, 16
    mov [edx + 6], ax       ; handler, high half
    pop edx
    pop eax
    ret

; for drivers written in assembly: eax = irq, ebx = handler (an ordinary function)
phi_irq_register:
    mov [phi_irq_handlers + eax * 4], ebx
    jmp phi_unmask

; ---- the stubs: each pushes an error code (0 if the CPU doesn't) and its vector
%macro PHI_STUB_NOERR 1
phi_isr_%1:
    push dword 0
    push dword %1
    jmp phi_interrupt_common
%endmacro
%macro PHI_STUB_ERR 1
phi_isr_%1:
    push dword %1
    jmp phi_interrupt_common
%endmacro
%macro PHI_STUB_ENTRY 1
    dd phi_isr_%1
%endmacro

%assign phi_vector 0
%rep 48
%if phi_vector = 8 || (phi_vector >= 10 && phi_vector <= 14) || phi_vector = 17 || phi_vector = 21 || phi_vector = 29 || phi_vector = 30
    PHI_STUB_ERR phi_vector
%else
    PHI_STUB_NOERR phi_vector
%endif
%assign phi_vector phi_vector + 1
%endrep

phi_isr_table:
%assign phi_vector 0
%rep 48
    PHI_STUB_ENTRY phi_vector
%assign phi_vector phi_vector + 1
%endrep

phi_isr_default:
    iretd

; stack here: pushad registers, vector, error code, eip, cs, eflags
phi_interrupt_common:
    pushad
    cld
    mov eax, [esp + 32]     ; vector
    cmp eax, IRQ_BASE
    jb .exception
    sub eax, IRQ_BASE       ; eax = irq
    mov ebx, [phi_irq_handlers + eax * 4]
    test ebx, ebx
    jz .end_of_interrupt
    push eax
    call ebx
    pop eax
.end_of_interrupt:
    mov ebx, eax
    mov al, 0x20
    cmp ebx, 8
    jb .master
    out PIC2_CMD, al        ; IRQ 8-15 also need the second controller told
.master:
    out PIC1_CMD, al
    popad
    add esp, 8              ; vector and error code
    iretd
.exception:
    mov eax, esp            ; the saved state, for the panic screen
    jmp phi_panic

; ---- for PHI programs

; args: vector, handler (an [isr] method)
OS_SetInterruptHandler:
    push ebp
    mov ebp, esp
    pushfd
    cli
    mov ecx, [ebp + 8]
    mov eax, [ebp + 12]
    call phi_set_gate
    popfd                   ; interrupts back on if they were on
    pop ebp
    ret

; args: irq, handler (an ordinary method; end-of-interrupt is sent for it)
OS_SetIrqHandler:
    push ebp
    mov ebp, esp
    mov eax, [ebp + 8]
    mov ebx, [ebp + 12]
    call phi_irq_register
    pop ebp
    ret

; args: irq   (only needed by [isr] handlers, which bypass the automatic one)
OS_EndOfInterrupt:
    push ebp
    mov ebp, esp
    mov al, 0x20
    cmp dword [ebp + 8], 8
    jb .master
    out PIC2_CMD, al
.master:
    out PIC1_CMD, al
    pop ebp
    ret

OS_EnableInterrupts:
    sti
    ret

OS_DisableInterrupts:
    cli
    ret

; args: irq
OS_UnmaskIrq:
    push ebp
    mov ebp, esp
    mov eax, [ebp + 8]
    call phi_unmask
    pop ebp
    ret

; args: irq
OS_MaskIrq:
    push ebp
    mov ebp, esp
    mov eax, [ebp + 8]
    call phi_irq_port_and_bit
    in al, dx
    or al, ah
    out dx, al
    pop ebp
    ret

; eax = irq
phi_unmask:
    call phi_irq_port_and_bit
    in al, dx
    not ah
    and al, ah
    out dx, al
    ret

; eax = irq -> dx = the controller's mask port, ah = the irq's bit
phi_irq_port_and_bit:
    mov ecx, eax
    mov dx, PIC1_DATA
    cmp cl, 8
    jb .bit
    mov dx, PIC2_DATA
    sub cl, 8
.bit:
    mov ah, 1
    shl ah, cl
    ret

align 8
phi_idt:
    times 256 * 8 db 0
phi_idt_descriptor:
    dw 256 * 8 - 1
    dd phi_idt
phi_irq_handlers:
    times 16 dd 0
