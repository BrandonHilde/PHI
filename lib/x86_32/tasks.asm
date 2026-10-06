; provides: OS_StartMultitasking OS_CreateUserTask OS_TaskState OS_TaskExitCode OS_TaskParent OS_FreeTask OS_Yield OS_CurrentTask OS_SetSyscallHandler OS_SyscallFrame
; requires: phi_set_gate phi_idt phi_scheduler phi_syscall_hook phi_user_fault OS_SetupInteruptTimer OS_GetTicks OS_KeyAvailable OS_SetupKeyboardInterupt phi_take_key phi_print phi_print_serial phi_print_int phi_print_hex
;
; Tasks for 32-bit kernels: user programs in ring 3, each with its own page
; directory and kernel stack, and the kernel itself as task 0.
;
;   - the timer takes the CPU away from a user program; the kernel gives it up
;     itself (OS_Yield, or when its main code ends), so kernel code never runs
;     twice at the same time
;   - system calls are int 0x80: eax = number, ebx = argument, result in eax
;   - a program that faults is stopped with a message; the kernel carries on
;
; Every switch happens at the end of an interrupt: the saved state on the task's
; kernel stack (see interrupts.asm) is what the next iretd resumes.
MAX_TASKS     equ 16
TASK_FREE     equ 0
TASK_READY    equ 1
TASK_SLEEPING equ 2
TASK_WAIT_KEY equ 3
TASK_WAIT_CHILD equ 4
TASK_EXITED   equ 5
TASK_COLLECTED equ 6            ; exited, and its parent has read the exit code

KERNEL_CODE   equ 0x08
KERNEL_DATA   equ 0x10
USER_CODE     equ 0x18 | 3
USER_DATA     equ 0x20 | 3
TSS_SELECTOR  equ 0x28
YIELD_VECTOR  equ 48
SYSCALL_VECTOR equ 0x80

USER_BASE     equ 0x40000000
USER_TOP      equ 0x40400000

; offsets in the saved state
F_EBX equ 32
F_EDX equ 36
F_ECX equ 40
F_EAX equ 44
F_VECTOR equ 48
F_EIP equ 56
F_SIZE equ 76                   ; everything, including the user esp and ss

OS_StartMultitasking:
    cmp byte [phi_tasks_started], 0
    jne .done
    mov byte [phi_tasks_started], 1
    cli

    ; a GDT with user segments and the TSS (which holds the kernel stack for ring 3)
    mov dword [phi_tss + 8], KERNEL_DATA      ; ss0
    mov word [phi_tss + 102], 104             ; no I/O permission bitmap
    mov eax, phi_tss
    mov [phi_gdt_tss + 2], ax
    shr eax, 16
    mov [phi_gdt_tss + 4], al
    mov [phi_gdt_tss + 7], ah
    lgdt [phi_gdt_descriptor]
    jmp KERNEL_CODE:.reload
.reload:
    mov ax, KERNEL_DATA
    mov ds, ax
    mov es, ax
    mov fs, ax
    mov gs, ax
    mov ss, ax
    mov ax, TSS_SELECTOR
    ltr ax

    ; int 0x80 can be used from ring 3; vector 48 gives up the CPU
    mov ecx, SYSCALL_VECTOR
    mov eax, phi_isr_syscall
    call phi_set_gate
    mov byte [phi_idt + SYSCALL_VECTOR * 8 + 5], 11101110b    ; present, ring 3, interrupt gate
    mov ecx, YIELD_VECTOR
    mov eax, phi_isr_yield
    call phi_set_gate

    ; task 0 is the kernel
    mov dword [phi_task_state], TASK_READY
    mov eax, cr3
    mov [phi_task_cr3], eax
    mov dword [phi_current_task], 0

    mov dword [phi_scheduler], phi_schedule
    mov dword [phi_syscall_hook], phi_syscall
    mov dword [phi_user_fault], phi_task_fault
    sti
    call OS_SetupInteruptTimer
.done:
    ret

phi_isr_yield:
    push dword 0
    push dword YIELD_VECTOR
    jmp phi_interrupt_common

