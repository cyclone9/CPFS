using System;
using System.Collections.Generic;
using System.Text;

namespace Cpfs.Core.Qr;

// The text does not fit a version 10 symbol (213 bytes in byte mode at level M).
public sealed class QrCapacityException : ArgumentException
{
    public QrCapacityException(int bytes)
        : base("Text too long for a QR code: " + bytes.ToString(System.Globalization.CultureInfo.InvariantCulture) + " bytes")
    {
        Bytes = bytes;
    }

    public int Bytes { get; }
}

// QR Code model 2 (ISO/IEC 18004): byte mode, UTF-8 without ECI, error correction level M, versions 1 to 10.
// Ported from the 0.2.3 connect guide's encoder; the mask is chosen with all four penalty rules of the standard.
public sealed class QrCode
{
    public const int MaxVersion = 10;

    // Per version at level M: EC codewords per block, then (blocks, data codewords) for group 1 and group 2.
    private static readonly int[][] BlocksM =
    {
        new[] { 10, 1, 16, 0, 0 },
        new[] { 16, 1, 28, 0, 0 },
        new[] { 26, 1, 44, 0, 0 },
        new[] { 18, 2, 32, 0, 0 },
        new[] { 24, 2, 43, 0, 0 },
        new[] { 16, 4, 27, 0, 0 },
        new[] { 18, 4, 31, 0, 0 },
        new[] { 22, 2, 38, 2, 39 },
        new[] { 22, 3, 36, 2, 37 },
        new[] { 26, 4, 43, 1, 44 },
    };

    private static readonly int[][] AlignmentCenters =
    {
        Array.Empty<int>(),
        new[] { 6, 18 },
        new[] { 6, 22 },
        new[] { 6, 26 },
        new[] { 6, 30 },
        new[] { 6, 34 },
        new[] { 6, 22, 38 },
        new[] { 6, 24, 42 },
        new[] { 6, 26, 46 },
        new[] { 6, 28, 50 },
    };

    private static readonly int[] Exp = new int[512];
    private static readonly int[] Log = new int[256];

    private readonly bool[,] _modules;
    private readonly bool[,] _isFunction;

    static QrCode()
    {
        var x = 1;
        for (var i = 0; i < 255; i++)
        {
            Exp[i] = x;
            Log[x] = i;
            x <<= 1;
            if (x >= 256)
            {
                x ^= 0x11D;
            }
        }

        for (var i = 255; i < 512; i++)
        {
            Exp[i] = Exp[i - 255];
        }
    }

    private QrCode(int version)
    {
        Version = version;
        Size = 17 + (4 * version);
        _modules = new bool[Size, Size];
        _isFunction = new bool[Size, Size];
    }

    public int Version { get; }

    // Modules per side, without the quiet zone (four light modules on every side, which the renderer adds).
    public int Size { get; }

    public int Mask { get; private set; }

    // True for a dark module; x is the column, y the row.
    public bool this[int x, int y] => _modules[y, x];

    public static QrCode Encode(string text) => Encode(text, -1);

    // mask -1 picks the mask with the lowest penalty; 0 to 7 forces one (for tests against reference encoders).
    public static QrCode Encode(string text, int mask)
    {
        if (text is null)
        {
            throw new ArgumentNullException(nameof(text));
        }

        if (mask < -1 || mask > 7)
        {
            throw new ArgumentOutOfRangeException(nameof(mask));
        }

        var bytes = Encoding.UTF8.GetBytes(text);
        var version = 0;
        for (var v = 1; v <= MaxVersion; v++)
        {
            if (4 + CountBits(v) + (8 * bytes.Length) <= DataCodewords(v) * 8)
            {
                version = v;
                break;
            }
        }

        if (version == 0)
        {
            throw new QrCapacityException(bytes.Length);
        }

        var qr = new QrCode(version);
        qr.DrawFunctionPatterns();
        qr.DrawCodewords(AddErrorCorrection(version, DataCodewordsFor(version, bytes)));
        qr.ChooseMask(mask);
        return qr;
    }

