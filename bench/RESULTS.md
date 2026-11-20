# PHI beside C and C#

Measured 2026-11-19 by `phi compare` on the benchmarks in `bench`, on Intel(R) Core(TM) Ultra 9 185H (Fedora Linux 42 (Workstation Edition)).

```
Seconds, the best of 7 runs of each program, from being started to having ended:

              PHI 64-bit  PHI 32-bit     gcc -O2     gcc -O0          C#
startup            0.000       0.000       0.000       0.000       0.015
collatz            0.341       0.864       0.240       0.523       0.300
crc                0.231       0.232       0.231       0.264       0.249
fib                0.296       0.329       0.095       0.523       0.330
interp             0.646       0.712       0.596       1.054       0.631
lists              0.250       0.248       0.215       0.260       0.157
mandelbrot         0.106       0.116       0.094       0.216       0.111
maps               0.210       0.270       0.162       0.275       0.297
matrix             0.072       0.122       0.045       0.345       0.140
pixels             0.039           -       0.102       1.174       0.477
sieve              0.331       0.321       0.302       0.606       0.348
sort               0.273       0.344       0.247       0.382       0.356
strings            0.267       0.352       0.313       0.271       0.333

PHI 64-bit's time over each of the others' (1.50: PHI took half as long again; 0.50: half as long):

              PHI 32-bit     gcc -O2     gcc -O0          C#
collatz             0.39        1.42        0.65        1.14
crc                 1.00        1.00        0.87        0.93
fib                 0.90        3.11        0.57        0.90
interp              0.91        1.08        0.61        1.02
lists               1.01        1.16        0.96        1.59
mandelbrot          0.91        1.12        0.49        0.95
maps                0.78        1.30        0.77        0.71
matrix              0.58        1.58        0.21        0.51
pixels                 -        0.38        0.03        0.08
sieve               1.03        1.09        0.55        0.95
sort                0.79        1.11        0.72        0.77
strings             0.76        0.85        0.99        0.80
together            0.80        1.14        0.49        0.74

(together: the geometric mean of the rows above it)
(startup: under 10 ms in PHI, which is starting and not work, so not in this table)

C: gcc (GCC) 15.2.1 20260123 (Red Hat 15.2.1-7)
C#: .NET 10.0.108, a Release build, started as any program is (its time has the runtime starting and the JIT in it)
```
