# Writes tests/vec_loops_program.phi: loops the optimiser makes vector code of, each run against
# the same loop written so it can't (its body in an if), on the same numbers, every element
# compared; and tests/vec_loops_program.expected, which says each is the same. Run it again
# after changing it:
#   python3 tests/support/vec_loops.py tests/vec_loops_program.phi tests/vec_loops_program.expected
import sys

KINDS = ['i8', 'u8', 'i16', 'u16', 'int', 'u32', 'i64', 'u64', 'f32', 'f64']
FLOAT = {'f32', 'f64'}
BITS = {'i8': 8, 'u8': 8, 'i16': 16, 'u16': 16, 'int': 32, 'u32': 32, 'i64': 64, 'u64': 64, 'f32': 32, 'f64': 64}

def cases(t):
    f = t in FLOAT
    c = [('add', 'q:i + r:i'), ('sub', 'q:i - r:i'), ('neg', '-q:i + r:i'), ('params', 'q:i * k + c'),
         ('constant', '7'), ('mix', '(q:i + k) * r:i - q:i')]
    if f:
        # (f32s are vector code one operation at a time; more are left as written, and still the same)
        c += [('div', 'q:i / r:i'), ('scale', 'q:i * k'), ('half', '-q:i * 0.5'), ('mixed', 'q:i * 0.5 + r:i / 3.0')]
    else:
        c += [('logic', '(q:i & r:i) ^ (r:i | 5)'), ('not', '~q:i + 1'), ('shl', 'q:i << 3'), ('shr', 'r:i >> 2'),
              ('shr2', '(q:i >> 1) + (r:i << 1)')]
        if BITS[t] == 64:
            c = [x for x in c if x[0] not in ('params', 'mix')]   # (no 64-bit multiply in lanes)
    return c

out = ["# Made by tests/support/vec_loops.py: loops the optimiser makes vector code of, each against",
       "# the same loop written so it can't (in an if), on the same numbers, every element compared.",
       "phi.VecLoopsProgram:Program", "{"]
want = []
for t in KINDS:
    out.append(f"\tcall Kind_{t};")
out.append("\texit 0;")
out.append("")

for t in KINDS:
    f = t in FLOAT
    one = '1.5' if f else '3'
    two = '0.25' if f else '2'
    # the cases: one method made vector code, one not
    for name, expr in cases(t):
        for vec in (True, False):
            m = f"{'V' if vec else 'S'}_{t}_{name}"
            out.append(f"\t[{m}: ptr<{t}> p: 0; ptr<{t}> q: 0; ptr<{t}> r: 0; int start: 0; int n: 0; {t} k: {one}; {t} c: {two};]")
            out.append(f"\t\twhile int i: start; i < n; i++;")
            if vec:
                out.append(f"\t\t\tp:i is {expr};")
            else:
                out.append(f"\t\t\tif true")
                out.append(f"\t\t\t\tp:i is {expr};")
                out.append(f"\t\t\t;;")
            out.append(f"\t\t;;")
            out.append(f"\t[end]")
            out.append("")
    # numbers to work on: made the same way each time
    out.append(f"\t[Fill_{t}: ptr<{t}> p: 0; int n: 0; int seed: 0;]")
    out.append(f"\t\tu32 x: seed as u32;")
    out.append(f"\t\twhile int i: 0; i < n; i++;")
    out.append(f"\t\t\tx is x * 1103515245 + 12345;")
    if f:
        out.append(f"\t\t\tp:i is ((x >> 16) % 61) as f64 * 0.25 + 0.5;")
    elif BITS[t] == 64:
        out.append(f"\t\t\tp:i is ((x as {t}) << 32) | ((x * 7) as {t});")
    else:
        out.append(f"\t\t\tp:i is (x >> 7) as {t};")
    out.append(f"\t\t;;")
    out.append(f"\t[end]")
    out.append("")
    out.append(f"\t[Same_{t}: ptr<{t}> a: 0; ptr<{t}> b: 0; int n: 0;]")
    out.append(f"\t\twhile int i: 0; i < n; i++;")
    out.append(f"\t\t\tif a:i is not b:i")
    out.append(f"\t\t\t\treturn i;")
    out.append(f"\t\t\t;;")
    out.append(f"\t\t;;")
    out.append(f"\t[end: -1]")
    out.append("")
    # each case: 37 elements (not a whole number of vecs), from 0 and from 3; in place; overlapping
    out.append(f"\t[Kind_{t}:]")
    out.append(f"\t\tint n: 37;")
    out.append(f"\t\tptr<{t}> q: new {t}[64];")
    out.append(f"\t\tptr<{t}> r: new {t}[64];")
    out.append(f"\t\tptr<{t}> a: new {t}[64];")
    out.append(f"\t\tptr<{t}> b: new {t}[64];")
    out.append(f"\t\tcall Fill_{t}: q 64 1;")
    out.append(f"\t\tcall Fill_{t}: r 64 2;")
    for name, expr in cases(t):
        for start in (0, 3):
            out.append(f"\t\tcall Fill_{t}: a 64 3;")
            out.append(f"\t\tcall Fill_{t}: b 64 3;")
            out.append(f"\t\tcall V_{t}_{name}: a q r {start} n;")
            out.append(f"\t\tcall S_{t}_{name}: b q r {start} n;")
            out.append(f"\t\tlog '{t} {name} from {start}: ' (Same_{t}: a b 64) '\\n';")
            want.append(f"{t} {name} from {start}: -1")
        # in place (the same elements read and written), and overlapping (one element on: done as written)
        out.append(f"\t\tcall Fill_{t}: a 64 4;")
        out.append(f"\t\tcall Fill_{t}: b 64 4;")
        out.append(f"\t\tcall V_{t}_{name}: a a r 0 n;")
        out.append(f"\t\tcall S_{t}_{name}: b b r 0 n;")
        out.append(f"\t\tlog '{t} {name} in place: ' (Same_{t}: a b 64) '\\n';")
        want.append(f"{t} {name} in place: -1")
        out.append(f"\t\tcall Fill_{t}: a 64 5;")
        out.append(f"\t\tcall Fill_{t}: b 64 5;")
        out.append(f"\t\tcall V_{t}_{name}: a + sizeof {t} a r 0 n;")
        out.append(f"\t\tcall S_{t}_{name}: b + sizeof {t} b r 0 n;")
        out.append(f"\t\tlog '{t} {name} overlapping: ' (Same_{t}: a b 64) '\\n';")
        want.append(f"{t} {name} overlapping: -1")
    out.append(f"\t\tfree q;")
    out.append(f"\t\tfree r;")
    out.append(f"\t\tfree a;")
    out.append(f"\t\tfree b;")
    out.append(f"\t[end]")
    out.append("")
out.append("}")
open(sys.argv[1], 'w').write('\n'.join(out) + '\n')
open(sys.argv[2], 'w').write('\n'.join(want) + '\n')