    private static int CountBits(int version) => version <= 9 ? 8 : 16;

    private static int DataCodewords(int version)
    {
        var t = BlocksM[version - 1];
        return (t[1] * t[2]) + (t[3] * t[4]);
    }

    internal static byte[] DataCodewordsFor(int version, byte[] bytes)
    {
        var capacityBits = DataCodewords(version) * 8;
        var bits = new List<bool>(capacityBits);
        void Add(int value, int count)
        {
            for (var i = count - 1; i >= 0; i--)
            {
                bits.Add(((value >> i) & 1) != 0);
            }
        }

        Add(0b0100, 4);
        Add(bytes.Length, CountBits(version));
        foreach (var b in bytes)
        {
            Add(b, 8);
        }

        Add(0, Math.Min(4, capacityBits - bits.Count));
        while (bits.Count % 8 != 0)
        {
            bits.Add(false);
        }

        var data = new List<byte>(capacityBits / 8);
        for (var i = 0; i < bits.Count; i += 8)
        {
            var v = 0;
            for (var j = 0; j < 8; j++)
            {
                v = (v << 1) | (bits[i + j] ? 1 : 0);
            }

            data.Add((byte)v);
        }

        for (var pad = 0xEC; data.Count < capacityBits / 8; pad ^= 0xEC ^ 0x11)
        {
            data.Add((byte)pad);
        }

        return data.ToArray();
    }

    // Splits the data into blocks, appends each block's Reed-Solomon codewords and interleaves them.
    private static byte[] AddErrorCorrection(int version, byte[] data)
    {
        var t = BlocksM[version - 1];
        var divisor = ReedSolomonDivisor(t[0]);
        var blocks = new List<(byte[] Data, byte[] Ec)>();
        var pos = 0;
        foreach (var (count, length) in new[] { (t[1], t[2]), (t[3], t[4]) })
        {
            for (var k = 0; k < count; k++)
            {
                var block = new byte[length];
                Array.Copy(data, pos, block, 0, length);
                pos += length;
                blocks.Add((block, ReedSolomonRemainder(block, divisor)));
            }
        }

        var result = new List<byte>();
        var longest = Math.Max(t[2], t[4]);
        for (var i = 0; i < longest; i++)
        {
            foreach (var block in blocks)
            {
                if (i < block.Data.Length)
                {
                    result.Add(block.Data[i]);
                }
            }
        }

        for (var i = 0; i < t[0]; i++)
        {
            foreach (var block in blocks)
            {
                result.Add(block.Ec[i]);
            }
        }

        return result.ToArray();
    }

    private static int Multiply(int a, int b) => a == 0 || b == 0 ? 0 : Exp[Log[a] + Log[b]];

    // Generator polynomial coefficients, highest degree first, the leading 1 left out.
    internal static byte[] ReedSolomonDivisor(int degree)
    {
        var result = new byte[degree];
        result[degree - 1] = 1;
        var root = 1;
        for (var i = 0; i < degree; i++)
        {
            for (var j = 0; j < degree; j++)
            {
                result[j] = (byte)Multiply(result[j], root);
                if (j + 1 < degree)
                {
                    result[j] ^= result[j + 1];
                }
            }

            root = Multiply(root, 2);
        }

        return result;
    }

    internal static byte[] ReedSolomonRemainder(byte[] data, byte[] divisor)
    {
        var result = new byte[divisor.Length];
        foreach (var b in data)
        {
            var factor = b ^ result[0];
            Array.Copy(result, 1, result, 0, result.Length - 1);
            result[result.Length - 1] = 0;
            for (var i = 0; i < result.Length; i++)
            {
                result[i] ^= (byte)Multiply(divisor[i], factor);
            }
        }

        return result;
    }

    private void SetFunction(int x, int y, bool dark)
    {
        _modules[y, x] = dark;
        _isFunction[y, x] = true;
    }

