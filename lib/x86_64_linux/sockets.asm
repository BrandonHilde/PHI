; provides: OS_NetAddress OS_UdpOpen OS_UdpClose OS_UdpReceive OS_UdpSend OS_TcpBegin OS_TcpState OS_TcpSend OS_TcpTake OS_TcpClose OS_TcpListen OS_TcpAccept OS_TcpWaiting OS_TcpAddress OS_TcpStopListening
;
; The network for 64-bit Linux programs (net.sockets), answered by Linux's sockets as
; x86_32_linux's sockets.asm answers it: the same calls, kinds and checks, with the 64-bit
; system calls (arguments in rdi, rsi, rdx, r10, r8, r9). Arguments PHI pushes are 8
; bytes each; each call keeps rbx, rsi and rdi.
SYS_SOCK_CLOSE     equ 3
SYS_SOCK_IOCTL     equ 16
SYS_SOCKET         equ 41
SYS_CONNECT        equ 42
SYS_SENDTO         equ 44
SYS_RECVFROM       equ 45
SYS_BIND           equ 49
SYS_LISTEN         equ 50
SYS_GETSOCKNAME    equ 51
SYS_GETPEERNAME    equ 52
SYS_SETSOCKOPT     equ 54
SYS_GETSOCKOPT     equ 55
SYS_ACCEPT4        equ 288

AF_INET            equ 2
SOCK_STREAM        equ 1
SOCK_DGRAM         equ 2
SOCK_NONBLOCK      equ 0x800
SOCK_CLOEXEC       equ 0x80000
SOL_SOCKET         equ 1
SO_REUSEADDR       equ 2
SO_ERROR           equ 4
SO_RCVBUF          equ 8
MSG_PEEK           equ 2
MSG_DONTWAIT       equ 0x40
MSG_NOSIGNAL       equ 0x4000
FIONREAD           equ 0x541B
EAGAIN             equ 11
EINPROGRESS        equ 115

SOCKETS_MOST       equ 1024
KIND_UDP           equ 1
KIND_TCP           equ 2
KIND_LISTENER      equ 3
KIND_FAILED        equ 4
UDP_MOST           equ 1472
DATAGRAM_SIZE      equ 1536

; each call's frame, as in 32-bit code: a sockaddr_in, its length, a number
%define ADDR       rbp - 16
%define ADDR_LEN   rbp - 20
%define OPTION     rbp - 24
%define ARG1       rbp + 16
%define ARG2       rbp + 24
%define ARG3       rbp + 32
%define ARG4       rbp + 40
%macro PHI_SOCK_ENTER 0
    push rbp
    mov rbp, rsp
    sub rsp, 32
    push rbx
    push rsi
    push rdi
    push r12
%endmacro
%macro PHI_SOCK_LEAVE 0
    pop r12
    pop rdi
    pop rsi
    pop rbx
    mov rsp, rbp
    pop rbp
    ret
%endmacro

; the sockaddr_in at ADDR for address eax and port edx
phi_sock_address:
    mov word [ADDR], AF_INET
    xchg dl, dh
    mov [ADDR + 2], dx
    bswap eax
    mov [ADDR + 4], eax
    mov qword [ADDR + 8], 0
    mov dword [ADDR_LEN], 16
    ret

; eax = a new non-blocking socket of type esi, kept as kind bl; or -1
phi_sock_new:
    mov eax, SYS_SOCKET
    mov edi, AF_INET
    or esi, SOCK_NONBLOCK | SOCK_CLOEXEC
    xor edx, edx
    syscall
    test eax, eax
    js .failed
    cmp eax, SOCKETS_MOST
    jae .too_many
    lea rcx, [rel phi_socket_kinds]
    mov [rcx + rax], bl
    ret
.too_many:
    mov edi, eax
    mov eax, SYS_SOCK_CLOSE
    syscall
.failed:
    mov eax, -1
    ret

; carry clear if eax is a socket of kind bl (or, with bh, kind bh too)
phi_sock_is:
    cmp eax, SOCKETS_MOST
    jae .no
    lea rcx, [rel phi_socket_kinds]
    cmp [rcx + rax], bl
    je .yes
    test bh, bh
    jz .no
    cmp [rcx + rax], bh
    je .yes
.no:
    stc
    ret
.yes:
    clc
    ret

