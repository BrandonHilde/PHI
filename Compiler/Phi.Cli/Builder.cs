using System.Text.RegularExpressions;
using Phi.Compiler;
using Phi.Compiler.CodeGen;
using Phi.Compiler.Semantics;

namespace Phi.Cli
{
    public sealed class BuildResult
    {
        public bool Success => Errors.Count == 0;
        public string ImagePath { get; init; } = string.Empty;
        public List<string> Errors { get; } = new();
        public List<string> Warnings { get; } = new();
    }

    /// <summary>
    /// phi source -> .asm files -> nasm -> a bootable raw disk image. Two layouts:
    ///
    ///   16-bit program:  sector 0 boot sector | sector 1.. OS classes (loaded at 0x7E00)
    ///   32-bit kernel:   sector 0 stage 1 | sectors 1-8 stage 2 | sector 9.. kernel (loaded at 0x10000)
    ///                    | sector 2048..: a 16 MB FAT16 partition with NAME.rootfs/ (or rootfs/)
    ///
    /// See docs/memory-map.md.
    /// </summary>
    public static class Builder
    {
        const int SectorSize = 512;

        // 16-bit OS classes are loaded at 0x7E00 and must end before 0x10000, the edge of segment 0.
        const int MaxOsSectors = (0x10000 - X86Generator.OsAddress) / SectorSize;

        // A 32-bit kernel is loaded at 0x10000 and must end by 0x90000, where the stack area begins.
        const int MaxKernelSectors = (0x90000 - X86Generator.KernelAddress) / SectorSize;
        const int Stage2Sectors = 8;

        // 1.44 MB: bootable as a hard disk, and the size QEMU and other tools expect of small images.
        const int ImageSize = 1_474_560;

        /// <param name="appendSource">Extra PHI text added after the file's own (used by the test runner).</param>
        public static BuildResult Build(string phiFile, string outDir, string appendSource = "")
        {
            string name = Path.GetFileNameWithoutExtension(phiFile);
            var result = new BuildResult { ImagePath = Path.Combine(outDir, name + ".img") };

            if (!File.Exists(phiFile))
            {
                result.Errors.Add($"File not found: {phiFile}");
                return result;
            }

            Directory.CreateDirectory(outDir);

            var source = SourceFile.Load(phiFile);
            CompileResult compiled = PhiCompiler.Compile(new SourceFile(Path.GetRelativePath(Environment.CurrentDirectory, phiFile), source.Text + appendSource));

            foreach (Diagnostic d in compiled.Diagnostics)
                (d.Severity == Severity.Error ? result.Errors : result.Warnings).Add(d.ToString());

            if (!compiled.Success) return result;

            byte[]? image = compiled.Units.Any(u => u.Kind == UnitKind.Kernel)
                ? Layout32(compiled, outDir, result, FindRootfs(phiFile))
                : Layout16(compiled, outDir, result);

            if (image != null) File.WriteAllBytes(result.ImagePath, image);
            return result;
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
                                  "can be loaded by a 16-bit program; a 32-bit kernel (phi.Name:Kernel) can be up to 512 KB");
                return null;
            }

            byte[]? bootBin = Assemble(boot, outDir, new[] { $"-dPHI_KERNEL_SECTORS={osSectors}" }, result);
            if (bootBin == null) return null;

            var image = new byte[ImageSize];
            bootBin.CopyTo(image, 0);
            osBin.CopyTo(image, SectorSize);
            return image;
        }

        /// <summary>The folder copied into a kernel's file system: NAME.rootfs/ or rootfs/ next to the program.</summary>
        public static string? FindRootfs(string phiFile)
        {
            string dir = Path.GetDirectoryName(Path.GetFullPath(phiFile))!;
            string own = Path.Combine(dir, Path.GetFileNameWithoutExtension(phiFile) + ".rootfs");
            if (Directory.Exists(own)) return own;
            string shared = Path.Combine(dir, "rootfs");
            return Directory.Exists(shared) ? shared : null;
        }

        static byte[]? Layout32(CompileResult compiled, string outDir, BuildResult result, string? rootfs)
        {
            AsmUnit kernel = compiled.Units.Single(u => u.Kind == UnitKind.Kernel);
            AsmUnit stage1 = compiled.BootStages.Single(u => u.Name == "stage1");
            AsmUnit stage2 = compiled.BootStages.Single(u => u.Name == "stage2");

            byte[]? kernelBin = Assemble(kernel, outDir, Array.Empty<string>(), result);
            if (kernelBin == null) return null;

            int kernelSectors = Math.Max(Sectors(kernelBin), 1);
            if (kernelSectors > MaxKernelSectors)
            {
                result.Errors.Add($"the kernel is {kernelBin.Length} bytes, but the loader can load at most {MaxKernelSectors * SectorSize}");
                return null;
            }

            byte[]? stage2Bin = Assemble(stage2, outDir, new[] { $"-dPHI_KERNEL_SECTORS={kernelSectors}" }, result);
            byte[]? stage1Bin = Assemble(stage1, outDir, new[] { $"-dPHI_STAGE2_SECTORS={Stage2Sectors}" }, result);
            if (stage1Bin == null || stage2Bin == null) return null;

            if (stage2Bin.Length != Stage2Sectors * SectorSize)
                throw new InvalidOperationException($"stage2 must be exactly {Stage2Sectors} sectors");

            var fsErrors = new List<string>();
            byte[] partition = FatImage.Build(rootfs, fsErrors);
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

        static byte[]? Assemble(AsmUnit unit, string outDir, string[] defines, BuildResult result)
        {
            string asmPath = Path.Combine(outDir, unit.Name + ".asm");
            string binPath = Path.Combine(outDir, unit.Name + ".bin");
            File.WriteAllText(asmPath, unit.Text);

            var args = new List<string> { "-f", "bin", "-o", binPath };
            args.AddRange(defines);
            args.Add(asmPath);

            var (code, output) = Toolchain.Run(Toolchain.Nasm, args);
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
    }
}
