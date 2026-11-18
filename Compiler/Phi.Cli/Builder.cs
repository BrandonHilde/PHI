using System.Text.RegularExpressions;
using Phi.Compiler;
using Phi.Compiler.CodeGen;
using Phi.Compiler.Libraries;
using Phi.Compiler.Semantics;

namespace Phi.Cli
{
    public sealed class BuildResult
    {
        public bool Success => Errors.Count == 0;
        public string ImagePath { get; init; } = string.Empty;

        /// <summary>For a user program (Program classes): the flat binary, instead of a disk image.</summary>
        public string ProgramPath => Path.ChangeExtension(ImagePath, ".bin");
        public bool IsProgram { get; set; }

        /// <summary>For a program built for another system: the executable (NAME on Linux, NAME.exe on Windows).</summary>
        public string? ExecutablePath { get; set; }

        /// <summary>A macOS program built with --app: its NAME.app.</summary>
        public string? AppPath { get; set; }

        /// <summary>NASM's listing of each unit is saved next to its .asm (as NAME.lst).</summary>
        public bool Listing { get; init; }
        public List<string> Errors { get; } = new();
        public List<string> Warnings { get; } = new();
    }

    /// <summary>
    /// phi source -> .asm files -> nasm -> a bootable raw disk image. Two layouts:
    ///
    ///   16-bit program:  sector 0 boot sector | sector 1.. OS classes (loaded at 0x7E00)
    ///   32-bit kernel:   sector 0 stage 1 | sectors 1-8 stage 2 | sector 9.. kernel (loaded at 0x100000)
    ///                    | sector 2048..: a 16 MB FAT16 partition with NAME.rootfs/ (or rootfs/)
    ///
    /// See docs/memory-map.md.
    /// </summary>
    /// <summary>What phi build --library made: the systems it built for, and those it didn't with why.</summary>
    public sealed class LibraryBuildResult
    {
        public string LibraryPath { get; init; } = "";
        public List<string> Built { get; } = new();
        public List<(string System, string Why)> Skipped { get; } = new();
        public List<string> Errors { get; } = new();
        public List<string> Warnings { get; } = new();
        public bool Success => Errors.Count == 0;
    }

    public static class Builder
    {
        const int SectorSize = 512;

        // 16-bit OS classes are loaded at 0x7E00 and must end before 0x10000, the edge of segment 0.
        const int MaxOsSectors = (0x10000 - X86Generator.OsAddress) / SectorSize;

        // A 32-bit kernel is loaded at 1 MB, where memory is no limit: what holds it to just
        // under 1 MB is the disk, where it sits between the loader and the FAT16 partition.
        const int Stage2Sectors = 8;
        const int MaxKernelSectors = FatImage.PartitionStart - (1 + Stage2Sectors);

        // 1.44 MB: bootable as a hard disk, and the size QEMU and other tools expect of small images.
        const int ImageSize = 1_474_560;

