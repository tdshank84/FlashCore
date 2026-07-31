using System.Text.Json;
using FlashCore.Abstractions.Interfaces;
using FlashCore.Core.Transport;
using FlashCore.ECU.Simos18;
using FlashCore.ECU.Simos18.Configuration;
using FlashCore.ECU.Simos18.Models;
using FlashCore.ECU.Simos18.Simulation;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace FlashCore.Tests;

public sealed class Simos18SafetyFeatureTests
{
    [Fact]
    public void EcuProfile_RejectsMismatchedHardwareAndBootloader()
    {
        var profile = CreateProfile();
        var ecu = new Simos18ECUInfo
        {
            HardwareNumber = "DIFFERENT",
            BootloaderIdentification = "SCG"
        };

        var errors = profile.ValidateAgainst(ecu);

        Assert.Contains(errors, error => error.Contains("hardware", StringComparison.OrdinalIgnoreCase));
        Assert.Contains(errors, error => error.Contains("bootloader", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public async Task PhysicalPolicy_RequiresEnablementConfirmationAndVoltage()
    {
        var parameters = new DeviceConnectionParams { CustomParams = [] };
        var exception = await Assert.ThrowsAsync<InvalidOperationException>(() =>
            PhysicalExecutionPolicy.ValidateAsync(
                parameters,
                CreateProfile(),
                CreateEcuInfo(),
                new ConfiguredSupplyVoltageMonitor(parameters),
                TestContext.Current.CancellationToken));

        Assert.Contains("not explicitly enabled", exception.Message);
        Assert.Contains("confirmation", exception.Message);
        Assert.Contains("voltage is unavailable", exception.Message);
    }

    [Fact]
    public async Task PhysicalPolicy_AcceptsExactProfileAndSafeInputs()
    {
        var parameters = new DeviceConnectionParams
        {
            CustomParams = new Dictionary<string, object>
            {
                ["EnablePhysicalProgramming"] = true,
                ["SafetyConfirmation"] = PhysicalExecutionPolicy.ConfirmationText,
                ["SupplyVoltage"] = 13.4m
            }
        };

        await PhysicalExecutionPolicy.ValidateAsync(
            parameters,
            CreateProfile(),
            CreateEcuInfo(),
            new ConfiguredSupplyVoltageMonitor(parameters),
            TestContext.Current.CancellationToken);
    }

    [Fact]
    public async Task TracingTransport_PersistsRequestAndResponse()
    {
        var path = Path.Combine(Path.GetTempPath(), $"flashcore-trace-{Guid.NewGuid():N}.jsonl");
        try
        {
            using var simulated = new Simos18SimulationTransport();
            using var tracing = new TracingTransport(simulated, path);
            await tracing.ConnectAsync(new(), TestContext.Current.CancellationToken);

            var response = await tracing.SendAsync(
                new byte[] { 0x10, 0x03 }, TestContext.Current.CancellationToken);
            var entries = (await File.ReadAllLinesAsync(path, TestContext.Current.CancellationToken))
                .Select(line => JsonSerializer.Deserialize<TransportTraceEntry>(line)!)
                .ToArray();

            Assert.Equal(new byte[] { 0x50, 0x03 }, response);
            Assert.Equal(2, entries.Length);
            Assert.Equal("request", entries[0].Direction);
            Assert.Equal("1003", entries[0].Payload);
            Assert.Equal("response", entries[1].Direction);
            Assert.Equal("5003", entries[1].Payload);
        }
        finally
        {
            if (File.Exists(path)) File.Delete(path);
        }
    }

    [Fact]
    public async Task TracingTransport_RedactsSecurityExchangeByDefault()
    {
        var path = Path.Combine(Path.GetTempPath(), $"flashcore-trace-{Guid.NewGuid():N}.jsonl");
        try
        {
            using var simulated = new Simos18SimulationTransport();
            using var tracing = new TracingTransport(simulated, path);
            await tracing.ConnectAsync(new(), TestContext.Current.CancellationToken);

            await tracing.SendAsync(new byte[] { 0x27, 0x01 }, TestContext.Current.CancellationToken);
            var entries = (await File.ReadAllLinesAsync(path, TestContext.Current.CancellationToken))
                .Select(line => JsonSerializer.Deserialize<TransportTraceEntry>(line)!)
                .ToArray();

            Assert.All(entries, entry => Assert.StartsWith("REDACTED:", entry.Payload));
        }
        finally
        {
            if (File.Exists(path)) File.Delete(path);
        }
    }

    [Fact]
    public async Task TracingTransport_RedactsVinExchangeByDefault()
    {
        var path = Path.Combine(Path.GetTempPath(), $"flashcore-vin-{Guid.NewGuid():N}.jsonl");
        try
        {
            using var simulated = new Simos18SimulationTransport();
            using var tracing = new TracingTransport(simulated, path);
            await tracing.ConnectAsync(new(), TestContext.Current.CancellationToken);
            await tracing.SendAsync(new byte[] { 0x22, 0xF1, 0x90 }, TestContext.Current.CancellationToken);
            var text = await File.ReadAllTextAsync(path, TestContext.Current.CancellationToken);
            Assert.DoesNotContain("SIMULATEDVIN", text);
            Assert.Contains("REDACTED:", text);
        }
        finally { if (File.Exists(path)) File.Delete(path); }
    }

    [Fact]
    public async Task TesterPresentScheduler_SendsPeriodicKeepAlive()
    {
        using var transport = new Simos18SimulationTransport();
        await transport.ConnectAsync(new(), TestContext.Current.CancellationToken);
        await using var scheduler = new TesterPresentScheduler(
            transport, TimeSpan.FromMilliseconds(10), NullLogger.Instance);
        scheduler.Start(TestContext.Current.CancellationToken);

        await Task.Delay(35, TestContext.Current.CancellationToken);
        await scheduler.StopAsync();

        Assert.Contains(transport.Requests,
            request => request.AsSpan().SequenceEqual(new byte[] { 0x3E, 0x00 }));
    }

    [Fact]
    public async Task TesterPresentScheduler_PropagatesFailure()
    {
        using var transport = new RejectingTesterPresentTransport();
        await transport.ConnectAsync(new(), TestContext.Current.CancellationToken);
        var failed = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        await using var scheduler = new TesterPresentScheduler(
            transport,
            TimeSpan.FromMilliseconds(5),
            NullLogger.Instance,
            _ => failed.TrySetResult());
        scheduler.Start(TestContext.Current.CancellationToken);

        await failed.Task.WaitAsync(TestContext.Current.CancellationToken);
        await Assert.ThrowsAsync<IOException>(scheduler.StopAsync);
    }

    [Fact]
    public async Task SupplyVoltageSupervisor_PropagatesVoltageDrop()
    {
        var failed = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        await using var supervisor = new SupplyVoltageSupervisor(
            new ConstantVoltageMonitor(11.5m),
            12.0m,
            TimeSpan.FromMilliseconds(5),
            NullLogger.Instance,
            _ => failed.TrySetResult());
        supervisor.Start(TestContext.Current.CancellationToken);

        await failed.Task.WaitAsync(TestContext.Current.CancellationToken);
        await Assert.ThrowsAsync<IOException>(supervisor.StopAsync);
    }

    private static Simos18EcuProfile CreateProfile() => new()
    {
        Name = "Test SC8",
        HardwareNumber = "SIMOS18-X13-SIM",
        BootloaderIdentifier = "SC8",
        EraseRoutineId = 0xFF00,
        LoaderAddress = 0x80000000,
        SampleModeDid = 0xF191,
        MinimumSupplyVoltage = 12.0m,
        UnlockLoaderPath = "simulated-loader.bin"
    };

    private static Simos18ECUInfo CreateEcuInfo() => new()
    {
        HardwareNumber = "SIMOS18-X13-SIM",
        BootloaderIdentification = "SC8"
    };

    private sealed class ConstantVoltageMonitor(decimal voltage) : ISupplyVoltageMonitor
    {
        public Task<decimal?> ReadVoltageAsync(CancellationToken cancellationToken = default) =>
            Task.FromResult<decimal?>(voltage);
    }

    private sealed class RejectingTesterPresentTransport : ITransport
    {
        public bool IsConnected { get; private set; }
        public Task<bool> ConnectAsync(DeviceConnectionParams parameters, CancellationToken cancellationToken = default)
        {
            IsConnected = true;
            return Task.FromResult(true);
        }
        public Task DisconnectAsync(CancellationToken cancellationToken = default)
        {
            IsConnected = false;
            return Task.CompletedTask;
        }
        public Task<byte[]> SendAsync(ReadOnlyMemory<byte> request, CancellationToken cancellationToken = default) =>
            Task.FromResult(new byte[] { 0x7F, 0x3E, 0x22 });
        public void Dispose() => IsConnected = false;
    }
}
