@echo off
nasm -f bin %1.asm -o %1.bin
nasm -f bin %2.asm -o %2.bin
type %1.bin %2.bin > os.bin
qemu-system-x86_64 -fda os.bin

:: this command in the terminal from the Compiler\x86ASM folder will run the hello world program
:: if you have qemu-system-x86_64 and nasm installed
:: & .\buildASM.bat example sectortwo
:: Build system will improve over time