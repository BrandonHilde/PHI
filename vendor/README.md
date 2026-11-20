# vendor: other people's libraries, ready to use

Each folder here is a C library with everything PHI needs to call it: a module that
declares all of its functions, structs and constants, the library itself for each system,
and its licence. A program names the one it wants and uses it:

```phi
use vendor.raylib;

phi.Hello:Program
{
	call Ray.InitWindow: 800 450 'Hello from PHI';
	while not (Ray.WindowShouldClose)
		call Ray.BeginDrawing;
		call Ray.ClearBackground: Ray.RAYWHITE;
		call Ray.DrawText: 'It works' 320 200 30 Ray.DARKGRAY;
		call Ray.EndDrawing;
	;;
	call Ray.CloseWindow;
	exit 0;
}
```

`phi build hello.phi --target windows64` (or `windows`, `macos`, `linux64`) puts the
library's file beside the program, so the folder it builds into is what you give to someone
else. Nothing is installed, and nothing is declared in the program.

| Library | Version | `use` | Called as | Licence | What it's for |
|---|---|---|---|---|---|
| [raylib](raylib) | 6.2 | `vendor.raylib` | `Ray.` | zlib | games: a window, 2D and 3D drawing, input, sound, with its maths (`raymath`) |
| [SDL3](sdl3) | 3.4.20 | `vendor.sdl3` | `SDL.` | zlib | windows, input (keyboard, mouse, gamepads, touch), sound, 2D drawing, the GPU |
| [SQLite](sqlite) | 3.54.1 | `vendor.sqlite` | `Sql.` | public domain | a database in one file or in memory, asked in SQL |
| [Lua](lua) | 5.5.1 | `vendor.lua` | `Lua.` | MIT | the Lua language, to script a program with |
| [zlib](zlib) | 1.4 | `vendor.zlib` | `Z.` | zlib | compression: deflate, zlib and gzip streams, checksums |

raylib and SDL3 are their makers' own builds; SQLite, Lua and zlib are built from their
makers' source, unchanged, by `vendor/fetch.py`. On Linux, SDL3 is the system's
(`libSDL3.so.0`, from the distribution's packages), and raylib's own build needs glibc 2.38
or later and X11.

## How a library reads in PHI

- **Functions** have C's names after the library's name: `InitWindow(800, 450, "Hi")` is
  `call Ray.InitWindow: 800 450 'Hi';`. Where every name of a library starts the same way,
  that start is the library's name instead: `SDL_Init` is `SDL.Init`, `sqlite3_open` is
  `Sql.open`, `SQLITE_OK` is `Sql.OK`. The top of each module says which it does. A library's own documentation and examples apply as
  they are. The comment C's header has for a function is beside it in the module.
- **Structs** have C's names (`Vector2`, `Color`, `Rectangle`) and C's fields. A function that
  gives one back can be used wherever a value goes: `Color half: (Ray.Fade: Ray.RED 0.5);`,
  or straight into another call, or `(Ray.GetMousePosition).x`.
- **Constants** (C's enums and `#define`s) are the library's: `Ray.KEY_SPACE`,
  `Ray.FLAG_VSYNC_HINT | Ray.FLAG_WINDOW_RESIZABLE`. They're numbers, so they combine as in C.
- **Values C has as constants of a struct** (raylib's colours) are variables of the
  library's: `Ray.RAYWHITE`, `Ray.SKYBLUE`.
- **A typedef of a struct** is the same struct under its other name: `Texture2D` is `Texture`.
- **What C has as a macro that calls a function** (`lua_pop`, `lua_tostring`,
  `deflateInit`) is a method of the library's, called the same way: `call Lua.lua_pop: L 1;`.
- **A union** (`SDL_Event`) is a struct with what all its forms start with (`event.type`); the
  rest is read through a pointer to the form it is:
  `ptr<SDL_KeyboardEvent> key: addr event as ptr<SDL_KeyboardEvent>;`.
- Text goes as text (`'Hi'`, or a `str`), a number where C takes a `float` is given as any
  number, and `true` and `false` are C's `bool`.
- What C takes by address is given with `addr` (`call Ray.ImageCrop: addr image area;`), and a
  function C calls back is `addr foreign Name` (see "Outside libraries" in
  [docs/language.md](../docs/language.md)).

A function that takes any number of values (`TextFormat`, `TraceLog`) is declared with the
values it always takes: give it the text already made (PHI's own `str` does what
`TextFormat` is for), or declare the form you need under a name of your own
(`Ray.TraceInt = 'TraceLog': int level ptr<u8> text ... int value;`).

## Where the vendor folder is

Beside `phi` in a release. A compiler built from its source finds the source's (the first
folder named `vendor`, with this file in it, beside the compiler or above it), and
`PHI_VENDOR` names another. A file `vendor/NAME.phi` beside your own program comes first, so a
program can carry its own version of a library, or one that isn't here.

## For whoever maintains this folder

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
| `rename`, `functions` | a struct's, or a function's, PHI name where C's can't be used |
| `clang`, `include` | more arguments for clang, and folders its includes are found in |

A library is added when it's one many programs want, it builds for every system PHI does,
and its licence lets it be passed on with a program. Ask before adding one.
