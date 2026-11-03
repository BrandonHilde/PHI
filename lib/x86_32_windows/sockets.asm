; provides: OS_NetAddress OS_UdpOpen OS_UdpClose OS_UdpReceive OS_UdpSend OS_TcpBegin OS_TcpState OS_TcpSend OS_TcpTake OS_TcpClose OS_TcpListen OS_TcpAccept OS_TcpWaiting OS_TcpAddress OS_TcpStopListening
; requires: phi_imports
;
; The network for Windows programs (net.sockets): the calls a PHI OS program makes of its
; kernel's network, answered by Winsock (ws2_32.dll), as x86_32_linux's sockets.asm
; answers them with Linux's sockets. Each never waits: the sockets are non-blocking.
;
; A socket, connection or listener is a slot in phi_sockets (Winsock's handles are any
; number), holding Winsock's socket and what kind it is (a bit each, so a call can take
; several); a number that isn't a slot of the right kind is refused, as on PHI OS. A
; connection is connecting until select says it can be written to (getpeername already
; works while it connects, on Windows). Winsock is started the first time it's needed.
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
INVALID_SOCKET     equ -1
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
; many, the socket) and a timeval of 0 (for select)
%define ADDR       ebp - 16
%define ADDR_LEN   ebp - 20
%define OPTION     ebp - 24
%define WRITABLE   ebp - 32
%define FAILING    ebp - 40
%define TIMEVAL    ebp - 48
%macro PHI_SOCK_ENTER 0
    push ebp
    mov ebp, esp
    sub esp, 48
    push ebx
    push esi
    push edi
    call phi_wsa_start
%endmacro
%macro PHI_SOCK_LEAVE 0
    pop edi
    pop esi
    pop ebx
    mov esp, ebp
    pop ebp
    ret
%endmacro

; Winsock, version 2.2, once
phi_wsa_start:
    cmp byte [phi_wsa_ready], 0
    jne .done
    push phi_wsa_data
    push 0x0202
    call [phi_imp_WSAStartup]
    mov byte [phi_wsa_ready], 1
.done:
    ret

; the sockaddr_in at ADDR for address eax and port edx
phi_sock_address:
    mov word [ADDR], AF_INET
    xchg dl, dh
    mov [ADDR + 2], dx
    bswap eax
    mov [ADDR + 4], eax
    mov dword [ADDR + 8], 0
    mov dword [ADDR + 12], 0
    mov dword [ADDR_LEN], 16
    ret

; make Winsock's socket esi non-blocking, and keep it in a free slot as kind bl: eax = the
; slot, or -1 (the socket closed) if every slot is taken
phi_sock_keep:
    mov dword [OPTION], 1
    lea eax, [OPTION]
    push eax
    push FIONBIO
    push esi
    call [phi_imp_ioctlsocket]
    xor eax, eax
.find:
    cmp byte [phi_socket_kinds + eax], 0
    je .found
    inc eax
    cmp eax, SOCKETS_MOST
    jb .find
    push esi
    call [phi_imp_closesocket]
    mov eax, -1
    ret
.found:
    mov [phi_socket_kinds + eax], bl
    mov [phi_sockets + eax * 4], esi
    ret

; eax = a slot with a new non-blocking socket of type ecx and protocol edx, kind bl; or -1
phi_sock_new:
    push edx
    push ecx
    push AF_INET
    call [phi_imp_socket]
    cmp eax, INVALID_SOCKET
    je .failed
    mov esi, eax
    jmp phi_sock_keep
.failed:
    mov eax, -1
    ret

; carry clear if eax is a slot of one of the kinds in dl, with its socket in ebx
phi_sock_is:
    cmp eax, SOCKETS_MOST
    jae .no
    test [phi_socket_kinds + eax], dl
    jnz .yes
.no:
    stc
    ret
.yes:
    mov ebx, [phi_sockets + eax * 4]
    clc
    ret

; close slot eax's socket and free the slot
phi_sock_close:
    mov byte [phi_socket_kinds + eax], 0
    push dword [phi_sockets + eax * 4]
    call [phi_imp_closesocket]
    ret

; bind socket ebx to port edx on every address; eax = 0 if it worked
phi_sock_bind:
    xor eax, eax
    call phi_sock_address
    push 16
    lea eax, [ADDR]
    push eax
    push ebx
    call [phi_imp_bind]
    ret

