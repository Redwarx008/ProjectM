using System;
using System.Collections.Generic;
using System.Linq;
using System.Numerics;
using System.Runtime.InteropServices;
using System.Text;
using System.Threading.Tasks;

namespace Core.Data;
/// <summary>
/// 64-bit fixed-point number for deterministic calculations across all platforms
/// Format: 32.32 (32 integer bits, 32 fractional bits)
/// Range: -2,147,483,648 to 2,147,483,647 with ~0.0000000002 precision
///
/// CRITICAL: This type is used for ALL simulation math to ensure multiplayer determinism.
/// Float operations produce different results on different CPUs/compilers.
/// Fixed-point math guarantees identical results across all platforms.
/// </summary>
[StructLayout(LayoutKind.Sequential)]
public struct FixedPoint64 : IEquatable<FixedPoint64>, IComparable<FixedPoint64>
{
    public readonly long RawValue;

    private const int FRACTIONAL_BITS = 32;
    private const long ONE_RAW = 1L << FRACTIONAL_BITS; // 4294967296
    private const long HALF_RAW = ONE_RAW / 2;

    // Constants
    public static readonly FixedPoint64 Zero = new FixedPoint64(0);
    public static readonly FixedPoint64 One = new FixedPoint64(ONE_RAW);
    public static readonly FixedPoint64 Half = new FixedPoint64(HALF_RAW);
    public static readonly FixedPoint64 Two = new FixedPoint64(ONE_RAW * 2);
    public static readonly FixedPoint64 MinValue = new FixedPoint64(long.MinValue);
    public static readonly FixedPoint64 MaxValue = new FixedPoint64(long.MaxValue);

    // Construction
    private FixedPoint64(long rawValue)
    {
        RawValue = rawValue;
    }

    /// <summary>
    /// Create from raw fixed-point value (for serialization)
    /// </summary>
    public static FixedPoint64 FromRaw(long raw) => new FixedPoint64(raw);

    /// <summary>
    /// Create from integer value
    /// </summary>
    public static FixedPoint64 FromInt(int value) => new FixedPoint64((long)value << FRACTIONAL_BITS);

    /// <summary>
    /// Create from long integer value
    /// </summary>
    public static FixedPoint64 FromLong(long value) => new FixedPoint64(value << FRACTIONAL_BITS);

    /// <summary>
    /// Create from fraction (numerator / denominator)
    /// </summary>
    public static FixedPoint64 FromFraction(long numerator, long denominator)
    {
        if (denominator == 0)
            throw new DivideByZeroException("Denominator cannot be zero");

        // Scale numerator and divide
        return new FixedPoint64((numerator << FRACTIONAL_BITS) / denominator);
    }

    /// <summary>
    /// Create from float (ONLY use during initialization, NEVER in simulation)
    /// </summary>
    public static FixedPoint64 FromFloat(float value)
    {
        return new FixedPoint64((long)(value * ONE_RAW));
    }

    /// <summary>
    /// Create from double (ONLY use during initialization, NEVER in simulation)
    /// </summary>
    public static FixedPoint64 FromDouble(double value)
    {
        return new FixedPoint64((long)(value * ONE_RAW));
    }

    // Arithmetic operators
    public static FixedPoint64 operator +(FixedPoint64 a, FixedPoint64 b) =>
        new FixedPoint64(a.RawValue + b.RawValue);

    public static FixedPoint64 operator -(FixedPoint64 a, FixedPoint64 b) =>
        new FixedPoint64(a.RawValue - b.RawValue);

    public static FixedPoint64 operator -(FixedPoint64 a) =>
        new FixedPoint64(-a.RawValue);

    public static FixedPoint64 operator *(FixedPoint64 a, FixedPoint64 b)
    {
        // 1. 将两个 64 位 RawValue 提升为 BigInteger
        BigInteger bigA = a.RawValue;
        BigInteger bigB = b.RawValue;

        // 2. 执行 128 位乘法 (BigInteger 可以处理任意大的数字)
        BigInteger bigResult = bigA * bigB;

        // 3. 右移 FRACTIONAL_BITS (32位) 来重新定位小数点
        //    BigInteger 的右移操作是安全的
        bigResult >>= FRACTIONAL_BITS;

        // 4. 检查结果是否超出 64 位 (long) 的范围
        //    对于 Q32.32 来说，结果应该仍在 long 的范围内
        //    (如果溢出，表示乘积超出了 FixedPoint64 的最大表示范围)
        if (bigResult > long.MaxValue || bigResult < long.MinValue)
        {
            // 抛出溢出异常或根据需求处理（例如 Clamp 到 Min/MaxValue）
            throw new OverflowException("FixedPoint64 multiplication resulted in overflow/underflow.");
        }

        // 5. 将结果安全地转换回 long
        return new FixedPoint64((long)bigResult);
    }

    public static FixedPoint64 operator /(FixedPoint64 a, FixedPoint64 b)
    {
        if (b.RawValue == 0)
        {
            throw new DivideByZeroException("Cannot divide by zero");
        }

        // 1. 将 RawValue 提升为 BigInteger
        BigInteger bigA = a.RawValue;
        BigInteger bigB = b.RawValue;

        // 2. 将分子左移 FRACTIONAL_BITS (相当于乘以 ONE_RAW)
        //    确保 BigInteger 安全地处理 96 位 (64 + 32) 级别的中间结果
        bigA <<= FRACTIONAL_BITS;

        // 3. 执行除法
        BigInteger bigResult = bigA / bigB;

        // 4. 检查结果是否超出 64 位 (long) 的范围
        if (bigResult > long.MaxValue || bigResult < long.MinValue)
        {
            throw new OverflowException("FixedPoint64 division resulted in overflow/underflow.");
        }

        // 5. 将结果安全地转换回 long
        return new FixedPoint64((long)bigResult);
    }

