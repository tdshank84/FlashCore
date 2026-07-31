using System.Security.Cryptography;

namespace FlashCore.Core.Checksums;

public enum ChecksumAlgorithm { Sha256, Crc32, Md5 }

public interface IChecksumService
{
    string Calculate(ReadOnlySpan<byte> data, ChecksumAlgorithm algorithm = ChecksumAlgorithm.Sha256);
    bool Verify(ReadOnlySpan<byte> data, string expected, ChecksumAlgorithm algorithm = ChecksumAlgorithm.Sha256);
}

public sealed class ChecksumService : IChecksumService
{
    public string Calculate(ReadOnlySpan<byte> data, ChecksumAlgorithm algorithm = ChecksumAlgorithm.Sha256)
    {
        var hash = algorithm switch
        {
            ChecksumAlgorithm.Sha256 => SHA256.HashData(data),
            ChecksumAlgorithm.Crc32 => CalculateCrc32(data),
            ChecksumAlgorithm.Md5 => MD5.HashData(data),
            _ => throw new ArgumentOutOfRangeException(nameof(algorithm))
        };
        return Convert.ToHexString(hash);
    }

    private static byte[] CalculateCrc32(ReadOnlySpan<byte> data)
    {
        uint crc = 0xFFFFFFFF;
        foreach (var value in data)
        {
            crc ^= value;
            for (var bit = 0; bit < 8; bit++)
                crc = (crc >> 1) ^ ((crc & 1) == 1 ? 0xEDB88320u : 0u);
        }
        crc = ~crc;
        return [(byte)(crc >> 24), (byte)(crc >> 16), (byte)(crc >> 8), (byte)crc];
    }

    public bool Verify(ReadOnlySpan<byte> data, string expected, ChecksumAlgorithm algorithm = ChecksumAlgorithm.Sha256)
    {
        if (string.IsNullOrWhiteSpace(expected)) return false;
        try
        {
            var actual = Convert.FromHexString(Calculate(data, algorithm));
            var expectedBytes = Convert.FromHexString(expected.Replace("-", string.Empty));
            return actual.Length == expectedBytes.Length && CryptographicOperations.FixedTimeEquals(actual, expectedBytes);
        }
        catch (FormatException)
        {
            return false;
        }
    }
}
