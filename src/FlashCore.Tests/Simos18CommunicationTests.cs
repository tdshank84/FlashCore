using FlashCore.ECU.Simos18;
using Xunit;

namespace FlashCore.Tests;

public class Simos18CommunicationTests
{
    [Fact]
    public void BuildPacket_UsesBridgeLegUsbIsoTpLayout()
    {
        var packet = Simos18Communication.BuildPacket(
            0x00,
            0x7E8,
            0x7E0,
            [0x22, 0xF1, 0x90]);

        Assert.Equal(
            new byte[] { 0xF1, 0x00, 0xE8, 0x07, 0xE0, 0x07, 0x03, 0x00, 0x22, 0xF1, 0x90 },
            packet);
    }

    [Fact]
    public void NegativeResponse_ThrowsDecodedException()
    {
        var exception = Assert.Throws<UdsNegativeResponseException>(() =>
            Simos18Communication.ThrowIfNegativeResponse([0x27, 0x02], [0x7F, 0x27, 0x35]));

        Assert.Equal(0x27, exception.Service);
        Assert.Equal(0x35, exception.ResponseCode);
        Assert.Contains("invalid key", exception.Message);
    }

    [Theory]
    [InlineData(new byte[] { 0x74, 0x10, 0x80 }, 0x80)]
    [InlineData(new byte[] { 0x74, 0x20, 0x10, 0x02 }, 0x1002)]
    public void DownloadResponse_ParsesMaximumBlockLength(byte[] response, int expected)
    {
        Assert.Equal(expected, Simos18FlashDevice.ParseMaximumTransferDataLength(response));
    }
}
