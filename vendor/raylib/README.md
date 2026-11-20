# raylib 6.2

A simple library for making games: a window, drawing in 2D and 3D, textures, text, input,
sound, and its maths (`raymath`). <https://www.raylib.com> has a cheatsheet of every
function and many examples; they read the same in PHI.

```phi
use vendor.raylib;

phi.Hello:Program
{
	call Ray.InitWindow: 800 450 'Hello from PHI';
	call Ray.SetTargetFPS: 60;
	while not (Ray.WindowShouldClose)
		call Ray.BeginDrawing;
		call Ray.ClearBackground: Ray.RAYWHITE;
		call Ray.DrawCircleV: (Ray.GetMousePosition) 30 Ray.MAROON;
		call Ray.DrawText: 'move the mouse' 20 20 20 Ray.DARKGRAY;
		call Ray.EndDrawing;
	;;
	call Ray.CloseWindow;
	exit 0;
}
```

- Everything is `Ray.`: functions (`Ray.DrawText`), constants (`Ray.KEY_SPACE`,
  `Ray.FLAG_VSYNC_HINT`) and colours (`Ray.SKYBLUE`).
- Structs have raylib's names: `Vector2`, `Color`, `Rectangle`, `Texture2D`, `Camera2D`.
- `raymath` is in it too: `Ray.Vector2Add`, `Ray.MatrixRotateY`, `Ray.Lerp`.
- `TraceLog` and `TextFormat` take only their text here (see the vendor README).
- [samples/raylib/ball.phi](../../samples/raylib/ball.phi) is a window;
  [tests/vendor/raylib_program.phi](../../tests/vendor/raylib_program.phi) uses it with none.

The library is raylib's own build for each system. Its Linux build needs glibc 2.38 or
later (Ubuntu 24.04, Fedora 39) and X11. Licence: zlib ([LICENSE](LICENSE)).
