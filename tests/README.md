# PHI tests

Each test is a `.phi` program plus a `.expected` file containing the serial output it
must produce. `phi test` builds each program, boots it headless in QEMU and compares
the output (line endings and trailing whitespace are ignored).

`log` writes to the screen and also to the COM1 serial port, which is what the tests read.

```
phi test                 # everything in tests/
phi test -f loop         # only tests whose name contains "loop"
phi test tests/pending   # tests that are expected to fail for now
```

On a mismatch the real output is saved as `NAME.actual`. To add a test, write
`NAME.phi`, run `phi test -f NAME`, check `NAME.actual`, and rename it to `NAME.expected`.

`pending/` holds tests for behavior the current translator gets wrong. They are the
spec for the Phase 1 compiler; move a test up into `tests/` once it passes.

| Pending test | Why it fails today |
|---|---|
| `math` | Expressions in declarations are pasted into the assembly unevaluated |
| `while_after_loop_stack` | `while` loops recurse once per iteration and overflow the stack |
