# PHI beside C and C#

Measured 2026-11-12 by `phi compare` on the benchmarks in `bench`, on Intel(R) Core(TM) Ultra 9 185H (Fedora Linux 42 (Workstation Edition)).

```
Seconds, the best of 7 runs of each program, from being started to having ended:

              PHI 64-bit  PHI 32-bit     gcc -O2     gcc -O0          C#
startup            0.000       0.000       0.000       0.000       0.015
collatz            0.546       0.863       0.241       0.537       0.301
crc                0.295       0.269       0.233       0.265       0.250
fib                0.414       0.401       0.096       0.529       0.312
interp             0.703       0.897       0.599       1.102       0.660
lists              0.456       0.372       0.218       0.256       0.164
mandelbrot         0.284       0.285       0.096       0.218       0.112
matrix             0.408       0.419       0.047       0.353       0.145
sieve              0.365       0.368       0.303       0.605       0.354
sort               0.286       0.305       0.251       0.402       0.359

PHI 64-bit's time over each of the others' (1.50: PHI took half as long again; 0.50: half as long):

              PHI 32-bit     gcc -O2     gcc -O0          C#
collatz             0.63        2.26        1.02        1.81
crc                 1.10        1.26        1.11        1.18
fib                 1.03        4.30        0.78        1.33
interp              0.78        1.17        0.64        1.07
lists               1.23        2.10        1.78        2.78
mandelbrot          1.00        2.96        1.30        2.53
matrix              0.98        8.71        1.16        2.82
sieve               0.99        1.20        0.60        1.03
sort                0.94        1.14        0.71        0.80
together            0.95        2.17        0.95        1.54

(together: the geometric mean of the rows above it)
(startup: under 10 ms in PHI, which is starting and not work, so not in this table)

C: gcc (GCC) 15.2.1 20260123 (Red Hat 15.2.1-7)
C#: .NET 10.0.108, a Release build, started as any program is (its time has the runtime starting and the JIT in it)
```
