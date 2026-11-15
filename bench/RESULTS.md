# PHI beside C and C#

Measured 2026-11-14 by `phi compare` on the benchmarks in `bench`, on Intel(R) Core(TM) Ultra 9 185H (Fedora Linux 42 (Workstation Edition)).

```
Seconds, the best of 3 runs of each program, from being started to having ended:

              PHI 64-bit  PHI 32-bit     gcc -O2     gcc -O0          C#
startup            0.000       0.000       0.000       0.000       0.015
collatz            0.546       0.875       0.241       0.537       0.307
crc                0.305       0.269       0.232       0.267       0.251
fib                0.428       0.402       0.099       0.528       0.333
interp             0.691       0.887       0.598       1.082       0.611
lists              0.458       0.375       0.210       0.253       0.164
mandelbrot         0.304       0.295       0.095       0.219       0.114
matrix             0.411       0.413       0.047       0.356       0.144
sieve              0.364       0.363       0.300       0.603       0.352
sort               0.279       0.312       0.251       0.409       0.362
strings            0.446       0.481       0.328       0.277       0.339

PHI 64-bit's time over each of the others' (1.50: PHI took half as long again; 0.50: half as long):

              PHI 32-bit     gcc -O2     gcc -O0          C#
collatz             0.62        2.27        1.02        1.78
crc                 1.14        1.31        1.14        1.21
fib                 1.06        4.34        0.81        1.29
interp              0.78        1.16        0.64        1.13
lists               1.22        2.18        1.81        2.80
mandelbrot          1.03        3.21        1.39        2.67
matrix              1.00        8.76        1.16        2.86
sieve               1.00        1.21        0.60        1.03
sort                0.89        1.11        0.68        0.77
strings             0.93        1.36        1.61        1.32
together            0.95        2.10        1.01        1.53

(together: the geometric mean of the rows above it)
(startup: under 10 ms in PHI, which is starting and not work, so not in this table)

C: gcc (GCC) 15.2.1 20260123 (Red Hat 15.2.1-7)
C#: .NET 10.0.108, a Release build, started as any program is (its time has the runtime starting and the JIT in it)
```
