# Maintaining the vendor folder

For whoever adds a library or moves one to a new version. A release has none of this: its
vendor folder is complete as it is.

The libraries themselves aren't in PHI's repository: `package.json` in each folder says
where each comes from (the library's own release), what SHA-256 it must have, and where its
files go.

```
python3 vendor/fetch.py            # the libraries, headers and licences, into each folder
python3 vendor/bindgen.py --all    # each module again from its headers (needs clang)
phi test tests/vendor              # each library called, and every struct's size beside C's
```

`fetch.py` is run once after cloning, and before a release is packed. `bindgen.py` is run
when a library's version changes: it reads the headers with clang and writes `NAME.phi`,
with a list of anything it couldn't declare (`left-out.txt`; none so far), and
`tests/vendor/NAME_layout_program.phi`, which compares each struct's size with C's on every
system. A module is never edited by hand: what C can't say for itself goes in
`package.json` (`"bindings"`), or in `extra.phi` beside it, which is added at the end.

The Linux libraries `fetch.py` builds are for glibc 2.34 (2021) and later, whatever Linux
they're built on: a C function a new glibc has a newer version of is asked for by its first
one, and the build stops if a library still needs more. raylib's own Linux build needs 2.38.

The Windows libraries `fetch.py` builds have parts of MinGW-w64's runtime in them, whose
licence goes with them: `vendor/LICENSE-mingw-w64-runtime`, which a release has. (stb is
built there without thread-local storage, which would bring in MinGW's threads library and
its licence too.)

`package.json`'s `"bindings"`:

| Key | What it says |
|---|---|
| `alias` | the name the library is called by (`Ray`) |
| `headers` | the headers to read, in the library's folder |
| `strip` | a prefix every function has, left off its PHI name (`SDL_`): `SDL.Init` is C's `SDL_Init` |
| `strip_constants` | the same for constants |
| `static_inline` | `true` when the library also exports the functions its headers define (`raymath`) |
| `skip` | names to leave out |
| `types` | a C type's PHI type, where the header doesn't say enough |
| `fields` | one field's PHI type (`"gzFile_s.pos": "i64"`), where C's type for it differs between systems in a way its name doesn't show |
| `own` | a folder whose headers are all the library's (the `headers` named are the ones included) |
| `skip_like` | names to leave out, as regular expressions |
| `startup` | statements the library's class runs when the program starts |
| `comment_drop` | text to take out of the header's comments, as regular expressions |
| `handles` | `true` to give each struct PHI can't lay out a `new_` function (in `alloc.c`, built into the library) |

`"bindings"` may be a list, for a library with several modules (`vendor.libc` and
`vendor.libc.math`): each with its own `output` file, and its own `libraries` if they differ.
`class.phi` beside it is added inside the library's class (methods for what C has as
macros), and `extra.phi` after everything else.

A library that has no builds of its own to fetch has a `"build"` in `package.json`: where
its source comes from, which files are compiled, and what each system's compiler is told.
`fetch.py` makes one shared library for each system this computer has a compiler for: clang
for macOS, MinGW for the two Windows, and gcc for 64-bit Linux (its own there, or the one in
the Linux machine a Mac keeps).
| `rename`, `functions` | a struct's, or a function's, PHI name where C's can't be used |
| `clang`, `include` | more arguments for clang, and folders its includes are found in |

A library is added when it's one many programs want, it builds for every system PHI does,
and its licence lets it be passed on with a program. Ask before adding one.