phi_isr_syscall:
    push dword 0
    push dword SYSCALL_VECTOR
    jmp phi_interrupt_common

; args: page directory, entry, user esp, kernel stack top, end of the program
; image, bottom of the user stack -> eax = the new task's number, or -1
OS_CreateUserTask:
    push ebp
    mov ebp, esp
    mov ecx, 1
.find:
    cmp dword [phi_task_state + ecx * 4], TASK_FREE
    je .found
    inc ecx
    cmp ecx, MAX_TASKS
    jb .find
    mov eax, -1
    pop ebp
    ret
.found:
    ; the state the scheduler will resume: an iretd into ring 3 at the entry
    mov edi, [ebp + 20]
    sub edi, F_SIZE
    push edi
    push ecx
    mov ecx, F_SIZE / 4
    xor eax, eax
    rep stosd
    pop ecx
    pop edi
    mov dword [edi + 0], USER_DATA            ; gs
    mov dword [edi + 4], USER_DATA            ; fs
    mov dword [edi + 8], USER_DATA            ; es
    mov dword [edi + 12], USER_DATA           ; ds
    mov eax, [ebp + 12]
    mov [edi + F_EIP], eax
    mov dword [edi + F_EIP + 4], USER_CODE
    mov dword [edi + F_EIP + 8], 0x202        ; interrupts on
    mov eax, [ebp + 16]
    mov [edi + F_EIP + 12], eax               ; user esp
    mov dword [edi + F_EIP + 16], USER_DATA   ; user ss

    mov [phi_task_esp + ecx * 4], edi
    mov eax, [ebp + 8]
    mov [phi_task_cr3 + ecx * 4], eax
    mov eax, [ebp + 20]
    mov [phi_task_kstack + ecx * 4], eax
    mov eax, [ebp + 24]
    mov [phi_task_image_end + ecx * 4], eax
    mov eax, [ebp + 28]
    mov [phi_task_stack_bottom + ecx * 4], eax
    mov eax, [phi_current_task]
    mov [phi_task_parent + ecx * 4], eax
    mov dword [phi_task_exit + ecx * 4], 0
    mov dword [phi_task_state + ecx * 4], TASK_READY
    mov eax, ecx
    pop ebp
    ret

; ---- the scheduler: eax = saved state of the current task -> eax = the one to resume
phi_schedule:
    mov ebx, [phi_current_task]
    mov [phi_task_esp + ebx * 4], eax
    call phi_wake_tasks

    ; round robin: the next ready task after the current one (the current one last)
    mov ebx, [phi_current_task]
    mov ecx, 1
.next:
    lea edx, [ebx + ecx]
    and edx, MAX_TASKS - 1
    cmp dword [phi_task_state + edx * 4], TASK_READY
    je .found
    inc ecx
    cmp ecx, MAX_TASKS
    jbe .next
    xor edx, edx                              ; the kernel is always ready
.found:
    mov [phi_current_task], edx
    mov eax, [phi_task_kstack + edx * 4]
    mov [phi_tss + 4], eax                    ; esp0: where ring 3 interrupts land
    mov eax, [phi_task_cr3 + edx * 4]
    mov ecx, cr3
    cmp eax, ecx
    je .same_space
    mov cr3, eax
.same_space:
    mov eax, [phi_task_esp + edx * 4]
    ret

; make blocked tasks ready when what they wait for has happened
phi_wake_tasks:
    call OS_GetTicks
    mov esi, eax                              ; now
    xor ecx, ecx
.task:
    mov eax, [phi_task_state + ecx * 4]
    cmp eax, TASK_SLEEPING
    je .sleeping
    cmp eax, TASK_WAIT_KEY
    je .key
    cmp eax, TASK_WAIT_CHILD
    je .child
    jmp .next

.sleeping:
    mov eax, esi
    sub eax, [phi_task_wake + ecx * 4]
    js .next                                  ; not yet (the difference works across wraparound)
    mov dword [phi_task_state + ecx * 4], TASK_READY
    jmp .next

.key:
    push ecx
    push esi
    call phi_take_key
    pop esi
    pop ecx
    test eax, eax
    jz .next
    mov edi, [phi_task_esp + ecx * 4]
    mov [edi + F_EAX], eax                    ; the system call's result
    mov dword [phi_task_state + ecx * 4], TASK_READY
    jmp .next

