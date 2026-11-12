# PHI beside C and C#

Measured 2026-11-12 by `phi compare` on the benchmarks in `bench`, on Intel(R) Core(TM) Ultra 9 185H (Fedora Linux 42 (Workstation Edition)).

```
Seconds, the best of 7 runs of each program, from being started to having ended:

              PHI 64-bit  PHI 32-bit     gcc -O2     gcc -O0          C#
startup            0.000       0.000       0.000       0.000       0.014
collatz            0.531       0.847       0.240       0.522       0.300
crc                0.293       0.261       0.231       0.263       0.249
fib                0.414       0.394       0.097       0.526       0.289
lists              0.444       0.350       0.206       0.240       0.158
mandelbrot         0.281       0.281       0.095       0.216       0.110
matrix             0.408       0.413       0.046       0.344       0.141
sieve              0.357       0.358       0.299       0.595       0.347
sort               0.275       0.302       0.246       0.386       0.355

PHI 64-bit's time over each of the others' (1.50: PHI took half as long again; 0.50: half as long):

              PHI 32-bit     gcc -O2     gcc -O0          C#
collatz             0.63        2.21        1.02        1.77
crc                 1.12        1.27        1.11        1.18
fib                 1.05        4.28        0.79        1.43
lists               1.27        2.16        1.85        2.82
mandelbrot          1.00        2.97        1.30        2.55
matrix              0.99        8.88        1.18        2.88
sieve               1.00        1.19        0.60        1.03
sort                0.91        1.12        0.71        0.77
together            0.98        2.34        1.01        1.63

(together: the geometric mean of the rows above it)
(startup: under 10 ms in PHI, which is starting and not work, so not in this table)

C: gcc (GCC) 15.2.1 20260123 (Red Hat 15.2.1-7)
C#: .NET 10.0.108, a Release build, started as any program is (its time has the runtime starting and the JIT in it)
```