; close socket edi and forget it
phi_sock_close:
    lea rcx, [rel phi_socket_kinds]
    mov byte [rcx + rdi], 0
    mov eax, SYS_SOCK_CLOSE
    syscall
    ret

; bind socket edi to port edx on every address; eax = 0 or -errno
phi_sock_bind:
    xor eax, eax
    call phi_sock_address
    mov eax, SYS_BIND
    lea rsi, [ADDR]
    mov edx, 16
    syscall
    ret

; -> this machine's address (the one it would reach the internet from), or 127.0.0.1
OS_NetAddress:
    PHI_SOCK_ENTER
    mov r12d, 0x7F000001
    mov eax, SYS_SOCKET
    mov edi, AF_INET
    mov esi, SOCK_DGRAM | SOCK_CLOEXEC
    xor edx, edx
    syscall
    test eax, eax
    js .done
    mov ebx, eax
    mov eax, 0x08080808
    mov edx, 53
    call phi_sock_address
    mov eax, SYS_CONNECT
    mov edi, ebx
    lea rsi, [ADDR]
    mov edx, 16
    syscall
    test eax, eax
    jnz .close
    mov eax, SYS_GETSOCKNAME
    mov edi, ebx
    lea rsi, [ADDR]
    lea rdx, [ADDR_LEN]
    syscall
    test eax, eax
    jnz .close
    mov eax, [ADDR + 4]
    bswap eax
    test eax, eax
    jz .close
    mov r12d, eax
.close:
    mov eax, SYS_SOCK_CLOSE
    mov edi, ebx
    syscall
.done:
    mov eax, r12d
    PHI_SOCK_LEAVE

; args: port queue -> socket, or -1 if the port is taken
OS_UdpOpen:
    PHI_SOCK_ENTER
    mov esi, SOCK_DGRAM
    mov bl, KIND_UDP
    call phi_sock_new
    test eax, eax
    js .done
    mov r12d, eax
    mov edi, eax
    mov edx, [ARG1]
    call phi_sock_bind
    test eax, eax
    jnz .failed
    mov eax, [ARG2]
    imul eax, eax, DATAGRAM_SIZE
    mov [OPTION], eax
    mov eax, SYS_SETSOCKOPT
    mov edi, r12d
    mov esi, SOL_SOCKET
    mov edx, SO_RCVBUF
    lea r10, [OPTION]
    mov r8d, 4
    syscall
    mov eax, r12d
    jmp .done
.failed:
    mov edi, r12d
    call phi_sock_close
    mov eax, -1
.done:
    PHI_SOCK_LEAVE

; arg: socket -> 1, or 0 if it isn't one
OS_UdpClose:
    PHI_SOCK_ENTER
    mov eax, [ARG1]
    mov bx, KIND_UDP
    call phi_sock_is
    jc .not
    mov edi, eax
    call phi_sock_close
    mov eax, 1
    jmp .done
.not:
    xor eax, eax
.done:
    PHI_SOCK_LEAVE

; args: socket datagram -> 1 with the oldest datagram in it, or 0 if none is waiting
OS_UdpReceive:
    PHI_SOCK_ENTER
    mov eax, [ARG1]
    mov bx, KIND_UDP
    call phi_sock_is
    jc .none
    mov edi, eax
    mov r12, [ARG2]
    lea rsi, [r12 + 12]                 ; data
    mov edx, UDP_MOST
    mov r10d, MSG_DONTWAIT
    lea r8, [ADDR]
    mov dword [ADDR_LEN], 16
    lea r9, [ADDR_LEN]
    mov eax, SYS_RECVFROM
    syscall
    test eax, eax
    js .none
    mov [r12 + 8], eax                  ; length
    mov eax, [ADDR + 4]
    bswap eax
    mov [r12], eax                      ; from
    movzx eax, word [ADDR + 2]
    xchg al, ah
    mov [r12 + 4], eax                  ; from_port
    mov eax, 1
    jmp .done
.none:
    xor eax, eax
.done:
    PHI_SOCK_LEAVE

; args: socket | port << 16, to, data, length -> 1 if sent
OS_UdpSend:
    PHI_SOCK_ENTER
    movzx eax, word [ARG1]
    mov bx, KIND_UDP
    call phi_sock_is
    jc .not
    mov r12d, eax
    mov eax, [ARG2]
    movzx edx, word [ARG1 + 2]
    call phi_sock_address
    mov edi, r12d
    mov rsi, [ARG3]
    mov edx, [ARG4]
    mov r10d, MSG_DONTWAIT | MSG_NOSIGNAL
    lea r8, [ADDR]
    mov r9d, 16
    mov eax, SYS_SENDTO
    syscall
    test eax, eax
    js .not
    mov eax, 1
    jmp .done
