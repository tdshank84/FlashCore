using System.Security.Cryptography;
using FlashCore.Abstractions.Interfaces;
using FlashCore.Abstractions.Models;
using FlashCore.Core.Artifacts;
using FlashCore.Core.Transport;
using FlashCore.ECU.Simos18.Configuration;
using FlashCore.ECU.Simos18.Planning;
using Xunit;

namespace FlashCore.Tests;

public sealed class ExtendedFeatureTests
{
    [Fact]
    public async Task ProfileLoader_RoundTripsAndVerifiesRsaSignature()
    {
        var path = Path.Combine(Path.GetTempPath(), $"profile-{Guid.NewGuid():N}.json");
        using var rsa = RSA.Create(2048);
        try
        {
            var loader = new Simos18ProfileLoader();
            var profile = CreateProfile();
            await loader.SaveAsync(path, profile, rsa, TestContext.Current.CancellationToken);
            var loaded = await loader.LoadAsync(path, true, rsa.ExportRSAPublicKeyPem(),
                TestContext.Current.CancellationToken);
            Assert.Equal(profile.Name, loaded.Name);
            Assert.Equal(profile.HardwareNumber, loaded.HardwareNumber);
            Assert.Equal(profile.LoaderAddress, loaded.LoaderAddress);
            Assert.Equal(profile.SampleModeMarkers, loaded.SampleModeMarkers);
        }
        finally { if (File.Exists(path)) File.Delete(path); }
    }

    [Fact]
    public async Task TranscriptReplayTransport_RequiresExactRequestOrder()
    {
        using var transport = new TranscriptReplayTransport([
            new TranscriptExchange("1003", "5003")
        ]);
        await transport.ConnectAsync(new(), TestContext.Current.CancellationToken);
        var response = await transport.SendAsync(new byte[] { 0x10, 0x03 }, TestContext.Current.CancellationToken);
        Assert.Equal(new byte[] { 0x50, 0x03 }, response);
        Assert.Equal(0, transport.Remaining);
    }

    [Fact]
    public async Task ReadOnlyGuardTransport_BlocksProgrammingServices()
    {
        using var inner = new TranscriptReplayTransport([]);
        using var guarded = new ReadOnlyGuardTransport(inner);
        await guarded.ConnectAsync(new(), TestContext.Current.CancellationToken);
        await Assert.ThrowsAsync<InvalidOperationException>(() =>
            guarded.SendAsync(new byte[] { 0x34, 0x00 }, TestContext.Current.CancellationToken));
    }

    [Fact]
    public void DryRunAnalyzer_ReturnsPlanWithoutTransport()
    {
        var file = new FlashFile
        {
            RawData = [1, 2, 3],
            IsVerified = true,
            TargetECU = "Simos18",
            TargetHW = "SIMOS18-X13-SIM",
            Blocks = [new FlashBlock { StartAddress = 0x80000000, Size = 3, Data = [1, 2, 3] }]
        };
        file.CalculateChecksum();
        var report = new Simos18DryRunAnalyzer().Analyze(file, CreateProfile());
        Assert.True(report.IsReady);
        Assert.Equal(3, report.TotalBytes);
        Assert.NotEmpty(report.Plan.Steps);
    }

    [Fact]
    public void ArtifactRetention_RemovesExpiredFiles()
    {
        var directory = Path.Combine(Path.GetTempPath(), $"flashcore-retention-{Guid.NewGuid():N}");
        Directory.CreateDirectory(directory);
        try
        {
            var expired = Path.Combine(directory, "expired.json");
            File.WriteAllText(expired, "{}");
            File.SetLastWriteTimeUtc(expired, DateTime.UtcNow.AddDays(-30));
            ArtifactStorage.ApplyRetention(directory, new(TimeSpan.FromDays(7)));
            Assert.False(File.Exists(expired));
        }
        finally { if (Directory.Exists(directory)) Directory.Delete(directory, true); }
    }

    [Fact]
    public async Task J2534Transport_UsesInjectedChannel()
    {
        using var channel = new FakeJ2534Channel();
        using var transport = new J2534Transport(channel);
        Assert.True(await transport.ConnectAsync(
            new DeviceConnectionParams { PortName = "fake", BaudRate = 500000 },
            TestContext.Current.CancellationToken));
        var response = await transport.SendAsync(
            new byte[] { 0x10, 0x03 }, TestContext.Current.CancellationToken);
        Assert.Equal(new byte[] { 0x50, 0x03 }, response);
    }

    [Fact]
    public async Task SocketCanTransport_IsPlatformAndInterfaceGuarded()
    {
        using var transport = new SocketCanIsoTpTransport();
        var parameters = new DeviceConnectionParams { PortName = "flashcore-interface-does-not-exist" };
        if (OperatingSystem.IsLinux())
            await Assert.ThrowsAsync<IOException>(() =>
                transport.ConnectAsync(parameters, TestContext.Current.CancellationToken));
        else
            await Assert.ThrowsAsync<PlatformNotSupportedException>(() =>
                transport.ConnectAsync(parameters, TestContext.Current.CancellationToken));
    }

    private static Simos18EcuProfile CreateProfile() => new()
    {
        Name = "Test",
        HardwareNumber = "SIMOS18-X13-SIM",
        BootloaderIdentifier = "SC8",
        EraseRoutineId = 0xFF00,
        LoaderAddress = 0x80000000,
        SampleModeDid = 0xF191,
        UnlockLoaderPath = "loader.bin"
    };

    private sealed class FakeJ2534Channel : IJ2534Channel
    {
        public bool IsOpen { get; private set; }
        public void Open(string? deviceName, uint baudRate, uint transmitId, uint receiveId, uint stminMicroseconds) => IsOpen = true;
        public void Close() => IsOpen = false;
        public byte[] Request(ReadOnlySpan<byte> payload, TimeSpan timeout, CancellationToken cancellationToken) => [0x50, payload[1]];
        public void Dispose() => Close();
    }
}