.child:
    mov edx, [phi_task_wait + ecx * 4]
    cmp dword [phi_task_state + edx * 4], TASK_EXITED
    jne .next
    mov eax, [phi_task_exit + edx * 4]
    mov edi, [phi_task_esp + ecx * 4]
    mov [edi + F_EAX], eax
    mov dword [phi_task_state + edx * 4], TASK_COLLECTED
    mov dword [phi_task_state + ecx * 4], TASK_READY

.next:
    inc ecx
    cmp ecx, MAX_TASKS
    jb .task
    ret

; ---- system calls: eax = saved state -> eax = 1 if the task can't continue now
phi_syscall:
    mov ebp, eax
    mov [phi_syscall_frame], eax
    mov ebx, [ebp + F_EBX]
    mov eax, [ebp + F_EAX]
    cmp eax, 10
    jae .extension
    jmp [.table + eax * 4]
.table:
    dd .exit, .write, .debug, .read_key, .sleep, .ticks, .getpid, .yield, .key_available, .wait

.exit:
    mov ecx, [phi_current_task]
    mov [phi_task_exit + ecx * 4], ebx
    mov dword [phi_task_state + ecx * 4], TASK_EXITED
    mov eax, 1
    ret

.write:
    call phi_user_string
    jc .bad_pointer
    mov esi, ebx
    call phi_print
    jmp .done

.debug:
    call phi_user_string
    jc .bad_pointer
    mov esi, ebx
    call phi_print_serial
    jmp .done

.read_key:
    call OS_SetupKeyboardInterupt
    call phi_take_key
    test eax, eax
    jz .block_for_key
    mov [ebp + F_EAX], eax
    jmp .done
.block_for_key:
    mov ecx, [phi_current_task]
    mov dword [phi_task_state + ecx * 4], TASK_WAIT_KEY
    mov eax, 1
    ret

.sleep:
    call OS_GetTicks
    add eax, ebx
    mov ecx, [phi_current_task]
    mov [phi_task_wake + ecx * 4], eax
    mov dword [phi_task_state + ecx * 4], TASK_SLEEPING
    mov eax, 1
    ret

.ticks:
    call OS_GetTicks
    mov [ebp + F_EAX], eax
    jmp .done

.getpid:
    mov eax, [phi_current_task]
    mov [ebp + F_EAX], eax
    jmp .done

.yield:
    mov eax, 1
    ret

.key_available:
    call OS_KeyAvailable
    mov [ebp + F_EAX], eax
    jmp .done

.wait:
    ; only for a child of the caller
    mov ecx, [phi_current_task]
    cmp ebx, MAX_TASKS
    jae .invalid
    cmp [phi_task_parent + ebx * 4], ecx
    jne .invalid
    mov eax, [phi_task_state + ebx * 4]
    cmp eax, TASK_FREE
    je .invalid
    cmp eax, TASK_COLLECTED
    je .invalid
    cmp eax, TASK_EXITED
    jne .block_for_child
    mov eax, [phi_task_exit + ebx * 4]
    mov [ebp + F_EAX], eax
    mov dword [phi_task_state + ebx * 4], TASK_COLLECTED
    jmp .done
.block_for_child:
    mov [phi_task_wait + ecx * 4], ebx
    mov dword [phi_task_state + ecx * 4], TASK_WAIT_CHILD
    mov eax, 1
    ret

.extension:
    ; numbers from 16 up go to the kernel's PHI handler (OS_SetSyscallHandler)
    mov edx, [phi_syscall_extension]
    test edx, edx
    jz .invalid
    cmp eax, 16
    jb .invalid
    call edx                ; it reads the number from the saved eax and sets the result there
    jmp .done

.bad_pointer:
.invalid:
    mov dword [ebp + F_EAX], -1
.done:
    xor eax, eax
    ret

; is ebx a zero-terminated string inside the current program's memory?
; carry set if not
phi_user_string:
    mov ecx, [phi_current_task]
    mov esi, ebx
