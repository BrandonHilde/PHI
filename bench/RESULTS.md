# PHI beside C and C#

Measured 2026-11-15 by `phi compare` on the benchmarks in `bench`, on Intel(R) Core(TM) Ultra 9 185H (Fedora Linux 42 (Workstation Edition)).

```
Seconds, the best of 5 runs of each program, from being started to having ended:

              PHI 64-bit  PHI 32-bit     gcc -O2     gcc -O0          C#
startup            0.000       0.000       0.000       0.000       0.014
collatz            0.343       0.867       0.239       0.518       0.297
crc                0.230       0.264       0.231       0.263       0.249
fib                0.313       0.472       0.097       0.528       0.293
interp             0.653       0.887       0.596       1.073       0.621
lists              0.201       0.264       0.204       0.246       0.159
mandelbrot         0.114       0.281       0.095       0.217       0.112
maps               0.264       0.229       0.163       0.277       0.304
matrix             0.138       0.420       0.046       0.343       0.142
sieve              0.344       0.363       0.296       0.591       0.347
sort               0.270       0.295       0.247       0.392       0.360
strings            0.274       0.371       0.320       0.274       0.335

PHI 64-bit's time over each of the others' (1.50: PHI took half as long again; 0.50: half as long):

              PHI 32-bit     gcc -O2     gcc -O0          C#
collatz             0.40        1.44        0.66        1.15
crc                 0.87        1.00        0.88        0.93
fib                 0.66        3.23        0.59        1.07
interp              0.74        1.10        0.61        1.05
lists               0.76        0.99        0.82        1.26
mandelbrot          0.41        1.20        0.53        1.02
maps                1.15        1.62        0.95        0.87
matrix              0.33        3.02        0.40        0.97
sieve               0.95        1.16        0.58        0.99
sort                0.92        1.09        0.69        0.75
strings             0.74        0.86        1.00        0.82
together            0.67        1.37        0.68        0.98

(together: the geometric mean of the rows above it)
(startup: under 10 ms in PHI, which is starting and not work, so not in this table)

C: gcc (GCC) 15.2.1 20260123 (Red Hat 15.2.1-7)
C#: .NET 10.0.108, a Release build, started as any program is (its time has the runtime starting and the JIT in it)
```