    private void DrawFunctionPatterns()
    {
        for (var i = 0; i < Size; i++)
        {
            SetFunction(6, i, i % 2 == 0);
            SetFunction(i, 6, i % 2 == 0);
        }

        DrawFinder(3, 3);
        DrawFinder(Size - 4, 3);
        DrawFinder(3, Size - 4);

        var centers = AlignmentCenters[Version - 1];
        var n = centers.Length;
        for (var i = 0; i < n; i++)
        {
            for (var j = 0; j < n; j++)
            {
                // The three corners already hold finder patterns.
                if ((i == 0 && j == 0) || (i == 0 && j == n - 1) || (i == n - 1 && j == 0))
                {
                    continue;
                }

                for (var dy = -2; dy <= 2; dy++)
                {
                    for (var dx = -2; dx <= 2; dx++)
                    {
                        SetFunction(centers[i] + dx, centers[j] + dy, Math.Max(Math.Abs(dx), Math.Abs(dy)) != 1);
                    }
                }
            }
        }

        DrawFormat(0);
        DrawVersion();
    }

    private void DrawFinder(int cx, int cy)
    {
        for (var dy = -4; dy <= 4; dy++)
        {
            for (var dx = -4; dx <= 4; dx++)
            {
                var x = cx + dx;
                var y = cy + dy;
                if (x >= 0 && x < Size && y >= 0 && y < Size)
                {
                    var distance = Math.Max(Math.Abs(dx), Math.Abs(dy));
                    SetFunction(x, y, distance != 2 && distance != 4);
                }
            }
        }
    }

    // Level M is 00, so the 5 data bits are the mask number; BCH(15,5) and the fixed XOR mask follow.
    internal static int FormatBits(int mask)
    {
        var rem = mask;
        for (var i = 0; i < 10; i++)
        {
            rem = (rem << 1) ^ ((rem >> 9) * 0x537);
        }

        return ((mask << 10) | rem) ^ 0x5412;
    }

    private void DrawFormat(int mask)
    {
        var bits = FormatBits(mask);
        bool Bit(int i) => ((bits >> i) & 1) != 0;

        for (var i = 0; i <= 5; i++)
        {
            SetFunction(8, i, Bit(i));
        }

        SetFunction(8, 7, Bit(6));
        SetFunction(8, 8, Bit(7));
        SetFunction(7, 8, Bit(8));
        for (var i = 9; i < 15; i++)
        {
            SetFunction(14 - i, 8, Bit(i));
        }

        for (var i = 0; i < 8; i++)
        {
            SetFunction(Size - 1 - i, 8, Bit(i));
        }

        for (var i = 8; i < 15; i++)
        {
            SetFunction(8, Size - 15 + i, Bit(i));
        }

        SetFunction(8, Size - 8, true);
    }

    private void DrawVersion()
    {
        if (Version < 7)
        {
            return;
        }

        var rem = Version;
        for (var i = 0; i < 12; i++)
        {
            rem = (rem << 1) ^ ((rem >> 11) * 0x1F25);
        }

        var bits = (Version << 12) | rem;
        for (var i = 0; i < 18; i++)
        {
            var bit = ((bits >> i) & 1) != 0;
            var a = Size - 11 + (i % 3);
            var b = i / 3;
            SetFunction(a, b, bit);
            SetFunction(b, a, bit);
        }
    }

    // Upward and downward zigzag through two-module columns, right to left, skipping the vertical timing column.
    // Modules left over (the remainder bits of versions 2 to 6) stay light before masking.
    private void DrawCodewords(byte[] codewords)
    {
        var total = codewords.Length * 8;
        var i = 0;
        for (var right = Size - 1; right >= 1; right -= 2)
        {
            if (right == 6)
            {
                right = 5;
            }

            var upward = ((right + 1) & 2) == 0;
            for (var vert = 0; vert < Size; vert++)
            {
                for (var j = 0; j < 2; j++)
                {
                    var x = right - j;
                    var y = upward ? Size - 1 - vert : vert;
                    if (!_isFunction[y, x] && i < total)
                    {
                        _modules[y, x] = ((codewords[i >> 3] >> (7 - (i & 7))) & 1) != 0;
                        i++;
                    }
                }
            }
        }
    }

