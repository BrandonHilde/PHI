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

# for 64-bit Linux: its own gcc there, or one in the Linux machine a Mac runs Linux programs in
linux=""
if [ "$(uname)" = Linux ] && [ "$(uname -m)" = x86_64 ]; then linux="gcc"
elif command -v lima >/dev/null && lima which x86_64-linux-gnu-gcc >/dev/null 2>&1; then linux="lima x86_64-linux-gnu-gcc"
fi
if [ -n "$linux" ]; then
    mkdir -p linux64
    $linux -O1 -shared -fPIC -o linux64/libphi_abi.so phi_abi.c
    $linux -O2 -w -DLUA_USE_POSIX -shared -fPIC -o linux64/liblua54.so $lua -lm
fi

# SDL3: Homebrew's on a Mac (brew install sdl3), SDL's own DLLs on Windows
for a in x64 x86; do
    fetch SDL3-win32-$a.zip https://github.com/libsdl-org/SDL/releases/download/release-3.4.20/SDL3-3.4.20-win32-$a.zip
    [ -d src/sdl3-$a ] || unzip -o -q src/SDL3-win32-$a.zip -d src/sdl3-$a
done
cp src/sdl3-x64/SDL3.dll windows64/
cp src/sdl3-x86/SDL3.dll windows/

# raylib: Homebrew's on a Mac (brew install raylib), raylib's own DLLs on Windows
for a in win64 win32; do
    fetch raylib-$a.zip https://github.com/raysan5/raylib/releases/download/5.5/raylib-5.5_${a}_msvc16.zip
    [ -d src/raylib-5.5_${a}_msvc16 ] || unzip -o -q src/raylib-$a.zip -d src
done
cp src/raylib-5.5_win64_msvc16/lib/raylib.dll windows64/
cp src/raylib-5.5_win32_msvc16/lib/raylib.dll windows/
ls -l macos windows64 windows linux64 2>/dev/null
