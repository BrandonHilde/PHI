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
    /// phi source -> boot.asm + kernel.asm -> nasm -> a bootable raw disk image:
    /// sector 0 is the boot sector, the kernel follows from sector 1 (loaded at 0x7E00).
    /// </summary>
    public static class Builder
    {
        const int SectorSize = 512;

        // The kernel is loaded at 0x7E00 and must end before 0x10000, the edge of segment 0.
        const int MaxKernelSectors = (0x10000 - X86_16Generator.KernelAddress) / SectorSize;

        // 1.44 MB: bootable as a hard disk, and the size QEMU and other tools expect of small images.
        const int ImageSize = 1_474_560;

        public static BuildResult Build(string phiFile, string outDir)
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
            CompileResult compiled = PhiCompiler.Compile(new SourceFile(Path.GetRelativePath(Environment.CurrentDirectory, phiFile), source.Text));

            foreach (Diagnostic d in compiled.Diagnostics)
                (d.Severity == Severity.Error ? result.Errors : result.Warnings).Add(d.ToString());

            if (!compiled.Success) return result;

            AsmUnit boot = compiled.Units.Single(u => u.Kind == UnitKind.Boot);
            AsmUnit? kernel = compiled.Units.SingleOrDefault(u => u.Kind == UnitKind.Kernel);

            byte[] kernelBin = Array.Empty<byte>();
            if (kernel != null)
            {
                byte[]? bin = Assemble(kernel, outDir, Array.Empty<string>(), result);
                if (bin == null) return result;
                kernelBin = bin;
            }

            int kernelSectors = (kernelBin.Length + SectorSize - 1) / SectorSize;
            if (kernelSectors > MaxKernelSectors)
            {
                result.Errors.Add($"the OS classes are {kernelBin.Length} bytes, but at most {MaxKernelSectors * SectorSize} " +
                                  "can be loaded in 16-bit mode for now (see Plan.md, Phase 3)");
                return result;
            }

            byte[]? bootBin = Assemble(boot, outDir, new[] { $"-dPHI_KERNEL_SECTORS={kernelSectors}" }, result);
            if (bootBin == null) return result;

            var image = new byte[ImageSize];
            bootBin.CopyTo(image, 0);
            kernelBin.CopyTo(image, SectorSize);
            File.WriteAllBytes(result.ImagePath, image);

            return result;
        }

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
