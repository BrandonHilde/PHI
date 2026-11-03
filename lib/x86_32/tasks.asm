; provides: OS_StartMultitasking OS_CreateUserTask OS_SetTaskHeapEnd OS_TaskState OS_TaskExitCode OS_TaskParent OS_FreeTask OS_KillTask OS_Reboot OS_Yield OS_Idle OS_CurrentTask OS_SetSyscallHandler OS_SyscallFrame phi_share_cores phi_own_add phi_task_core phi_thread_start
; requires: phi_set_gate phi_idt phi_scheduler phi_syscall_hook phi_user_fault OS_SetupInteruptTimer OS_GetTicks OS_KeyAvailable OS_SetupKeyboardInterupt phi_take_key phi_print phi_print_serial phi_print_int phi_print_hex phi_enter_hook phi_leave_hook phi_halt_hook phi_core_join_hook phi_share_hook phi_preempt_hook phi_core_of_apic phi_kernel_halt phi_gdt_tss phi_gdt_descriptor phi_core_blocks phi_own_call_hook
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
MAX_CORES     equ 8                ; as proc.cores has it
TSS_SIZE      equ 104
TICK_VECTOR   equ 0xF1             ; another core's own timer (its local APIC's)
LOCAL_APIC    equ 0xFEE00000
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
OWN_RANGES equ 8               ; ranges of system calls with a lock of their own (phi_own_add)

USER_BASE     equ 0x40000000
USER_TOP      equ 0x40400000

; offsets in the saved state
F_ESI equ 20
F_EBX equ 32
F_EDX equ 36
F_ECX equ 40
F_EAX equ 44
F_VECTOR equ 48
F_EIP equ 56
ERROR_DIVIDE  equ 3              ; ErrorKind (lib/phi/lang/error.phi), for a program's fault entry
ERROR_EMPTY   equ 4
ERROR_ACCESS  equ 5
F_SIZE equ 76                   ; everything, including the user esp and ss

