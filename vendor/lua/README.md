# Lua 5.5.1

The Lua language, to script a program with: its interpreter and standard libraries.
<https://www.lua.org/manual/5.5/> is the manual; its C functions are the ones here.

```phi
use vendor.lua;

phi.Script:Program
{
	ptr<lua_State> L: (Lua.luaL_newstate);
	call Lua.luaL_openlibs: L;
	call Lua.lua_register: L 'double' addr foreign Double;
	if (Lua.luaL_dostring: L 'answer = double(21)') is not Lua.LUA_OK
		log 'lua says: ' (Lua.lua_tostring: L (-1)) '\n';
	;;
	int kind: (Lua.lua_getglobal: L 'answer');
	log 'answer: ' (Lua.lua_tointeger: L (-1)) '\n';
	call Lua.lua_close: L;
	exit 0;

	[Double: ptr<lua_State> state: 0;]
		call Lua.lua_pushinteger: state (Lua.lua_tointeger: state 1) * 2;
	[end: 1]                                    # how many values it gives back
}
```

- Functions have C's whole names, as the manual has them: `Lua.lua_pushinteger`,
  `Lua.luaL_newstate`. Constants too: `Lua.LUA_OK`, `Lua.LUA_TSTRING`.
- What C has as macros are methods, called the same way: `lua_pop`, `lua_pcall`,
  `lua_tostring`, `lua_tointeger`, `lua_newtable`, `lua_register`, `luaL_dostring`,
  `luaL_dofile`, `luaL_openlibs` and the rest ([class.phi](class.phi) has them all).
- A function Lua calls is a PHI method that takes the state and gives how many values it
  pushed, given as `addr foreign Name`.
- A whole number of Lua's is an `i64`, a number with a point an `f64`.

Built from Lua's own source, unchanged. Licence: MIT ([LICENSE](LICENSE)).
