# PHI beside C and C#

Measured 2026-11-12 by `phi compare` on the benchmarks in `bench`, on Intel(R) Core(TM) Ultra 9 185H (Fedora Linux 42 (Workstation Edition)).

```
Seconds, the best of 7 runs of each program, from being started to having ended:

              PHI 64-bit  PHI 32-bit     gcc -O2     gcc -O0          C#
startup            0.000       0.000       0.000       0.000       0.014
collatz            0.531       0.852       0.240       0.527       0.298
crc                0.293       0.259       0.232       0.264       0.249
fib                0.412       0.397       0.096       0.525       0.294
lists              0.442       0.355       0.219       0.250       0.155
mandelbrot         0.281       0.281       0.095       0.216       0.112
matrix             0.410       0.416       0.046       0.346       0.141
sieve              0.357       0.357       0.299       0.589       0.344
sort               0.277       0.304       0.248       0.387       0.357

PHI 64-bit's time over each of the others' (1.50: PHI took half as long again; 0.50: half as long):

              PHI 32-bit     gcc -O2     gcc -O0          C#
collatz             0.62        2.22        1.01        1.78
crc                 1.13        1.26        1.11        1.18
fib                 1.04        4.28        0.78        1.40
lists               1.24        2.02        1.77        2.85
mandelbrot          1.00        2.95        1.30        2.51
matrix              0.99        8.98        1.18        2.90
sieve               1.00        1.19        0.61        1.04
sort                0.91        1.12        0.72        0.78
together            0.98        2.33        1.00        1.63

(together: the geometric mean of the rows above it)
(startup: under 10 ms in PHI, which is starting and not work, so not in this table)

C: gcc (GCC) 15.2.1 20260123 (Red Hat 15.2.1-7)
C#: .NET 10.0.108, a Release build, started as any program is (its time has the runtime starting and the JIT in it)
```