.byte:
    cmp esi, USER_BASE
    jb .bad
    cmp esi, [phi_task_image_end + ecx * 4]
    jb .readable
    cmp esi, [phi_task_stack_bottom + ecx * 4]
    jb .bad
    cmp esi, USER_TOP
    jae .bad
.readable:
    cmp byte [esi], 0
    je .good
    inc esi
    jmp .byte
.good:
    clc
    ret
.bad:
    stc
    ret

; ---- an exception in a user program: eax = saved state. Stop the program.
phi_task_fault:
    mov ebp, eax
    mov esi, .stopped
    call phi_print
    mov eax, [phi_current_task]
    call phi_print_int
    mov esi, .stopped_by
    call phi_print
    mov eax, [ebp + F_VECTOR]
    call phi_print_int
    mov esi, .at
    call phi_print
    mov eax, [ebp + F_EIP]
    call phi_print_hex
    cmp dword [ebp + F_VECTOR], 14
    jne .end
    mov esi, .address
    call phi_print
    mov eax, cr2
    call phi_print_hex
.end:
    mov esi, .newline
    call phi_print
    mov ecx, [phi_current_task]
    mov dword [phi_task_exit + ecx * 4], -1
    mov dword [phi_task_state + ecx * 4], TASK_EXITED
    ret
.stopped:    db 13, 10, 'PHI: program ', 0
.stopped_by: db ' stopped by exception ', 0
.at:         db ' at ', 0
.address:    db ', address ', 0
.newline:    db 13, 10, 0

; ---- for PHI code in the kernel

OS_Yield:
    int YIELD_VECTOR
    ret

OS_CurrentTask:
    mov eax, [phi_current_task]
    ret

; arg: task -> eax = its state (0 free, 1 ready, 2 sleeping, 3 waiting for a key,
; 4 waiting for a child, 5 exited, 6 exited and collected by its parent)
OS_TaskState:
    mov eax, [esp + 4]
    and eax, MAX_TASKS - 1
    mov eax, [phi_task_state + eax * 4]
    ret

OS_TaskExitCode:
    mov eax, [esp + 4]
    and eax, MAX_TASKS - 1
    mov eax, [phi_task_exit + eax * 4]
    ret

OS_TaskParent:
    mov eax, [esp + 4]
    and eax, MAX_TASKS - 1
    mov eax, [phi_task_parent + eax * 4]
    ret

; arg: task: its slot can be used again (its memory must already be freed)
OS_FreeTask:
    mov eax, [esp + 4]
    and eax, MAX_TASKS - 1
    jz .kernel
    mov dword [phi_task_state + eax * 4], TASK_FREE
.kernel:
    ret

; arg: an ordinary method, run for system calls numbered 16 and up; it finds the
; number in the saved eax (OS_SyscallFrame) and must put the result there
OS_SetSyscallHandler:
    mov eax, [esp + 4]
    mov [phi_syscall_extension], eax
    ret

; eax = the address of the saved state of the system call being handled
OS_SyscallFrame:
    mov eax, [phi_syscall_frame]
    ret

phi_tasks_started:     db 0
phi_current_task:      dd 0
phi_syscall_frame:     dd 0
phi_syscall_extension: dd 0
phi_task_state:        times MAX_TASKS dd 0
phi_task_esp:          times MAX_TASKS dd 0
phi_task_cr3:          times MAX_TASKS dd 0
phi_task_kstack:       times MAX_TASKS dd 0
phi_task_image_end:    times MAX_TASKS dd 0
phi_task_stack_bottom: times MAX_TASKS dd 0
phi_task_parent:       times MAX_TASKS dd 0
phi_task_exit:         times MAX_TASKS dd 0
phi_task_wake:         times MAX_TASKS dd 0
phi_task_wait:         times MAX_TASKS dd 0

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
    dw 103, 0x0000
    db 0x00, 10001001b, 00000000b, 0x00    ; 0x28 the TSS (base filled in at startup)
phi_gdt_end:
phi_gdt_descriptor:
    dw phi_gdt_end - phi_gdt - 1
    dd phi_gdt

align 4
phi_tss:
    times 104 db 0
