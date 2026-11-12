# PHI beside C and C#

Measured 2026-11-12 by `phi compare` on the benchmarks in `../PHI-Code-Library/libs/web`, on Intel(R) Core(TM) Ultra 9 185H (Fedora Linux 42 (Workstation Edition)).

```
Seconds, the best of 3 runs of each program, from being started to having ended:

                    PHI 64-bit  PHI 32-bit
release_js_bench         9.227       9.959

PHI 64-bit's time over each of the others' (1.50: PHI took half as long again; 0.50: half as long):

                    PHI 32-bit
release_js_bench          0.93

C: gcc (GCC) 15.2.1 20260123 (Red Hat 15.2.1-7)
Each program was given the arguments: run

What release_js_bench (PHI 64-bit) printed, the last time it ran:

  for-loop 100000001 in 981 ms = 101.94 M/s r=100000001
  for-loop-let-sum 30000001 in 568 ms = 52.82 M/s r=-888471104
  calls 20000001 in 974 ms = 20.53 M/s r=20000001
  prop-get 20000001 in 433 ms = 46.19 M/s r=60000003
  prop-set 20000001 in 461 ms = 43.38 M/s r=20000000
  method-call 10000001 in 669 ms = 14.95 M/s r=10000001
  array-rw 20000001 in 859 ms = 23.28 M/s r=422894520
  alloc-obj 3000001 in 341 ms = 8.8 M/s r=3000000
  alloc-arr 2000001 in 223 ms = 8.97 M/s r=2000000
  closure 3000001 in 218 ms = 13.76 M/s r=3000001
  str-concat 3000001 in 585 ms = 5.13 M/s r=3000001
  str-index 10000001 in 557 ms = 17.95 M/s r=992500104
  double-math 20000001 in 820 ms = 24.39 M/s r=6389059.798353338
  fib(32) 2 in 231 ms = 0 M/s r=2178309
  class-new 2000001 in 568 ms = 3.52 M/s r=-1453759936
  for-of 5000001 in 326 ms = 15.34 M/s r=27500055
  try-catch 1000001 in 429 ms = 2.33 M/s r=1784293664
  done
```
