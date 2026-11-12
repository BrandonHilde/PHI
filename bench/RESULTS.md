# PHI beside C and C#

Measured 2026-11-12 by `phi compare` on the benchmarks in `bench`, on Intel(R) Core(TM) Ultra 9 185H (Fedora Linux 42 (Workstation Edition)).

```
Seconds, the best of 7 runs of each program, from being started to having ended:

              PHI 64-bit  PHI 32-bit     gcc -O2     gcc -O0          C#
startup            0.000       0.000       0.000       0.000       0.016
collatz            0.552       0.877       0.244       0.544       0.306
crc                0.306       0.271       0.237       0.269       0.255
fib                0.423       0.405       0.097       0.536       0.310
interp             0.732       1.006       0.610       1.103       0.650
lists              0.466       0.380       0.219       0.263       0.166
mandelbrot         0.283       0.286       0.096       0.220       0.112
matrix             0.414       0.422       0.047       0.357       0.148
sieve              0.374       0.372       0.314       0.609       0.356
sort               0.283       0.309       0.255       0.405       0.361

PHI 64-bit's time over each of the others' (1.50: PHI took half as long again; 0.50: half as long):

              PHI 32-bit     gcc -O2     gcc -O0          C#
collatz             0.63        2.26        1.01        1.80
crc                 1.13        1.29        1.14        1.20
fib                 1.04        4.37        0.79        1.36
interp              0.73        1.20        0.66        1.13
lists               1.23        2.13        1.77        2.80
mandelbrot          0.99        2.96        1.29        2.52
matrix              0.98        8.83        1.16        2.79
sieve               1.01        1.19        0.61        1.05
sort                0.92        1.11        0.70        0.78
together            0.94        2.18        0.96        1.56

(together: the geometric mean of the rows above it)
(startup: under 10 ms in PHI, which is starting and not work, so not in this table)

C: gcc (GCC) 15.2.1 20260123 (Red Hat 15.2.1-7)
C#: .NET 10.0.108, a Release build, started as any program is (its time has the runtime starting and the JIT in it)
```
