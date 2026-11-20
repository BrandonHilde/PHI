# vendor: other people's libraries, ready to use

Each folder here is a C library with everything PHI needs to call it: a module that
declares all of its functions, structs and constants, the library itself for each system,
and its licence. A program names the one it wants and uses it:

```phi
use vendor.raylib;

phi.Hello:Program
{
	call Ray.SetTraceLogLevel: Ray.LOG_WARNING;       # (raylib says less on the console)
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
else (without `obj`, where the build keeps its own working files). Nothing is installed, and nothing is declared in the program.

| Library | Version | `use` | Called as | Licence | What it's for |
|---|---|---|---|---|---|
| [raylib](raylib) | 6.2 | `vendor.raylib` | `Ray.` | zlib | games: a window, 2D and 3D drawing, input, sound, with its maths (`raymath`) |
| [SDL3](sdl3) | 3.4.20 | `vendor.sdl3` | `SDL.` | zlib | windows, input (keyboard, mouse, gamepads, touch), sound, 2D drawing, the GPU |
| [SQLite](sqlite) | 3.54.1 | `vendor.sqlite` | `Sql.` | public domain | a database in one file or in memory, asked in SQL |
| [Lua](lua) | 5.5.1 | `vendor.lua` | `Lua.` | MIT | the Lua language, to script a program with |
| [zlib](zlib) | 1.4 | `vendor.zlib` | `Z.` | zlib | compression: deflate, zlib and gzip streams, checksums |
| [stb](stb) | 2026-11 | `vendor.stb` | `Stb.` | public domain or MIT | pictures read and written (PNG, JPEG and more), TrueType fonts, rectangle packing, Ogg Vorbis |
| [miniaudio](miniaudio) | 0.11.26 | `vendor.miniaudio` | `Ma.` | public domain or MIT-0 | sound played and recorded, files decoded, mixing and effects |
| [Vulkan](vulkan) | 1.4, MoltenVK 1.4.2 | `vendor.vulkan` | `Vk.` | Apache-2.0 | the GPU through Vulkan, with SDL3 for its window: the system's own on Windows and Linux, MoltenVK on a Mac |
| [libc](libc) | C89 | `vendor.libc`, `vendor.libc.math` | `C.`, `CMath.` | the system's own | C's own library: memory, text, files, sorting, maths |

`phi vendor` lists them. Each folder's README has an example and what to know about it. raylib and SDL3 are their
makers' own builds; SQLite, Lua, zlib, stb and miniaudio are built from their makers'
source, unchanged; libc is every system's own, and so is Vulkan but on a Mac, where
MoltenVK's own build stands in for it. On Linux, SDL3 is the system's
(`libSDL3.so.0`, from the distribution's packages), and raylib's own build needs glibc 2.38
or later and X11.

## How a library reads in PHI

- **Functions** have C's names after the library's name: `InitWindow(800, 450, "Hi")` is
  `call Ray.InitWindow: 800 450 'Hi';`. Where every name of a library starts the same way,
  that start is the library's name instead: `SDL_Init` is `SDL.Init`, `sqlite3_open` is
  `Sql.open`, `SQLITE_OK` is `Sql.OK`. The top of each module says which it does. A library's own documentation and examples apply as
  they are. The comment C's header has for a function is beside it in the module.
- **Structs** have C's names (`Vector2`, `Color`, `Rectangle`) and C's fields. A function of
  the library's that gives one back can be used wherever a value goes:
  `Color half: (Ray.Fade: Ray.RED 0.5);`, or straight into another call, or
  `(Ray.GetMousePosition).x`.
- **A struct of your own making** comes from a helper, declared from and then given to the
  call. It's a method like any other; one that gives a struct back needs
  `use memory.program_heap;` in the program.

  ```phi
  [Vec: f32 x; f32 y; : Vector2]
      Vector2 made;
      made.x is x;
      made.y is y;
  [end: made]

  Vector2 centre: (Vec: 400 225);
  call Ray.DrawCircleV: centre 30 Ray.MAROON;
  ```
- **What a function gives** is after its second `:` in the module (`Ray.GetScreenWidth: : int;`),
  and one that gives nothing has none (`Ray.CloseWindow;`). What it gives needn't be taken:
  `call Sql.finalize: st;` is as good as `r is (Sql.finalize: st);`.
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

**A program's own files.** A program is started from anywhere, so one that reads files kept
beside it (a script, a database, pictures) goes to its own folder first. With raylib that's
`int moved: (Ray.ChangeDirectory: (Ray.GetApplicationDirectory));`, and SDL has
`SDL.GetBasePath`. A build puts libraries beside the program, not your data files: copy
those into the folder it builds into.

A function that takes any number of values (`TextFormat`, `TraceLog`) is declared with the
values it always takes: give it the text already made (PHI's own `str` does what
`TextFormat` is for), or declare the form you need under a name of your own
(`Ray.TraceInt = 'TraceLog': int level ptr<u8> text ... int value;`).

## Where the vendor folder is

Beside `phi` in a release. A compiler built from its source finds the source's (the first
folder named `vendor`, with this file in it, beside the compiler or above it), and
`PHI_VENDOR` names another. A file `vendor/NAME.phi` beside your own program comes first, so a
program can carry its own version of a library, or one that isn't here.

How a library is added or updated is in [MAINTAINING.md](MAINTAINING.md), in PHI's repository.
