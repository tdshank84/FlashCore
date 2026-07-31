using FlashCore.ECU.Simos18.Exploits;
using Xunit;

namespace FlashCore.Tests;

public class Simos18SeedKeyAlgorithmTests
{
    [Fact]
    public void CalculateSa2Key_MatchesPublishedReferenceVector()
    {
        var script = new byte[]
        {
            0x68, 0x02, 0x81, 0x49, 0x93, 0xA5, 0x5A, 0x55, 0xAA, 0x4A, 0x05,
            0x87, 0x81, 0x05, 0x95, 0x26, 0x68, 0x05, 0x82, 0x49, 0x84, 0x5A,
            0xA5, 0xAA, 0x55, 0x87, 0x03, 0xF7, 0x80, 0x6A, 0x4C
        };
        var algorithm = new Simos18SeedKeyAlgorithm();

        var key = algorithm.CalculateSa2Key([0x1A, 0x1B, 0x1C, 0x1D], script);

        Assert.Equal(new byte[] { 0x6A, 0x37, 0xF0, 0x2E }, key);
    }

    [Fact]
    public void CalculateSa2Key_RejectsUnknownOpcode()
    {
        var algorithm = new Simos18SeedKeyAlgorithm();
        Assert.Throws<InvalidDataException>(() =>
            algorithm.CalculateSa2Key([0, 0, 0, 1], [0xFF]));
    }
}