        /// <param name="appendSource">Extra PHI text added after the file's own (used by the test runner).</param>
        /// <param name="kernel">For a program in a kernel's rootfs: the kernel's file, whose folder `use` searches too.</param>
        /// <param name="listing">
        /// Also save NASM's listing of each unit (NAME.lst), and of the programs on a kernel's
        /// disk, which phi profile and a test that times out map addresses with.
        /// </param>
        /// <param name="host">The system a program is for: PHI OS, or Linux or Windows (an ordinary executable).</param>
        public static BuildResult Build(string phiFile, string outDir, string appendSource = "", string? kernel = null,
                                        bool listing = false, Host host = Host.PhiOS, bool debug = false, int stack = 0, bool v2 = false, bool app = false)
        {
            string name = Path.GetFileNameWithoutExtension(phiFile);
            Phi.Compiler.CodeGen.X86Generator.MainStack = stack;
            Phi.Compiler.CodeGen.X86Generator.X8664V2 = v2;
            var result = new BuildResult { ImagePath = Path.Combine(outDir, name + ".img"), Listing = listing };

            if (!File.Exists(phiFile))
            {
                result.Errors.Add($"File not found: {phiFile}");
                return result;
            }

            Directory.CreateDirectory(outDir);

            var source = SourceFile.Load(phiFile);
            CompileResult compiled = PhiCompiler.Compile(new SourceFile(Path.GetRelativePath(Environment.CurrentDirectory, phiFile), source.Text + appendSource),
                                                         kernel: kernel, host: host, debug: debug && (host.IsWindows() || host.IsMacOS()));

            foreach (Diagnostic d in compiled.Diagnostics)
                (d.Severity == Severity.Error ? result.Errors : result.Warnings).Add(d.ToString());

            if (!compiled.Success) return result;

            if (host.IsMacOS())
            {
                // a macOS program: Apple's clang assembles its ARM code and links it with libSystem
                // (and signs it, which Apple Silicon needs to run it)
                result.ExecutablePath = Path.Combine(outDir, name);
                if (!AssembleMacOS(compiled.Units.Single(), outDir, result.ExecutablePath, result)) return result;
                if (debug && !DebugSymbols(result.ExecutablePath, result)) return result;
                if (app && !MakeApp(name, outDir, result)) return result;
                result.IsProgram = true;
                return result;
            }

            if (host != Host.PhiOS)
            {
                // an ordinary program: NASM writes the whole executable, headers and all
                AsmUnit unit = compiled.Units.Single();
                byte[]? executable = Assemble(unit, outDir, Array.Empty<string>(), result);
                if (executable == null) return result;
                result.ExecutablePath = Path.Combine(outDir, name + (host.IsWindows() ? ".exe" : ""));
                if (unit.Debug != null) Pdb.Write(unit.Debug, outDir, name, host, executable);
                WriteWhenFree(result.ExecutablePath, executable);
                if (!OperatingSystem.IsWindows())
                    File.SetUnixFileMode(result.ExecutablePath, File.GetUnixFileMode(result.ExecutablePath)
                                                                | UnixFileMode.UserExecute | UnixFileMode.GroupExecute | UnixFileMode.OtherExecute);
                result.IsProgram = true;
                return result;
            }

            if (compiled.Units.Any(u => u.Kind == UnitKind.Program))
            {
                // a user program isn't bootable: it's a file for a kernel's disk
                byte[]? program = Assemble(compiled.Units.Single(), outDir, Array.Empty<string>(), result);
                if (program != null && program.Length > ProgramMost)
                {
                    result.Errors.Add($"the program is {program.Length:N0} bytes, {program.Length - ProgramMost:N0} more than a PHI OS program can be " +
                                      $"({ProgramMost:N0}: its code and variables go from 0x40000000 up to its stack, so Process.Spawn couldn't start it). " +
                                      "Put large arrays on the heap (ptr<u8> buffer: new u8[100000];) or leave out modules it doesn't need");
                    program = null;
                }
                if (program != null) File.WriteAllBytes(result.ProgramPath, program);
                result.IsProgram = true;
                return result;
            }

            byte[]? image = compiled.Units.Any(u => u.Kind == UnitKind.Kernel)
                ? Layout32(compiled, phiFile, outDir, result, FindRootfs(phiFile))
                : Layout16(compiled, outDir, result);

            if (image != null) File.WriteAllBytes(result.ImagePath, image);
            return result;
        }

