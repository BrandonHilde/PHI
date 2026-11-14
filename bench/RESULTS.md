# PHI beside C and C#

Measured 2026-11-13 by `phi compare` on the benchmarks in `bench`, on Intel(R) Core(TM) Ultra 9 185H (Fedora Linux 42 (Workstation Edition)).

```
Seconds, the best of 7 runs of each program, from being started to having ended:

              PHI 64-bit  PHI 32-bit     gcc -O2     gcc -O0          C#
startup            0.000       0.000       0.000       0.000       0.015
collatz            0.540       0.871       0.240       0.528       0.301
crc                0.294       0.267       0.232       0.265       0.249
fib                0.410       0.401       0.098       0.527       0.317
interp             0.703       0.898       0.609       1.100       0.615
lists              0.447       0.367       0.221       0.253       0.158
mandelbrot         0.289       0.282       0.095       0.216       0.111
matrix             0.408       0.411       0.047       0.346       0.144
sieve              0.360       0.364       0.298       0.598       0.349
sort               0.280       0.299       0.249       0.397       0.359
strings            0.440       0.473       0.323       0.272       0.337

PHI 64-bit's time over each of the others' (1.50: PHI took half as long again; 0.50: half as long):

              PHI 32-bit     gcc -O2     gcc -O0          C#
collatz             0.62        2.25        1.02        1.80
crc                 1.10        1.27        1.11        1.18
fib                 1.02        4.18        0.78        1.29
interp              0.78        1.15        0.64        1.14
lists               1.22        2.02        1.77        2.83
mandelbrot          1.03        3.04        1.33        2.60
matrix              0.99        8.72        1.18        2.83
sieve               0.99        1.21        0.60        1.03
sort                0.93        1.12        0.71        0.78
strings             0.93        1.36        1.62        1.30
together            0.95        2.06        1.01        1.53

(together: the geometric mean of the rows above it)
(startup: under 10 ms in PHI, which is starting and not work, so not in this table)

C: gcc (GCC) 15.2.1 20260123 (Red Hat 15.2.1-7)
C#: .NET 10.0.108, a Release build, started as any program is (its time has the runtime starting and the JIT in it)
```
