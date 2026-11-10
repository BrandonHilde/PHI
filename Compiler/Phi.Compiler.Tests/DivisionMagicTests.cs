using Phi.Compiler.CodeGen;

namespace Phi.Compiler.Tests
{
    /// <summary>
    /// Dividing by a constant with a multiply (DivisionMagic): the same steps the generated
    /// instructions take, against real division, for the numbers where rounding goes wrong first.
    /// </summary>
    public class DivisionMagicTests
    {
        static IEnumerable<uint> Divisors()
        {
            for (uint d = 3; d <= 2000; d++)
                if ((d & (d - 1)) != 0) yield return d;
            foreach (uint d in new uint[]
                     {
                         3600, 10000, 65535, 65537, 86400, 100000, 1000000, 1000003, 16777215, 16777217,
                         1000000000, 0x3FFFFFFF, 0x40000001, 0x7FFFFFFD, 0x7FFFFFFF,
                     })
                yield return d;
        }

        static IEnumerable<uint> Numbers(uint d)
        {
            // the ends, every power of two and its neighbours, and around multiples of d
            foreach (uint x in new uint[] { 0, 1, 2, 0x7FFFFFFE, 0x7FFFFFFF, 0x80000000, 0x80000001, 0xFFFFFFFE, 0xFFFFFFFF })
                yield return x;
            for (int bit = 1; bit < 32; bit++)
            {
                yield return (1u << bit) - 1;
                yield return 1u << bit;
                yield return (1u << bit) + 1;
            }
            foreach (uint times in new uint[] { 1, 2, 3, 7, 1000, 0xFFFFFFFF / d / 2, 0xFFFFFFFF / d - 1, 0xFFFFFFFF / d })
            {
                uint multiple = unchecked(times * d);
                yield return unchecked(multiple - 1);
                yield return multiple;
                yield return unchecked(multiple + 1);
            }
            var random = new Random((int)d);
            for (int i = 0; i < 200; i++) yield return (uint)random.NextInt64(0, 0x100000000);
        }

        [Fact]
        public void UnsignedDivisionByAConstantIsTheSameAsDividing()
        {
            foreach (uint d in Divisors().Concat(new uint[] { 0x80000001, 0xC0000000 + 1, 0xFFFFFFFD, 0xFFFFFFFF }))
                foreach (uint x in Numbers(d))
                    Assert.True(DivisionMagic.UnsignedDivide(x, d) == x / d, $"{x} / {d}");
        }

        [Fact]
        public void SignedDivisionByAConstantIsTheSameAsDividing()
        {
            foreach (uint d in Divisors())
                foreach (uint bits in Numbers(d))
                {
                    int x = unchecked((int)bits);
                    Assert.True(DivisionMagic.SignedDivide(x, (int)d) == x / (int)d, $"{x} / {d}");
                    if (x != int.MinValue)
                        Assert.True(DivisionMagic.SignedDivide(-x, (int)d) == -x / (int)d, $"{-x} / {d}");
                }
        }

        [Fact]
        public void EverySixteenBitNumberDividesRightByTheCommonDivisors()
        {
            foreach (uint d in new uint[] { 3, 5, 6, 7, 9, 10, 12, 60, 100, 255, 641, 1000 })
                for (uint x = 0; x <= 0x1FFFF; x++)
                {
                    Assert.True(DivisionMagic.UnsignedDivide(x, d) == x / d, $"{x} / {d}");
                    Assert.True(DivisionMagic.UnsignedDivide(0xFFFFFFFF - x, d) == (0xFFFFFFFF - x) / d, $"{0xFFFFFFFF - x} / {d}");
                    int signed = (int)x - 0x10000;
                    Assert.True(DivisionMagic.SignedDivide(signed, (int)d) == signed / (int)d, $"{signed} / {d}");
                }
        }

        [Fact]
        public void TheKnownMultipliers()
        {
            // the ones every compiler gives: unsigned 10 and 7, signed 10 and 7
            Assert.Equal(new DivisionMagic.Unsigned(0xCCCCCCCD, 3, false), DivisionMagic.ForUnsigned(10));
            Assert.Equal(new DivisionMagic.Unsigned(0x24924925, 3, true), DivisionMagic.ForUnsigned(7));
            Assert.Equal(new DivisionMagic.Signed(0x66666667, 2, false), DivisionMagic.ForSigned(10));
            Assert.Equal(new DivisionMagic.Signed(unchecked((int)0x92492493), 2, true), DivisionMagic.ForSigned(7));
        }
    }
}
