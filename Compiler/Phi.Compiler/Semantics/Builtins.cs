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

        /// <summary>Indexes of parameters that take text (a str or ptr&lt;u8&gt;) rather than a number.</summary>
        public int[] TextParameters { get; init; } = Array.Empty<int>();

        /// <summary>Indexes of parameters that take the address of a buffer: an array passes its address, as it does to a method.</summary>
        public int[] BufferParameters { get; init; } = Array.Empty<int>();
        public bool Returns { get; init; }

        /// <summary>The result is text (a ptr&lt;u8&gt;), so it prints as text, rather than a number.</summary>
        public bool ReturnsText { get; init; }

        /// <summary>What a call gives back, for one that Returns.</summary>
        public PhiType ResultType => FloatInstruction != null ? PhiType.F64 : ReturnsText ? PhiType.PointerTo(PhiType.U8) : PhiType.Int;

        /// <summary>
        /// For a program's call: what a 32-bit kernel calls to do the same (Fat.ReadFile), and
        /// the library it's in (fs.fat16). A kernel's call goes there, so code shared by
        /// kernels and programs can use the same name.
        /// </summary>
        public string? InKernel { get; init; }
        public string? InKernelUse { get; init; }

        /// <summary>Null when the function works in both the boot sector and the kernel.</summary>
        public UnitKind? OnlyIn { get; init; }

        public string Description { get; init; } = "";

        /// <summary>
        /// For the floating point functions (Float.Sqrt ...): the FPU instruction that works it
        /// out on st0, and the rounding it needs (Floor, Ceil, Trunc), if any. They take and
        /// give an f64.
        /// </summary>
        public string? FloatInstruction { get; init; }
        public int FloatRounding { get; init; } = -1;
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
            new() { Name = "OS.ReadKey", Label = "OS_ReadKey", Returns = true,
                    Description = "32-bit: wait for a key press and return its character" },
            new() { Name = "OS.KeyAvailable", Label = "OS_KeyAvailable", Returns = true,
                    Description = "32-bit: 1 if OS.ReadKey has a character waiting" },
            new() { Name = "OS.GetTicks", Label = "OS_GetTicks", Returns = true,
                    Description = "32-bit: milliseconds since the timer started" },
            new() { Name = "OS.Sleep", Label = "OS_Sleep", Parameters = new[] { "milliseconds" },
                    Description = "32-bit: wait" },

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
            new() { Name = "OS.SetIrqHandler", Label = "OS_SetIrqHandler", Parameters = new[] { "irq", "handler" },
                    Description = "32-bit: run an ordinary method (addr Name) for a hardware interrupt; end-of-interrupt is automatic" },
            new() { Name = "OS.EnableInterrupts", Label = "OS_EnableInterrupts", Description = "sti" },

            // ---- tasks (lib/x86_32/tasks.asm in kernels, system calls in programs)
            new() { Name = "OS.Yield", Label = "OS_Yield", Description = "give the CPU to another task" },
            new() { Name = "OS.Idle", Label = "OS_Idle",
                    Description = "kernel: give the CPU to the programs, and if none of them can run, wait for the next interrupt instead of coming straight back" },

            // ---- floating point (worked out on the FPU, in 32-bit kernels and programs)
            new() { Name = "Float.Sqrt", Label = "", Parameters = new[] { "x" }, Returns = true, FloatInstruction = "fsqrt", Description = "the square root" },
            new() { Name = "Float.Abs", Label = "", Parameters = new[] { "x" }, Returns = true, FloatInstruction = "fabs", Description = "without its sign" },
            new() { Name = "Float.Sin", Label = "", Parameters = new[] { "x" }, Returns = true, FloatInstruction = "fsin", Description = "the sine of x radians" },
            new() { Name = "Float.Cos", Label = "", Parameters = new[] { "x" }, Returns = true, FloatInstruction = "fcos", Description = "the cosine of x radians" },
            new() { Name = "Float.Round", Label = "", Parameters = new[] { "x" }, Returns = true, FloatInstruction = "frndint", FloatRounding = 0, Description = "the nearest whole number (halves to even)" },
            new() { Name = "Float.Floor", Label = "", Parameters = new[] { "x" }, Returns = true, FloatInstruction = "frndint", FloatRounding = 1, Description = "the whole number at or below" },
            new() { Name = "Float.Ceil", Label = "", Parameters = new[] { "x" }, Returns = true, FloatInstruction = "frndint", FloatRounding = 2, Description = "the whole number at or above" },
            new() { Name = "Float.Trunc", Label = "", Parameters = new[] { "x" }, Returns = true, FloatInstruction = "frndint", FloatRounding = 3, Description = "the whole number toward zero" },
            new() { Name = "OS.CurrentTask", Label = "OS_CurrentTask", Returns = true, Description = "this task's number (0 is the kernel)" },
            new() { Name = "OS.StartMultitasking", Label = "OS_StartMultitasking", Description = "kernel: set up tasks, user mode and system calls" },
            new() { Name = "OS.CreateUserTask", Label = "OS_CreateUserTask", Returns = true,
                    Parameters = new[] { "pageDirectory", "entry", "userStack", "kernelStack", "imageEnd", "stackBottom" },
                    Description = "kernel: start a ring-3 task (proc.process does this for you)" },
            new() { Name = "OS.TaskState", Label = "OS_TaskState", Parameters = new[] { "task" }, Returns = true, Description = "kernel: a task's state" },
            new() { Name = "OS.TaskExitCode", Label = "OS_TaskExitCode", Parameters = new[] { "task" }, Returns = true, Description = "kernel: a finished task's exit code" },
            new() { Name = "OS.TaskParent", Label = "OS_TaskParent", Parameters = new[] { "task" }, Returns = true, Description = "kernel: the task that started it" },
            new() { Name = "OS.FreeTask", Label = "OS_FreeTask", Parameters = new[] { "task" }, Description = "kernel: let a finished task's slot be reused" },
            new() { Name = "OS.SetTaskHeapEnd", Label = "OS_SetTaskHeapEnd", Parameters = new[] { "task", "end" },
                    Description = "kernel: where a task's heap (from 0x40400000) ends, so text it passes from there is accepted" },
            new() { Name = "OS.SurfaceOpen", Label = "OS_SurfaceOpen", Parameters = new[] { "width", "height" }, Returns = true,
                    Description = "program: a window of width x height pixels (gui.surfaces in the kernel); 1, or 0 (gui.app does this)" },
            new() { Name = "OS.SurfacePresent", Label = "OS_SurfacePresent", Parameters = new[] { "pixels" }, BufferParameters = new[] { 0 }, Returns = true,
                    Description = "program: show width x height pixels from its memory in its window; 1, or 0" },
            new() { Name = "OS.NextEvent", Label = "OS_NextEvent", Parameters = new[] { "event" }, BufferParameters = new[] { 0 }, Returns = true,
                    Description = "program: the oldest event for its window, into a GuiEvent (gui.events); 1, or 0 if there's none" },
            new() { Name = "OS.CopyFont", Label = "OS_CopyFont", Parameters = new[] { "glyphs" }, BufferParameters = new[] { 0 }, Returns = true,
                    Description = "program: copy the kernel's 8x16 font (4096 bytes) into glyphs; 1, or 0 if it has none" },
            new() { Name = "OS.SetWindowTitle", Label = "OS_SetWindowTitle", Parameters = new[] { "title" }, TextParameters = new[] { 0 }, Returns = true,
                    Description = "program: name its window (gui.app's App.SetTitle); 1" },
            new() { Name = "OS.OpenWindow", Label = "OS_OpenWindow", Parameters = new[] { "path", "arguments" }, TextParameters = new[] { 0, 1 }, Returns = true,
                    Description = "program: start a program with a command line, in a window of its own (gui.app's App.OpenWindow); its process number, or -1" },
            new() { Name = "OS.WindowFocused", Label = "OS_WindowFocused", Returns = true,
                    Description = "program: 1 if its window is in front (gui.app's App.Focused)" },
            new() { Name = "OS.SetResizable", Label = "OS_SetResizable", Parameters = new[] { "resizable" }, Returns = true,
                    Description = "program: 0 if its window can't be resized, 1 if it can (gui.app's App.SetResizable); 1" },
            new() { Name = "OS.NetAddress", Label = "OS_NetAddress", Returns = true,
                    Description = "program: this machine's address, or 0 if the kernel has no network (net.sockets does this)" },
            new() { Name = "OS.UdpOpen", Label = "OS_UdpOpen", Parameters = new[] { "port", "queue" }, Returns = true,
                    Description = "program: a UDP socket on port (0: any free one), keeping up to queue datagrams; -1 if it can't (net.sockets does this)" },
            new() { Name = "OS.UdpClose", Label = "OS_UdpClose", Parameters = new[] { "socket" }, Returns = true,
                    Description = "program: close a UDP socket; 1, or 0 if it isn't this program's (net.sockets does this)" },
            new() { Name = "OS.UdpReceive", Label = "OS_UdpReceive", Parameters = new[] { "socket", "datagram" }, BufferParameters = new[] { 1 }, Returns = true,
                    Description = "program: the oldest datagram for the socket, into a UdpDatagram; 1, or 0 if none is waiting (net.sockets does this)" },
            new() { Name = "OS.UdpSend", Label = "OS_UdpSend", Parameters = new[] { "socketAndPort", "to", "data", "length" }, BufferParameters = new[] { 2 }, Returns = true,
                    Description = "program: send a datagram from a socket (its number, plus the port times 65536); 1 if it went (net.sockets does this)" },
            new() { Name = "OS.TcpBegin", Label = "OS_TcpBegin", Parameters = new[] { "to", "port" }, Returns = true,
                    Description = "program: start a TCP connection; its number (ask OS.TcpState), or -1 (net.sockets does this)" },
            new() { Name = "OS.TcpState", Label = "OS_TcpState", Parameters = new[] { "connection" }, Returns = true,
                    Description = "program: 1 once a connection is open, 0 while it's connecting, -1 if it failed (net.sockets does this)" },
            new() { Name = "OS.TcpSend", Label = "OS_TcpSend", Parameters = new[] { "connection", "data", "length" }, BufferParameters = new[] { 1 }, Returns = true,
                    Description = "program: put bytes in a connection's send buffer; how many it took, or -1 (net.sockets does this)" },
            new() { Name = "OS.TcpTake", Label = "OS_TcpTake", Parameters = new[] { "connection", "buffer", "max" }, BufferParameters = new[] { 1 }, Returns = true,
                    Description = "program: what has arrived, up to max bytes: the bytes, 0 if none yet, -2 once closed and all read, -1 if broken (net.sockets does this)" },
            new() { Name = "OS.TcpClose", Label = "OS_TcpClose", Parameters = new[] { "connection" }, Returns = true,
                    Description = "program: close a connection; 1 (net.sockets does this)" },
            new() { Name = "OS.TcpListen", Label = "OS_TcpListen", Parameters = new[] { "port", "backlog" }, Returns = true,
                    Description = "program: take connections on a port; a listener, or -1 (net.sockets does this)" },
            new() { Name = "OS.TcpAccept", Label = "OS_TcpAccept", Parameters = new[] { "listener" }, Returns = true,
                    Description = "program: a connection that has come in, or -1 if none is waiting (net.sockets does this)" },
            new() { Name = "OS.TcpWaiting", Label = "OS_TcpWaiting", Parameters = new[] { "connection" }, Returns = true,
                    Description = "program: bytes to read; -1 once it's closed or reset (net.sockets does this)" },
            new() { Name = "OS.TcpAddress", Label = "OS_TcpAddress", Parameters = new[] { "connection" }, Returns = true,
                    Description = "program: the address of the other end (net.sockets does this)" },
            new() { Name = "OS.TcpStopListening", Label = "OS_TcpStopListening", Parameters = new[] { "listener" }, Returns = true,
                    Description = "program: stop taking connections; 1, or 0 if it isn't this program's (net.sockets does this)" },
            new() { Name = "OS.MailOpen", Label = "OS_MailOpen", Parameters = new[] { "name", "size", "capacity" }, TextParameters = new[] { 0 }, Returns = true,
                    Description = "program: a mailbox of this program's for records of size bytes; its number, or -1 (user.mail does this)" },
            new() { Name = "OS.MailFind", Label = "OS_MailFind", Parameters = new[] { "name" }, TextParameters = new[] { 0 }, Returns = true,
                    Description = "program: the number of the mailbox with this name, or -1 (user.mail does this)" },
            new() { Name = "OS.MailSend", Label = "OS_MailSend", Parameters = new[] { "box", "record" }, BufferParameters = new[] { 1 }, Returns = true,
                    Description = "program: a record to a mailbox; 1 if it went in (user.mail does this)" },
            new() { Name = "OS.MailReceive", Label = "OS_MailReceive", Parameters = new[] { "box", "into" }, BufferParameters = new[] { 1 }, Returns = true,
                    Description = "program: the oldest record in one of its mailboxes: who sent it plus one, or 0 if none is waiting (user.mail does this)" },
            new() { Name = "OS.MailWaiting", Label = "OS_MailWaiting", Parameters = new[] { "box" }, Returns = true,
                    Description = "program: how many records are waiting in a mailbox, or -1 (user.mail does this)" },
            new() { Name = "OS.MailClose", Label = "OS_MailClose", Parameters = new[] { "box" }, Returns = true,
                    Description = "program: close one of its mailboxes; 1, or 0 if it isn't its own (user.mail does this)" },
            new() { Name = "OS.Grow", Label = "OS_Grow", Parameters = new[] { "bytes" }, Returns = true,
                    Description = "program: more memory for its heap, right after what it has; where it starts, or 0 (memory.heap does this for new)" },
            new() { Name = "OS.SetSyscallHandler", Label = "OS_SetSyscallHandler", Parameters = new[] { "handler" },
                    Description = "kernel: run a method for system calls numbered 16 and up" },
            new() { Name = "OS.SyscallFrame", Label = "OS_SyscallFrame", Returns = true, Description = "kernel: the saved registers of the system call being handled" },
            new() { Name = "OS.Spawn", Label = "OS_Spawn", Parameters = new[] { "path" }, TextParameters = new[] { 0 }, Returns = true,
                    Description = "program: start a program from the disk; its process number, or -1" },
            new() { Name = "OS.Wait", Label = "OS_Wait", Parameters = new[] { "process" }, Returns = true,
                    Description = "program: wait for a child to finish; its exit code" },
            new() { Name = "OS.Run", Label = "OS_Run", Parameters = new[] { "path", "arguments" }, TextParameters = new[] { 0, 1 }, Returns = true,
                    Description = "program: start a program with a command line; its process number, or -1" },
            new() { Name = "OS.Arguments", Label = "OS_Arguments", Returns = true, ReturnsText = true,
                    Description = "program: its command line, as a ptr<u8>" },
            new() { Name = "OS.ReadFile", Label = "OS_ReadFile", InKernel = "Fat.ReadFile", InKernelUse = "fs.fat16", Parameters = new[] { "path", "buffer", "max" }, TextParameters = new[] { 0 }, BufferParameters = new[] { 1 }, Returns = true,
                    Description = "program: read a file into buffer; bytes read, or -1" },
            new() { Name = "OS.WriteFile", Label = "OS_WriteFile", InKernel = "Fat.WriteFile", InKernelUse = "fs.fat16", Parameters = new[] { "path", "data", "length" }, TextParameters = new[] { 0, 1 }, Returns = true,
                    Description = "program: create or replace a file; 1 if written" },
            new() { Name = "OS.AppendFile", Label = "OS_AppendFile", InKernel = "Fat.Append", InKernelUse = "fs.fat16", Parameters = new[] { "path", "data", "length" }, TextParameters = new[] { 0 }, BufferParameters = new[] { 1 }, Returns = true,
                    Description = "program: add to the end of a file (creating it); 1 if written" },
            new() { Name = "OS.DeleteFile", Label = "OS_DeleteFile", InKernel = "Fat.Delete", InKernelUse = "fs.fat16", Parameters = new[] { "path" }, TextParameters = new[] { 0 }, Returns = true,
                    Description = "program: delete a file; 1 if deleted" },
            new() { Name = "OS.ListFiles", Label = "OS_ListFiles", InKernel = "Fat.List", InKernelUse = "fs.fat16", Parameters = new[] { "path" }, TextParameters = new[] { 0 }, Returns = true,
                    Description = "program: print a folder; how many entries it has" },
            new() { Name = "OS.FileSize", Label = "OS_FileSize", InKernel = "Fat.FileSize", InKernelUse = "fs.fat16", Parameters = new[] { "path" }, TextParameters = new[] { 0 }, Returns = true,
                    Description = "program: a file's size, or -1" },
            new() { Name = "OS.ListFolder", Label = "OS_ListFolder", InKernel = "Fat.Entries", InKernelUse = "fs.fat16", Parameters = new[] { "path", "into", "max" }, TextParameters = new[] { 0 }, BufferParameters = new[] { 1 }, Returns = true,
                    Description = "program: a folder's entries, as FileInfo (use fs.fileinfo), into up to max of them; how many, or -1" },
            new() { Name = "OS.MakeFolder", Label = "OS_MakeFolder", InKernel = "Fat.MakeFolder", InKernelUse = "fs.fat16", Parameters = new[] { "path" }, TextParameters = new[] { 0 }, Returns = true,
                    Description = "program: make a folder; 1 if made" },
            new() { Name = "OS.Rename", Label = "OS_Rename", InKernel = "Fat.Rename", InKernelUse = "fs.fat16", Parameters = new[] { "from", "to" }, TextParameters = new[] { 0, 1 }, Returns = true,
                    Description = "program: rename a file or folder, or move it to another folder; 1 if done" },
            new() { Name = "OS.DeleteFolder", Label = "OS_DeleteFolder", InKernel = "Fat.DeleteFolder", InKernelUse = "fs.fat16", Parameters = new[] { "path" }, TextParameters = new[] { 0 }, Returns = true,
                    Description = "program: delete an empty folder; 1 if deleted" },
            new() { Name = "OS.ReadFileAt", Label = "OS_ReadFileAt", InKernel = "Fat.ReadAt", InKernelUse = "fs.fat16", Parameters = new[] { "path", "offset", "buffer", "max" }, TextParameters = new[] { 0 }, BufferParameters = new[] { 2 }, Returns = true,
                    Description = "program: read part of a file, from offset on, into buffer; bytes read, or -1" },
            new() { Name = "OS.SoundWrite", Label = "OS_SoundWrite", Parameters = new[] { "data", "length" }, BufferParameters = new[] { 0 }, Returns = true,
                    Description = "program: give the sound card 16-bit stereo sound; the bytes it took (0 while it's full), or -1 if there's no card" },
            new() { Name = "OS.SoundRate", Label = "OS_SoundRate", Parameters = new[] { "hz" }, Returns = true,
                    Description = "program: set the frames a second the sound card plays; the rate it got, or -1 if there's no card" },
            new() { Name = "OS.SoundQueued", Label = "OS_SoundQueued", Returns = true,
                    Description = "program: bytes of sound still to play, or -1 if there's no card" },
            new() { Name = "OS.SoundStop", Label = "OS_SoundStop", Returns = true,
                    Description = "program: stop the sound at once, dropping what hasn't played" },
            new() { Name = "OS.SoundVolume", Label = "OS_SoundVolume", Parameters = new[] { "percent" }, Returns = true,
                    Description = "program: set the sound card's volume, 0 to 100" },
            new() { Name = "OS.TaskName", Label = "OS_TaskName", InKernel = "Process.TaskName", InKernelUse = "proc.process", Parameters = new[] { "process", "buffer" }, BufferParameters = new[] { 1 }, Returns = true,
                    Description = "program: copy a process's name (up to 15 characters) into buffer" },
            new() { Name = "OS.Kill", Label = "OS_Kill", InKernel = "OS.KillTask", Parameters = new[] { "process" }, Returns = true,
                    Description = "program: stop another program; 1 if it was stopped" },
            new() { Name = "OS.KillTask", Label = "OS_KillTask", Parameters = new[] { "task" }, Returns = true,
                    Description = "kernel: stop a task with exit code -1" },
            new() { Name = "OS.FreeMemory", Label = "OS_FreeMemory", Returns = true, Description = "program: free memory in KB" },
            new() { Name = "OS.TotalMemory", Label = "OS_TotalMemory", Returns = true, Description = "program: usable memory in KB" },
            new() { Name = "OS.Reboot", Label = "OS_Reboot", Description = "restart the machine" },
            new() { Name = "OS.ClearScreen", Label = "OS_ClearScreen", Description = "clear the text screen" },
            new() { Name = "OS.PutCell", Label = "OS_PutCell", Parameters = new[] { "column", "row", "cell" },
                    Description = "put a character (low byte) and color (high byte) at a place on the text screen" },
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
