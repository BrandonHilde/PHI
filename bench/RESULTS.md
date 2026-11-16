# PHI beside C and C#

Measured 2026-11-16 by `phi compare` on the benchmarks in `.`, on Intel(R) Core(TM) Ultra 9 185H (Fedora Linux 42 (Workstation Edition)).

```
Seconds, the best of 5 runs of each program, from being started to having ended:

              PHI 64-bit  PHI 32-bit     gcc -O2     gcc -O0          C#
startup            0.000       0.000       0.000       0.000       0.017
collatz            0.361       0.916       0.261       0.555       0.318
crc                0.247       0.283       0.246       0.276       0.269
fib                0.318       0.499       0.099       0.560       0.340
interp             0.676       0.909       0.634       1.117       0.681
lists              0.244       0.267       0.220       0.261       0.189
mandelbrot         0.121       0.291       0.099       0.230       0.123
maps               0.261       0.235       0.181       0.300       0.327
matrix             0.081       0.427       0.048       0.364       0.154
pixels             0.041           -       0.105       1.237       0.480
sieve              0.345       0.383       0.324       0.648       0.372
sort               0.299       0.310       0.261       0.415       0.372
strings            0.282       0.383       0.330       0.275       0.358

PHI 64-bit's time over each of the others' (1.50: PHI took half as long again; 0.50: half as long):

              PHI 32-bit     gcc -O2     gcc -O0          C#
collatz             0.39        1.38        0.65        1.14
crc                 0.87        1.00        0.89        0.91
fib                 0.64        3.21        0.57        0.93
interp              0.74        1.07        0.61        0.99
lists               0.91        1.11        0.94        1.29
mandelbrot          0.41        1.22        0.53        0.98
maps                1.11        1.44        0.87        0.80
matrix              0.19        1.70        0.22        0.53
pixels                 -        0.39        0.03        0.09
sieve               0.90        1.07        0.53        0.93
sort                0.96        1.14        0.72        0.80
strings             0.74        0.86        1.02        0.79
together            0.65        1.17        0.50        0.74

(together: the geometric mean of the rows above it)
(startup: under 10 ms in PHI, which is starting and not work, so not in this table)

C: gcc (GCC) 15.2.1 20260123 (Red Hat 15.2.1-7)
C#: .NET 10.0.108, a Release build, started as any program is (its time has the runtime starting and the JIT in it)
```
