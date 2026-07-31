using FlashCore.Abstractions.Interfaces;
using FlashCore.ECU.Simos18;
using FlashCore.ECU.Simos18.Configuration;
using FlashCore.ECU.Simos18.Exploits;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace FlashCore.Tests;

public sealed class Simos18DeviceSimulationTests
{
    [Fact]
    public async Task RunExploitAsync_ExecutesThroughDeviceInSimulationMode()
    {
        using var loggerFactory = LoggerFactory.Create(_ => { });
        using var device = new Simos18FlashDevice(NullLogger<Simos18FlashDevice>.Instance, loggerFactory);
        var profile = new Simos18EcuProfile
        {
            Name = "Simulation",
            HardwareNumber = "SIMOS18-X13-SIM",
            BootloaderIdentifier = "SC8",
            EraseRoutineId = 0xFF00,
            LoaderAddress = 0x80000000,
            SampleModeDid = 0xF191,
            UnlockLoaderPath = "not-used-in-simulation.bin"
        };

        var connected = await device.ConnectAsync(new DeviceConnectionParams
        {
            PortName = "SIMULATED",
            CustomParams = new Dictionary<string, object>
            {
                ["SimulationMode"] = true,
                ["EcuProfile"] = profile,
                ["SimulationLoader"] = Enumerable.Range(0, 300).Select(index => (byte)index).ToArray()
            }
        });
        var succeeded = await device.RunExploitAsync(cancellationToken: TestContext.Current.CancellationToken);

        Assert.True(connected);
        Assert.True(succeeded);
        Assert.NotNull(device.LastWorkflowResult);
        Assert.True(device.LastWorkflowResult.IsSuccess);
        Assert.Equal(Simos18WorkflowStage.Complete, device.LastWorkflowResult.Stage);
    }
}