.not:
    xor eax, eax
.done:
    PHI_SOCK_LEAVE

; args: to port -> a connection, still connecting, or -1
OS_TcpBegin:
    PHI_SOCK_ENTER
    mov esi, SOCK_STREAM
    mov bl, KIND_TCP
    call phi_sock_new
    test eax, eax
    js .done
    mov r12d, eax
    mov eax, [ARG1]
    mov edx, [ARG2]
    call phi_sock_address
    mov eax, SYS_CONNECT
    mov edi, r12d
    lea rsi, [ADDR]
    mov edx, 16
    syscall
    test eax, eax
    jz .begun
    cmp eax, -EINPROGRESS
    je .begun
    mov edi, r12d
    call phi_sock_close
    mov eax, -1
    jmp .done
.begun:
    mov eax, r12d
.done:
    PHI_SOCK_LEAVE

; arg: connection -> 1 open, 0 still connecting, -1 failed (or broken)
OS_TcpState:
    PHI_SOCK_ENTER
    mov eax, [ARG1]
    mov bl, KIND_TCP
    mov bh, KIND_FAILED
    call phi_sock_is
    jc .failed
    mov r12d, eax
    lea rcx, [rel phi_socket_kinds]
    cmp byte [rcx + rax], KIND_FAILED
    je .failed
    mov dword [OPTION], 0
    mov dword [ADDR_LEN], 4
    mov eax, SYS_GETSOCKOPT
    mov edi, r12d
    mov esi, SOL_SOCKET
    mov edx, SO_ERROR
    lea r10, [OPTION]
    lea r8, [ADDR_LEN]
    syscall
    test eax, eax
    jnz .broke
    cmp dword [OPTION], 0
    jne .broke
    mov dword [ADDR_LEN], 16
    mov eax, SYS_GETPEERNAME
    mov edi, r12d
    lea rsi, [ADDR]
    lea rdx, [ADDR_LEN]
    syscall
    test eax, eax
    jnz .connecting
    mov eax, 1
    jmp .done
.connecting:
    xor eax, eax
    jmp .done
.broke:
    lea rcx, [rel phi_socket_kinds]
    mov byte [rcx + r12], KIND_FAILED
.failed:
    mov eax, -1
.done:
    PHI_SOCK_LEAVE

