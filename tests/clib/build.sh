#!/bin/sh
# Builds the C libraries tests/clib's tests call, into macos/, windows64/ and windows/ beside
# this file (a build puts a library found there beside the program). On a Mac: clang for
# macOS, and MinGW (brew install mingw-w64) for the two Windows.
set -e
cd "$(dirname "$0")"
mkdir -p macos windows64 windows
if [ "$(uname)" = Darwin ]; then
    clang -arch arm64 -O1 -dynamiclib -install_name @rpath/libphi_abi.dylib -o macos/libphi_abi.dylib phi_abi.c
fi
if command -v x86_64-w64-mingw32-gcc >/dev/null; then
    x86_64-w64-mingw32-gcc -O1 -shared -o windows64/phi_abi.dll phi_abi.c
    i686-w64-mingw32-gcc -O1 -shared -static-libgcc -o windows/phi_abi.dll phi_abi.c
fi

# Lua and zlib, from their own sources (kept in src/): the same version on each system
mkdir -p src
fetch() { [ -f "src/$1" ] || curl -L --fail -sS -o "src/$1" "$2"; }
fetch lua-5.4.7.tar.gz https://www.lua.org/ftp/lua-5.4.7.tar.gz
fetch zlib-1.3.1.tar.gz https://github.com/madler/zlib/releases/download/v1.3.1/zlib-1.3.1.tar.gz
[ -d src/lua-5.4.7 ] || tar -xzf src/lua-5.4.7.tar.gz -C src
[ -d src/zlib-1.3.1 ] || tar -xzf src/zlib-1.3.1.tar.gz -C src
lua=$(ls src/lua-5.4.7/src/*.c | grep -v -e '/lua\.c$' -e '/luac\.c$')
zlib=$(ls src/zlib-1.3.1/*.c)
if [ "$(uname)" = Darwin ]; then
    clang -arch arm64 -O2 -w -DLUA_USE_MACOSX -dynamiclib -install_name @rpath/liblua54.dylib -o macos/liblua54.dylib $lua
fi
if command -v x86_64-w64-mingw32-gcc >/dev/null; then
    x86_64-w64-mingw32-gcc -O2 -w -DLUA_BUILD_AS_DLL -shared -o windows64/lua54.dll $lua
    i686-w64-mingw32-gcc -O2 -w -DLUA_BUILD_AS_DLL -shared -static-libgcc -o windows/lua54.dll $lua
    x86_64-w64-mingw32-gcc -O2 -w -DZLIB_DLL -shared -o windows64/zlib1.dll $zlib
    i686-w64-mingw32-gcc -O2 -w -DZLIB_DLL -shared -static-libgcc -o windows/zlib1.dll $zlib
fi
ls -l macos windows64 windows