    public static FixedPoint64 operator %(FixedPoint64 a, FixedPoint64 b)
    {
        return new FixedPoint64(a.RawValue % b.RawValue);
    }

    // Integer multiplication/division (more efficient)
    public static FixedPoint64 operator *(FixedPoint64 a, int b) =>
        new FixedPoint64(a.RawValue * b);

    public static FixedPoint64 operator /(FixedPoint64 a, int b) =>
        new FixedPoint64(a.RawValue / b);

    // Comparison operators
    public static bool operator ==(FixedPoint64 a, FixedPoint64 b) => a.RawValue == b.RawValue;
    public static bool operator !=(FixedPoint64 a, FixedPoint64 b) => a.RawValue != b.RawValue;
    public static bool operator <(FixedPoint64 a, FixedPoint64 b) => a.RawValue < b.RawValue;
    public static bool operator >(FixedPoint64 a, FixedPoint64 b) => a.RawValue > b.RawValue;
    public static bool operator <=(FixedPoint64 a, FixedPoint64 b) => a.RawValue <= b.RawValue;
    public static bool operator >=(FixedPoint64 a, FixedPoint64 b) => a.RawValue >= b.RawValue;

    // Conversion to primitives
    public int ToInt() => (int)(RawValue >> FRACTIONAL_BITS);
    public long ToLong() => RawValue >> FRACTIONAL_BITS;

    /// <summary>
    /// Convert to float (ONLY for presentation layer, NEVER use result in simulation)
    /// </summary>
    public float ToFloat() => (float)RawValue / ONE_RAW;

    /// <summary>
    /// Convert to double (ONLY for presentation layer, NEVER use result in simulation)
    /// </summary>
    public double ToDouble() => (double)RawValue / ONE_RAW;

    // Math functions
    public static FixedPoint64 Abs(FixedPoint64 value) =>
        value.RawValue >= 0 ? value : new FixedPoint64(-value.RawValue);

    public static FixedPoint64 Min(FixedPoint64 a, FixedPoint64 b) =>
        a.RawValue < b.RawValue ? a : b;

    public static FixedPoint64 Max(FixedPoint64 a, FixedPoint64 b) =>
        a.RawValue > b.RawValue ? a : b;

    public static FixedPoint64 Clamp(FixedPoint64 value, FixedPoint64 min, FixedPoint64 max)
    {
        if (value.RawValue < min.RawValue) return min;
        if (value.RawValue > max.RawValue) return max;
        return value;
    }

    /// <summary>
    /// Floor to nearest integer
    /// </summary>
    public static FixedPoint64 Floor(FixedPoint64 value)
    {
        return new FixedPoint64((value.RawValue >> FRACTIONAL_BITS) << FRACTIONAL_BITS);
    }

    /// <summary>
    /// Ceiling to nearest integer
    /// </summary>
    public static FixedPoint64 Ceiling(FixedPoint64 value)
    {
        long fractional = value.RawValue & (ONE_RAW - 1);
        if (fractional == 0)
            return value;

        return Floor(value) + One;
    }

    /// <summary>
    /// Round to nearest integer
    /// </summary>
    public static FixedPoint64 Round(FixedPoint64 value)
    {
        return Floor(value + Half);
    }

    // IEquatable implementation
    public bool Equals(FixedPoint64 other) => RawValue == other.RawValue;

    public override bool Equals(object obj) =>
        obj is FixedPoint64 other && Equals(other);

    public override int GetHashCode() => RawValue.GetHashCode();

    // IComparable implementation
    public int CompareTo(FixedPoint64 other) => RawValue.CompareTo(other.RawValue);

    // String conversion
    public override string ToString() => ToDouble().ToString("F6");

    public string ToString(string format) => ToDouble().ToString(format);

    /// <summary>
    /// Serialize to bytes for networking (8 bytes exactly)
    /// </summary>
    public byte[] ToBytes()
    {
        byte[] bytes = new byte[8];
        bytes[0] = (byte)(RawValue >> 56);
        bytes[1] = (byte)(RawValue >> 48);
        bytes[2] = (byte)(RawValue >> 40);
        bytes[3] = (byte)(RawValue >> 32);
        bytes[4] = (byte)(RawValue >> 24);
        bytes[5] = (byte)(RawValue >> 16);
        bytes[6] = (byte)(RawValue >> 8);
        bytes[7] = (byte)RawValue;
        return bytes;
    }

    /// <summary>
    /// Deserialize from bytes for networking
    /// </summary>
    public static FixedPoint64 FromBytes(byte[] bytes, int offset = 0)
    {
        if (bytes == null)
            throw new ArgumentNullException(nameof(bytes));
        if (bytes.Length < offset + 8)
            throw new ArgumentException("Not enough bytes to deserialize FixedPoint64");

        long value = ((long)bytes[offset] << 56) |
                    ((long)bytes[offset + 1] << 48) |
                    ((long)bytes[offset + 2] << 40) |
                    ((long)bytes[offset + 3] << 32) |
                    ((long)bytes[offset + 4] << 24) |
                    ((long)bytes[offset + 5] << 16) |
                    ((long)bytes[offset + 6] << 8) |
                    bytes[offset + 7];

        return new FixedPoint64(value);
    }
}