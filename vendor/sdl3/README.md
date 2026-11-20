# SDL3 3.4.20

Windows, input (keyboard, mouse, gamepads, touch, pens), sound, 2D drawing, cameras, and
the GPU. <https://wiki.libsdl.org/SDL3> documents every function.

```phi
use vendor.sdl3;

phi.Hello:Program
{
	ptr<SDL_Window> window: 0;
	ptr<SDL_Renderer> renderer: 0;
	SDL_Event event;
	bln running: (SDL.Init: SDL.INIT_VIDEO);
	bln ok: (SDL.CreateWindowAndRenderer: 'Hello from PHI' 800 450 0 addr window addr renderer);
	while running
		while (SDL.PollEvent: addr event)
			if event.type is SDL.EVENT_QUIT
				running is false;
			;;
		;;
		ok is (SDL.SetRenderDrawColor: renderer 24 28 48 255);
		ok is (SDL.RenderClear: renderer);
		ok is (SDL.RenderPresent: renderer);
	;;
	call SDL.Quit;
	exit 0;
}
```

- A function is `SDL.Name` for C's `SDL_Name` (`SDL.Init`, `SDL.CreateWindow`), and a
  constant the same (`SDL.INIT_VIDEO`, `SDL.EVENT_KEY_DOWN`, `SDL.SCANCODE_ESCAPE`).
- Structs keep C's whole names: `SDL_Event`, `SDL_FRect`, `SDL_Window`.
- An `SDL_Event` has its `type`; the rest is read as the kind of event it is:
  `ptr<SDL_KeyboardEvent> key: addr event as ptr<SDL_KeyboardEvent>;` then `key.scancode`.
- A program has its own start, so the module tells SDL so (`SDL_SetMainReady`) before
  anything else runs: there's no `SDL_main` to write.
- Functions C's headers define in themselves (`SDL_PointInRect` and the like) aren't in the
  library, so they aren't here; neither are the twelve constants past 32 bits
  ([left-out.txt](left-out.txt)).
- [samples/sdl3/bounce.phi](../../samples/sdl3/bounce.phi) is a window;
  [tests/vendor/sdl3_program.phi](../../tests/vendor/sdl3_program.phi) draws with none.

The library is SDL's own build for Windows and macOS. On Linux it's the system's
(`libSDL3.so.0`: the `libsdl3-0` package or the like). Licence: zlib ([LICENSE](LICENSE)).
