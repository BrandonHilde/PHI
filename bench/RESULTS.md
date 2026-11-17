# PHI beside C and C#

Measured 2026-11-16 by `phi compare` on the benchmarks in `bench`, on Intel(R) Core(TM) Ultra 9 185H (Fedora Linux 42 (Workstation Edition)).

```
Seconds, the best of 5 runs of each program, from being started to having ended:

              PHI 64-bit  PHI 32-bit     gcc -O2     gcc -O0          C#
startup            0.000       0.000       0.000       0.000       0.016
collatz            0.344       0.857       0.239       0.515       0.299
crc                0.231       0.232       0.231       0.265       0.251
fib                0.307       0.307       0.095       0.522       0.326
interp             0.646       0.785       0.598       1.057       0.627
lists              0.226       0.221       0.209       0.241       0.161
mandelbrot         0.114       0.119       0.095       0.216       0.113
maps               0.213       0.264       0.159       0.266       0.302
matrix             0.076       0.124       0.046       0.343       0.137
pixels             0.041           -       0.096       1.177       0.452
sieve              0.325       0.319       0.298       0.588       0.343
sort               0.269       0.341       0.247       0.380       0.356
strings            0.269       0.376       0.311       0.272       0.331

PHI 64-bit's time over each of the others' (1.50: PHI took half as long again; 0.50: half as long):

              PHI 32-bit     gcc -O2     gcc -O0          C#
collatz             0.40        1.44        0.67        1.15
crc                 0.99        1.00        0.87        0.92
fib                 1.00        3.23        0.59        0.94
interp              0.82        1.08        0.61        1.03
lists               1.02        1.08        0.94        1.40
mandelbrot          0.95        1.19        0.53        1.01
maps                0.81        1.34        0.80        0.71
matrix              0.62        1.65        0.22        0.56
pixels                 -        0.43        0.03        0.09
sieve               1.02        1.09        0.55        0.95
sort                0.79        1.09        0.71        0.75
strings             0.72        0.87        0.99        0.81
together            0.80        1.17        0.50        0.75

(together: the geometric mean of the rows above it)
(startup: under 10 ms in PHI, which is starting and not work, so not in this table)

C: gcc (GCC) 15.2.1 20260123 (Red Hat 15.2.1-7)
C#: .NET 10.0.108, a Release build, started as any program is (its time has the runtime starting and the JIT in it)
```
