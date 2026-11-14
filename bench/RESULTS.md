# PHI beside C and C#

Measured 2026-11-13 by `phi compare` on the benchmarks in `bench`, on Intel(R) Core(TM) Ultra 9 185H (Fedora Linux 42 (Workstation Edition)).

```
Seconds, the best of 7 runs of each program, from being started to having ended:

              PHI 64-bit  PHI 32-bit     gcc -O2     gcc -O0          C#
startup            0.000       0.000       0.000       0.000       0.015
collatz            0.557       0.883       0.241       0.547       0.305
crc                0.298       0.270       0.235       0.268       0.256
fib                0.428       0.414       0.099       0.543       0.335
interp             0.701       0.897       0.605       1.104       0.618
lists              0.464       0.378       0.213       0.257       0.166
mandelbrot         0.292       0.286       0.096       0.217       0.114
matrix             0.413       0.417       0.047       0.355       0.148
sieve              0.372       0.371       0.306       0.612       0.354
sort               0.281       0.307       0.252       0.405       0.363
strings            1.497       5.775       0.321       0.277       0.333

PHI 64-bit's time over each of the others' (1.50: PHI took half as long again; 0.50: half as long):

              PHI 32-bit     gcc -O2     gcc -O0          C#
collatz             0.63        2.31        1.02        1.83
crc                 1.10        1.27        1.11        1.16
fib                 1.03        4.33        0.79        1.28
interp              0.78        1.16        0.64        1.14
lists               1.23        2.18        1.81        2.80
mandelbrot          1.02        3.06        1.34        2.56
matrix              0.99        8.81        1.16        2.78
sieve               1.00        1.22        0.61        1.05
sort                0.91        1.11        0.69        0.77
strings             0.26        4.66        5.40        4.49
together            0.84        2.36        1.14        1.72

(together: the geometric mean of the rows above it)
(startup: under 10 ms in PHI, which is starting and not work, so not in this table)

C: gcc (GCC) 15.2.1 20260123 (Red Hat 15.2.1-7)
C#: .NET 10.0.108, a Release build, started as any program is (its time has the runtime starting and the JIT in it)
```