; -> this machine's address (the one it would reach the internet from), or 127.0.0.1
OS_NetAddress:
    PHI_SOCK_ENTER
    mov edi, 0x7F000001
    push IPPROTO_UDP
    push SOCK_DGRAM
    push AF_INET
    call [phi_imp_socket]
    cmp eax, INVALID_SOCKET
    je .done
    mov esi, eax
    mov eax, 0x08080808
    mov edx, 53
    call phi_sock_address
    push 16
    lea eax, [ADDR]
    push eax
    push esi
    call [phi_imp_connect]
    test eax, eax
    jnz .close
    lea eax, [ADDR_LEN]
    push eax
    lea eax, [ADDR]
    push eax
    push esi
    call [phi_imp_getsockname]
    test eax, eax
    jnz .close
    mov eax, [ADDR + 4]
    bswap eax
    test eax, eax
    jz .close
    mov edi, eax
.close:
    push esi
    call [phi_imp_closesocket]
.done:
    mov eax, edi
    PHI_SOCK_LEAVE

; args: port queue -> socket, or -1 if the port is taken
OS_UdpOpen:
    PHI_SOCK_ENTER
    mov ecx, SOCK_DGRAM
    mov edx, IPPROTO_UDP
    mov bl, KIND_UDP
    call phi_sock_new
    test eax, eax
    js .done
    mov edi, eax
    mov ebx, esi
    mov edx, [ebp + 8]
    call phi_sock_bind
    test eax, eax
    jnz .failed
    mov eax, [ebp + 12]
    imul eax, eax, DATAGRAM_SIZE
    mov [OPTION], eax
    push 4
    lea eax, [OPTION]
    push eax
    push SO_RCVBUF
    push SOL_SOCKET
    push ebx
    call [phi_imp_setsockopt]
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
    mov eax, [ebp + 8]
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
; is waiting. (A datagram bigger than a UdpDatagram holds is cut short; an earlier send
; that came back unreachable is passed over.)
OS_UdpReceive:
    PHI_SOCK_ENTER
    mov eax, [ebp + 8]
    mov dl, KIND_UDP
    call phi_sock_is
    jc .none
    mov edi, [ebp + 12]
.again:
    mov dword [ADDR_LEN], 16
    lea eax, [ADDR_LEN]
    push eax
    lea eax, [ADDR]
    push eax
    push 0
    push UDP_MOST
    lea eax, [edi + 12]                 ; data
    push eax
    push ebx
    call [phi_imp_recvfrom]
    cmp eax, -1
    jne .got
    call [phi_imp_WSAGetLastError]
    cmp eax, WSAECONNRESET
    je .again
    cmp eax, WSAEMSGSIZE
    jne .none
    mov eax, UDP_MOST
.got:
    mov [edi + 8], eax                  ; length
    mov eax, [ADDR + 4]
    bswap eax
    mov [edi], eax                      ; from
    movzx eax, word [ADDR + 2]
    xchg al, ah
    mov [edi + 4], eax                  ; from_port
    mov eax, 1
    jmp .done
.none:
    xor eax, eax
.done:
    PHI_SOCK_LEAVE

; args: socket | port << 16, to, data, length -> 1 if sent
OS_UdpSend:
    PHI_SOCK_ENTER
    movzx eax, word [ebp + 8]
    mov dl, KIND_UDP
    call phi_sock_is
    jc .not
    mov eax, [ebp + 12]
    movzx edx, word [ebp + 10]
    call phi_sock_address
    push 16
    lea eax, [ADDR]
    push eax
    push 0
    push dword [ebp + 20]
    push dword [ebp + 16]
    push ebx
    call [phi_imp_sendto]
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
    mov ecx, SOCK_STREAM
    mov edx, IPPROTO_TCP
    mov bl, KIND_CONNECTING
    call phi_sock_new
    test eax, eax
    js .done
    mov edi, eax
    mov eax, [ebp + 8]
    mov edx, [ebp + 12]
    call phi_sock_address
    push 16
    lea eax, [ADDR]
    push eax
    push esi
    call [phi_imp_connect]
    test eax, eax
    jz .begun
    call [phi_imp_WSAGetLastError]
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
    mov eax, [ebp + 8]
    mov dl, KIND_ANY_TCP
    call phi_sock_is
    jc .failed
    mov edi, eax
    mov cl, [phi_socket_kinds + eax]
    cmp cl, KIND_FAILED
    je .failed
    cmp cl, KIND_CONNECTING
    je .connecting
    ; open, unless an error is waiting on it (a reset)
    mov dword [OPTION], 0
    mov dword [ADDR_LEN], 4
    lea eax, [ADDR_LEN]
    push eax
    lea eax, [OPTION]
    push eax
    push SO_ERROR
    push SOL_SOCKET
    push ebx
    call [phi_imp_getsockopt]
    test eax, eax
    jnz .broke
    cmp dword [OPTION], 0
    jne .broke
    jmp .open
    ; connected once select says it can be written to; failed if it says so
