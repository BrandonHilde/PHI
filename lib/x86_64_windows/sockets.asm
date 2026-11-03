; provides: OS_NetAddress OS_UdpOpen OS_UdpClose OS_UdpReceive OS_UdpSend OS_TcpBegin OS_TcpState OS_TcpSend OS_TcpTake OS_TcpClose OS_TcpListen OS_TcpAccept OS_TcpWaiting OS_TcpAddress OS_TcpStopListening
; requires: phi_imports
;
; The network for 64-bit Windows programs (net.sockets), answered by Winsock as
; x86_32_windows/sockets.asm answers it: the same slots, kinds and checks, with Winsock's
; functions called the x64 way (PHI_CALL) and its sockets 8 bytes. Arguments PHI pushes
; are 8 bytes each; each call keeps rbx, rsi and rdi.
AF_INET            equ 2
SOCK_STREAM        equ 1
SOCK_DGRAM         equ 2
IPPROTO_TCP        equ 6
IPPROTO_UDP        equ 17
SOL_SOCKET         equ 0xFFFF
SO_RCVBUF          equ 0x1002
SO_ERROR           equ 0x1007
FIONBIO            equ 0x8004667E
FIONREAD           equ 0x4004667F
MSG_PEEK           equ 2
WSAEWOULDBLOCK     equ 10035
WSAEMSGSIZE        equ 10040
WSAECONNRESET      equ 10054

SOCKETS_MOST       equ 1024
KIND_UDP           equ 1
KIND_TCP           equ 2                ; a connection that's open
KIND_LISTENER      equ 4
KIND_FAILED        equ 8                ; a connection that couldn't connect, or broke
KIND_CONNECTING    equ 16
KIND_CONNECTION    equ KIND_TCP | KIND_CONNECTING
KIND_ANY_TCP       equ KIND_TCP | KIND_CONNECTING | KIND_FAILED
UDP_MOST           equ 1472
DATAGRAM_SIZE      equ 1536

; each call's frame: a sockaddr_in, its length, a number, two fd_sets of one socket (how
; many, then the 8-byte socket) and a timeval of 0 (for select)
%define ADDR       rbp - 16
%define ADDR_LEN   rbp - 20
%define OPTION     rbp - 24
%define WRITABLE   rbp - 40
%define FAILING    rbp - 56
%define TIMEVAL    rbp - 64
%define ARG1       rbp + 16
%define ARG2       rbp + 24
%define ARG3       rbp + 32
%define ARG4       rbp + 40

