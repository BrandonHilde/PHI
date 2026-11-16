# PHI beside C and C#

Measured 2026-11-15 by `phi compare` on the benchmarks in `.`, on Intel(R) Core(TM) Ultra 9 185H (Fedora Linux 42 (Workstation Edition)).

```
Seconds, the best of 5 runs of each program, from being started to having ended:

              PHI 64-bit  PHI 32-bit     gcc -O2     gcc -O0          C#
startup            0.000       0.000       0.000       0.000       0.014
collatz            0.342       0.865       0.242       0.525       0.297
crc                0.233       0.265       0.231       0.266       0.254
fib                0.310       0.475       0.095       0.528       0.324
interp             0.653       0.883       0.597       1.056       0.630
lists              0.204       0.257       0.212       0.243       0.157
mandelbrot         0.115       0.282       0.096       0.217       0.112
maps               0.264       0.225       0.161       0.271       0.297
matrix             0.134       0.412       0.045       0.346       0.144
pixels             0.037           -       0.095       1.183       0.462
sieve              0.317       0.365       0.302       0.601       0.349
sort               0.268       0.291       0.248       0.392       0.357
strings            0.271       0.371       0.319       0.277       0.336

PHI 64-bit's time over each of the others' (1.50: PHI took half as long again; 0.50: half as long):

              PHI 32-bit     gcc -O2     gcc -O0          C#
collatz             0.40        1.42        0.65        1.15
crc                 0.88        1.01        0.88        0.92
fib                 0.65        3.27        0.59        0.96
interp              0.74        1.09        0.62        1.04
lists               0.79        0.96        0.84        1.30
mandelbrot          0.41        1.20        0.53        1.03
maps                1.18        1.64        0.97        0.89
matrix              0.33        2.96        0.39        0.93
pixels                 -        0.39        0.03        0.08
sieve               0.87        1.05        0.53        0.91
sort                0.92        1.08        0.68        0.75
strings             0.73        0.85        0.98        0.81
together            0.67        1.22        0.52        0.78

(together: the geometric mean of the rows above it)
(startup: under 10 ms in PHI, which is starting and not work, so not in this table)

C: gcc (GCC) 15.2.1 20260123 (Red Hat 15.2.1-7)
C#: .NET 10.0.108, a Release build, started as any program is (its time has the runtime starting and the JIT in it)
```