.connecting:
    mov dword [WRITABLE], 1
    mov [WRITABLE + 4], ebx
    mov dword [FAILING], 1
    mov [FAILING + 4], ebx
    mov dword [TIMEVAL], 0
    mov dword [TIMEVAL + 4], 0
    lea eax, [TIMEVAL]
    push eax
    lea eax, [FAILING]
    push eax
    lea eax, [WRITABLE]
    push eax
    push 0
    push 0
    call [phi_imp_select]
    cmp eax, -1
    je .broke
    cmp dword [FAILING], 0
    jne .broke
    cmp dword [WRITABLE], 0
    jne .opened
    xor eax, eax
    jmp .done
.opened:
    mov byte [phi_socket_kinds + edi], KIND_TCP
.open:
    mov eax, 1
    jmp .done
.broke:
    mov byte [phi_socket_kinds + edi], KIND_FAILED
.failed:
    mov eax, -1
.done:
    PHI_SOCK_LEAVE

; args: connection data length -> the bytes taken (0 when Winsock's buffer is full), or -1
OS_TcpSend:
    PHI_SOCK_ENTER
    mov eax, [ebp + 8]
    mov dl, KIND_CONNECTION
    call phi_sock_is
    jc .broken
    cmp byte [phi_socket_kinds + eax], KIND_CONNECTING
    je .none
    push 0
    push dword [ebp + 16]
    push dword [ebp + 12]
    push ebx
    call [phi_imp_send]
    cmp eax, -1
    jne .done
    call [phi_imp_WSAGetLastError]
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
    mov eax, [ebp + 8]
    mov dl, KIND_CONNECTION
    call phi_sock_is
    jc .broken
    cmp byte [phi_socket_kinds + eax], KIND_CONNECTING
    je .none
    cmp dword [ebp + 16], 0
    je .none
    push 0
    push dword [ebp + 16]
    push dword [ebp + 12]
    push ebx
    call [phi_imp_recv]
    test eax, eax
    jg .done
    jz .closed
    call [phi_imp_WSAGetLastError]
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

; arg: connection -> 1. Winsock sends what's left, then a FIN, after the program has gone on.
OS_TcpClose:
    PHI_SOCK_ENTER
    mov eax, [ebp + 8]
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
    mov ecx, SOCK_STREAM
    mov edx, IPPROTO_TCP
    mov bl, KIND_LISTENER
    call phi_sock_new
    test eax, eax
    js .done
    mov edi, eax
    mov ebx, esi
    mov edx, [ebp + 8]
    call phi_sock_bind
    test eax, eax
    jnz .failed
    push dword [ebp + 12]
    push ebx
    call [phi_imp_listen]
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
    mov eax, [ebp + 8]
    mov dl, KIND_LISTENER
    call phi_sock_is
    jc .none
    push 0
    push 0
    push ebx
    call [phi_imp_accept]
    cmp eax, INVALID_SOCKET
    je .none
    mov esi, eax
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
    mov eax, [ebp + 8]
    mov dl, KIND_CONNECTION
    call phi_sock_is
    jc .closed
    cmp byte [phi_socket_kinds + eax], KIND_CONNECTING
    je .zero
    mov dword [OPTION], 0
    lea eax, [OPTION]
    push eax
    push FIONREAD
    push ebx
    call [phi_imp_ioctlsocket]
    test eax, eax
    jnz .closed
    mov eax, [OPTION]
    test eax, eax
    jnz .done
    ; nothing to read: closed, or not yet? A peek says (0 bytes once it's closed)
    push MSG_PEEK
    push 1
    lea eax, [OPTION]
    push eax
    push ebx
    call [phi_imp_recv]
    cmp eax, -1
    jne .closed
    call [phi_imp_WSAGetLastError]
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
    mov eax, [ebp + 8]
    mov dl, KIND_CONNECTION
    call phi_sock_is
    jc .none
    cmp byte [phi_socket_kinds + eax], KIND_CONNECTING
    je .none
    mov dword [ADDR_LEN], 16
    lea eax, [ADDR_LEN]
    push eax
    lea eax, [ADDR]
    push eax
    push ebx
    call [phi_imp_getpeername]
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
    mov eax, [ebp + 8]
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
align 4, db 0
phi_sockets: times SOCKETS_MOST dd 0          ; Winsock's socket in each slot
phi_socket_kinds: times SOCKETS_MOST db 0     ; what each slot is (KIND_...), 0: free
phi_wsa_data: times 512 db 0                  ; what WSAStartup tells (not used)