; (the same as system.asm's: whichever comes first defines it)
%ifnmacro PHI_CALL 1-4
%macro PHI_CALL 1-4
    mov r12, rsp
    and rsp, -16
    sub rsp, 64
%if %0 >= 2
    mov qword [rsp + 32], %2
%endif
%if %0 >= 3
    mov qword [rsp + 40], %3
%endif
%if %0 >= 4
    mov qword [rsp + 48], %4
%endif
    call [rel phi_imp_%1]
    mov rsp, r12
%endmacro
%endif

%macro PHI_SOCK_ENTER 0
    push rbp
    mov rbp, rsp
    sub rsp, 64
    push rbx
    push rsi
    push rdi
    push r12
    call phi_wsa_start
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

; Winsock, version 2.2, once
phi_wsa_start:
    cmp byte [rel phi_wsa_ready], 0
    jne .done
    mov ecx, 0x0202
    lea rdx, [rel phi_wsa_data]
    PHI_CALL WSAStartup
    mov byte [rel phi_wsa_ready], 1
.done:
    ret

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

; make Winsock's socket rsi non-blocking, and keep it in a free slot as kind bl: eax = the
; slot, or -1 (the socket closed) if every slot is taken
phi_sock_keep:
    mov dword [OPTION], 1
    mov rcx, rsi
    mov edx, FIONBIO
    lea r8, [OPTION]
    PHI_CALL ioctlsocket
    lea rcx, [rel phi_socket_kinds]
    xor eax, eax
.find:
    cmp byte [rcx + rax], 0
    je .found
    inc eax
    cmp eax, SOCKETS_MOST
    jb .find
    mov rcx, rsi
    PHI_CALL closesocket
    mov eax, -1
    ret
.found:
    mov [rcx + rax], bl
    lea rcx, [rel phi_sockets]
    mov [rcx + rax * 8], rsi
    ret

; eax = a slot with a new non-blocking socket of type edx and protocol r8d, kind bl; or -1
phi_sock_new:
    mov ecx, AF_INET
    PHI_CALL socket
    cmp rax, -1
    je .failed
    mov rsi, rax
    jmp phi_sock_keep
.failed:
    mov eax, -1
    ret

; carry clear if eax is a slot of one of the kinds in dl, with its socket in rbx
phi_sock_is:
    cmp eax, SOCKETS_MOST
    jae .no
    lea rcx, [rel phi_socket_kinds]
    test [rcx + rax], dl
    jnz .yes
.no:
    stc
    ret
.yes:
    lea rcx, [rel phi_sockets]
    mov rbx, [rcx + rax * 8]
    clc
    ret

; the kind of slot eax, in cl
phi_sock_kind:
    lea rcx, [rel phi_socket_kinds]
    mov cl, [rcx + rax]
    ret

; slot eax becomes kind dl
phi_sock_set_kind:
    lea rcx, [rel phi_socket_kinds]
    mov [rcx + rax], dl
    ret

; close slot eax's socket and free the slot
phi_sock_close:
    lea rcx, [rel phi_socket_kinds]
    mov byte [rcx + rax], 0
    lea rcx, [rel phi_sockets]
    mov rcx, [rcx + rax * 8]
    PHI_CALL closesocket
    ret

; bind socket rbx to port edx on every address; eax = 0 if it worked
phi_sock_bind:
    xor eax, eax
    call phi_sock_address
    mov rcx, rbx
    lea rdx, [ADDR]
    mov r8d, 16
    PHI_CALL bind
    ret

; -> this machine's address (the one it would reach the internet from), or 127.0.0.1
OS_NetAddress:
    PHI_SOCK_ENTER
    mov edi, 0x7F000001
    mov ecx, AF_INET
    mov edx, SOCK_DGRAM
    mov r8d, IPPROTO_UDP
    PHI_CALL socket
    cmp rax, -1
    je .done
    mov rsi, rax
    mov eax, 0x08080808
    mov edx, 53
    call phi_sock_address
    mov rcx, rsi
    lea rdx, [ADDR]
    mov r8d, 16
    PHI_CALL connect
    test eax, eax
    jnz .close
    mov rcx, rsi
    lea rdx, [ADDR]
    lea r8, [ADDR_LEN]
    PHI_CALL getsockname
    test eax, eax
    jnz .close
    mov eax, [ADDR + 4]
    bswap eax
    test eax, eax
    jz .close
    mov edi, eax
.close:
    mov rcx, rsi
    PHI_CALL closesocket
.done:
    mov eax, edi
    PHI_SOCK_LEAVE

; args: port queue -> socket, or -1 if the port is taken
OS_UdpOpen:
    PHI_SOCK_ENTER
    mov edx, SOCK_DGRAM
    mov r8d, IPPROTO_UDP
    mov bl, KIND_UDP
    call phi_sock_new
    test eax, eax
    js .done
    mov edi, eax
    mov rbx, rsi
    mov edx, [ARG1]
    call phi_sock_bind
    test eax, eax
    jnz .failed
    mov eax, [ARG2]
    imul eax, eax, DATAGRAM_SIZE
    mov [OPTION], eax
    lea rax, [OPTION]
    mov rcx, rbx
    mov edx, SOL_SOCKET
    mov r8d, SO_RCVBUF
    mov r9, rax
    PHI_CALL setsockopt, 4
    mov eax, edi
    jmp .done
.failed:
    mov eax, edi
    call phi_sock_close
    mov eax, -1
.done:
    PHI_SOCK_LEAVE

; arg: socket -> 1, or 0 if it isn't one
OS_UdpClose:
    PHI_SOCK_ENTER
    mov eax, [ARG1]
    mov dl, KIND_UDP
    call phi_sock_is
    jc .not
    call phi_sock_close
    mov eax, 1
    jmp .done
.not:
    xor eax, eax
.done:
    PHI_SOCK_LEAVE

; args: socket datagram -> 1 with the oldest datagram in it (a UdpDatagram), or 0 if none
; is waiting (a datagram too big is cut short; an unreachable earlier send passed over)
OS_UdpReceive:
    PHI_SOCK_ENTER
    mov eax, [ARG1]
    mov dl, KIND_UDP
    call phi_sock_is
    jc .none
    mov rdi, [ARG2]
.again:
    mov dword [ADDR_LEN], 16
    lea rax, [ADDR]
    lea rsi, [ADDR_LEN]
    mov rcx, rbx
    lea rdx, [rdi + 12]                 ; data
    mov r8d, UDP_MOST
    xor r9d, r9d
    PHI_CALL recvfrom, rax, rsi
    cmp eax, -1
    jne .got
    PHI_CALL WSAGetLastError
    cmp eax, WSAECONNRESET
    je .again
    cmp eax, WSAEMSGSIZE
    jne .none
    mov eax, UDP_MOST
.got:
    mov [rdi + 8], eax                  ; length
    mov eax, [ADDR + 4]
    bswap eax
    mov [rdi], eax                      ; from
    movzx eax, word [ADDR + 2]
    xchg al, ah
    mov [rdi + 4], eax                  ; from_port
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
    mov dl, KIND_UDP
    call phi_sock_is
    jc .not
    mov eax, [ARG2]
    movzx edx, word [ARG1 + 2]
    call phi_sock_address
    lea rax, [ADDR]
    mov rcx, rbx
    mov rdx, [ARG3]
    mov r8d, [ARG4]
    xor r9d, r9d
    PHI_CALL sendto, rax, 16
    cmp eax, -1
    je .not
    mov eax, 1
    jmp .done
.not:
    xor eax, eax
.done:
    PHI_SOCK_LEAVE

; args: to port -> a connection, still connecting, or -1
OS_TcpBegin:
    PHI_SOCK_ENTER
    mov edx, SOCK_STREAM
    mov r8d, IPPROTO_TCP
    mov bl, KIND_CONNECTING
    call phi_sock_new
    test eax, eax
    js .done
    mov edi, eax
    mov eax, [ARG1]
    mov edx, [ARG2]
    call phi_sock_address
    mov rcx, rsi
    lea rdx, [ADDR]
    mov r8d, 16
    PHI_CALL connect
    test eax, eax
    jz .begun
    PHI_CALL WSAGetLastError
    cmp eax, WSAEWOULDBLOCK
    je .begun
    mov eax, edi
    call phi_sock_close
    mov eax, -1
    jmp .done
.begun:
    mov eax, edi
.done:
    PHI_SOCK_LEAVE

; arg: connection -> 1 open, 0 still connecting, -1 failed (or broken)
OS_TcpState:
    PHI_SOCK_ENTER
    mov eax, [ARG1]
    mov dl, KIND_ANY_TCP
    call phi_sock_is
    jc .failed
    mov edi, eax
    call phi_sock_kind
    cmp cl, KIND_FAILED
    je .failed
    cmp cl, KIND_CONNECTING
    je .connecting
    ; open, unless an error is waiting on it (a reset)
    mov dword [OPTION], 0
    mov dword [ADDR_LEN], 4
    lea rax, [ADDR_LEN]
    mov rcx, rbx
    mov edx, SOL_SOCKET
    mov r8d, SO_ERROR
    lea r9, [OPTION]
    PHI_CALL getsockopt, rax
    test eax, eax
    jnz .broke
    cmp dword [OPTION], 0
    jne .broke
    jmp .open
    ; connected once select says it can be written to; failed if it says so
.connecting:
    mov dword [WRITABLE], 1
    mov [WRITABLE + 8], rbx
    mov dword [FAILING], 1
    mov [FAILING + 8], rbx
    mov qword [TIMEVAL], 0
    xor ecx, ecx
    xor edx, edx
    lea r8, [WRITABLE]
    lea r9, [FAILING]
    lea rax, [TIMEVAL]
    PHI_CALL select, rax
    cmp eax, -1
    je .broke
    cmp dword [FAILING], 0
    jne .broke
    cmp dword [WRITABLE], 0
    jne .opened
    xor eax, eax
    jmp .done
.opened:
    mov eax, edi
    mov dl, KIND_TCP
    call phi_sock_set_kind
.open:
    mov eax, 1
    jmp .done
.broke:
    mov eax, edi
    mov dl, KIND_FAILED
    call phi_sock_set_kind
.failed:
    mov eax, -1
.done:
    PHI_SOCK_LEAVE

; args: connection data length -> the bytes taken (0 when Winsock's buffer is full), or -1
OS_TcpSend:
    PHI_SOCK_ENTER
    mov eax, [ARG1]
    mov dl, KIND_CONNECTION
    call phi_sock_is
    jc .broken
    call phi_sock_kind
    cmp cl, KIND_CONNECTING
    je .none
    mov rcx, rbx
    mov rdx, [ARG2]
    mov r8d, [ARG3]
    xor r9d, r9d
    PHI_CALL send
    cmp eax, -1
    jne .done
    PHI_CALL WSAGetLastError
    cmp eax, WSAEWOULDBLOCK
    jne .broken
.none:
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
    mov dl, KIND_CONNECTION
    call phi_sock_is
    jc .broken
    call phi_sock_kind
    cmp cl, KIND_CONNECTING
    je .none
    cmp dword [ARG3], 0
    je .none
    mov rcx, rbx
    mov rdx, [ARG2]
    mov r8d, [ARG3]
    xor r9d, r9d
    PHI_CALL recv
    test eax, eax
    jg .done
    jz .closed
    PHI_CALL WSAGetLastError
    cmp eax, WSAEWOULDBLOCK
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
    mov dl, KIND_ANY_TCP
    call phi_sock_is
    jc .done
    call phi_sock_close
.done:
    mov eax, 1
    PHI_SOCK_LEAVE

; args: port backlog -> a listener, or -1 if the port is taken
OS_TcpListen:
    PHI_SOCK_ENTER
    mov edx, SOCK_STREAM
    mov r8d, IPPROTO_TCP
    mov bl, KIND_LISTENER
    call phi_sock_new
    test eax, eax
    js .done
    mov edi, eax
    mov rbx, rsi
    mov edx, [ARG1]
    call phi_sock_bind
    test eax, eax
    jnz .failed
    mov rcx, rbx
    mov edx, [ARG2]
    PHI_CALL listen
    test eax, eax
    jnz .failed
    mov eax, edi
    jmp .done
.failed:
    mov eax, edi
    call phi_sock_close
    mov eax, -1
.done:
    PHI_SOCK_LEAVE

; arg: listener -> a connection that has come in, or -1 if none is waiting
OS_TcpAccept:
    PHI_SOCK_ENTER
    mov eax, [ARG1]
    mov dl, KIND_LISTENER
    call phi_sock_is
    jc .none
    mov rcx, rbx
    xor edx, edx
    xor r8d, r8d
    PHI_CALL accept
    cmp rax, -1
    je .none
    mov rsi, rax
    mov bl, KIND_TCP
    call phi_sock_keep
    jmp .done
.none:
    mov eax, -1
.done:
    PHI_SOCK_LEAVE

; arg: connection -> the bytes that can be read now, or -1 once closed (and read) or broken
OS_TcpWaiting:
    PHI_SOCK_ENTER
    mov eax, [ARG1]
    mov dl, KIND_CONNECTION
    call phi_sock_is
    jc .closed
    call phi_sock_kind
    cmp cl, KIND_CONNECTING
    je .zero
    mov dword [OPTION], 0
    mov rcx, rbx
    mov edx, FIONREAD
    lea r8, [OPTION]
    PHI_CALL ioctlsocket
    test eax, eax
    jnz .closed
    mov eax, [OPTION]
    test eax, eax
    jnz .done
    ; nothing to read: closed, or not yet? A peek says (0 bytes once it's closed)
    mov rcx, rbx
    lea rdx, [OPTION]
    mov r8d, 1
    mov r9d, MSG_PEEK
    PHI_CALL recv
    cmp eax, -1
    jne .closed
    PHI_CALL WSAGetLastError
    cmp eax, WSAEWOULDBLOCK
    jne .closed
.zero:
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
    mov dl, KIND_CONNECTION
    call phi_sock_is
    jc .none
    call phi_sock_kind
    cmp cl, KIND_CONNECTING
    je .none
    mov dword [ADDR_LEN], 16
    mov rcx, rbx
    lea rdx, [ADDR]
    lea r8, [ADDR_LEN]
    PHI_CALL getpeername
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
    mov dl, KIND_LISTENER
    call phi_sock_is
    jc .not
    call phi_sock_close
    mov eax, 1
    jmp .done
.not:
    xor eax, eax
.done:
    PHI_SOCK_LEAVE

phi_wsa_ready: db 0
align 8, db 0
phi_sockets: times SOCKETS_MOST dq 0          ; Winsock's socket in each slot
phi_socket_kinds: times SOCKETS_MOST db 0     ; what each slot is (KIND_...), 0: free
phi_wsa_data: times 512 db 0                  ; what WSAStartup tells (not used)
