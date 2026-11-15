# PHI beside C and C#

Measured 2026-11-15 by `phi compare` on the benchmarks in `bench`, on Intel(R) Core(TM) Ultra 9 185H (Fedora Linux 42 (Workstation Edition)).

```
Seconds, the best of 5 runs of each program, from being started to having ended:

              PHI 64-bit  PHI 32-bit     gcc -O2     gcc -O0          C#
startup            0.000       0.000       0.000       0.000       0.013
collatz            0.340       0.862       0.239       0.519       0.303
crc                0.233       0.265       0.231       0.262       0.249
fib                0.316       0.470       0.095       0.524       0.310
interp             0.645       0.880       0.596       1.062       0.613
lists              0.193       0.253       0.201       0.237       0.154
mandelbrot         0.114       0.281       0.095       0.216       0.110
matrix             0.133       0.411       0.045       0.342       0.142
sieve              0.337       0.361       0.296       0.586       0.344
sort               0.267       0.289       0.245       0.382       0.357
strings            0.274       0.364       0.314       0.274       0.321

PHI 64-bit's time over each of the others' (1.50: PHI took half as long again; 0.50: half as long):

              PHI 32-bit     gcc -O2     gcc -O0          C#
collatz             0.39        1.42        0.65        1.12
crc                 0.88        1.01        0.89        0.94
fib                 0.67        3.32        0.60        1.02
interp              0.73        1.08        0.61        1.05
lists               0.76        0.96        0.82        1.26
mandelbrot          0.40        1.19        0.53        1.03
matrix              0.32        2.93        0.39        0.94
sieve               0.93        1.14        0.58        0.98
sort                0.93        1.09        0.70        0.75
strings             0.75        0.87        1.00        0.85
together            0.64        1.34        0.65        0.98

(together: the geometric mean of the rows above it)
(startup: under 10 ms in PHI, which is starting and not work, so not in this table)

C: gcc (GCC) 15.2.1 20260123 (Red Hat 15.2.1-7)
C#: .NET 10.0.108, a Release build, started as any program is (its time has the runtime starting and the JIT in it)
```
