# Writes tests/vec_lanes_program.phi, every vec operation on every kind of lane, with numbers at
# the ends of each kind's range, and tests/vec_lanes_program.expected, what it should print,
# worked out here on its own (not by PHI). Run it again after changing it:
#   python3 tests/support/vec_lanes.py tests/vec_lanes_program.phi tests/vec_lanes_program.expected
import struct, math, sys

KINDS = {
    'i8':  (8, True,  False), 'u8':  (8, False, False),
    'i16': (16, True, False), 'u16': (16, False, False),
    'int': (32, True, False), 'u32': (32, False, False),
    'i64': (64, True, False), 'u64': (64, False, False),
    'f32': (32, True, True),  'f64': (64, True, True),
}
A = {
 'i8':  [-128,-127,-100,-2,-1,0,1,2,3,50,100,126,127,-50,7,-7],
 'u8':  [0,1,2,127,128,200,255,254,10,20,30,40,50,60,70,80],
 'i16': [-32768,-1,0,1,32767,-300,1234,-5000],
 'u16': [0,1,65535,32768,40000,255,256,1000],
 'int': [-2147483648,-7,2147483647,123456],
 'u32': [0,4294967295,2147483648,77],
 'i64': [-9223372036854775808,123456789012],
 'u64': [18446744073709551615,5],
 'f32': [1.5,-2.25,0.5,100.0],
 'f64': [1.5,-2.25],
}
B = {
 'i8':  [1,-1,100,-2,127,-128,5,-3,0,60,-100,1,127,-128,9,13],
 'u8':  [255,1,254,1,128,100,255,1,5,7,9,11,13,15,17,19],
 'i16': [1,-1,32767,-32768,1,300,-1234,7],
 'u16': [65535,1,1,32768,30000,1,255,999],
 'int': [-1,3,2,-654321],
 'u32': [1,2,2147483648,4000000000],
 'i64': [-1,-98765],
 'u64': [2,18446744073709551610],
 'f32': [0.5,1.5,-0.125,3.0],
 'f64': [0.5,4.0],
}

def wrap(x, bits, signed):
    x &= (1 << bits) - 1
    if signed and x >= 1 << (bits - 1): x -= 1 << bits
    return x

def sat(x, bits, signed):
    lo, hi = (-(1 << (bits-1)), (1 << (bits-1)) - 1) if signed else (0, (1 << bits) - 1)
    return max(lo, min(hi, x))

def f32(x): return struct.unpack('<f', struct.pack('<f', x))[0]

def fmt(x, kind):
    bits, signed, isfloat = KINDS[kind]
    if not isfloat: return str(x)
    # phi_float_to_str: up to 6 digits after the point, without trailing zeros (values here are simple)
    if x == int(x) and abs(x) < 1e12: return str(int(x)) if x != 0 or math.copysign(1, x) > 0 else '-0' if False else '0'
    s = ('%.6f' % x).rstrip('0').rstrip('.')
    return s

def lit(x, kind):
    bits, signed, isfloat = KINDS[kind]
    if isfloat:
        s = repr(float(x))
        return s
    if kind == 'i64' and x == -9223372036854775808: return '0x8000000000000000'
    if kind == 'u64' and x >= 1 << 63: return hex(x)
    return str(x)

def show(vals, kind): return ' '.join(fmt(v, kind) for v in vals)

prog = []
want = []
def line(label, expr, expected):
    prog.append(f"\t\tlog '{label}: ' {expr} '\\n';")
    want.append(f"{label}: {expected}")

def mask(cond, kind):
    bits, signed, isfloat = KINDS[kind]
    return -1 if signed else (1 << bits) - 1

