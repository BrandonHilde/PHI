@echo off
:: uncomment and remove the nasm line to use fasm instead
::fasm %1.asm %1.bin 
nasm -f bin %1.asm -o %1.bin
qemu-system-x86_64 -fda %1.bin

:: this command in the terminal from the Compiler\x86ASM folder will run the hello world program
:: if you have qemu-system-x86_64 and nasm installed
:: & .\buildSingleASM.bat example
:: this builds an example.asm file into a example.bin and runs it with qemu
:: Build system will improve over time
