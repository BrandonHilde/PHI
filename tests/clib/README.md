# Tests of C libraries

Each test here is one PHI source that calls a C library, run as a macOS program and as a
Windows one (32 and 64 bits); the three give the same output. They aren't part of plain
`phi test`, because the libraries have to be built first:

```
tests/clib/build.sh                          # once: needs clang, and MinGW for the Windows ones
phi test tests/clib --target macos
phi test tests/clib --target windows64       # on Windows, or with PHI_WINDOWS_RUN set
phi test tests/clib --target windows
```

`build.sh` puts each library in `macos/`, `windows64/` and `windows/` beside the tests. A
build puts a library it finds there beside the program (see "A library several systems
have" in docs/language.md), which is how a program of your own carries its libraries too.

| Test | Library | What it's there for |
|---|---|---|
| `c_abi_program` | `phi_abi.c`, made for the test | every way C takes and gives a value, one function each |
| `c_zlib_program` | zlib | buffers and lengths C writes back; C's `long` |
| `c_lua_program` | Lua 5.4 | C calling PHI methods, over and over |
| `c_sdl3_program` | SDL3 (on a Mac, `brew install sdl3`) | floats as arguments, C's structs read and given by address, events; with no screen, drawing into memory |

`samples/sdl3/bounce.phi` is SDL3 with a window.

The C runtime and SQLite, which Windows and macOS come with, are tested in plain
`phi test` (`tests/c_runtime_program.phi`, `tests/c_sqlite_program.phi`).