for kind, (bits, signed, isfloat) in KINDS.items():
    a, b = A[kind], B[kind]
    n = len(a)
    T = f'vec<{kind}>'
    prog.append(f"\t[Test_{kind}: int s: 0;]")
    prog.append(f"\t\t{T} a: {' '.join(lit(x, kind) for x in a)};")
    prog.append(f"\t\t{T} b: {' '.join(lit(x, kind) for x in b)};")
    W = lambda x: wrap(x, bits, signed) if not isfloat else (f32(x) if kind == 'f32' else x)
    line(f'{kind} a', 'a', show(a, kind))
    line(f'{kind} add', '(a + b)', show([W(x + y) for x, y in zip(a, b)], kind))
    line(f'{kind} sub', '(a - b)', show([W(x - y) for x, y in zip(a, b)], kind))
    line(f'{kind} mul', '(a * b)', show([W(x * y) for x, y in zip(a, b)], kind))
    line(f'{kind} neg', '(-a)', show([W(-x) for x in a], kind))
    line(f'{kind} min', '(a.Min: b)', show([min(x, y) for x, y in zip(a, b)], kind))
    line(f'{kind} max', '(a.Max: b)', show([max(x, y) for x, y in zip(a, b)], kind))
    line(f'{kind} smallest', '(a.Smallest)', fmt(min(a), kind))
    line(f'{kind} largest', '(a.Largest)', fmt(max(a), kind))
    if isfloat:
        line(f'{kind} sum', '(a.Sum)', fmt(W(sum(a)), kind))
        line(f'{kind} div', '(a / b)', show([W(x / y) for x, y in zip(a, b)], kind))
        line(f'{kind} abs', '(a.Abs)', show([abs(x) for x in a], kind))
        sq = [4.0, 2.25, 0.25, 9.0][:n]
        prog.append(f"\t\t{T} r: {' '.join(lit(x, kind) for x in sq)};")
        line(f'{kind} sqrt', '(r.Sqrt)', show([math.sqrt(x) for x in sq], kind))
    else:
        line(f'{kind} sum', '(a.Sum)', fmt(wrap(sum(a), bits, signed), kind))
        line(f'{kind} and', '(a & b)', show([wrap(x & y, bits, signed) for x, y in zip(a, b)], kind))
        line(f'{kind} or', '(a | b)', show([wrap(x | y, bits, signed) for x, y in zip(a, b)], kind))
        line(f'{kind} xor', '(a ^ b)', show([wrap(x ^ y, bits, signed) for x, y in zip(a, b)], kind))
        line(f'{kind} not', '(~a)', show([wrap(~x, bits, signed) for x in a], kind))
        if signed:
            line(f'{kind} abs', '(a.Abs)', show([wrap(abs(x), bits, signed) for x in a], kind))
        for c in [0, 1, 3, bits - 1, bits, 100]:
            sl = [wrap(x << c, bits, signed) if c < bits else 0 for x in a]
            if signed: sr = [x >> min(c, bits - 1) for x in a]
            else: sr = [x >> c if c < bits else 0 for x in a]
            line(f'{kind} shl {c}', f'(a << {c})', show(sl, kind))
            line(f'{kind} shr {c}', f'(a >> {c})', show(sr, kind))
        # by a variable count
        prog.append(f"\t\tif s >= 0")
        prog.append(f"\t\t\tlog '{kind} shl var: ' (a << s) '\\n';")
        prog.append(f"\t\t\tlog '{kind} shr var: ' (a >> s) '\\n';")
        prog.append(f"\t\t;;")
        for c in [0, 2, bits - 1, bits, 40]:
            sl = [wrap(x << c, bits, signed) if c < bits else 0 for x in a]
            if signed: sr = [x >> min(c, bits - 1) for x in a]
            else: sr = [x >> c if c < bits else 0 for x in a]
            want.append(('VAR', kind, c, f"{kind} shl var: {show(sl, kind)}", f"{kind} shr var: {show(sr, kind)}"))
        if bits <= 16:
            line(f'{kind} addsat', '(a.AddSat: b)', show([sat(x + y, bits, signed) for x, y in zip(a, b)], kind))
            line(f'{kind} subsat', '(a.SubSat: b)', show([sat(x - y, bits, signed) for x, y in zip(a, b)], kind))
        if kind in ('u8', 'u16'):
            line(f'{kind} average', '(a.Average: b)', show([(x + y + 1) >> 1 for x, y in zip(a, b)], kind))
    # comparisons, as masks picked from
    M = lambda conds: show([1 if c else 0 for c in conds], kind)
    prog.append(f"\t\t{T} one: 1;")
    prog.append(f"\t\t{T} zero: 0;")
    prog.append(f"\t\t{T} lt: (a.Less: b);")
    prog.append(f"\t\t{T} gt: (a.Greater: b);")
    prog.append(f"\t\t{T} eq: (a.Equal: a);")
    prog.append(f"\t\t{T} eqb: (a.Equal: b);")
    line(f'{kind} less', '(lt.Pick: one zero)', M([x < y for x, y in zip(a, b)]))
    line(f'{kind} greater', '(gt.Pick: one zero)', M([x > y for x, y in zip(a, b)]))
    line(f'{kind} equal', '(eq.Pick: one zero)', M([True] * n))
    line(f'{kind} equal b', '(eqb.Pick: one zero)', M([x == y for x, y in zip(a, b)]))
    line(f'{kind} any', '(lt.Any)', str(int(any(x < y for x, y in zip(a, b)))))
    line(f'{kind} all', '(lt.All)', str(int(all(x < y for x, y in zip(a, b)))))
    line(f'{kind} all equal', '(eq.All)', '1')
    line(f'{kind} is', '(a is a)', '1')
    line(f'{kind} is b', '(a is b)', '0')
    line(f'{kind} is not', '(a is not b)', '1')
    line(f'{kind} lane', 'a:1', fmt(a[1], kind))
    prog.append(f"\t\ta:0 is b:1;")
    line(f'{kind} set', 'a:0', fmt(b[1], kind))
    prog.append(f"\t\ta:0 is {lit(A[kind][0], kind)};")
    # widen and narrow
    wider = {'i8': 'i16', 'u8': 'u16', 'i16': 'int', 'u16': 'u32', 'int': 'i64', 'u32': 'u64', 'f32': 'f64'}
    if kind in wider:
        line(f'{kind} widen', '(a.Widen)', show(a[:n // 2], wider[kind]))
        line(f'{kind} widen high', '(a.WidenHigh)', show(a[n // 2:], wider[kind]))
    narrower = {v: k for k, v in wider.items()}
    if kind in narrower:
        nk = narrower[kind]
        nb, ns, nf = KINDS[nk]
        if nf:
            out = [f32(x) for x in a + b]
        else:
            out = [sat(x, nb, ns) for x in a + b]
        line(f'{kind} narrow', '(a.Narrow: b)', show(out, nk))
    prog.append("\t[end]")
    prog.append("")

# conversions
conv = []
conv.append("\t[Conversions:]")
conv.append("\t\tvec<int> i: -7 0 3 2147483647;")
conv.append("\t\tvec<f32> f: (i as vec<f32>);")
conv.append("\t\tlog 'int as f32: ' f '\\n';")
want_conv = ["int as f32: -7 0 3 2147483648"]
conv.append("\t\tvec<f32> g: -2.75 1.5 100.25 -0.5;")
conv.append("\t\tlog 'f32 as int: ' (g as vec<int>) '\\n';")
want_conv.append("f32 as int: -2 1 100 0")
conv.append("\t\tvec<u32> u: 4000000000 1 0 3000000000;")
conv.append("\t\tlog 'u32 as f32: ' (u as vec<f32>) '\\n';")
want_conv.append("u32 as f32: 4000000000 1 0 3000000000")
conv.append("\t\tvec<f32> big: 4000000000.0 2.5 0.0 3000000000.0;")
conv.append("\t\tlog 'f32 as u32: ' (big as vec<u32>) '\\n';")
want_conv.append("f32 as u32: 4000000000 2 0 3000000000")
conv.append("\t\tvec<i64> l: -5 123456789012;")
conv.append("\t\tlog 'i64 as f64: ' (l as vec<f64>) '\\n';")
want_conv.append("i64 as f64: -5 123456789012")
conv.append("\t\tvec<u64> ul: 0xFFFFFFFFFFFFF800 7;")
conv.append("\t\tlog 'u64 as f64: ' (ul as vec<f64>) '\\n';")
want_conv.append("u64 as f64: 1.844674e19 7")
conv.append("\t\tvec<f64> d: -3.75 1e10;")
conv.append("\t\tlog 'f64 as i64: ' (d as vec<i64>) '\\n';")
want_conv.append("f64 as i64: -3 10000000000")
conv.append("\t\tvec<u8> bytes: 1 2 3 4 5 6 7 8 9 10 11 12 13 14 15 16;")
conv.append("\t\tvec<u32> words: (bytes as vec<u32>);")
conv.append("\t\tlog 'u8 as u32: ' words '\\n';")
want_conv.append("u8 as u32: 67305985 134678021 202050057 269422093")
conv.append("\t[end]")

main = ["# Made by tests/support/vec_lanes.py: every vec operation on every kind of lane (see there).", "phi.VecLanesProgram:Program", "{"]
for kind in KINDS:
    if KINDS[kind][2]: main.append(f"\tcall Test_{kind};")
    else:
        for c in [0, 2, KINDS[kind][0] - 1, KINDS[kind][0], 40]:
            main.append(f"\tcall Test_{kind}: {c};")
main.append("\tcall Conversions;")
main.append("\texit 0;")
main.append("")
open(sys.argv[1], 'w').write('\n'.join(main + prog + conv + ["}"]) + '\n')

# expected: each Test_ call prints its lines; the variable shift lines come where the `if` is, for each call
out = []
for kind in KINDS:
    calls = [None] if KINDS[kind][2] else [0, 2, KINDS[kind][0] - 1, KINDS[kind][0], 40]
    lines = [w for w in want if (isinstance(w, str) and w.startswith(kind + ' ')) or (isinstance(w, tuple) and w[1] == kind)]
    for c in calls:
        # the plain lines up to the shifts by a variable, those for this count, then the rest
        before, after, var = [], [], None
        seen_var = False
        for w in lines:
            if isinstance(w, tuple):
                if w[2] == c: var = (w[3], w[4])
                seen_var = True
                continue
            (after if seen_var else before).append(w)
        out += before
        if var: out += list(var)
        out += after
out += want_conv
open(sys.argv[2], 'w').write('\n'.join(out) + '\n')