; args: connection data length -> the bytes taken (0 when Linux's buffer is full), or -1
OS_TcpSend:
    PHI_SOCK_ENTER
    mov eax, [ARG1]
    mov bx, KIND_TCP
    call phi_sock_is
    jc .broken
    mov edi, eax
    mov rsi, [ARG2]
    mov edx, [ARG3]
    mov r10d, MSG_DONTWAIT | MSG_NOSIGNAL
    xor r8d, r8d
    xor r9d, r9d
    mov eax, SYS_SENDTO
    syscall
    test eax, eax
    jns .done
    cmp eax, -EAGAIN
    jne .broken
    xor eax, eax
    jmp .done
.broken:
    mov eax, -1
.done:
    PHI_SOCK_LEAVE

; args: connection buffer max -> the bytes read, 0 if none yet, -2 once closed and read,
; -1 if it's broken
OS_TcpTake:
    PHI_SOCK_ENTER
    mov eax, [ARG1]
    mov bx, KIND_TCP
    call phi_sock_is
    jc .broken
    cmp dword [ARG3], 0
    je .none
    mov edi, eax
    mov rsi, [ARG2]
    mov edx, [ARG3]
    mov r10d, MSG_DONTWAIT
    xor r8d, r8d
    xor r9d, r9d
    mov eax, SYS_RECVFROM
    syscall
    test eax, eax
    jg .done
    jz .closed
    cmp eax, -EAGAIN
    jne .broken
.none:
    xor eax, eax
    jmp .done
.closed:
    mov eax, -2
    jmp .done
.broken:
    mov eax, -1
.done:
    PHI_SOCK_LEAVE

; arg: connection -> 1
OS_TcpClose:
    PHI_SOCK_ENTER
    mov eax, [ARG1]
    mov bl, KIND_TCP
    mov bh, KIND_FAILED
    call phi_sock_is
    jc .done
    mov edi, eax
    call phi_sock_close
.done:
    mov eax, 1
    PHI_SOCK_LEAVE

; args: port backlog -> a listener, or -1 if the port is taken
OS_TcpListen:
    PHI_SOCK_ENTER
    mov esi, SOCK_STREAM
    mov bl, KIND_LISTENER
    call phi_sock_new
    test eax, eax
    js .done
    mov r12d, eax
    mov dword [OPTION], 1
    mov eax, SYS_SETSOCKOPT
    mov edi, r12d
    mov esi, SOL_SOCKET
    mov edx, SO_REUSEADDR
    lea r10, [OPTION]
    mov r8d, 4
    syscall
    mov edi, r12d
    mov edx, [ARG1]
    call phi_sock_bind
    test eax, eax
    jnz .failed
    mov eax, SYS_LISTEN
    mov edi, r12d
    mov esi, [ARG2]
    syscall
    test eax, eax
    jnz .failed
    mov eax, r12d
    jmp .done
.failed:
    mov edi, r12d
    call phi_sock_close
    mov eax, -1
.done:
    PHI_SOCK_LEAVE

; arg: listener -> a connection that has come in, or -1 if none is waiting
OS_TcpAccept:
    PHI_SOCK_ENTER
    mov eax, [ARG1]
    mov bx, KIND_LISTENER
    call phi_sock_is
    jc .none
    mov edi, eax
    mov eax, SYS_ACCEPT4
    xor esi, esi
    xor edx, edx
    mov r10d, SOCK_NONBLOCK | SOCK_CLOEXEC
    syscall
    test eax, eax
    js .none
    cmp eax, SOCKETS_MOST
    jae .too_many
    lea rcx, [rel phi_socket_kinds]
    mov byte [rcx + rax], KIND_TCP
    jmp .done
.too_many:
    mov edi, eax
    mov eax, SYS_SOCK_CLOSE
    syscall
.none:
    mov eax, -1
.done:
    PHI_SOCK_LEAVE

; arg: connection -> the bytes that can be read now, or -1 once closed (and read) or broken
OS_TcpWaiting:
    PHI_SOCK_ENTER
    mov eax, [ARG1]
    mov bx, KIND_TCP
    call phi_sock_is
    jc .closed
    mov r12d, eax
    mov dword [OPTION], 0
    mov eax, SYS_SOCK_IOCTL
    mov edi, r12d
    mov esi, FIONREAD
    lea rdx, [OPTION]
    syscall
    test eax, eax
    jnz .closed
    mov eax, [OPTION]
    test eax, eax
    jnz .done
    mov edi, r12d
    lea rsi, [OPTION]
    mov edx, 1
    mov r10d, MSG_PEEK | MSG_DONTWAIT
    xor r8d, r8d
    xor r9d, r9d
    mov eax, SYS_RECVFROM
    syscall
    cmp eax, -EAGAIN
    jne .closed
    xor eax, eax
    jmp .done
.closed:
    mov eax, -1
.done:
    PHI_SOCK_LEAVE

; arg: connection -> the other end's address, or 0
OS_TcpAddress:
    PHI_SOCK_ENTER
    mov eax, [ARG1]
    mov bx, KIND_TCP
    call phi_sock_is
    jc .none
    mov edi, eax
    mov dword [ADDR_LEN], 16
    mov eax, SYS_GETPEERNAME
    lea rsi, [ADDR]
    lea rdx, [ADDR_LEN]
    syscall
    test eax, eax
    jnz .none
    mov eax, [ADDR + 4]
    bswap eax
    jmp .done
.none:
    xor eax, eax
.done:
    PHI_SOCK_LEAVE

; arg: listener -> 1, or 0 if it isn't one
OS_TcpStopListening:
    PHI_SOCK_ENTER
    mov eax, [ARG1]
    mov bx, KIND_LISTENER
    call phi_sock_is
    jc .not
    mov edi, eax
    call phi_sock_close
    mov eax, 1
    jmp .done
.not:
    xor eax, eax
.done:
    PHI_SOCK_LEAVE

phi_socket_kinds: times SOCKETS_MOST db 0     ; what each file descriptor is to these calls (KIND_...), 0: none
