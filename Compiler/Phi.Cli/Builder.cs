using PhiBasicTranslator;
using PhiBasicTranslator.Structure;

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
    /// phi source -> one .asm per class -> nasm -> bootable raw disk image.
    /// </summary>
    public static class Builder
    {
        const int SectorSize = 512;

        // The boot sector currently loads a fixed 6 sectors after itself (BIT16x86_SectorPrep).
        const int SectorsLoadedByBootloader = 6;

        // 1.44 MB: large enough for any program today, and bootable as a floppy or a hard disk.
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

            PhiCodebase codebase;
            TextWriter console = Console.Out;
            var translatorLog = new StringWriter();
            try
            {
                // the translator prints its own debugging output; keep it out of the CLI's
                Console.SetOut(translatorLog);
                codebase = new Translator().TranslateFile(phiFile);
            }
            catch (Exception e)
            {
                result.Errors.Add($"Translator crashed: {e.GetType().Name}: {e.Message}");
                return result;
            }
            finally
            {
                Console.SetOut(console);
                File.WriteAllText(Path.Combine(outDir, "translator.log"), translatorLog.ToString());
            }

            var classes = codebase.ClassList.Where(c => c.translatedASM.Count > 0).ToList();

            if (classes.Count == 0)
            {
                result.Errors.Add($"No code was generated. Supported base classes are " +
                                  $"'{Defs.OSBootloader}' and '{Defs.OSSectorTwo}', e.g. phi.Hello:{Defs.OSBootloader} {{ ... }}");
                return result;
            }

            if (classes[0].Inherit != Defs.OSBootloader)
                result.Errors.Add($"The first class must inherit {Defs.OSBootloader} (found '{classes[0].Name}:{classes[0].Inherit}').");

            var image = new List<byte>();

            for (int i = 0; i < classes.Count; i++)
            {
                PhiClass cls = classes[i];
                string stem = Path.Combine(outDir, $"{i}_{cls.Name}");
                string asmPath = stem + ".asm";
                string binPath = stem + ".bin";

                File.WriteAllLines(asmPath, cls.translatedASM);

                var (code, output) = Toolchain.Run(Toolchain.Nasm, new[] { "-f", "bin", asmPath, "-o", binPath });
                if (code != 0)
                {
                    result.Errors.Add($"nasm failed on {Path.GetFileName(asmPath)} (class {cls.Name}):\n{output}");
                    continue;
                }

                byte[] bin = File.ReadAllBytes(binPath);

                if (i == 0 && (bin.Length != SectorSize || bin[510] != 0x55 || bin[511] != 0xAA))
                    result.Errors.Add($"Boot sector must be exactly {SectorSize} bytes ending in 0x55AA; got {bin.Length} bytes.");

                image.AddRange(bin);
                while (image.Count % SectorSize != 0) image.Add(0);
            }

            if (!result.Success) return result;

            int extraSectors = image.Count / SectorSize - 1;
            if (extraSectors > SectorsLoadedByBootloader)
                result.Warnings.Add($"Code after the boot sector is {extraSectors} sectors, but the bootloader only loads " +
                                    $"{SectorsLoadedByBootloader}. Anything past that will be missing at runtime.");

            if (image.Count > ImageSize)
            {
                result.Errors.Add($"Program is {image.Count} bytes, larger than the {ImageSize}-byte disk image.");
                return result;
            }

            while (image.Count < ImageSize) image.Add(0);
            File.WriteAllBytes(result.ImagePath, image.ToArray());

            return result;
        }
    }
}
