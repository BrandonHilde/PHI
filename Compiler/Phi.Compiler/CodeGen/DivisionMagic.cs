using System.Numerics;

namespace Phi.Compiler.CodeGen
{
    /// <summary>
    /// Dividing a 32-bit number by a constant without the CPU's divide, which is many times
    /// slower than a multiply: x / d is the top half of x times a "magic" number near 2^32 / d,
    /// shifted right. Nothing here depends on the generator: it gives the numbers, and
    /// <see cref="UnsignedDivide"/> and <see cref="SignedDivide"/> do with them what the
    /// generated instructions do, so the tests can check every step against real division.
    /// </summary>
    public static class DivisionMagic
    {
        /// <summary>
        /// For an unsigned x and a divisor of 2 or more that isn't a power of two:
        /// without Add, x / d = (the top 32 bits of x * Multiplier) >> Shift;
        /// with Add (the true multiplier is 2^32 + Multiplier, which doesn't fit),
        /// t = the top 32 bits of x * Multiplier, and x / d = (((x - t) >> 1) + t) >> (Shift - 1).
        /// </summary>
        public readonly record struct Unsigned(uint Multiplier, int Shift, bool Add);

        /// <summary>
        /// For a signed x and a divisor of 2 or more that isn't a power of two:
        /// t = the top 32 bits of x * Multiplier (signed), plus x when AddDividend;
        /// x / d = (t >> Shift, keeping the sign) + 1 if that's negative.
        /// </summary>
        public readonly record struct Signed(int Multiplier, int Shift, bool AddDividend);

        public static Unsigned ForUnsigned(uint d)
        {
            if (d < 2 || (d & (d - 1)) == 0) throw new ArgumentOutOfRangeException(nameof(d));
            // the smallest shift whose rounded-up multiplier is exact for every 32-bit x:
            // m = ceil(2^(32+s) / d), and m * d - 2^(32+s) must be at most 2^s
            for (int s = 0; s <= 32; s++)
            {
                BigInteger power = BigInteger.One << (32 + s);
                BigInteger m = (power + d - 1) / d;
                if (m * d - power > (BigInteger.One << s)) continue;
                if (m < (BigInteger.One << 32)) return new Unsigned((uint)m, s, false);
                return new Unsigned((uint)(m - (BigInteger.One << 32)), s, true);
            }
            throw new InvalidOperationException($"no multiplier for {d}");
        }

        public static Signed ForSigned(int d)
        {
            if (d < 2 || (d & (d - 1)) == 0) throw new ArgumentOutOfRangeException(nameof(d));
            // the smallest shift whose multiplier m = floor(2^(32+s) / d) + 1 is exact for every
            // x from -2^31 to 2^31 - 1: m * d - 2^(32+s) must be at most 2^(s+1)
            for (int s = 0; s <= 31; s++)
            {
                BigInteger power = BigInteger.One << (32 + s);
                BigInteger m = power / d + 1;
                if (m * d - power > (BigInteger.One << (s + 1))) continue;
                // a multiplier of 2^31 or more is negative as an i32: adding x makes up for it
                bool add = m >= (BigInteger.One << 31);
                return new Signed(unchecked((int)(uint)m), s, add);
            }
            throw new InvalidOperationException($"no multiplier for {d}");
        }

        /// <summary>x / d as the generated instructions work it out (see X86Generator.TryDivideByConstant).</summary>
        public static uint UnsignedDivide(uint x, uint d)
        {
            Unsigned magic = ForUnsigned(d);
            uint high = (uint)(((ulong)x * magic.Multiplier) >> 32);          // mul edx
            if (!magic.Add) return high >> magic.Shift;                        // shr edx, s
            uint t = unchecked(x - high) >> 1;                                 // sub ecx, edx / shr ecx, 1
            return unchecked(t + high) >> (magic.Shift - 1);                   // add edx, ecx / shr edx, s - 1
        }

        /// <summary>x / d, toward zero, as the generated instructions work it out.</summary>
        public static int SignedDivide(int x, int d)
        {
            Signed magic = ForSigned(d);
            int high = (int)(((long)x * magic.Multiplier) >> 32);             // imul edx
            if (magic.AddDividend) high = unchecked(high + x);                 // add edx, ecx
            high >>= magic.Shift;                                              // sar edx, s
            return unchecked(high + (int)((uint)high >> 31));                  // shr eax, 31 / add eax, edx
        }
    }
}
