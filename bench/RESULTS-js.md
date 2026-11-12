# PHI beside C and C#

Measured 2026-11-12 by `phi compare` on the benchmarks in `../PHI-Code-Library/libs/web`, on Intel(R) Core(TM) Ultra 9 185H (Fedora Linux 42 (Workstation Edition)).

```
Seconds, the best of 3 runs of each program, from being started to having ended:

                    PHI 64-bit  PHI 32-bit
release_js_bench         9.621       9.599

PHI 64-bit's time over each of the others' (1.50: PHI took half as long again; 0.50: half as long):

                    PHI 32-bit
release_js_bench          1.00

C: gcc (GCC) 15.2.1 20260123 (Red Hat 15.2.1-7)
Each program was given the arguments: run

What release_js_bench (PHI 64-bit) printed, the last time it ran:

  for-loop 100000001 in 1047 ms = 95.51 M/s r=100000001
  for-loop-let-sum 30000001 in 643 ms = 46.66 M/s r=-888471104
  calls 20000001 in 1118 ms = 17.89 M/s r=20000001
  prop-get 20000001 in 501 ms = 39.92 M/s r=60000003
  prop-set 20000001 in 510 ms = 39.22 M/s r=20000000
  method-call 10000001 in 710 ms = 14.08 M/s r=10000001
  array-rw 20000001 in 969 ms = 20.64 M/s r=422894520
  alloc-obj 3000001 in 346 ms = 8.67 M/s r=3000000
  alloc-arr 2000001 in 225 ms = 8.89 M/s r=2000000
  closure 3000001 in 211 ms = 14.22 M/s r=3000001
  str-concat 3000001 in 565 ms = 5.31 M/s r=3000001
  str-index 10000001 in 615 ms = 16.26 M/s r=992500104
  double-math 20000001 in 808 ms = 24.75 M/s r=6389059.798353338
  fib(32) 2 in 283 ms = 0 M/s r=2178309
  class-new 2000001 in 553 ms = 3.62 M/s r=-1453759936
  for-of 5000001 in 295 ms = 16.95 M/s r=27500055
  try-catch 1000001 in 422 ms = 2.37 M/s r=1784293664
  done
```
