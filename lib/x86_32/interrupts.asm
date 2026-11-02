; provides: phi_interrupts_init phi_cores_running phi_idt phi_irq_register phi_irq_handlers phi_set_gate phi_interrupt_common phi_scheduler phi_syscall_hook phi_user_fault OS_SetInterruptHandler OS_SetIrqHandler OS_EndOfInterrupt OS_EnableInterrupts OS_DisableInterrupts OS_UnmaskIrq OS_MaskIrq phi_local_apic phi_io_apic phi_irq_inputs phi_irq_routes phi_enter_hook phi_leave_hook phi_halt_hook phi_core_join_hook phi_share_hook phi_preempt_hook phi_core_of_apic phi_kernel_halt phi_gdt_tss phi_gdt_descriptor phi_load_core_fs phi_fault_hook
; requires: phi_panic
; init: phi_interrupts_init
;
; Protected-mode interrupts for 32-bit kernels. Every kernel gets this:
;   - the interrupt controllers (8259 PICs) are remapped so IRQ 0-15 are
;     vectors 32-47 instead of colliding with CPU exceptions 0-31
;   - a 256-entry IDT: exceptions go to the panic screen, IRQs to the handler
;     registered for them (then end-of-interrupt is sent automatically)
;   - interrupts are enabled, with every IRQ masked until a driver unmasks it
;   - drivers.apic can move the IRQs to the I/O APIC (the same vectors): masks and
;     end-of-interrupt then go there and to the local APIC (phi_io_apic, phi_local_apic)
;
; Multitasking (tasks.asm) plugs in through three hooks, which are 0 until then:
; phi_scheduler picks the task to resume, phi_syscall_hook handles int 0x80,
; and phi_user_fault deals with an exception in a user program.
;
; The saved state on the stack, from esp up:
;   +0 gs, fs, es, ds   +16 edi, esi, ebp, esp, ebx, edx, ecx, eax (pushad)
;   +48 vector   +52 error code   +56 eip, cs, eflags (+ user esp, ss from ring 3)
FRAME_VECTOR equ 48
FRAME_CS     equ 60
IRQ_BASE   equ 32
PIC1_CMD   equ 0x20
PIC1_DATA  equ 0x21
PIC2_CMD   equ 0xA0
PIC2_DATA  equ 0xA1
KERNEL_CS  equ 0x08

phi_interrupts_init:
    ; the GDT: each core's data segment, based at its block, then loaded, with fs this core's
    xor ecx, ecx
.core_segment:
    imul eax, ecx, PHI_CORE_BLOCK
    add eax, phi_core_blocks
    mov [eax + PHI_CORE_SELF], eax
    lea edx, [phi_gdt_cores + ecx * 8]
    mov word [edx], PHI_CORE_BLOCK - 1       ; its limit: the block
    mov [edx + 2], ax                        ; its base
    shr eax, 16
    mov [edx + 4], al
    mov byte [edx + 5], 10010010b            ; present, ring 0, data
    mov byte [edx + 6], 0
    mov [edx + 7], ah
    inc ecx
    cmp ecx, PHI_CORES
    jb .core_segment
    lgdt [phi_gdt_descriptor]
    jmp 0x08:.reloaded
.reloaded:
    mov ax, 0x10
    mov ds, ax
    mov es, ax
    mov gs, ax
    mov ss, ax
    mov ax, PHI_CORE_SELECTOR
    mov fs, ax

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

; 1 once proc.cores has started other cores (so a log takes a lock: log_lock.asm)
phi_cores_running: dd 0

phi_interrupt_common:
    pushad
    push ds
    push es
    push fs
    push gs
    mov ax, 0x10            ; kernel data
    mov ds, ax
    mov es, ax
    mov gs, ax
    call phi_load_core_fs   ; and this core's block
    cld
    mov eax, [phi_enter_hook]       ; once programs share the cores: the kernel lock (tasks.asm)
    test eax, eax
    jz .entered
    call eax
.entered:
    mov eax, [esp + FRAME_VECTOR]
    cmp eax, IRQ_BASE
    jb .exception
    cmp eax, 0x80
    je .syscall
    cmp eax, IRQ_BASE + 16
    jae .schedule           ; vector 48: a task gives up the CPU (OS_Yield)

    sub eax, IRQ_BASE       ; eax = irq
    mov ebx, [phi_irq_handlers + eax * 4]
    test ebx, ebx
    jz .end_of_interrupt
    push eax
    push dword [fs:PHI_CORE_CATCH]
    mov dword [fs:PHI_CORE_CATCH], 0    ; an error in a handler isn't caught by the code it interrupted
    call ebx
    pop dword [fs:PHI_CORE_CATCH]
    pop eax
