# PHI beside C and C#

Measured 2026-11-12 by `phi compare` on the benchmarks in `bench`, on Intel(R) Core(TM) Ultra 9 185H (Fedora Linux 42 (Workstation Edition)).

```
Seconds, the best of 7 runs of each program, from being started to having ended:

              PHI 64-bit  PHI 32-bit     gcc -O2     gcc -O0          C#
startup            0.000       0.000       0.000       0.000       0.014
collatz            0.887       0.835       0.242       0.523       0.301
crc                0.294       0.262       0.234       0.266       0.249
fib                0.416       0.395       0.095       0.527       0.290
lists              0.443       0.350       0.205       0.236       0.160
mandelbrot         0.281       0.281       0.095       0.217       0.112
matrix             0.407       0.412       0.046       0.344       0.144
sieve              0.361       0.357       0.301       0.595       0.348
sort               0.288       0.302       0.247       0.384       0.358

PHI 64-bit's time over each of the others' (1.50: PHI took half as long again; 0.50: half as long):

              PHI 32-bit     gcc -O2     gcc -O0          C#
collatz             1.06        3.67        1.70        2.95
crc                 1.12        1.26        1.11        1.18
fib                 1.05        4.35        0.79        1.44
lists               1.27        2.16        1.88        2.77
mandelbrot          1.00        2.97        1.29        2.51
matrix              0.99        8.83        1.18        2.82
sieve               1.01        1.20        0.61        1.04
sort                0.95        1.16        0.75        0.81
together            1.05        2.51        1.09        1.74

(together: the geometric mean of the rows above it)
(startup: under 10 ms in PHI, which is starting and not work, so not in this table)

C: gcc (GCC) 15.2.1 20260123 (Red Hat 15.2.1-7)
C#: .NET 10.0.108, a Release build, started as any program is (its time has the runtime starting and the JIT in it)
```
