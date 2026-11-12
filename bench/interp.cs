// A small bytecode interpreter adding the numbers below 16,000,000 three times over: see interp.phi.
using System;

public static unsafe class Interp
{
    static uint* code;
    static ulong* stack;
    static uint* pc;
    static ulong* sp;
    static ulong* bp;

    static ulong Run()
    {
        uint ins, op;
        pc = code;
        bp = stack;
        sp = bp + 2;
        while (true)
        {
            ins = *pc;
            pc++;
            op = ins & 0xFF;
            switch (op)
            {
                case 0:
                    sp[0] = bp[ins >> 8];
                    sp++;
                    break;
                case 1:
                    sp--;
                    bp[ins >> 8] = sp[0];
                    break;
                case 2:
                    sp--;
                    sp[-1] = sp[-1] + sp[0];
                    break;
                case 3:
                    sp[0] = ins >> 8;
                    sp++;
                    break;
                case 4:
                    sp -= 2;
                    if (sp[0] < sp[1])
                        pc = code + (ins >> 8);
                    break;
                case 5:
                    return bp[0];
            }
        }
    }

    public static void Main()
    {
        uint[] program =
        {
            0 | (0 << 8), 0 | (1 << 8), 2, 1 | (0 << 8), 0 | (1 << 8), 3 | (1 << 8),
            2, 1 | (1 << 8), 0 | (1 << 8), 0xF4240003u, 4 | (0 << 8), 5,
        };
        uint* codeRoom = stackalloc uint[16];
        ulong* stackRoom = stackalloc ulong[64];
        code = codeRoom;
        stack = stackRoom;
        ulong total = 0;
        for (int i = 0; i < 12; i++) code[i] = program[i];
        for (int round = 0; round < 3; round++)
        {
            stack[0] = 0;
            stack[1] = 0;
            total += Run();
        }
        Console.WriteLine(total);
    }
}
