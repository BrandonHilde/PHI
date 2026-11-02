# The mmo sample's certificates

**For the sample only: these private keys are public, in this repository. A real server
makes its own (below) and keeps its key to itself.**

The lobby (`server.rootfs/lobby.phi`) takes logins over TLS, showing `LOBBY.CRT` (DER), a
certificate for `lobby.mmo.phi.test` with a P-256 key (`LOBBY.KEY`, DER), signed by a
root of its own:

| File | What |
|---|---|
| `root.der` | the root, which clients trust (`X509.Trust`); `root.pem` is the same in PEM |
| `root.key` | its RSA-2048 key, to sign another lobby certificate with |
| `lobby.pem`, `lobby.key.pem` | the lobby's certificate and key in PEM (`server.rootfs/` has them in DER) |

The root signs the lobby's certificate itself, with no CA in between, so a client checks
one RSA signature of the chain (and the lobby's P-256 signature of the handshake).

Making them again, with OpenSSL:

```
openssl genrsa -out root.key 2048
openssl req -x509 -new -key root.key -sha256 -days 3650 -subj "/O=PHI samples/CN=PHI mmo sample root" \
    -addext "basicConstraints=critical,CA:TRUE" -addext "keyUsage=critical,keyCertSign,cRLSign" -out root.pem
openssl x509 -in root.pem -outform DER -out root.der
openssl ecparam -name prime256v1 -genkey -noout -out lobby.key.pem
openssl req -new -key lobby.key.pem -subj "/CN=lobby.mmo.phi.test" -out lobby.csr
printf 'basicConstraints=CA:FALSE\nkeyUsage=critical,digitalSignature\nsubjectAltName=DNS:lobby.mmo.phi.test\n' > lobby.ext
openssl x509 -req -in lobby.csr -CA root.pem -CAkey root.key -sha256 -days 3650 -set_serial 2 -extfile lobby.ext -out lobby.pem
openssl x509 -in lobby.pem -outform DER -out ../server.rootfs/LOBBY.CRT
openssl ec -in lobby.key.pem -outform DER -out ../server.rootfs/LOBBY.KEY
```

A real server's certificate comes from a CA its players' clients already trust (Let's
Encrypt, say, for a certificate with a P-256 key), and its key never leaves the server.