.end_of_interrupt:
    mov ebx, eax
    call phi_end_of_interrupt
    ; the timer takes the CPU away from user programs (the kernel gives it up itself), and
    ; from a kernel thread (proc.threads) that isn't in the middle of kernel code
    test ebx, ebx
    jnz .return
    test byte [esp + FRAME_CS], 3
    jnz .schedule
    mov eax, [phi_preempt_hook]
    test eax, eax
    jz .return
    call eax                ; eax = 1 if what was interrupted can be put aside
    test eax, eax
    jz .return
.schedule:
    mov ebx, [phi_scheduler]
    test ebx, ebx
    jz .return
    mov eax, esp
    call ebx                ; eax = the saved state of the task to resume
    mov esp, eax
.return:
    ; kernel code goes on with this core's block in fs, whichever core it last ran on (a
    ; thread, or a system call put aside on one core and resumed on another)
    test byte [esp + FRAME_CS], 3
    jnz .to_user
    mov [esp + 4], fs
.to_user:
    mov eax, [phi_leave_hook]
    test eax, eax
    jz .left
    call eax
.left:
    pop gs
    pop fs
    pop es
    pop ds
    popad
    add esp, 8              ; vector and error code
    iretd

.syscall:
    mov ebx, [phi_syscall_hook]
    test ebx, ebx
    jz .return
    mov eax, esp
    push dword [fs:PHI_CORE_CATCH]
    mov dword [fs:PHI_CORE_CATCH], 0    ; nor in a system call, by the kernel code that was running
    call ebx                ; eax = 1 when the task can't continue right now
    pop dword [fs:PHI_CORE_CATCH]
    test eax, eax
    jnz .schedule
    jmp .return

.exception:
    test byte [esp + FRAME_CS], 3
    jnz .user_exception
    ; kernel code inside a catch: dividing by zero or using memory that isn't there becomes
    ; an error it catches (lib/x86_32/throw.asm sets the hook)
    mov eax, [phi_fault_hook]
    test eax, eax
    jz .panic
    push esp
    call eax                ; eax = 1 if the state now goes to the throw
    add esp, 4
    test eax, eax
    jz .panic
    jmp .return
.user_exception:
    mov ebx, [phi_user_fault]
    test ebx, ebx
    jz .panic
    mov eax, esp
    call ebx                ; the program is stopped; run something else
    jmp .schedule
.panic:
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
    push ebx
    mov ebx, [ebp + 8]
    call phi_end_of_interrupt
    pop ebx
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
    call phi_mask
    pop ebp
    ret

; fs = this core's block (its number from its local APIC's ID once other cores have
; started; changes eax)
phi_load_core_fs:
    xor eax, eax
    cmp dword [phi_cores_running], 0
    je .first
    mov eax, [0xFEE00020]
    shr eax, 24
    movzx eax, byte [phi_core_of_apic + eax]
.first:
    lea eax, [PHI_CORE_SELECTOR + eax * 8]
    mov fs, ax
    ret