OS_StartMultitasking:
    cmp byte [phi_tasks_started], 0
    jne .done
    mov byte [phi_tasks_started], 1
    cli

    ; a GDT with user segments and a TSS for each core (which holds the kernel stack for
    ; ring 3: core n's is at phi_tss + n * TSS_SIZE, selector TSS_SELECTOR + n * 8)
    push edi
    xor ecx, ecx
.tss:
    imul edx, ecx, TSS_SIZE
    add edx, phi_tss
    mov dword [edx + 8], KERNEL_DATA          ; ss0
    mov word [edx + 102], TSS_SIZE            ; no I/O permission bitmap
    lea edi, [phi_gdt_tss + ecx * 8]
    mov word [edi], TSS_SIZE - 1
    mov [edi + 2], dx
    mov eax, edx
    shr eax, 16
    mov [edi + 4], al
    mov byte [edi + 5], 10001001b             ; present, a 32-bit TSS
    mov byte [edi + 6], 0
    mov [edi + 7], ah
    inc ecx
    cmp ecx, MAX_CORES
    jb .tss
    pop edi
    mov ax, TSS_SELECTOR                      ; (the GDT is interrupts.asm's, loaded at startup)
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
    mov dword [phi_share_hook], phi_share_cores
    mov dword [phi_own_call_hook], phi_own_call
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
    mov dword [phi_task_heap_end + ecx * 4], 0   ; no heap until it grows one
    mov byte [phi_task_fpu_fresh + ecx], 1       ; its FPU starts clean
    mov byte [phi_task_thread + ecx], 0          ; a program, not a thread
    mov dword [phi_task_fault_entry + ecx * 4], 0
    mov dword [phi_task_catch + ecx * 4], 0
    mov dword [phi_task_unwind + ecx * 4], 0
    mov eax, [phi_current_task]
    mov [phi_task_parent + ecx * 4], eax
    mov dword [phi_task_exit + ecx * 4], 0
    mov dword [phi_task_state + ecx * 4], TASK_READY
    mov eax, ecx
    pop ebp
    ret

; ---- the scheduler: eax = saved state of the current task -> eax = the one to resume
;
; Round robin over the tasks that are ready: on the first core the kernel (task 0) takes
; its turn too, and is what runs when nothing else can; once programs share the cores,
; each other core takes a ready program no other core is running, or its idle loop
; (MAX_TASKS + its number) when there's none. This runs with the kernel lock held.
phi_schedule:
    mov ebx, [phi_current_task]
    cmp ebx, MAX_TASKS
    jae .save_idle
    mov [phi_task_esp + ebx * 4], eax
    jmp .saved
.save_idle:
    mov [phi_idle_esp - MAX_TASKS * 4 + ebx * 4], eax
.saved:
    call phi_wake_tasks                       ; (which may change ebx: giving a program a key)
    mov ebx, [phi_current_task]
    call phi_this_core
    mov edi, eax                              ; this core

    mov esi, ebx                              ; where the round starts
    cmp esi, MAX_TASKS
    jb .round
    mov esi, [phi_last_picked]
.round:
    mov ecx, 1
.next:
    lea edx, [esi + ecx]
    and edx, MAX_TASKS - 1
    cmp dword [phi_task_state + edx * 4], TASK_READY
    jne .skip
    test edx, edx
    jnz .program
    test edi, edi
    jz .found                                 ; the kernel, on the first core
    jmp .skip
.program:
    cmp edx, ebx
    je .found                                 ; itself, still ready
    cmp dword [phi_task_core + edx * 4], 0
    je .found                                 ; no other core is running it
.skip:
    inc ecx
    cmp ecx, MAX_TASKS
    jbe .next
    xor edx, edx                              ; nothing: the kernel (always ready), or on
    test edi, edi                             ; another core its idle loop
    jz .found
    lea edx, [MAX_TASKS + edi]
.found:
    ; which core runs which task
    cmp ebx, MAX_TASKS
    jae .old_off
    mov dword [phi_task_core + ebx * 4], 0
.old_off:
    cmp edx, MAX_TASKS
    jae .new_on
    lea eax, [edi + 1]
    mov [phi_task_core + edx * 4], eax
    mov [phi_last_picked], edx
.new_on:
    ; the kernel's own code counts as being in the kernel (it holds the lock while it runs)
    cmp dword [phi_enter_hook], 0
    je .depth_done
    test ebx, ebx
    jnz .old_not_kernel
    dec dword [phi_core_depth + edi * 4]
.old_not_kernel:
    test edx, edx
    jnz .depth_done
    inc dword [phi_core_depth + edi * 4]
.depth_done:

    ; each task has its own FPU registers (floating point numbers): the old task's are
    ; saved (which also clears them), and the new one's put back, or left clean for a new
    ; task; an idle loop has none
    cmp edx, ebx
    je .same_fpu
    cmp ebx, MAX_TASKS
    jae .old_saved
    imul esi, ebx, FPU_STATE
    fnsave [phi_task_fpu + esi]
.old_saved:
    cmp edx, MAX_TASKS
    jae .same_fpu
    cmp byte [phi_task_fpu_fresh + edx], 0
    je .restore_fpu
    mov byte [phi_task_fpu_fresh + edx], 0
    fninit
    jmp .same_fpu
.restore_fpu:
    imul esi, edx, FPU_STATE
    frstor [phi_task_fpu + esi]
.same_fpu:
    ; each task's catches (lib/x86_32/throw.asm): this core's block holds the running task's
    cmp ebx, MAX_TASKS
    jae .catches_kept
    mov eax, [fs:PHI_CORE_CATCH]
    mov [phi_task_catch + ebx * 4], eax
    mov eax, [fs:PHI_CORE_UNWIND]
    mov [phi_task_unwind + ebx * 4], eax
.catches_kept:
    xor eax, eax
    xor ecx, ecx
    cmp edx, MAX_TASKS
    jae .catches_set
    mov eax, [phi_task_catch + edx * 4]
    mov ecx, [phi_task_unwind + edx * 4]
.catches_set:
    mov [fs:PHI_CORE_CATCH], eax
    mov [fs:PHI_CORE_UNWIND], ecx
    mov [phi_current_task], edx
    mov [phi_core_task + edi * 4], edx
    xor ecx, ecx                              ; (an idle loop has the kernel's address space)
    cmp edx, MAX_TASKS
    jae .space
    mov ecx, edx
    mov eax, [phi_task_kstack + edx * 4]
    imul esi, edi, TSS_SIZE
    mov [phi_tss + esi + 4], eax              ; esp0: where ring 3 interrupts land, on this core
.space:
    mov eax, [phi_task_cr3 + ecx * 4]
    mov ecx, cr3
    cmp eax, ecx
    je .same_space
    mov cr3, eax
.same_space:
    cmp edx, MAX_TASKS
    jae .resume_idle
    mov eax, [phi_task_esp + edx * 4]
    ret
.resume_idle:
    mov eax, [phi_idle_esp - MAX_TASKS * 4 + edx * 4]
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
    ; killed while it waited for the kernel lock (another core stopped it): it doesn't make
    ; the call, which could set its state again (sleeping, say) and bring it back
    mov ecx, [phi_current_task]
    cmp dword [phi_task_state + ecx * 4], TASK_EXITED
    jae .killed
    mov ebx, [ebp + F_EBX]
    mov eax, [ebp + F_EAX]
    cmp eax, 11
    jae .extension
    jmp [.table + eax * 4]
.table:
    dd .exit, .write, .debug, .read_key, .sleep, .ticks, .getpid, .yield, .key_available, .wait, .fault_entry

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

.fault_entry:
    ; where this program catches CPU exceptions (its throw.asm's phi_fault_entry): in its code
    mov ecx, [phi_current_task]
    cmp ebx, USER_BASE
    jb .invalid
    cmp ebx, [phi_task_image_end + ecx * 4]
    jae .invalid
    mov [phi_task_fault_entry + ecx * 4], ebx
    jmp .done

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
.killed:
    mov eax, 1                                ; it can't continue: the scheduler moves on
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
    jb .readable
    cmp esi, [phi_task_heap_end + ecx * 4]     ; its heap, right above the stack
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

; ---- an exception in a user program: eax = saved state. A program that catches errors
; (it said where, with system call 10) goes there for the ones it can catch, to be thrown:
; dividing by zero, memory that isn't there, what the CPU refused. Otherwise it's stopped.
phi_task_fault:
    mov ebp, eax
    mov ecx, [phi_current_task]
    mov edx, [phi_task_fault_entry + ecx * 4]
    test edx, edx
    jz .stop
    mov eax, [ebp + F_VECTOR]
    mov ebx, ERROR_DIVIDE
    test eax, eax
    jz .send
    mov ebx, ERROR_ACCESS
    cmp eax, 13
    je .send
    cmp eax, 14
    jne .stop
    mov eax, cr2
    cmp eax, 4096
    jae .send
    mov ebx, ERROR_EMPTY
.send:
    ; not when its stack has run off its end, or the entry itself was stopped: the throw
    ; would only be stopped again
    mov eax, [ebp + F_EIP + 12]               ; its esp
    cmp eax, [phi_task_stack_bottom + ecx * 4]
    jbe .stop
    cmp eax, USER_TOP
    ja .stop
    cmp [ebp + F_EIP], edx
    je .stop
    mov eax, [ebp + F_EIP]
    mov [ebp + F_ECX], eax                    ; where it happened, for its line
    mov [ebp + F_EIP], edx
    mov [ebp + F_EBX], ebx
    ret
.stop:
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

; ---- programs on every core (Process.UseAllCores), with one kernel lock
;
; User programs run on any core at once, but kernel code (interrupts, system calls, and
; the kernel's own code, task 0) runs on one core at a time: it holds the kernel lock,
; a ticket lock, so cores get it in the order they asked. Each core counts how deep it
; is in kernel code (phi_core_depth: the kernel's own code is 1, each interrupt adds 1)
; and holds the lock while that isn't 0. Whoever holds it sets phi_current_task to its
; core's task, so everything that reads phi_current_task stays as it was.

; eax = this core's number (0 for the first; always 0 until programs share the cores)
phi_this_core:
    xor eax, eax
    cmp dword [phi_enter_hook], 0
    je .first
    mov eax, [LOCAL_APIC + 0x20]
    shr eax, 24
    movzx eax, byte [phi_core_of_apic + eax]
.first:
    ret

; eax = this core's number, from its block (fs, in kernel code), which is quicker to read
; than its local APIC's ID
phi_fs_core:
    mov eax, [fs:PHI_CORE_SELF]
    sub eax, phi_core_blocks
    shr eax, 10                               ; (PHI_CORE_BLOCK bytes each)
    ret

; with interrupts off: into the kernel (the lock, unless this core holds it); changes eax, ecx
phi_kernel_enter:
    call phi_this_core
    mov ecx, eax
    cmp dword [phi_core_depth + ecx * 4], 0
    jne .deeper
    call phi_kernel_take
    mov eax, [phi_core_task + ecx * 4]
    mov [phi_current_task], eax
.deeper:
    inc dword [phi_core_depth + ecx * 4]
    ret

; with interrupts off: out of the kernel (the lock given back once this core is out); changes eax, ecx
phi_kernel_leave:
    call phi_this_core
    dec dword [phi_core_depth + eax * 4]
    jnz .held
    lock inc dword [phi_kernel_serving]
.held:
    ret

; wait for the kernel lock (a ticket, then its turn); keeps ecx
phi_kernel_take:
    mov eax, 1
    lock xadd [phi_kernel_next], eax
.wait:
    cmp [phi_kernel_serving], eax
    je .taken
    pause
    jmp .wait
.taken:
    ret

; sti; hlt for kernel code that waits (OS_Sleep, OS_Idle), with the lock let go meanwhile
; and taken back after; keeps ebx, esi and edi
phi_kernel_halt_unlocked:
    cli
    call phi_this_core
    mov ecx, eax
    mov eax, [phi_core_depth + ecx * 4]
    test eax, eax
    jz .plain                                 ; (kernel code on another core without the lock: Cores.Run)
    push eax
    mov dword [phi_core_depth + ecx * 4], 0
    lock inc dword [phi_kernel_serving]
    sti
    hlt                                       ; (an interrupt here takes the lock for itself)
    cli
    call phi_this_core
    mov ecx, eax
    call phi_kernel_take
    pop dword [phi_core_depth + ecx * 4]
    mov eax, [phi_core_task + ecx * 4]
    mov [phi_current_task], eax
    sti
    ret
.plain:
    sti
    hlt
    ret

; a system call with a lock of its own (a range of numbers that phi_own_add registered:
; the network's, net.calls, and the files', proc.process) is answered by that range's
; method (number, then the saved ebx, ecx, edx and esi; its result in eax) without the
; kernel lock, so calls on other cores, and the rest of the kernel, don't wait for it (nor
; it for them); from phi_interrupt_common, with interrupts off and [esp + 4] the saved
; state. eax = 1 if it was one of those (answered), else 0, with the other registers kept.
phi_own_call:
    push ebp
    lea ebp, [esp + 8]
    push ecx
    push edx
    xor ecx, ecx
.find:
    cmp ecx, [phi_own_ranges]
    jae .not_own
    mov eax, [ebp + F_EAX]
    mov edx, ecx
    imul edx, edx, 12
    sub eax, [phi_own_table + edx]
    cmp eax, [phi_own_table + edx + 4]
    jb .found
    inc ecx
    jmp .find
.found:
    push dword [phi_own_table + edx + 8]      ; its method
    ; (from a program another core has stopped: not made, as phi_syscall does)
    call OS_CurrentTask
    cmp dword [phi_task_state + eax * 4], TASK_EXITED
    jae .refused
    push dword [fs:PHI_CORE_CATCH]
    mov dword [fs:PHI_CORE_CATCH], 0          ; an error in it isn't caught by what it interrupted
    push dword [ebp + F_EAX]                  ; (PHI's arguments go first to last)
    push dword [ebp + F_EBX]
    push dword [ebp + F_ECX]
    push dword [ebp + F_EDX]
    push dword [ebp + F_ESI]
    call [esp + 24]                           ; (past the arguments and the catch)
    add esp, 20
    pop dword [fs:PHI_CORE_CATCH]
    add esp, 4
    mov [ebp + F_EAX], eax
    mov eax, 1
    jmp .left
.refused:
    add esp, 4
    mov dword [ebp + F_EAX], -1
    mov eax, 1
    jmp .left
.not_own:
    xor eax, eax
.left:
    pop edx
    pop ecx
    pop ebp
    ret

; system calls eax to eax + ecx - 1 are answered by the method at edx, without the kernel
; lock (phi_own_call): for a part of the kernel with a lock of its own. At most
; OWN_RANGES of them; registering a range again changes its method. Keeps the registers.
phi_own_add:
    push ebx
    push esi
    xor ebx, ebx
.look:
    cmp ebx, [phi_own_ranges]
    jae .new
    mov esi, ebx
    imul esi, esi, 12
    cmp [phi_own_table + esi], eax
    je .set
    inc ebx
    jmp .look
.new:
    cmp ebx, OWN_RANGES
    jae .full
    mov esi, ebx
    imul esi, esi, 12
    mov [phi_own_table + esi], eax
.set:
    mov [phi_own_table + esi + 4], ecx
    mov [phi_own_table + esi + 8], edx
    cmp ebx, [phi_own_ranges]
    jb .full
    inc dword [phi_own_ranges]                ; (counted once the entry is whole)
.full:
    pop esi
    pop ebx
    ret

; let the cores waiting for the lock have it, then take it back (the kernel's OS_Yield)
phi_kernel_let_in:
    cmp dword [phi_enter_hook], 0
    je .alone
    pushfd
    cli
    call phi_this_core
    mov ecx, eax
    push dword [phi_core_depth + ecx * 4]
    mov dword [phi_core_depth + ecx * 4], 0
    lock inc dword [phi_kernel_serving]
    pause
    call phi_kernel_take
    pop dword [phi_core_depth + ecx * 4]
    mov eax, [phi_core_task + ecx * 4]
    mov [phi_current_task], eax
    popfd
.alone:
    ret

; the kernel's own code (task 0, on the first core) starts sharing: eax = the count for
; the other cores' timers (their local APICs', for TIMESLICE ms). The other cores join
; when proc.cores wakes them (phi_core_join).
phi_share_cores:
    pushfd
    cli
    mov [phi_tick_count], eax
    mov eax, [phi_current_task]
    mov [phi_core_task], eax
    mov dword [phi_task_core], 1              ; task 0 runs on core 0
    call phi_kernel_take
    mov dword [phi_core_depth], 1             ; the kernel's own code holds the lock
    mov ecx, TICK_VECTOR
    mov eax, phi_isr_tick
    call phi_set_gate
    mov dword [phi_halt_hook], phi_kernel_halt_unlocked
    mov dword [phi_core_join_hook], phi_core_join
    mov dword [phi_leave_hook], phi_kernel_leave
    mov dword [phi_enter_hook], phi_kernel_enter
    popfd
    ret

; another core joins (from proc.cores, ebx = its number): its own TSS and block, its
; local APIC's timer to take programs back, then its idle loop, which the timer
; interrupts to run programs
phi_core_join:
    cli
    lea eax, [PHI_CORE_SELECTOR + ebx * 8]
    mov fs, ax                                ; its block (proc.cores set it too)
    lea eax, [TSS_SELECTOR + ebx * 8]
    ltr ax
    lea eax, [MAX_TASKS + ebx]
    mov [phi_core_task + ebx * 4], eax        ; its idle loop, to start with
    mov dword [LOCAL_APIC + 0x3E0], 3         ; the timer counts every 16 bus clocks
    mov dword [LOCAL_APIC + 0x320], 0x20000 | TICK_VECTOR   ; again and again
    mov eax, [phi_tick_count]
    mov [LOCAL_APIC + 0x380], eax
    fninit
.idle:
    sti
    hlt
    jmp .idle

; another core's timer: it can take the CPU back from its program
phi_isr_tick:
    mov dword [LOCAL_APIC + 0xB0], 0          ; end of interrupt
    push dword 0
    push dword TICK_VECTOR
    jmp phi_interrupt_common

; ---- kernel threads (proc.threads): a method run as a task of its own, in ring 0 on any
; core, without the kernel lock, so threads run at the same time on several cores (once
; programs share them). The timer takes the CPU back from a thread as from a program.

; eax = the method (a method<int>), ecx = the value it's given, edx = the top of its own
; stack -> eax = the thread's task number, or -1 if every task is taken
phi_thread_start:
    push ebx
    push esi
    push edi
    pushfd
    cli
    mov esi, eax
    cmp dword [phi_enter_hook], 0
    je .alone
    push ecx
    push edx
    call phi_kernel_enter                     ; (the task table is the scheduler's)
    pop edx
    pop ecx
.alone:
    mov dword [phi_preempt_hook], phi_thread_preemptible
    mov ebx, 1
.find:
    cmp dword [phi_task_state + ebx * 4], TASK_FREE
    je .found
    inc ebx
    cmp ebx, MAX_TASKS
    jb .find
    mov eax, -1
    jmp .out
.found:
    ; on its stack: the method and its value for phi_thread_main, and below them the state
    ; the scheduler resumes (an iretd within ring 0: no user esp and ss)
    sub edx, 8
    mov [edx], esi
    mov [edx + 4], ecx
    mov edi, edx
    sub edi, F_SIZE - 8
    push edi
    mov ecx, (F_SIZE - 8) / 4
    xor eax, eax
    rep stosd
    pop edi
    mov dword [edi + 0], KERNEL_DATA          ; gs
    mov dword [edi + 4], KERNEL_DATA          ; fs
    mov dword [edi + 8], KERNEL_DATA          ; es
    mov dword [edi + 12], KERNEL_DATA         ; ds
    mov dword [edi + F_EIP], phi_thread_main
    mov dword [edi + F_EIP + 4], KERNEL_CODE
    mov dword [edi + F_EIP + 8], 0x202        ; interrupts on
    mov [phi_task_esp + ebx * 4], edi
    mov eax, [phi_task_cr3]
    mov [phi_task_cr3 + ebx * 4], eax         ; the kernel's address space
    mov [phi_task_kstack + ebx * 4], edx
    xor eax, eax
    mov [phi_task_image_end + ebx * 4], eax
    mov [phi_task_stack_bottom + ebx * 4], eax
    mov [phi_task_heap_end + ebx * 4], eax
    mov [phi_task_exit + ebx * 4], eax
    mov byte [phi_task_fpu_fresh + ebx], 1
    mov byte [phi_task_thread + ebx], 1
    mov dword [phi_task_catch + ebx * 4], 0
    mov dword [phi_task_unwind + ebx * 4], 0
    mov eax, [phi_current_task]
    mov [phi_task_parent + ebx * 4], eax
    mov dword [phi_task_state + ebx * 4], TASK_READY
    mov eax, ebx
.out:
    cmp dword [phi_enter_hook], 0
    je .done
    push eax
    call phi_kernel_leave
    pop eax
.done:
    popfd
    pop edi
    pop esi
    pop ebx
    ret

; where a thread starts: [esp] = its method, [esp + 4] = its value (which the method finds
; as its argument); when the method returns, the thread has finished
phi_thread_main:
    pop eax
    call eax
    cli
    call phi_this_core
    mov ecx, [phi_core_task + eax * 4]        ; this thread (phi_current_task is the lock holder's)
    mov dword [phi_task_state + ecx * 4], TASK_EXITED
    int YIELD_VECTOR                          ; it isn't ready: the scheduler won't come back
.finished:
    hlt
    jmp .finished

; eax = 1 if the task the timer interrupted is a kernel thread in its own code (not in
; the middle of kernel code: only this interrupt deep), so the timer may put it aside
phi_thread_preemptible:
    call phi_this_core
    mov ecx, [phi_core_task + eax * 4]
    cmp ecx, MAX_TASKS
    jae .no
    cmp byte [phi_task_thread + ecx], 0
    je .no
    cmp dword [phi_enter_hook], 0
    je .yes                                   ; (one core and no lock: a thread is never in kernel code)
    cmp dword [phi_core_depth + eax * 4], 1
    jne .no
.yes:
    mov eax, 1
    ret
.no:
    xor eax, eax
    ret

; ---- for PHI code in the kernel

OS_Yield:
    int YIELD_VECTOR
    call phi_kernel_let_in      ; (once programs share the cores: their system calls get a turn)
    ret

; give the CPU to the programs; if none of them can run when it comes back (all
; waiting, sleeping or gone), halt until the next interrupt (the timer, a key, the
; mouse) instead of coming straight back, so a kernel waiting on them doesn't spin
OS_Idle:
    int YIELD_VECTOR
    mov ecx, 1
.task:
    cmp dword [phi_task_state + ecx * 4], TASK_READY
    jne .not_ready
    cmp dword [phi_task_core + ecx * 4], 0
    je .done                ; ready, and no other core is running it: it's this core's to run
.not_ready:
    inc ecx
    cmp ecx, MAX_TASKS
    jb .task
    call OS_SetupInteruptTimer  ; so there's always a next interrupt (it starts only once)
    call phi_kernel_halt    ; sti; hlt (which can't miss an interrupt in between), the kernel lock let go
.done:
    ret

; (this core's task: phi_current_task is the kernel lock holder's, which a system call
; answered without it, phi_own_call, doesn't hold)
OS_CurrentTask:
    mov eax, [phi_current_task]
    cmp dword [phi_enter_hook], 0
    je .alone
    call phi_fs_core
    mov eax, [phi_core_task + eax * 4]
.alone:
    ret

; arg: task -> eax = its state (0 free, 1 ready, 2 sleeping, 3 waiting for a key,
; 4 waiting for a child, 5 exited, 6 exited and collected by its parent)
OS_TaskState:
    mov eax, [esp + 4]
    and eax, MAX_TASKS - 1
    mov ecx, [phi_task_core + eax * 4]
    mov eax, [phi_task_state + eax * 4]
    cmp eax, TASK_EXITED
    jb .done
    test ecx, ecx
    jz .done
    mov eax, TASK_READY     ; killed, but another core is still running it until its next tick:
.done:                      ; its memory mustn't be freed yet
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
; args: task, end of its heap (its heap starts at USER_TOP)
OS_SetTaskHeapEnd:
    mov eax, [esp + 4]
    and eax, MAX_TASKS - 1
    mov ecx, [esp + 8]
    mov [phi_task_heap_end + eax * 4], ecx
    ret

OS_FreeTask:
    mov eax, [esp + 4]
    and eax, MAX_TASKS - 1
    jz .kernel
    mov dword [phi_task_state + eax * 4], TASK_FREE
.kernel:
    ret

; arg: task -> eax = 1 if it was stopped (with exit code -1). The kernel, the task
; doing the killing, and tasks that have already finished can't be killed.
OS_KillTask:
    mov eax, [esp + 4]
    cmp eax, MAX_TASKS
    jae .no
    test eax, eax
    jz .no
    cmp eax, [phi_current_task]
    je .no
    mov ecx, [phi_task_state + eax * 4]
    cmp ecx, TASK_READY
    jb .no
    cmp ecx, TASK_EXITED
    jae .no
    mov dword [phi_task_exit + eax * 4], -1
    mov dword [phi_task_state + eax * 4], TASK_EXITED
    mov eax, 1
    ret
.no:
    xor eax, eax
    ret

; restart the machine, through the keyboard controller
OS_Reboot:
    cli
.wait:
    in al, 0x64
    test al, 2
    jnz .wait
    mov al, 0xFE
    out 0x64, al
.halt:
    hlt
    jmp .halt

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
phi_task_heap_end:     times MAX_TASKS dd 0
phi_task_fpu_fresh:    times MAX_TASKS db 0
FPU_STATE              equ 108                 ; what fnsave keeps
phi_task_fpu:          times MAX_TASKS * FPU_STATE db 0
phi_task_parent:       times MAX_TASKS dd 0
phi_task_exit:         times MAX_TASKS dd 0
phi_task_wake:         times MAX_TASKS dd 0
phi_task_wait:         times MAX_TASKS dd 0
phi_task_core:         times MAX_TASKS dd 0     ; the core running it, plus 1 (0: none)
phi_task_thread:       times MAX_TASKS db 0     ; 1 for a kernel thread (proc.threads)
phi_task_catch:        times MAX_TASKS dd 0     ; its catches and methods' records, while another task runs
phi_task_unwind:       times MAX_TASKS dd 0
phi_task_fault_entry:  times MAX_TASKS dd 0     ; where a program catches CPU exceptions (system call 10), or 0

; system calls with a lock of their own (phi_own_call): first, how many, method; each 12 bytes
phi_own_ranges:        dd 0
phi_own_table:         times OWN_RANGES * 3 dd 0

; programs on every core (phi_share_cores)
phi_kernel_next:       dd 0                    ; the kernel lock: the next ticket
phi_kernel_serving:    dd 0                    ; and whose turn it is
phi_core_depth:        times MAX_CORES dd 0    ; how deep each core is in kernel code
phi_core_task:         times MAX_CORES dd 0    ; the task each core is running
phi_idle_esp:          times MAX_CORES dd 0    ; each other core's idle loop, while a program runs there
phi_last_picked:       dd 0                    ; where an idle core starts looking
phi_tick_count:        dd 0                    ; the other cores' timers

align 4
phi_tss:
    times MAX_CORES * TSS_SIZE db 0
