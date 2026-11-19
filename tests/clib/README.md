# Tests of C libraries

Each test here is one PHI source that calls a C library, run as a macOS program, a Windows
one (32 and 64 bits) and a 64-bit Linux one; all give the same output. They aren't part of plain
`phi test`, because the libraries have to be built first:

```
tests/clib/build.sh                          # once: needs clang, and MinGW for the Windows ones
phi test tests/clib --target macos
phi test tests/clib --target windows64       # on Windows, or with PHI_WINDOWS_RUN set
phi test tests/clib --target windows
phi test tests/clib --target linux64         # needs zlib and SDL3 on the Linux machine (libz.so.1, libSDL3.so.0)
```

`build.sh` puts each library in `macos/`, `windows64/`, `windows/` and `linux64/` beside the tests. A
build puts a library it finds there beside the program (see "A library several systems
have" in docs/language.md), which is how a program of your own carries its libraries too.

| Test | Library | What it's there for |
|---|---|---|
| `c_abi_program` | `phi_abi.c`, made for the test | every way C takes and gives a value, one function each |
| `c_structs_program` | `phi_abi.c` | structs taken and given by value, each size and make-up the conventions treat their own way |
| `c_zlib_program` | zlib | buffers and lengths C writes back; C's `long` |
| `c_lua_program` | Lua 5.4 | C calling PHI methods, over and over |
| `c_sdl3_program` | SDL3 (on a Mac, `brew install sdl3`) | floats as arguments, C's structs read and given by address, events; with no screen, drawing into memory |
| `c_raylib_program` | raylib (on a Mac, `brew install raylib`; not on Linux, where no file of it is named) | structs by value in a real library: maths, colours, collisions, images in memory; with no window |

`samples/sdl3/bounce.phi` is SDL3 with a window, and `samples/raylib/ball.phi` raylib.

The C runtime and SQLite, which Windows and macOS come with, are tested in plain
`phi test` (`tests/c_runtime_program.phi`, `tests/c_sqlite_program.phi`).