        /// <summary>
        /// phi build NAME.phi --library: NAME.philib, with a version for each system the library
        /// builds for (or only <paramref name="only"/>). One that doesn't build for a system (a
        /// driver on Linux, say) just has no version for it; if none builds, those are the errors.
        /// </summary>
        public static LibraryBuildResult BuildLibrary(string phiFile, string outDir, Host? only = null)
        {
            string name = Path.GetFileNameWithoutExtension(phiFile);
            var result = new LibraryBuildResult { LibraryPath = Path.Combine(outDir, name + CompiledLibrary.Extension) };
            if (!File.Exists(phiFile))
            {
                result.Errors.Add($"File not found: {phiFile}");
                return result;
            }

            Directory.CreateDirectory(outDir);
            string work = Path.Combine(Path.GetTempPath(), "phi-library-" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(work);
            var library = new CompiledLibrary { Name = name };
            List<string>? firstErrors = null;
            try
            {
                var source = SourceFile.Load(phiFile);
                foreach (LibraryPlatform platform in LibraryPlatform.All.Where(p => only == null || p.Host == only))
                {
                    var file = new SourceFile(Path.GetRelativePath(Environment.CurrentDirectory, phiFile), source.Text);
                    CompileResult compiled = PhiCompiler.CompileLibrary(file, platform, name);
                    foreach (Diagnostic w in compiled.Diagnostics.Where(d => d.Severity != Severity.Error))
                        if (!result.Warnings.Contains(w.ToString())) result.Warnings.Add(w.ToString());

                    var errors = compiled.Diagnostics.Where(d => d.Severity == Severity.Error).Select(d => d.ToString()).ToList();
                    if (errors.Count == 0 && platform.Host.IsArm())
                    {
                        // macOS's: the object file Apple's clang makes, kept whole, for the linker
                        // (CodeGen/A64/A64Generator.Library.cs)
                        AsmUnit armUnit = compiled.Units.Single();
                        string armAsm = Path.Combine(work, platform.Key + ".s");
                        string armObj = Path.Combine(work, platform.Key + ".o");
                        File.WriteAllText(armAsm, armUnit.Text);
                        string clang;
                        if (!OperatingSystem.IsMacOS() && Environment.GetEnvironmentVariable("PHI_CLANG") == null)
                        {
                            errors.Add("its object file is made by Apple's clang, so it's built on a Mac (or with PHI_CLANG set to a clang that makes Mach-O arm64)");
                            clang = "";
                        }
                        else try { clang = Toolchain.Clang; }
                        catch (Exception e) when (e is FileNotFoundException or InvalidOperationException)
                        {
                            errors.Add(e.Message);
                            clang = "";
                        }
                        if (clang != "")
                        {
                            var (armCode, armOutput) = Toolchain.Run(clang, new[] { "-arch", "arm64", "-mmacosx-version-min=11.0", "-c", armAsm, "-o", armObj });
                            if (armCode != 0) errors.Add($"clang failed on {name} for {platform.Description} (this is a bug in phi's ARM code, or in an arm. block):\n{armOutput}");
                            else
                            {
                                CompiledTarget target = armUnit.Library!.Target;
                                target.Path = result.LibraryPath;
                                target.Sections.Add(new LibrarySection(File.ReadAllBytes(armObj), 16));
                                library.Targets[platform.Key] = target;
                                result.Built.Add(platform.Description);
                                continue;
                            }
                        }
                    }
                    else if (errors.Count == 0)
                    {
                        AsmUnit unit = compiled.Units.Single();
                        string asm = Path.Combine(work, platform.Key + ".asm");
                        string obj = Path.Combine(work, platform.Key + ".o");
                        File.WriteAllText(asm, unit.Text);
                        var (code, output) = Toolchain.Run(Toolchain.Nasm, new[] { "-f", platform.Host.Is64() ? "elf64" : "elf32", "-o", obj, asm }, null);
                        if (code != 0)
                        {
                            errors.Add($"nasm failed on {name} for {platform.Description} (this is a bug in phi, or in an asm. block):\n{output}");
                        }
                        else
                        {
                            CompiledTarget target = unit.Library!.Target;
                            target.Path = result.LibraryPath;
                            ElfObject.ReadInto(target, File.ReadAllBytes(obj), unit.Library.Exports, unit.Library.Lines);
                            library.Targets[platform.Key] = target;
                            result.Built.Add(platform.Description);
                            continue;
                        }
                    }

                    firstErrors ??= errors;
                    result.Skipped.Add((platform.Description, errors[0]));
                }
            }
            finally
            {
                try { Directory.Delete(work, recursive: true); } catch (IOException) { }
            }

            if (library.Targets.Count == 0)
            {
                result.Errors.AddRange(firstErrors ?? new List<string> { "nothing to build" });
                return result;
            }
            library.Write(result.LibraryPath);
            return result;
        }

        /// <summary>A user program from a kernel's rootfs, compiled for its disk; null (with errors) if it fails.</summary>
        static byte[]? CompileProgram(string phiFile, string kernel, string outDir, List<string> errors, List<string> warnings, bool listing)
        {
            string dir = Path.Combine(outDir, "programs", Path.GetFileNameWithoutExtension(phiFile));
            BuildResult built = Build(phiFile, dir, kernel: kernel, listing: listing);
            // a mistake in code that several programs use is reported once, not once for each
            errors.AddRange(built.Errors.Where(e => !errors.Contains(e)));
            warnings.AddRange(built.Warnings.Where(w => !warnings.Contains(w)));
            if (!built.Success) return null;
            if (!built.IsProgram)
            {
                errors.Add($"{phiFile}: a .phi file in a rootfs folder becomes a program on the disk, so its classes must inherit Program");
                return null;
            }
            return File.ReadAllBytes(built.ProgramPath);
        }

        static byte[]? Layout16(CompileResult compiled, string outDir, BuildResult result)
        {
            AsmUnit boot = compiled.Units.Single(u => u.Kind == UnitKind.Boot);
            AsmUnit? os = compiled.Units.SingleOrDefault(u => u.Kind == UnitKind.Os);

            byte[] osBin = Array.Empty<byte>();
            if (os != null)
            {
                byte[]? bin = Assemble(os, outDir, Array.Empty<string>(), result);
                if (bin == null) return null;
                osBin = bin;
            }

            int osSectors = Sectors(osBin);
            if (osSectors > MaxOsSectors)
            {
                result.Errors.Add($"the OS classes are {osBin.Length} bytes, but at most {MaxOsSectors * SectorSize} " +
                                  "can be loaded by a 16-bit program; a 32-bit kernel (phi.Name:Kernel) can be almost 1 MB");
                return null;
            }

            byte[]? bootBin = Assemble(boot, outDir, new[] { $"-dPHI_KERNEL_SECTORS={osSectors}" }, result);
            if (bootBin == null) return null;

            var image = new byte[ImageSize];
            bootBin.CopyTo(image, 0);
            osBin.CopyTo(image, SectorSize);
            return image;
        }

        /// <summary>
        /// Where a file's build folder goes: next to it, or for a program in a kernel's rootfs
        /// folder, next to that folder (which becomes a disk, with only 8.3 names).
        /// </summary>
        public static string BuildHome(string file)
        {
            string home = Path.GetDirectoryName(Path.GetFullPath(file))!;
            string name = Path.GetFileName(home);
            bool rootfs = name.Equals("rootfs", StringComparison.OrdinalIgnoreCase) || name.EndsWith(".rootfs", StringComparison.OrdinalIgnoreCase);
            return rootfs ? Path.GetDirectoryName(home)! : home;
        }

        /// <summary>
        /// The folder copied into a kernel's file system: NAME.rootfs/ or rootfs/ next to the
        /// program. NAME.rootfs can also be a text file holding the path of a folder to use.
        /// </summary>
        public static string? FindRootfs(string phiFile)
        {
            string dir = Path.GetDirectoryName(Path.GetFullPath(phiFile))!;
            string own = Path.Combine(dir, Path.GetFileNameWithoutExtension(phiFile) + ".rootfs");
            if (Directory.Exists(own)) return own;
            if (File.Exists(own))
            {
                string target = Path.GetFullPath(Path.Combine(dir, File.ReadAllText(own).Trim()));
                if (Directory.Exists(target)) return target;
            }
            string shared = Path.Combine(dir, "rootfs");
            return Directory.Exists(shared) ? shared : null;
        }

        static byte[]? Layout32(CompileResult compiled, string kernelFile, string outDir, BuildResult result, string? rootfs)
        {
            AsmUnit kernel = compiled.Units.Single(u => u.Kind == UnitKind.Kernel);
            AsmUnit stage1 = compiled.BootStages.Single(u => u.Name == "stage1");
            AsmUnit stage2 = compiled.BootStages.Single(u => u.Name == "stage2");

            byte[]? kernelBin = Assemble(kernel, outDir, Array.Empty<string>(), result);
            if (kernelBin == null) return null;

            int kernelSectors = Math.Max(Sectors(kernelBin), 1);
            if (kernelSectors > MaxKernelSectors)
            {
                result.Errors.Add($"the kernel is {kernelBin.Length} bytes, but at most {MaxKernelSectors * SectorSize} fit on the disk before its file system");
                return null;
            }

            byte[]? stage2Bin = Assemble(stage2, outDir, new[] { $"-dPHI_KERNEL_SECTORS={kernelSectors}" }, result);
            byte[]? stage1Bin = Assemble(stage1, outDir, new[] { $"-dPHI_STAGE2_SECTORS={Stage2Sectors}" }, result);
            if (stage1Bin == null || stage2Bin == null) return null;

            if (stage2Bin.Length != Stage2Sectors * SectorSize)
                throw new InvalidOperationException($"stage2 must be exactly {Stage2Sectors} sectors");

            var fsErrors = new List<string>();
            byte[] partition = FatImage.Build(rootfs, fsErrors, source => CompileProgram(source, kernelFile, outDir, fsErrors, result.Warnings, result.Listing));
            if (fsErrors.Count > 0)
            {
                result.Errors.AddRange(fsErrors);
                return null;
            }

            var image = new byte[(FatImage.PartitionStart + FatImage.PartitionSectors) * SectorSize];
            stage1Bin.CopyTo(image, 0);
            stage2Bin.CopyTo(image, SectorSize);
            kernelBin.CopyTo(image, (1 + Stage2Sectors) * SectorSize);
            FatImage.WritePartitionEntry(image);
            partition.CopyTo(image, FatImage.PartitionStart * SectorSize);
            return image;
        }

        static int Sectors(byte[] bin) => (bin.Length + SectorSize - 1) / SectorSize;

        /// <summary>
        /// Writes an executable, waiting (up to 5 seconds) while the last one is still in use: a
        /// program that was just ended can keep its file open a moment longer on Windows.
        /// </summary>
        static void WriteWhenFree(string path, byte[] bytes)
        {
            for (int tries = 0; ; tries++)
            {
                try
                {
                    File.WriteAllBytes(path, bytes);
                    return;
                }
                catch (IOException) when (tries < 50)
                {
                    Thread.Sleep(100);
                }
            }
        }

        const int SlowAssembly = 30;

        /// <summary>
        /// The most a PHI OS program (NAME.BIN, code and variables) can be: proc.process maps one
        /// page table, 1024 pages, and keeps 20 of them for the stack (16), the gap under it (2) and
        /// the spare its running out becomes an error on (2). See Process.Spawn.
        /// </summary>
        public const int ProgramMost = (1024 - 16 - 2 - 2) * 4096;

        /// <summary>
        /// A macOS program's assembly (NAME.s) made a Mach-O executable: clang -c, then clang to
        /// link it (libSystem, and the linker's ad-hoc signature). Two steps, so an assembler's
        /// complaint (phi's mistake) and a linker's (a function that isn't anywhere) are told apart.
        /// </summary>
        /// <summary>
        /// phi build --target macos --app: the program as a Mac app, NAME.app, which the Finder
        /// opens with a double click and the Dock shows: NAME.app/Contents/MacOS/NAME (the
        /// program), Info.plist (what macOS asks of an app: its name, its identifier,
        /// org.phi.NAME, the program to start), and Resources/; signed as a whole (ad hoc, as the
        /// linker signs the program alone). result.ExecutablePath is the program inside it.
        /// </summary>
        static bool MakeApp(string name, string outDir, BuildResult result)
        {
            string bundle = Path.GetFullPath(Path.Combine(outDir, name + ".app"));
            if (Directory.Exists(bundle)) Directory.Delete(bundle, recursive: true);
            string contents = Path.Combine(bundle, "Contents");
            Directory.CreateDirectory(Path.Combine(contents, "MacOS"));
            Directory.CreateDirectory(Path.Combine(contents, "Resources"));
            string inside = Path.Combine(contents, "MacOS", name);
            File.Copy(result.ExecutablePath!, inside);
            File.SetUnixFileMode(inside, File.GetUnixFileMode(result.ExecutablePath!));
            string identifier = "org.phi." + new string(name.Select(c => char.IsAsciiLetterOrDigit(c) || c == '-' ? c : '-').ToArray());
            string Escape(string text) => System.Security.SecurityElement.Escape(text);
            File.WriteAllText(Path.Combine(contents, "Info.plist"), $"""
                <?xml version="1.0" encoding="UTF-8"?>
                <!DOCTYPE plist PUBLIC "-//Apple//DTD PLIST 1.0//EN" "http://www.apple.com/DTDs/PropertyList-1.0.dtd">
                <plist version="1.0">
                <dict>
                	<key>CFBundleDevelopmentRegion</key>
                	<string>en</string>
                	<key>CFBundleExecutable</key>
                	<string>{Escape(name)}</string>
                	<key>CFBundleIdentifier</key>
                	<string>{Escape(identifier)}</string>
                	<key>CFBundleInfoDictionaryVersion</key>
                	<string>6.0</string>
                	<key>CFBundleName</key>
                	<string>{Escape(name)}</string>
                	<key>CFBundlePackageType</key>
                	<string>APPL</string>
                	<key>CFBundleShortVersionString</key>
                	<string>1.0</string>
                	<key>CFBundleVersion</key>
                	<string>1</string>
                	<key>LSMinimumSystemVersion</key>
                	<string>11.0</string>
                	<key>NSHighResolutionCapable</key>
                	<true/>
                	<key>NSPrincipalClass</key>
                	<string>NSApplication</string>
                </dict>
                </plist>

                """);
            File.WriteAllText(Path.Combine(contents, "PkgInfo"), "APPL????");
            var (code, output) = Toolchain.Run("/usr/bin/plutil", new[] { "-lint", Path.Combine(contents, "Info.plist") });
            if (code != 0)
            {
                result.Errors.Add($"the app's Info.plist isn't right (a bug in phi):\n{output}");
                return false;
            }
            (code, output) = Toolchain.Run("/usr/bin/codesign", new[] { "--force", "--sign", "-", bundle });
            if (code != 0)
            {
                result.Errors.Add($"codesign couldn't sign {bundle}:\n{output}");
                return false;
            }
            result.ExecutablePath = inside;
            result.AppPath = bundle;
            return true;
        }

        /// <summary>
        /// phi build --target macos --debug: the program's lines, for a debugger: the assembly's
        /// .loc lines went into its object file's DWARF, which dsymutil gathers into NAME.dSYM
        /// beside it (the linker leaves debug information in the object files), where lldb and
        /// atos find it.
        /// </summary>
        static bool DebugSymbols(string executable, BuildResult result)
        {
            var (code, output) = Toolchain.Run("/usr/bin/dsymutil", new[] { executable, "-o", executable + ".dSYM" });
            if (code != 0)
            {
                result.Errors.Add($"dsymutil couldn't gather the program's debug information:\n{output}");
                return false;
            }
            return true;
        }

        static bool AssembleMacOS(AsmUnit unit, string outDir, string executable, BuildResult result)
        {
            string asmPath = Path.GetFullPath(Path.Combine(outDir, unit.Name + ".s"));
            string objPath = Path.ChangeExtension(asmPath, ".o");
            File.WriteAllText(asmPath, unit.Text);
            string[] arch = { "-arch", "arm64", "-mmacosx-version-min=11.0" };
            string clang;
            try { clang = Toolchain.Clang; }
            catch (ToolNotFoundException e)
            {
                result.Errors.Add($"{e.Message} (Apple's Command Line Tools: xcode-select --install)");
                return false;
            }
            var (code, output) = Toolchain.Run(clang, arch.Concat(new[] { "-c", asmPath, "-o", objPath }));
            if (code != 0)
            {
                result.Errors.Add($"clang failed to assemble {asmPath} (this is a bug in phi's ARM code, or in an arm. block):\n{output}");
                return false;
            }
            // (with the compiled libraries' object files, each written out beside it)
            var objects = new List<string> { objPath };
            foreach (var (libraryName, bytes) in unit.Objects)
            {
                string libraryObject = Path.Combine(Path.GetDirectoryName(objPath)!, libraryName + ".philib.o");
                File.WriteAllBytes(libraryObject, bytes);
                objects.Add(libraryObject);
            }
            (code, output) = Toolchain.Run(clang, arch.Concat(objects).Concat(new[] { "-o", Path.GetFullPath(executable) }).Concat(unit.Links));
            if (code != 0)
            {
                result.Errors.Add($"clang failed to link {objPath}:\n{output}");
                return false;
            }
            return true;
        }

        static byte[]? Assemble(AsmUnit unit, string outDir, string[] defines, BuildResult result)
        {
            string asmPath = Path.Combine(outDir, unit.Name + ".asm");
            string binPath = Path.Combine(outDir, unit.Name + ".bin");
            // a debugger's information needs to know where every label ended up. (NASM takes the
            // map's name as one word, so it's just the name, with NASM running in the folder.)
            string mapLine = unit.Debug != null ? $"[map symbols {unit.Name}.map]\n" : "";
            File.WriteAllText(asmPath, mapLine + unit.Text);

            var args = new List<string> { "-f", "bin", "-o", Path.GetFullPath(binPath) };
            if (result.Listing) args.AddRange(new[] { "-l", Path.GetFullPath(Path.ChangeExtension(asmPath, ".lst")) });
            args.AddRange(defines);
            args.Add(Path.GetFullPath(asmPath));

            var clock = System.Diagnostics.Stopwatch.StartNew();
            var (code, output) = Toolchain.Run(Toolchain.Nasm, args, unit.Debug != null ? Path.GetFullPath(outDir) : null);
            // most of a big program's build is NASM making each jump as short as it can
            if (clock.Elapsed.TotalSeconds >= SlowAssembly && Environment.GetEnvironmentVariable("NASMENV") == null)
                result.Warnings.Add($"NASM took {clock.Elapsed.TotalSeconds:F0} s; while developing, set the environment variable NASMENV=-O1 for builds many times faster and a program a few percent larger");
            if (code != 0)
            {
                Match tooBig = Regex.Match(output, @"TIMES value (-\d+) is negative");
                if (unit.Kind == UnitKind.Boot && tooBig.Success)
                {
                    int over = -int.Parse(tooBig.Groups[1].Value);
                    result.Errors.Add($"the boot sector is {over} bytes too big (it has to fit in 512 bytes). " +
                                      "Move code into a phi.Name:OS class and start it with call Bootloader.JumpToSectorTwo;");
                }
                else
                {
                    result.Errors.Add($"nasm failed on {asmPath} (this is a bug in phi, or in an asm. block):\n{output}");
                }
                return null;
            }

            return File.ReadAllBytes(binPath);
        }

        /// <summary>
        /// A copy of a kernel's disk image that keeps what the kernel saved: the first time it is the
        /// new image; after that the boot sectors and kernel (everything before the FAT16 partition)
        /// are replaced, and so are the programs (NAME.BIN) that changed or are new. Every other
        /// file in the partition is still there.
        /// </summary>
        /// <param name="refreshed">gets the path of each program that was replaced or added</param>
        /// <returns>the kept image, next to the new one as NAME.kept.img</returns>
        public static string KeepDisk(string image, out bool fresh, List<string> refreshed, List<string> errors)
        {
            string kept = Path.ChangeExtension(image, ".kept.img");
            byte[] built = File.ReadAllBytes(image);
            int partition = FatImage.PartitionStart * SectorSize;

            fresh = !File.Exists(kept) || new FileInfo(kept).Length != built.Length || built.Length <= partition;
            if (fresh)
            {
                File.Copy(image, kept, overwrite: true);
                return kept;
            }

            byte[] old = File.ReadAllBytes(kept);
            byte[] merged = FatImage.RefreshPrograms(old[partition..], built[partition..], refreshed, errors);
            if (errors.Count > 0) return kept;

            built.AsSpan(0, partition).CopyTo(old);
            merged.CopyTo(old, partition);
            File.WriteAllBytes(kept, old);
            return kept;
        }
    }
}
