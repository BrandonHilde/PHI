# Makes lib/phi/lang/float.phi from float.phi.in: each {0xHHHHHHHHHHHHHHHH} becomes the f64
# with those bits, written so it reads back exactly, and {IPIO2}, {PIO2} and {NPIO2} become
# tables worked out from pi (2/pi and pi/2 in 24-bit pieces, n pi/2's high halves), which
# match fdlibm's own. Run from anywhere: python3 tools/float/gen.py
import os, re, struct, sys
from fractions import Fraction
# pi to 3000 bits with Machin's formula, in whole numbers
B = 3000
def arctan_inv(n):
    one = 1 << (B + 20); s = 0; t = one // n; k = 1; sign = 1; n2 = n * n
    while t:
        s += sign * (t // k); t //= n2; k += 2; sign = -sign
    return s
PI = Fraction(16 * arctan_inv(5) - 4 * arctan_inv(239), 1 << (B + 20))

def f64(h):
    return struct.unpack('>d', h.to_bytes(8, 'big'))[0]
def lit(x):
    r = repr(x).replace('e+', 'e')
    return r
def hi_word(x):
    return struct.unpack('>Q', struct.pack('>d', x))[0] >> 32

# 2/pi, 24 bits at a time
two_pi = 2 / PI
ipio2 = []
for j in range(66):
    v = two_pi * (1 << (24 * (j + 1)))
    ipio2.append(int(v) % (1 << 24))
# pi/2, 24 bits at a time (the first from its 2^0 bit)
half = PI / 2
pio2 = []; done = Fraction(0)
for i in range(8):
    scale = 1 << (23 + 24 * i)
    piece = Fraction(int(half * scale), scale) - done
    pio2.append(float(piece)); done += piece
    assert Fraction(pio2[-1]) == piece
npio2 = [hi_word(float(n * half)) for n in range(1, 33)]

here = os.path.dirname(os.path.abspath(__file__))
src = open(os.path.join(here, 'float.phi.in')).read()
src = re.sub(r'\{0x([0-9A-Fa-f]{16})\}', lambda m: lit(f64(int(m.group(1), 16))), src)
src = src.replace('{IPIO2}', ' '.join(f'0x{v:06X}' for v in ipio2) + ';')
src = src.replace('{PIO2}', ' '.join(lit(v) for v in pio2) + ';')
src = src.replace('{NPIO2}', ' '.join(f'0x{v:08X}' for v in npio2) + ';')
assert not re.search(r'\{(0x|IPIO2|PIO2|NPIO2)', src)
open(os.path.join(here, '..', '..', 'lib', 'phi', 'lang', 'float.phi'), 'w').write(src)
print('ipio2', ' '.join(f'{v:06X}' for v in ipio2[:6]), '...', f'{ipio2[-1]:06X}')
print('pio2', ' '.join(f'{struct.unpack(">Q", struct.pack(">d", v))[0]:016X}' for v in pio2))
print('npio2', ' '.join(f'{v:08X}' for v in npio2[:6]), '...', f'{npio2[-1]:08X}')
