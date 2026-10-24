# Common patterns

PHI has no classes in the object-oriented sense, no `switch` and no enums. This page shows
how the things those usually do are written in PHI: tables of handlers, callbacks,
building text, returning text from a method, and sets of states. The full rules are in
the [language reference](language.md).

Every example here is part of [tests/patterns.phi](../tests/patterns.phi), which `phi test`
runs, so they work as written.

## A table of handlers

A struct can hold methods (`method<...>` fields). Fill in one struct per thing, each with
its own methods, and code that knows nothing about them can call them. This is how a
window manager can run apps it has never heard of; it does the job of a virtual method
table in C++.

```phi
struct.App
{
    u8 title[16];
    method<int: int> key;               # handles a key; 1 if it used it
    method<: int> closing;              # 1 if the app may close now (none: always)
}

const int NOTES: 0;
const int CLOCK: 1;
App apps[2];

call Text.Copy: apps:NOTES.title 'Notes' 16;
apps:NOTES.key is addr NotesKey;
apps:NOTES.closing is addr NotesClosing;
call Text.Copy: apps:CLOCK.title 'Clock' 16;
apps:CLOCK.key is addr ClockKey;        # no closing handler

while int i: 0; i < apps.len; i++;
    if (apps:i.key: 'q') is 1
        debug 'used q, ';
    ;;
    if apps:i.closing is 0              # none set
        debug 'closes\n';
    ;;
    elif (apps:i.closing) is 1          # called: it takes no arguments
        debug 'closes\n';
    ;;
;;
```

- `(apps:i.key: 'q')` calls the method in the field and gives its result.
- `(apps:i.closing)` calls one that takes no arguments, like `(Name)` for an ordinary method.
  Without the parentheses, `apps:i.closing` is the method itself: `is 0` checks that one is
  set. Calling an empty one stops the program.
- A handler can't take parameters with defaults; it gets exactly the arguments its type
  says. For a method of a struct, pass the struct: `method<ptr<Shape>: int> area;` called
  as `(shapes:i.area: addr shapes:i)`.

## A table that fills up

For records that come and go (accounts, open windows, entries read from a file), use an
`atmost` array instead of an array plus a count kept by hand. `.len` is always right, and
reading a slot nothing was put in is an index error instead of stale data:

```phi
struct.Account
{
    u8 name[12];
    int score;
}

Account list[atmost 8];

[Add: ptr<u8> name: 0;]
    if list.len is list.max return false;
    list++;                                     # an Account of zeros at the end
    call Text.Copy: list:(list.len - 1).name name sizeof list:0.name;
[end: true]
```

## Callbacks

Pass `addr Method` to a method that takes a `method<...>`, and it can call you back:

```phi
call Ask: 'name?' addr Greet;

[Ask: ptr<u8> question: 0; method<ptr<u8>> done: 0;]
    debug question ' ';
    call done: 'Ada';
[end]

[Greet: ptr<u8> name: 0;]
    debug 'hello ' name '\n';
[end]
```

To call back later, when an answer arrives, keep the method in a variable and clear it
once it's used:

```phi
method<ptr<u8>> on_answer: addr Greet;

[Answer: ptr<u8> name: 0;]
    if on_answer is 0 return;
    call on_answer: name;
    on_answer is 0;                     # answered once
[end]
```

## Building text

A `str` takes several values, written in one after another, the way `log` writes them.
`++` adds to the end:

```phi
str status: [40];
status is 'Ln ' line + 1 ', Col ' column + 1;
status++ ' (saved)';                    # Ln 5, Col 12 (saved)
```

For text in a `u8` array, or reached through a `ptr<u8>`, `user.text` has the same
operations. Each takes the size of the buffer in bytes, so nothing is written past the
end. That's `sizeof` of the buffer, for a `u8` array and a `str` alike:

```phi
use user.text;

u8 label[24];
call Text.Copy: label 'Size: ' sizeof label;
call Text.AppendSize: label 12800 sizeof label;   # Size: 13 KB
debug addr label '\n';                  # addr: print the text, not the array

str info: [24];
info is '';
call Text.AppendSize: info 12800 sizeof info;     # not info.len: that's 0 here
```

Text longer than a `str` is cut to fit, without an error. Make it big enough for the
longest text it will hold (a path, a line of a file), or check `.len` against what you
expect.

## Returning text from a method

A method whose `[end: ...]` is a `ptr<u8>` gives back text. That can be a place inside
text it was given, or a string literal:

```phi
[AfterSlash: ptr<u8> text: 0;]
    int pos: (Text.Length: text);
    while pos > 0 and text:(pos - 1) is not '/'
        pos--;
    ;;
[end: text + pos]                       # (AfterSlash: 'docs/notes.txt') is notes.txt
```

Don't return a pointer to the method's own `str` or `u8` array: those are gone once it
returns. To build new text, have the caller pass a buffer, or return a `str`. A `str`
result lasts until the end of the statement that called the method, so the caller stores
it in a `str` of its own; keeping it in a `ptr<u8>` is an error.

## A set of states

Use constants in capitals, one per state, and a method to name them:

```phi
const int READY: 0;
const int PLAYING: 1;
const int OVER: 2;
int state: READY;

[StateName: int s: 0;]
    if s is PLAYING return 'playing';
    if s is OVER return 'game over';
    ptr<u8> name: 'ready';
[end: name]
```

A constant in capitals also works as an index: `apps:NOTES.key` is the field of element
`NOTES`. Nothing stops a state of one set being given where another set goes, so keep
each set's names distinct (`STATE_READY`, `MODE_EDIT`) when there are several.

## One-line guards

Code that checks a lot (key handling, validation) reads better with its early exits on
one line each:

```phi
[NotesKey: int key: 0;]
    if key is 'q' return 1;
[end: 0]
```

`return`, `break`, `continue`, `call` and `exit` can follow a condition on the same line,
with no `;;`. Anything longer, or with an `else`, is a block.