    private static bool MaskHit(int mask, int x, int y) => mask switch
    {
        0 => (x + y) % 2 == 0,
        1 => y % 2 == 0,
        2 => x % 3 == 0,
        3 => (x + y) % 3 == 0,
        4 => ((x / 3) + (y / 2)) % 2 == 0,
        5 => ((x * y) % 2) + ((x * y) % 3) == 0,
        6 => (((x * y) % 2) + ((x * y) % 3)) % 2 == 0,
        _ => (((x + y) % 2) + ((x * y) % 3)) % 2 == 0,
    };

    private void ApplyMask(int mask)
    {
        for (var y = 0; y < Size; y++)
        {
            for (var x = 0; x < Size; x++)
            {
                if (!_isFunction[y, x] && MaskHit(mask, x, y))
                {
                    _modules[y, x] = !_modules[y, x];
                }
            }
        }
    }

    private void ChooseMask(int forced)
    {
        var best = forced;
        if (best < 0)
        {
            var lowest = int.MaxValue;
            for (var k = 0; k < 8; k++)
            {
                ApplyMask(k);
                DrawFormat(k);
                var penalty = Penalty();
                if (penalty < lowest)
                {
                    lowest = penalty;
                    best = k;
                }

                ApplyMask(k);
            }
        }

        ApplyMask(best);
        DrawFormat(best);
        Mask = best;
    }

    internal int Penalty()
    {
        var penalty = 0;
        var line = new bool[Size];
        for (var pass = 0; pass < 2; pass++)
        {
            for (var a = 0; a < Size; a++)
            {
                for (var b = 0; b < Size; b++)
                {
                    line[b] = pass == 0 ? _modules[a, b] : _modules[b, a];
                }

                penalty += LinePenalty(line);
            }
        }

        var dark = 0;
        for (var y = 0; y < Size; y++)
        {
            for (var x = 0; x < Size; x++)
            {
                if (_modules[y, x])
                {
                    dark++;
                }

                if (x < Size - 1 && y < Size - 1)
                {
                    var c = _modules[y, x];
                    if (c == _modules[y, x + 1] && c == _modules[y + 1, x] && c == _modules[y + 1, x + 1])
                    {
                        penalty += 3;
                    }
                }
            }
        }

        var all = Size * Size;
        var k = ((Math.Abs((dark * 20) - (all * 10)) + all - 1) / all) - 1;
        return penalty + (k * 10);
    }

    // Rule 1 (runs of five or more) and rule 3 (1:1:3:1:1 finder-like pattern with four light modules on one side;
    // the quiet zone counts as light).
    private static int LinePenalty(bool[] line)
    {
        var penalty = 0;
        var run = 1;
        for (var i = 1; i <= line.Length; i++)
        {
            if (i < line.Length && line[i] == line[i - 1])
            {
                run++;
                continue;
            }

            if (run >= 5)
            {
                penalty += 3 + (run - 5);
            }

            run = 1;
        }

        bool At(int i) => i >= 0 && i < line.Length && line[i];
        for (var i = -4; i < line.Length; i++)
        {
            // Dark modules of the core 1:1:3:1:1 starting at i.
            if (At(i) && !At(i + 1) && At(i + 2) && At(i + 3) && At(i + 4) && !At(i + 5) && At(i + 6))
            {
                var lightBefore = !At(i - 1) && !At(i - 2) && !At(i - 3) && !At(i - 4);
                var lightAfter = !At(i + 7) && !At(i + 8) && !At(i + 9) && !At(i + 10);
                if (lightBefore || lightAfter)
                {
                    penalty += 40;
                }
            }
        }

        return penalty;
    }
}
