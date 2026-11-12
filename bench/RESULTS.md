# PHI beside C and C#

Measured 2026-11-11 by `phi compare` on the benchmarks in `bench`, on Intel(R) Core(TM) Ultra 9 185H (Fedora Linux 42 (Workstation Edition)).

```
Seconds, the best of 7 runs of each program, from being started to having ended:

              PHI 64-bit  PHI 32-bit     gcc -O2     gcc -O0          C#
startup            0.000       0.000       0.000       0.000       0.016
collatz            0.925       0.885       0.248       0.553       0.310
crc                0.301       0.276       0.245       0.270       0.262
fib                0.430       0.416       0.099       0.548       0.298
lists              0.459       0.367       0.224       0.264       0.170
mandelbrot         0.413       0.409       0.100       0.223       0.113
matrix             0.457       0.462       0.048       0.364       0.148
sieve              0.379       0.373       0.311       0.635       0.361
sort               0.305       0.314       0.250       0.410       0.368

PHI 64-bit's time over each of the others' (1.50: PHI took half as long again; 0.50: half as long):

              PHI 32-bit     gcc -O2     gcc -O0          C#
collatz             1.05        3.73        1.67        2.99
crc                 1.09        1.23        1.12        1.15
fib                 1.03        4.33        0.79        1.44
lists               1.25        2.04        1.74        2.69
mandelbrot          1.01        4.12        1.85        3.64
matrix              0.99        9.58        1.26        3.09
sieve               1.02        1.22        0.60        1.05
sort                0.97        1.22        0.74        0.83
together            1.05        2.64        1.13        1.84

(together: the geometric mean of the rows above it)
(startup: under 10 ms in PHI, which is starting and not work, so not in this table)

C: gcc (GCC) 15.2.1 20260123 (Red Hat 15.2.1-7)
C#: .NET 10.0.108, a Release build, started as any program is (its time has the runtime starting and the JIT in it)
```