; sti; hlt: wait for an interrupt (letting go of the kernel lock meanwhile, once programs
; share the cores, so system calls on other cores aren't held up by a kernel that waits)
phi_kernel_halt:
    mov eax, [phi_halt_hook]
    test eax, eax
    jz .plain
    jmp eax
.plain:
    sti
    hlt
    ret

; ebx = irq: tell whichever controller sent it that it has been handled (changes eax)
phi_end_of_interrupt:
    mov eax, [phi_local_apic]
    test eax, eax
    jz .pic
    mov dword [eax + 0xB0], 0       ; the local APIC's end-of-interrupt register
    ret
.pic:
    mov al, 0x20
    cmp ebx, 8
    jb .master
    out PIC2_CMD, al        ; IRQ 8-15 also need the second controller told
.master:
    out PIC1_CMD, al
    ret

; eax = irq: let it interrupt
phi_unmask:
    cmp dword [phi_io_apic], 0
    jne .io_apic
    call phi_irq_port_and_bit
    in al, dx
    not ah
    and al, ah
    out dx, al
    ret
.io_apic:
    mov ecx, [phi_irq_routes + eax * 4]
    jmp phi_io_apic_route

; eax = irq: keep it from interrupting
phi_mask:
    cmp dword [phi_io_apic], 0
    jne .io_apic
    call phi_irq_port_and_bit
    in al, dx
    or al, ah
    out dx, al
    ret
.io_apic:
    mov ecx, [phi_irq_routes + eax * 4]
    or ecx, 0x10000                 ; masked
    jmp phi_io_apic_route

; eax = irq, ecx = the low half of its I/O APIC redirection entry: written (with
; interrupts off, as the I/O APIC is reached through an index register and a window)
phi_io_apic_route:
    movzx eax, byte [phi_irq_inputs + eax]
    cmp eax, 0xFF
    je .none                        ; an IRQ with no input of its own (IRQ 2, the PICs' cascade)
    pushfd
    cli
    push edx
    lea eax, [0x10 + eax * 2]       ; the entry's low half
    mov edx, [phi_io_apic]
    mov [edx], eax                  ; IOREGSEL
    mov [edx + 0x10], ecx           ; IOWIN
    pop edx
    popfd
    ret
.none:
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

; once drivers.apic has moved the IRQs from the 8259 PICs to the I/O APIC: the local
; APIC's address (end-of-interrupt goes there), the I/O APIC's, and for each IRQ the input
; it comes in on and its redirection entry's low half (its vector, polarity and trigger)
phi_local_apic:   dd 0
phi_io_apic:      dd 0
phi_irq_inputs:   times 16 db 0
phi_irq_routes:   times 16 dd 0

; once programs share the cores (tasks.asm's phi_share_cores): entering and leaving the
; kernel take and give back the kernel lock, a halt lets go of it meanwhile, and another
; core waiting for work (proc.cores) joins the scheduler instead
phi_enter_hook:     dd 0
phi_leave_hook:     dd 0
phi_halt_hook:      dd 0
phi_core_join_hook: dd 0
phi_share_hook:     dd 0                ; phi_share_cores, once multitasking has started
phi_preempt_hook:   dd 0                ; once kernel threads have started: whether the timer may take the CPU back
phi_fault_hook:     dd 0                ; throw.asm's, in a kernel that catches errors: CPU exceptions as errors
; each core's number by its local APIC ID (proc.cores fills it in: the first core is 0)
phi_core_of_apic:   times 256 db 0
phi_scheduler:    dd 0
phi_syscall_hook: dd 0
phi_user_fault:   dd 0

; ---- the GDT, and a block of data for each core
;
; Every kernel's segments: its code and data (flat), user programs' (tasks.asm), a TSS
; for each core (tasks.asm fills them in), and for each core a data segment whose base is
; that core's block, which kernel code reaches through fs: fs:PHI_CORE_CATCH is the
; innermost catch of the code this core is running (lib/x86_32/throw.asm), so threads on
; different cores catch errors of their own. The scheduler keeps each task's (tasks.asm).
PHI_CORE_SELF     equ 0                 ; the block's own address
PHI_CORE_CATCH    equ 4                 ; the innermost catch, or 0
PHI_CORE_UNWIND   equ 8                 ; the innermost running method with things to give back, or 0
PHI_CORE_ERROR    equ 12                ; the error being thrown (an Error: 713 bytes)
PHI_CORE_FLAGS    equ 728               ; interrupts as they were at the throw, until the catch has the error
PHI_CORE_BLOCK    equ 1024
PHI_CORE_SELECTOR equ 0x68              ; core n's data segment is PHI_CORE_SELECTOR + n * 8
PHI_CORES         equ 8                 ; as proc.cores has it

align 8
phi_gdt:
    dq 0
    dw 0xFFFF, 0x0000
    db 0x00, 10011010b, 11001111b, 0x00    ; 0x08 kernel code
    dw 0xFFFF, 0x0000
    db 0x00, 10010010b, 11001111b, 0x00    ; 0x10 kernel data
    dw 0xFFFF, 0x0000
    db 0x00, 11111010b, 11001111b, 0x00    ; 0x18 user code (ring 3)
    dw 0xFFFF, 0x0000
    db 0x00, 11110010b, 11001111b, 0x00    ; 0x20 user data (ring 3)
phi_gdt_tss:
    times PHI_CORES dq 0                   ; 0x28 and on: each core's TSS (tasks.asm fills them in)
phi_gdt_cores:
    times PHI_CORES dq 0                   ; 0x68 and on: each core's block (filled in at startup)
phi_gdt_end:
phi_gdt_descriptor:
    dw phi_gdt_end - phi_gdt - 1
    dd phi_gdt

align 16
phi_core_blocks:
    times PHI_CORES * PHI_CORE_BLOCK db 0
