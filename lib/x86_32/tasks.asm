; provides: OS_StartMultitasking OS_CreateUserTask OS_SetTaskHeapEnd OS_TaskState OS_TaskExitCode OS_TaskParent OS_FreeTask OS_KillTask OS_Reboot OS_Yield OS_Idle OS_CurrentTask OS_SetSyscallHandler OS_SyscallFrame phi_share_cores phi_core_runs
; requires: phi_set_gate phi_idt phi_scheduler phi_syscall_hook phi_user_fault phi_cores_running phi_core_of_apic phi_core_tick phi_switched OS_SetupInteruptTimer OS_GetTicks OS_KeyAvailable OS_SetupKeyboardInterupt phi_take_key phi_print phi_print_serial phi_print_int phi_print_hex
;
; Tasks for 32-bit kernels: user programs in ring 3, each with its own page
; directory and kernel stack, and the kernel itself as task 0.
;
;   - the timer takes the CPU away from a user program; the kernel gives it up
;     itself (OS_Yield, or when its main code ends)
;   - system calls are int 0x80: eax = number, ebx = argument, result in eax
;   - a program that faults is stopped with a message; the kernel carries on
;
; Several cores (proc.cores, Cores.SharePrograms): every core runs programs, each with its
; own current task, TSS and timer (its local APIC's), and a task runs on one core at a time.
; The kernel's own task runs only on the first core; another core with nothing to run
; rests (halted) until its timer looks again. Kernel code still runs on one core at a
; time: the kernel lock (phi_bkl) is the kernel task's while it runs, and a system call,
; a fault or the scheduler on any core takes it first. So programs' own code runs on every
; core at once, and the kernel's code, as before, never twice at the same time.
;
; Every switch happens at the end of an interrupt: the saved state on the task's
; kernel stack (see interrupts.asm) is what the next iretd resumes.
MAX_TASKS     equ 16
MAX_CORES     equ 8
IDLE          equ 0xFF          ; a core's current task while it has none
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
TSS_SELECTOR  equ 0x28          ; core n's is TSS_SELECTOR + 8 * n
TSS_SIZE      equ 104
YIELD_VECTOR  equ 48
SYSCALL_VECTOR equ 0x80
CORE_TIMER_VECTOR equ 0xF1
APIC          equ 0xFEE00000

USER_BASE     equ 0x40000000
USER_TOP      equ 0x40400000

; offsets in the saved state
F_EBX equ 32
F_EDX equ 36
F_ECX equ 40
F_EAX equ 44
F_VECTOR equ 48
F_EIP equ 56
F_CS  equ 60
F_SIZE equ 76                   ; everything, including the user esp and ss

OS_StartMultitasking:
    cmp byte [phi_tasks_started], 0
    jne .done
    mov byte [phi_tasks_started], 1
    cli

    ; a GDT with user segments and a TSS for each core (which holds the kernel stack for ring 3)
    xor ecx, ecx
.tss:
    imul edx, ecx, TSS_SIZE
    lea eax, [phi_tss + edx]
    mov dword [eax + 8], KERNEL_DATA          ; ss0
    mov word [eax + 102], TSS_SIZE            ; no I/O permission bitmap
    lea edx, [phi_gdt_tss + ecx * 8]
    mov word [edx], TSS_SIZE - 1
    mov [edx + 2], ax
    shr eax, 16
    mov [edx + 4], al
    mov byte [edx + 5], 10001001b             ; present, a 32-bit TSS
    mov byte [edx + 6], 0
    mov [edx + 7], ah
    inc ecx
    cmp ecx, MAX_CORES
    jb .tss
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

    ; task 0 is the kernel, running on the first core, with the kernel lock
    mov dword [phi_task_state], TASK_READY
    mov eax, cr3
    mov [phi_task_cr3], eax
    mov byte [phi_task_running], 1
    mov dword [phi_bkl_owner], 1
    mov dword [phi_bkl_depth], 1

    mov dword [phi_scheduler], phi_schedule
    mov dword [phi_switched], phi_task_switched
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

phi_isr_core_timer:
    push dword 0
    push dword CORE_TIMER_VECTOR
    jmp phi_interrupt_common

; ---- which core this is, and its task

; -> ecx = this core's current task (IDLE if it has none), edx = this core's number;
; keeps every other register
phi_get_current:
    xor edx, edx
    cmp dword [phi_cores_running], 0
    je .known
    mov edx, [APIC + 0x20]
    shr edx, 24
    movzx edx, byte [phi_core_of_apic + edx]
.known:
    mov ecx, [phi_core_task + edx * 4]
    ret

; ---- the kernel lock: one core at a time in kernel code

; take it (again, if this core has it already); keeps every register. While another core
; has it, interrupts come in (a timer tick, the network card), but the scheduler doesn't
; run on this core in the meantime.
phi_bkl_take:
    push eax
    push ecx
    push edx
    call phi_get_current
    lea ecx, [edx + 1]                        ; the owner: this core's number + 1
    cmp [phi_bkl_owner], ecx
    je .again
.try:
    xor eax, eax
    lock cmpxchg [phi_bkl_owner], ecx
    jz .again
    sti
    pause
    cli
    jmp .try
.again:
    inc dword [phi_bkl_depth]
    pop edx
    pop ecx
    pop eax
    ret

; give one taking of it back; keeps every register
phi_bkl_give:
    dec dword [phi_bkl_depth]
    jnz .held
    mov dword [phi_bkl_owner], 0
.held:
    ret

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
    mov byte [phi_task_running + ecx], 0
    push ecx
    call phi_get_current
    mov eax, ecx                                 ; its parent: the task starting it
    pop ecx
    mov [phi_task_parent + ecx * 4], eax
    mov dword [phi_task_exit + ecx * 4], 0
    mov dword [phi_task_state + ecx * 4], TASK_READY
    mov eax, ecx
    pop ebp
    ret

; ---- the scheduler: eax = saved state of the current task -> eax = the one to resume
phi_schedule:
    mov ebp, eax
    call phi_bkl_take
    call phi_get_current                      ; ecx = the task (or IDLE), edx = the core
    mov byte [phi_core_busy + edx], 1
    cmp ecx, IDLE
    jne .task
    mov [phi_core_idle_esp + edx * 4], ebp
    jmp .saved
.task:
    mov [phi_task_esp + ecx * 4], ebp
.saved:
    push edx
    call phi_wake_tasks
    pop edx
    mov ebx, [phi_core_task + edx * 4]        ; the task it was running (or IDLE)

    ; round robin: the next ready task after that one, that no other core is running (the
    ; kernel's only on the first core); the same one again last
    mov edi, ebx
    cmp edi, IDLE
    jne .search
    xor edi, edi
.search:
    mov ecx, 1
.next:
    lea esi, [edi + ecx]
    and esi, MAX_TASKS - 1
    cmp dword [phi_task_state + esi * 4], TASK_READY
    jne .skip
    movzx eax, byte [phi_task_running + esi]  ; no other core running it (this one may)
    test eax, eax
    jz .free
    dec eax
    cmp eax, edx
    jne .skip
.free:
    test esi, esi
    jnz .found
    test edx, edx
    jz .found
.skip:
    inc ecx
    cmp ecx, MAX_TASKS
    jbe .next
    test edx, edx
    jnz .rest
    xor esi, esi                              ; the first core: the kernel is always ready

.found:
    ; each task has its own FPU registers (floating point numbers): the old task's are
    ; saved (which also clears them), and the new one's put back, or left clean for a new task
    cmp esi, ebx
    je .same_fpu
    cmp ebx, IDLE
    je .load_fpu
    imul eax, ebx, FPU_STATE
    fnsave [phi_task_fpu + eax]
.load_fpu:
    cmp byte [phi_task_fpu_fresh + esi], 0
    je .restore_fpu
    mov byte [phi_task_fpu_fresh + esi], 0
    fninit
    jmp .same_fpu
.restore_fpu:
    imul eax, esi, FPU_STATE
    frstor [phi_task_fpu + eax]
.same_fpu:
    ; how many times each core has taken up a program (for proc.cores' Cores.Ran)
    test esi, esi
    jz .counted
    cmp esi, ebx
    je .counted
    inc dword [phi_core_runs + edx * 4]
.counted:
    mov [phi_core_task + edx * 4], esi
    lea eax, [edx + 1]
    mov [phi_task_running + esi], al
    mov [phi_core_left + edx * 4], ebx        ; phi_task_switched lets that one go
    mov byte [phi_core_switching + edx], 1
    imul eax, edx, TSS_SIZE
    mov ecx, [phi_task_kstack + esi * 4]
    mov [phi_tss + eax + 4], ecx              ; esp0: where this core's ring 3 interrupts land
    mov eax, [phi_task_cr3 + esi * 4]
    mov ecx, cr3
    cmp eax, ecx
    je .same_space
    mov cr3, eax
.same_space:
    mov byte [phi_core_busy + edx], 0
    mov eax, [phi_task_esp + esi * 4]
    ret

.rest:
    ; another core with nothing to run: back to its halted loop until its timer looks again
    cmp ebx, IDLE
    je .rested_fpu
    imul eax, ebx, FPU_STATE
    fnsave [phi_task_fpu + eax]
.rested_fpu:
    ; the kernel's address space: the program's goes back to the kernel when the program
    ; ends, and its frames get used again while this core rests
    mov eax, [phi_task_cr3]
    mov ecx, cr3
    cmp eax, ecx
    je .rest_space
    mov cr3, eax
.rest_space:
    mov dword [phi_core_task + edx * 4], IDLE
    mov [phi_core_left + edx * 4], ebx
    mov byte [phi_core_switching + edx], 1
    mov byte [phi_core_busy + edx], 0
    mov eax, [phi_core_idle_esp + edx * 4]
    ret

; right after a switch, on the new stack (interrupts.asm's phi_switched): the task this
; core left is free for another core now, and the kernel lock is the kernel task's if
; that's what runs here, or nobody's
phi_task_switched:
    call phi_get_current                      ; ecx = the task now (or IDLE), edx = the core
    cmp byte [phi_core_switching + edx], 0
    je .done                                  ; no switch (a tick that left a system call alone)
    mov byte [phi_core_switching + edx], 0
    mov eax, [phi_core_left + edx * 4]
    cmp eax, IDLE
    je .lock
    cmp eax, ecx
    je .lock                                  ; the same task again
    mov byte [phi_task_running + eax], 0
.lock:
    test ecx, ecx
    jnz .give
    mov dword [phi_bkl_depth], 1
    ret
.give:
    mov dword [phi_bkl_depth], 0
    mov dword [phi_bkl_owner], 0
.done:
    ret

; a tick of another core's timer (interrupts.asm): eax = saved state -> eax = the state to
; resume. It takes the CPU from a program, or wakes the core's halted loop to look for one;
; it leaves a system call (kernel code, in ring 0) to finish.
phi_core_timer_tick:
    push eax
    call phi_get_current
    pop eax
    cmp byte [phi_core_busy + edx], 0
    jne .stay
    test byte [eax + F_CS], 3
    jnz phi_schedule
    cmp ecx, IDLE
    je phi_schedule
.stay:
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
    cmp byte [phi_task_running + edx], 0      ; stopped, but still on another core for now
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

; ---- system calls: eax = saved state -> eax = 1 if the task can't continue now (still
; holding the kernel lock, which the scheduler sorts out)
phi_syscall:
    call phi_bkl_take
    mov ebp, eax
    call phi_get_current
    mov [phi_core_syscall_frame + edx * 4], ebp
    mov ebx, [ebp + F_EBX]
    mov eax, [ebp + F_EAX]
    cmp eax, 10
    jae .extension
    jmp [.table + eax * 4]
.table:
    dd .exit, .write, .debug, .read_key, .sleep, .ticks, .getpid, .yield, .key_available, .wait

.exit:
    call phi_get_current
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
    call phi_get_current
    mov dword [phi_task_state + ecx * 4], TASK_WAIT_KEY
    mov eax, 1
    ret

.sleep:
    call OS_GetTicks
    add eax, ebx
    call phi_get_current
    mov [phi_task_wake + ecx * 4], eax
    mov dword [phi_task_state + ecx * 4], TASK_SLEEPING
    mov eax, 1
    ret

.ticks:
    call OS_GetTicks
    mov [ebp + F_EAX], eax
    jmp .done

.getpid:
    call phi_get_current
    mov [ebp + F_EAX], ecx
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
    call phi_get_current
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
    cmp byte [phi_task_running + ebx], 0      ; stopped, but still on another core for now
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
    call phi_bkl_give
    xor eax, eax
    ret

; is ebx a zero-terminated string inside the current program's memory?
; carry set if not
phi_user_string:
    call phi_get_current
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

; ---- an exception in a user program: eax = saved state. Stop the program.
phi_task_fault:
    call phi_bkl_take
    mov ebp, eax
    mov esi, .stopped
    call phi_print
    call phi_get_current
    mov eax, ecx
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
    call phi_get_current
    mov dword [phi_task_exit + ecx * 4], -1
    mov dword [phi_task_state + ecx * 4], TASK_EXITED
    ret
.stopped:    db 13, 10, 'PHI: program ', 0
.stopped_by: db ' stopped by exception ', 0
.at:         db ' at ', 0
.address:    db ', address ', 0
.newline:    db 13, 10, 0

; ---- the other cores taking programs too (proc.cores' Cores.SharePrograms, on the first
; core, once the cores have started): eax = the counts of the local APIC timer in a
; millisecond, as the first core measured it. The other cores come to phi_core_join.
phi_share_cores:
    mov [phi_core_timer_count], eax
    ; the kernel's address space, as it is now (multitasking may have started before
    ; paging did, when cr3 meant nothing yet): resting cores go back to it
    mov eax, cr3
    mov [phi_task_cr3], eax
    mov ecx, CORE_TIMER_VECTOR
    mov eax, phi_isr_core_timer
    call phi_set_gate
    mov dword [phi_core_tick], phi_core_timer_tick
    ret

; on another core (ebx = its number), with interrupts off: from now on it runs programs
phi_core_join:
    lgdt [phi_gdt_descriptor]
    jmp KERNEL_CODE:.reload
.reload:
    mov ax, KERNEL_DATA
    mov ds, ax
    mov es, ax
    mov fs, ax
    mov gs, ax
    mov ss, ax
    lea eax, [ebx * 8 + TSS_SELECTOR]
    ltr ax
    mov dword [phi_core_task + ebx * 4], IDLE
    ; its timer, every millisecond: divided by 16, periodic, vector 0xF1
    mov dword [APIC + 0x3E0], 3
    mov dword [APIC + 0x320], 0x20000 | CORE_TIMER_VECTOR
    mov eax, [phi_core_timer_count]
    mov [APIC + 0x380], eax
.rest:
    sti
    hlt
    jmp .rest

; ---- for PHI code in the kernel

OS_Yield:
    int YIELD_VECTOR
    ret

; give the CPU to the programs; if none of them can run when it comes back (all
; waiting, sleeping or gone), halt until the next interrupt (the timer, a key, the
; mouse) instead of coming straight back, so a kernel waiting on them doesn't spin
OS_Idle:
    int YIELD_VECTOR
    mov ecx, 1
.task:
    cmp dword [phi_task_state + ecx * 4], TASK_READY
    je .done
    inc ecx
    cmp ecx, MAX_TASKS
    jb .task
    call OS_SetupInteruptTimer  ; so there's always a next interrupt (it starts only once)
    sti
    hlt                     ; sti; hlt can't miss an interrupt in between
.done:
    ret

OS_CurrentTask:
    call phi_get_current
    mov eax, ecx
    ret

; arg: task -> eax = its state (0 free, 1 ready, 2 sleeping, 3 waiting for a key,
; 4 waiting for a child, 5 exited, 6 exited and collected by its parent). One that's
; stopped but still on another core until its next tick still counts as running (1), so
; its memory isn't given back under it.
OS_TaskState:
    mov ecx, [esp + 4]
    and ecx, MAX_TASKS - 1
    mov eax, [phi_task_state + ecx * 4]
    cmp eax, TASK_EXITED
    jb .known
    cmp byte [phi_task_running + ecx], 0
    je .known
    mov eax, TASK_READY
.known:
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

; args: task, end of its heap (its heap starts at USER_TOP)
OS_SetTaskHeapEnd:
    mov eax, [esp + 4]
    and eax, MAX_TASKS - 1
    mov ecx, [esp + 8]
    mov [phi_task_heap_end + eax * 4], ecx
    ret

; arg: task: its slot can be used again (its memory must already be freed)
OS_FreeTask:
    mov eax, [esp + 4]
    and eax, MAX_TASKS - 1
    jz .kernel
    mov dword [phi_task_state + eax * 4], TASK_FREE
.kernel:
    ret

; arg: task -> eax = 1 if it was stopped (with exit code -1). The kernel, the task
; doing the killing, and tasks that have already finished can't be killed. One running
; on another core stops at that core's next tick.
OS_KillTask:
    call phi_get_current
    mov eax, [esp + 4]
    cmp eax, MAX_TASKS
    jae .no
    test eax, eax
    jz .no
    cmp eax, ecx
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

; eax = the address of the saved state of the system call this core is handling
OS_SyscallFrame:
    call phi_get_current
    mov eax, [phi_core_syscall_frame + edx * 4]
    ret

phi_tasks_started:     db 0
phi_syscall_extension: dd 0
phi_bkl_owner:         dd 0                     ; the core that has the kernel lock, + 1 (0: nobody)
phi_bkl_depth:         dd 0
phi_core_timer_count:  dd 0
phi_core_task:         times MAX_CORES dd 0     ; each core's current task (the first core's starts as the kernel's)
phi_core_idle_esp:     times MAX_CORES dd 0     ; where each other core's halted loop was interrupted
phi_core_syscall_frame: times MAX_CORES dd 0
phi_core_runs:         times MAX_CORES dd 0
phi_core_busy:         times MAX_CORES db 0     ; 1 while the core is in the scheduler
phi_core_switching:    times MAX_CORES db 0     ; 1 from the scheduler's choice until phi_task_switched
phi_core_left:         times MAX_CORES dd 0     ; the task (or IDLE) each core is switching away from
phi_task_running:      times MAX_TASKS db 0     ; the core running each task, + 1 (0: none)
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
    times MAX_CORES dq 0                   ; 0x28 on: each core's TSS (filled in at startup)
phi_gdt_end:
phi_gdt_descriptor:
    dw phi_gdt_end - phi_gdt - 1
    dd phi_gdt

align 4
phi_tss:
    times MAX_CORES * TSS_SIZE db 0
