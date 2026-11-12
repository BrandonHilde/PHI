// A small bytecode interpreter adding the numbers below 16,000,000 three times over: see interp.phi.
#include <stdio.h>
#include <stdint.h>

static uint32_t code[16];
static uint64_t stack[64];
static uint32_t *pc;
static uint64_t *sp;
static uint64_t *bp;

static uint64_t run(void)
{
    uint32_t ins, op;
    pc = code;
    bp = stack;
    sp = bp + 2;
    for (;;) {
        ins = *pc;
        pc++;
        op = ins & 0xFF;
        switch (op) {
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

int main(void)
{
    static const uint32_t program[12] = {
        0 | (0 << 8), 0 | (1 << 8), 2, 1 | (0 << 8), 0 | (1 << 8), 3 | (1 << 8),
        2, 1 | (1 << 8), 0 | (1 << 8), 0xF4240003u, 4 | (0 << 8), 5,
    };
    uint64_t total = 0;
    for (int i = 0; i < 12; i++) code[i] = program[i];
    for (int round = 0; round < 3; round++) {
        stack[0] = 0;
        stack[1] = 0;
        total += run();
    }
    printf("%llu\n", (unsigned long long)total);
    return 0;
}
