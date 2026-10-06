namespace Phi.Compiler.Semantics
{
    /// <summary>
    /// A function from the standard library (lib/x86_16). Arguments are pushed right to left
    /// as 32-bit values, the caller removes them, and results come back in eax.
    /// </summary>
    public sealed class BuiltinFunction
    {
        public string Name { get; init; } = "";
        public string[] Aliases { get; init; } = Array.Empty<string>();
        public string Label { get; init; } = "";
        public string[] Parameters { get; init; } = Array.Empty<string>();
        public bool Returns { get; init; }

        /// <summary>Null when the function works in both the boot sector and the kernel.</summary>
        public UnitKind? OnlyIn { get; init; }

        public string Description { get; init; } = "";
    }

    public sealed class HookInfo
    {
        public string Name { get; init; } = "";
        public string Label { get; init; } = "";
        public string Description { get; init; } = "";
    }

    public static class Builtins
    {
        public static readonly IReadOnlyList<BuiltinFunction> Functions = new List<BuiltinFunction>
        {
            new() { Name = "Bootloader.JumpToSectorTwo", Label = "Bootloader_JumpToSectorTwo", OnlyIn = UnitKind.Boot,
                    Description = "Load the OS classes from disk and start running them" },
            new() { Name = "Bootloader.WaitForKeyPress", Aliases = new[] { "OS.WaitForKeyPress" }, Label = "Bootloader_WaitForKeyPress", Returns = true,
                    Description = "Wait for a key (through the BIOS) and return its character" },
            new() { Name = "Bootloader.EnableVideoMode", Aliases = new[] { "OS.EnableVideoMode" }, Label = "Bootloader_EnableVideoMode",
                    Description = "Switch to 320x200, 256-color graphics (VGA mode 13h)" },

            new() { Name = "OS.SetupInteruptTimer", Aliases = new[] { "OS.SetupInterruptTimer" }, Label = "OS_SetupInteruptTimer",
                    Description = "Call [OS.TimerEvent] 60 times a second" },
            new() { Name = "OS.SetupKeyboardInterupt", Aliases = new[] { "OS.SetupKeyboardInterrupt" }, Label = "OS_SetupKeyboardInterupt",
                    Description = "Track key presses and call [OS.KeyboardEvent] on every key event (replaces the BIOS keyboard)" },
            new() { Name = "OS.GetKey", Label = "OS_GetKey", Returns = true,
                    Description = "The character of the last key pressed or released" },
            new() { Name = "OS.IsKeyDown", Label = "OS_IsKeyDown", Parameters = new[] { "key" }, Returns = true,
                    Description = "1 if the key (e.g. 'w') is held down" },

            new() { Name = "OS.SetupMouse", Label = "OS_SetupMouse",
                    Description = "Turn on the PS/2 mouse" },
            new() { Name = "OS.UpdateMouse", Label = "OS_UpdateMouse",
                    Description = "Read any waiting mouse movement (GetMouseX/Y/Down/Up do this too)" },
            new() { Name = "OS.GetMouseX", Label = "OS_GetMouseX", Returns = true, Description = "Mouse cursor x (0-310)" },
            new() { Name = "OS.GetMouseY", Label = "OS_GetMouseY", Returns = true, Description = "Mouse cursor y (0-190)" },
            new() { Name = "OS.GetMouseDown", Label = "OS_GetMouseDown", Returns = true, Description = "1 if the left button is down" },
            new() { Name = "OS.GetMouseUp", Label = "OS_GetMouseUp", Returns = true, Description = "1 if the left button is up" },

            new() { Name = "OS.DrawRectangle", Label = "OS_DrawRectangle", Parameters = new[] { "x", "y", "width", "height", "color" },
                    Description = "Fill a rectangle in graphics mode (clipped to the screen)" },
            new() { Name = "OS.DrawPixel", Label = "OS_DrawPixel", Parameters = new[] { "x", "y", "color" },
                    Description = "Set one pixel in graphics mode" },

            new() { Name = "OS.SetInterruptHandler", Label = "OS_SetInterruptHandler", Parameters = new[] { "vector", "handler" },
                    Description = "Run an [isr Name] method for an interrupt vector (handler is addr Name)" },
            new() { Name = "OS.EndOfInterrupt", Label = "OS_EndOfInterrupt", Parameters = new[] { "irq" },
                    Description = "Tell the interrupt controller a hardware interrupt (IRQ 0-15) was handled" },
            new() { Name = "OS.EnableInterrupts", Label = "OS_EnableInterrupts", Description = "sti" },
            new() { Name = "OS.DisableInterrupts", Label = "OS_DisableInterrupts", Description = "cli" },
            new() { Name = "OS.UnmaskIrq", Label = "OS_UnmaskIrq", Parameters = new[] { "irq" },
                    Description = "Let a hardware interrupt (IRQ 0-15) through the interrupt controller" },
            new() { Name = "OS.MaskIrq", Label = "OS_MaskIrq", Parameters = new[] { "irq" },
                    Description = "Block a hardware interrupt (IRQ 0-15) at the interrupt controller" },
        };

        /// <summary>Methods a program can define to react to events, e.g. [OS.TimerEvent] ... [end]</summary>
        public static readonly IReadOnlyList<HookInfo> Hooks = new List<HookInfo>
        {
            new() { Name = "OS.TimerEvent", Label = "OS_TimerEvent", Description = "Runs 60 times a second after OS.SetupInteruptTimer" },
            new() { Name = "OS.KeyboardEvent", Label = "OS_KeyboardEvent", Description = "Runs on each key press and release after OS.SetupKeyboardInterupt" },
        };

        public static readonly IReadOnlyDictionary<string, long> Constants = new Dictionary<string, long>
        {
            ["Colors.Black"] = 0x0,
            ["Colors.Blue"] = 0x1,
            ["Colors.Green"] = 0x2,
            ["Colors.Cyan"] = 0x3,
            ["Colors.Red"] = 0x4,
            ["Colors.Magenta"] = 0x5,
            ["Colors.Brown"] = 0x6,
            ["Colors.LightGray"] = 0x7,
            ["Colors.Gray"] = 0x8,
            ["Colors.LightBlue"] = 0x9,
            ["Colors.LightGreen"] = 0xA,
            ["Colors.LightCyan"] = 0xB,
            ["Colors.LightRed"] = 0xC,
            ["Colors.LightMagenta"] = 0xD,
            ["Colors.Yellow"] = 0xE,
            ["Colors.White"] = 0xF,
        };

        static readonly Dictionary<string, BuiltinFunction> byName =
            Functions.SelectMany(f => f.Aliases.Append(f.Name).Select(n => (n, f)))
                     .ToDictionary(p => p.n, p => p.f);

        public static BuiltinFunction? Find(string name) => byName.GetValueOrDefault(name);
        public static HookInfo? FindHook(string name) => Hooks.FirstOrDefault(h => h.Name == name);
    }
}
