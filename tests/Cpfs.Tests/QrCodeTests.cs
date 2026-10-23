using System;
using System.Security.Cryptography;
using System.Text;
using Cpfs.Core.Qr;
using Xunit;

namespace Cpfs.Tests;

// Reference matrices come from segno 1.6.6, an independent encoder (byte mode, level M, no ECI, the same mask forced),
// stored as the SHA-256 of the modules row by row, "1" for dark. segno adds a zero codeword when the bit stream already
// ends on a codeword boundary; the vectors were made with that step set to ISO/IEC 18004 7.4.10 (padding bits only up
// to the boundary), as this encoder and the 0.2.3 script do. Every vector was also decoded by zxing-cpp.
public class QrCodeTests
{
    public static readonly object[][] SegnoVectors =
    {
        new object[] { "HOST", 0, 1, "a820a13379e37e8b624ce20db11d1015db888c6dadcda9b79be532de1fed271b" },
        new object[] { "smb://CPFS@HOST-01.local/Shared", 1, 3, "55f4784084d7d63536a45b921a1de80f3e9bd0eba9c6f8f7acb6467f398a4cfd" },
        new object[] { "smb://phoneuser@HOST-02.local/Family-Files-2026", 0, 4, "b360da870dca01895931d552aa2ce98a63202695dde23e6158163cc5025247a7" },
        new object[] { "smb://phoneuser@HOST-02.local/Family-Files-2026", 1, 4, "3528d29f5ed665fa44505ac70185deb4de2f7ee161735fcc1ef4dac84933fd63" },
        new object[] { "smb://phoneuser@HOST-02.local/Family-Files-2026", 2, 4, "a10198183bdef13ada40a00a82f8a26205c8ac0e3345c614edef63008b999f66" },
        new object[] { "smb://phoneuser@HOST-02.local/Family-Files-2026", 3, 4, "8f5ee1cd4f3f4157291bfd5cccb255464acd97d6d88abadda85b4ec51499b12f" },
        new object[] { "smb://phoneuser@HOST-02.local/Family-Files-2026", 4, 4, "aaff04b5572a93623dde13ce6777fa63d3524c3fee070ecb3162f0e80b0abc2f" },
        new object[] { "smb://phoneuser@HOST-02.local/Family-Files-2026", 5, 4, "22bc3ba5338db7c24ad010185e4a6704d5d4166eb783ac9cb917c759205c576e" },
        new object[] { "smb://phoneuser@HOST-02.local/Family-Files-2026", 6, 4, "6a08b23b06b2de34b6b9166b452bd7192ddf6bee29bd499d9d9cc852a84bab2c" },
        new object[] { "smb://phoneuser@HOST-02.local/Family-Files-2026", 7, 4, "391238574173d15de7474a6a79b921119104783c03e43b95b874945f1b766a0c" },
        new object[] { "100.101.102.103", 3, 2, "14964e2645c46d613aa0e2e01a215ffd188ad6536f63266496561e1e684b7599" },
        new object[] { new string('x', 60), 4, 4, "94341b002eb11e6c8a11ea823d38492e46ac812b087f92fc1b95f81d9ed2f4f8" },
        new object[] { new string('y', 100), 5, 6, "c5971f977751582abaf9399b7fb5e3851ac02cbdfa6a08e6523bc6f81173f2e1" },
        new object[] { new string('z', 140), 6, 8, "c2a18ebbb0b90db206b08231b4c23f596d7fe413dc6ec41a98030ad559edb7bb" },
        new object[] { new string('w', 180), 7, 9, "8fdbfcf5f623e927c54980189fddcbaf9cdcb1d4ccc1182a22485f01d8222065" },
        new object[] { new string('v', 213), 0, 10, "4160a75021821bc7e5c26d05ce7fdb288610941cbecb9566dc3d8f1df49a0fa9" },
    };

    private static readonly string[] HostMask2 =
    {
        "111111100101101111111",
        "100000100000001000001",
        "101110101001001011101",
        "101110101111101011101",
        "101110101101101011101",
        "100000101000101000001",
        "111111101010101111111",
        "000000001001100000000",
        "101111100010101111100",
        "111110001110100101000",
        "001000110101010011010",
        "101001000110000111100",
        "111011111001010011101",
        "000000001001111000100",
        "111111100110101101010",
        "100000101011111001100",
        "101110101010100101110",
        "101110101110100101100",
        "101110101101010010100",
        "100000100100000110100",
        "111111101001010010110",
    };

    private static string Bits(QrCode qr)
    {
        var sb = new StringBuilder(qr.Size * qr.Size);
        for (var y = 0; y < qr.Size; y++)
        {
            for (var x = 0; x < qr.Size; x++)
            {
                sb.Append(qr[x, y] ? '1' : '0');
            }
        }

        return sb.ToString();
    }

    private static string Sha256(string text) => Convert.ToHexStringLower(SHA256.HashData(Encoding.ASCII.GetBytes(text)));

    [Theory]
    [MemberData(nameof(SegnoVectors))]
    public void MatchesTheReferenceEncoder(string text, int mask, int version, string sha256)
    {
        var qr = QrCode.Encode(text, mask);
        Assert.Equal(version, qr.Version);
        Assert.Equal(17 + (4 * version), qr.Size);
        Assert.Equal(sha256, Sha256(Bits(qr)));
    }

    [Fact]
    public void MatchesTheReferenceModuleForModule()
    {
        var qr = QrCode.Encode("HOST", 2);
        Assert.Equal(string.Concat(HostMask2), Bits(qr));
    }

    // The worked example of the Reed-Solomon step for "HELLO WORLD" at 1-M (thonky.com QR tutorial).
    [Fact]
    public void ReedSolomonMatchesThePublishedExample()
    {
        var data = new byte[] { 32, 91, 11, 120, 209, 114, 220, 77, 67, 64, 236, 17, 236, 17, 236, 17 };
        var ec = QrCode.ReedSolomonRemainder(data, QrCode.ReedSolomonDivisor(10));
        Assert.Equal(new byte[] { 196, 35, 39, 119, 235, 215, 231, 226, 93, 23 }, ec);
    }

    [Fact]
    public void FormatBitsMatchTheStandardTable()
    {
        Assert.Equal(0b101010000010010, QrCode.FormatBits(0));
        Assert.Equal(0b101000100100101, QrCode.FormatBits(1));
    }

    [Fact]
    public void PicksTheSmallestVersionAndRefusesWhatDoesNotFit()
    {
        Assert.Equal(1, QrCode.Encode(new string('a', 14)).Version);
        Assert.Equal(2, QrCode.Encode(new string('a', 15)).Version);
        Assert.Equal(10, QrCode.Encode(new string('a', 213)).Version);
        var ex = Assert.Throws<QrCapacityException>(() => QrCode.Encode(new string('a', 214)));
        Assert.Equal(214, ex.Bytes);
    }

    [Fact]
    public void AutomaticMaskIsOneOfTheEightAndStable()
    {
        var a = QrCode.Encode("smb://CPFS@HOST-01.local/Shared");
        var b = QrCode.Encode("smb://CPFS@HOST-01.local/Shared");
        Assert.InRange(a.Mask, 0, 7);
        Assert.Equal(Bits(a), Bits(b));
        Assert.Equal(Bits(QrCode.Encode("smb://CPFS@HOST-01.local/Shared", a.Mask)), Bits(a));
    }
}
