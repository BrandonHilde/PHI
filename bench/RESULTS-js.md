# PHI beside C and C#

Measured 2026-11-12 by `phi compare` on the benchmarks in `../PHI-Code-Library/libs/web`, on Intel(R) Core(TM) Ultra 9 185H (Fedora Linux 42 (Workstation Edition)).

```
Seconds, the best of 3 runs of each program, from being started to having ended:

                    PHI 64-bit  PHI 32-bit
release_js_bench         9.793       9.625

PHI 64-bit's time over each of the others' (1.50: PHI took half as long again; 0.50: half as long):

                    PHI 32-bit
release_js_bench          1.02

C: gcc (GCC) 15.2.1 20260123 (Red Hat 15.2.1-7)
Each program was given the arguments: run

What release_js_bench (PHI 64-bit) printed, the last time it ran:

  for-loop 100000001 in 1203 ms = 83.13 M/s r=100000001
  for-loop-let-sum 30000001 in 715 ms = 41.96 M/s r=-888471104
  calls 20000001 in 1063 ms = 18.81 M/s r=20000001
  prop-get 20000001 in 518 ms = 38.61 M/s r=60000003
  prop-set 20000001 in 509 ms = 39.29 M/s r=20000000
  method-call 10000001 in 700 ms = 14.29 M/s r=10000001
  array-rw 20000001 in 1025 ms = 19.51 M/s r=422894520
  alloc-obj 3000001 in 348 ms = 8.62 M/s r=3000000
  alloc-arr 2000001 in 229 ms = 8.73 M/s r=2000000
  closure 3000001 in 212 ms = 14.15 M/s r=3000001
  str-concat 3000001 in 646 ms = 4.64 M/s r=3000001
  str-index 10000001 in 599 ms = 16.69 M/s r=992500104
  double-math 20000001 in 768 ms = 26.04 M/s r=6389059.798353338
  fib(32) 2 in 267 ms = 0 M/s r=2178309
  class-new 2000001 in 563 ms = 3.55 M/s r=-1453759936
  for-of 5000001 in 296 ms = 16.89 M/s r=27500055
  try-catch 1000001 in 128 ms = 7.81 M/s r=1784293664
  done
```
