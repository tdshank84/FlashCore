using FlashCore.Abstractions.Interfaces;
using FlashCore.Abstractions.Models;
using FlashCore.Core;
using FlashCore.Core.Checksums;
using FlashCore.Core.Journaling;
using FlashCore.Core.Planning;
using FlashCore.Core.Validation;
using FlashCore.Core.Transport;
using FlashCore.ECU.Simos18.Exploits;
using FlashCore.ECU.Simos18.Configuration;
using FlashCore.ECU.Simos18.Models;
using FlashCore.ECU.Simos18.Parsers;
using FlashCore.ECU.Simos18.Simulation;
using Microsoft.Extensions.Logging;

namespace FlashCore.ECU.Simos18;

public class Simos18FlashDevice : FlashDeviceBase
{
    private ITransport _transport;
    private readonly ILoggerFactory _loggerFactory;
    private readonly Simos18SeedKeyAlgorithm _seedKey;
    private readonly Simos18FRFParser _frfParser;
    private Simos18ECUInfo _ecuInfo = null!;
    private bool _sampleModeActive;
    private DeviceConnectionParams? _connectionParameters;
    private readonly IChecksumService _checksumService = new ChecksumService();
    private FlashPlan? _activePlan;
    private IFlashJournal? _journal;
    private Simos18EcuProfile? _profile;
    private bool _simulationMode;
    private bool _transportSelected;
    public Simos18WorkflowResult? LastWorkflowResult { get; private set; }

    public Simos18FlashDevice(
        ILogger<Simos18FlashDevice> logger,
        ILoggerFactory loggerFactory)
        : base(logger)
    {
        _loggerFactory = loggerFactory;
        _transport = new Simos18Communication(logger);
        _seedKey = new Simos18SeedKeyAlgorithm();
        _frfParser = new Simos18FRFParser(loggerFactory.CreateLogger<Simos18FRFParser>());
    }

    protected override void InitializeCapabilities()
    {
        base.InitializeCapabilities();
        _capabilities.SupportedECUs.Add("Simos18");
        _capabilities.SupportsSecurityAccess = true;
        _capabilities.SupportsBootloader = true;
    }

    public override async Task<bool> ConnectAsync(DeviceConnectionParams parameters)
    {
        return await ExecuteOperationAsync(async () =>
        {
            TransitionTo(DeviceState.Connecting);
            OnStatusUpdated("Connecting to Simos18 ECU...");

            _simulationMode = parameters.CustomParams?.TryGetValue("SimulationMode", out var simulation) == true &&
                              simulation is true;
            if (!_transportSelected)
            {
                _transport.Dispose();
                _transport = await new Simos18TransportFactory(_logger).CreateAsync(parameters);
                _transportSelected = true;
            }
            if (parameters.CustomParams?.TryGetValue("ReadOnlyHilMode", out var readOnlyHil) == true &&
                readOnlyHil is true && _transport is not ReadOnlyGuardTransport)
                _transport = new ReadOnlyGuardTransport(_transport);

            if (parameters.CustomParams?.TryGetValue("TracePath", out var configuredTrace) == true &&
                configuredTrace is string tracePath && !string.IsNullOrWhiteSpace(tracePath) &&
                _transport is not TracingTransport)
                _transport = new TracingTransport(_transport, tracePath);

            if (!await _transport.ConnectAsync(parameters))
                throw new InvalidOperationException("Failed to connect to ECU");

            _isConnected = true;
            TransitionTo(DeviceState.Connected);
            _connectionParameters = parameters;
            _profile = parameters.CustomParams?.TryGetValue("EcuProfile", out var configuredProfile) == true
                ? configuredProfile as Simos18EcuProfile
                : null;
            _ecuInfo = await GetECUInfoInternalAsync(CancellationToken.None);
            TransitionTo(DeviceState.Identified);

            _sampleModeActive = _ecuInfo.HardwareVersion.Contains("X13") ||
                               _ecuInfo.HardwareVersion.Contains("X14");

            OnStatusUpdated($"Connected: {_ecuInfo.HardwareNumber} (HW: {_ecuInfo.HardwareVersion})");
            OnStatusUpdated($"Sample Mode: {(_sampleModeActive ? "Active" : "Inactive")}");
        }, "Connecting to Simos18", CancellationToken.None);
    }

    public override async Task DisconnectAsync()
    {
        await _transport.DisconnectAsync();
        _isConnected = false;
        TransitionTo(DeviceState.Disconnected);
        OnStatusUpdated("Disconnected");
    }

    public override async Task<DeviceInfo> GetDeviceInfoAsync(CancellationToken cancellationToken = default)
    {
        var info = await GetECUInfoInternalAsync(cancellationToken);
        return new DeviceInfo
        {
            DeviceId = info.VIN,
            Manufacturer = "Volkswagen AG",
            Model = info.Model ?? "Simos18",
            FirmwareVersion = info.SoftwareVersion,
            HardwareVersion = info.HardwareVersion,
            LastConnected = DateTime.Now
        };
    }

    private async Task<Simos18ECUInfo> GetECUInfoInternalAsync(CancellationToken cancellationToken)
    {
        var info = new Simos18ECUInfo();

        try
        {
            var vin = await ReadDataByIdentifierAsync(0xF190, cancellationToken);
            if (vin.Length >= 17) info.VIN = System.Text.Encoding.ASCII.GetString(vin[..17]);

            var hwNumber = await ReadDataByIdentifierAsync(0xF191, cancellationToken);
            if (hwNumber.Length > 0) info.HardwareNumber = System.Text.Encoding.ASCII.GetString(hwNumber);

            var swVersion = await ReadDataByIdentifierAsync(0xF192, cancellationToken);
            if (swVersion.Length > 0) info.SoftwareVersion = System.Text.Encoding.ASCII.GetString(swVersion);

            var hwVersion = await ReadDataByIdentifierAsync(0xF193, cancellationToken);
            if (hwVersion.Length > 0) info.HardwareVersion = System.Text.Encoding.ASCII.GetString(hwVersion);

            var model = await ReadDataByIdentifierAsync(0xF194, cancellationToken);
            if (model.Length > 0) info.Model = System.Text.Encoding.ASCII.GetString(model);

            var bootloader = await ReadDataByIdentifierAsync(0xF1F4, cancellationToken);
            if (bootloader.Length > 0)
                info.BootloaderIdentification = System.Text.Encoding.ASCII.GetString(bootloader);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Partial ECU info read");
        }

        return info;
    }

    public override async Task<bool> FlashAsync(FlashFile file, IProgress<FlashProgress>? progress = null, CancellationToken cancellationToken = default)
    {
        return await ExecuteOperationAsync(async operationToken =>
        {
            using var safetyCancellation = CancellationTokenSource.CreateLinkedTokenSource(operationToken);
            var safetyToken = safetyCancellation.Token;
            try
            {
                if (file is not Simos18FlashFile simos18File)
                    throw new ArgumentException("Invalid flash file type. Expected Simos18 FRF file.");

                if (_connectionParameters is null || _profile is null)
                    throw new InvalidOperationException(
                        "A validated Simos18EcuProfile is required before physical programming.");
                await PhysicalExecutionPolicy.ValidateAsync(
                    _connectionParameters,
                    _profile,
                    _ecuInfo,
                    GetVoltageMonitor(_connectionParameters),
                    safetyToken);

                ValidateCompatibility(simos18File);
                var preflight = new FlashPreflightValidator(_checksumService).Validate(
                    simos18File,
                    new PreflightContext("Simos18", _ecuInfo.HardwareNumber));
                preflight.ThrowIfInvalid();
                _activePlan = FlashPlan.Create(simos18File,
                    _checksumService.Calculate(simos18File.RawData, ChecksumAlgorithm.Sha256));
                _journal = CreateJournal();
                await JournalAsync(FlashOperation.PreFlash, "Preflight validation passed", true, safetyToken);

                OnStatusUpdated($"Starting Simos18 flash: {simos18File.FileName}");
                _logger.LogInformation($"Flashing {simos18File.FileName} to {_ecuInfo.HardwareNumber}");

                if (!_sampleModeActive)
                {
                    throw new InvalidOperationException(
                        "The connected ECU is not in verified sample mode; refusing to erase or program memory.");
                }

                OnStatusUpdated("Entering programming session...");
                if (!await DiagnosticSessionControlAsync(DiagnosticSessionType.Programming, safetyToken))
                    throw new InvalidOperationException("Failed to enter programming session");
                TransitionTo(DeviceState.ProgrammingSession);
                await using var keepAlive = new TesterPresentScheduler(
                    _transport, _profile.TesterPresentInterval, _logger, _ => safetyCancellation.Cancel());
                await using var voltageSupervisor = new SupplyVoltageSupervisor(
                    GetVoltageMonitor(_connectionParameters),
                    _profile.MinimumSupplyVoltage,
                    TimeSpan.FromSeconds(1),
                    _logger,
                    _ => safetyCancellation.Cancel());
                keepAlive.Start(safetyToken);
                voltageSupervisor.Start(safetyToken);

                OnStatusUpdated("Performing security access...");
                await SecurityAccessInternalAsync(SecurityAccessType.SeedKey, safetyToken);
                TransitionTo(DeviceState.SecurityUnlocked);
                await JournalAsync(FlashOperation.SecurityAccess, "Security access granted", true, safetyToken);

                OnStatusUpdated("Erasing target memory...");
                TransitionTo(DeviceState.Erasing);
                await EraseMemoryInternalAsync(simos18File, safetyToken);
                await JournalAsync(FlashOperation.Erasing, "Target memory erased", true, safetyToken);

                var totalBlocks = simos18File.Blocks.Count;
                TransitionTo(DeviceState.Programming);
                for (int i = 0; i < totalBlocks; i++)
                {
                    var block = simos18File.Blocks[i];
                    OnStatusUpdated($"Programming block {i + 1}/{totalBlocks} at 0x{block.StartAddress:X8}");

                    if (!await ProgramBlockAsync(block, safetyToken))
                        throw new InvalidOperationException($"Failed to program block {i + 1}");
                    await JournalAsync(FlashOperation.Programming, $"Programmed block {i + 1}", true, safetyToken);

                    await TesterPresentAsync(safetyToken);
                    keepAlive.ThrowIfFailed();
                    voltageSupervisor.ThrowIfFailed();

                    var blockProgress = (float)(i + 1) / totalBlocks * 100;
                    progress?.Report(new FlashProgress
                    {
                        OverallProgress = blockProgress / 100,
                        CurrentOperationProgress = blockProgress,
                        OperationName = $"Flashing block {i + 1}/{totalBlocks}",
                        CurrentOperation = FlashOperation.Programming,
                        BlocksProcessed = i + 1,
                        TotalBlocks = totalBlocks
                    });
                }

                OnStatusUpdated("Reading back programmed blocks...");
                TransitionTo(DeviceState.Verifying);
                await VerifyBlocksInternalAsync(simos18File, progress, safetyToken);
                await JournalAsync(FlashOperation.Verifying, "Programmed blocks verified", true, safetyToken);
                await keepAlive.StopAsync();
                await voltageSupervisor.StopAsync();

                OnStatusUpdated("Resetting ECU...");
                TransitionTo(DeviceState.Finalizing);
                if (!await ECUResetAsync(safetyToken))
                    throw new InvalidOperationException("ECU did not acknowledge the reset request.");
                await Task.Delay(3000, safetyToken);
                await JournalAsync(FlashOperation.Finalizing, "ECU reset acknowledged", true, safetyToken);
                TransitionTo(DeviceState.Connected);

                OnStatusUpdated("Flash completed successfully!");
                _logger.LogInformation("Simos18 flash completed successfully");
            }
            catch (Exception exception)
            {
                await JournalAsync(
                    FlashOperation.None,
                    "Flash operation failed",
                    false,
                    CancellationToken.None,
                    exception.Message);
                throw;
            }
        }, "Flashing Simos18 ECU", cancellationToken);
    }

    private async Task<bool> ProgramBlockAsync(FlashBlock block, CancellationToken cancellationToken)
    {
        var downloadCommand = CreateDownloadCommand(block.StartAddress, (uint)block.Data.Length);
        var downloadResponse = await _transport.SendAsync(downloadCommand, cancellationToken);
        if (downloadResponse == null || downloadResponse.Length < 3 || downloadResponse[0] != 0x74) return false;

        var chunkSize = ParseMaximumTransferDataLength(downloadResponse) - 2;
        if (chunkSize <= 0)
            return false;
        var sequenceNumber = 1;
        var offset = 0;

        while (offset < block.Data.Length)
        {
            var actualChunkSize = Math.Min(chunkSize, block.Data.Length - offset);
            var chunk = new byte[actualChunkSize];
            Array.Copy(block.Data, offset, chunk, 0, actualChunkSize);

            var transferCommand = CreateTransferCommand(chunk, sequenceNumber);
            var transferResponse = await SendTransferWithRetryAsync(transferCommand, cancellationToken);
            if (transferResponse == null || transferResponse.Length < 2 || transferResponse[0] != 0x76 ||
                transferResponse[1] != (byte)sequenceNumber) return false;

            offset += actualChunkSize;
            sequenceNumber++;
        }

        var exitCommand = new byte[] { 0x37, 0x00 };
        var exitResponse = await _transport.SendAsync(exitCommand, cancellationToken);
        return exitResponse != null && exitResponse[0] == 0x77;
    }

    private async Task<byte[]> SendTransferWithRetryAsync(byte[] command, CancellationToken cancellationToken)
    {
        var maximumAttempts = _profile?.MaximumTransferAttempts ?? 1;
        for (var attempt = 1; ; attempt++)
        {
            try
            {
                return await _transport.SendAsync(command, cancellationToken);
            }
            catch (IOException exception) when (attempt < maximumAttempts)
            {
                _logger.LogWarning(exception,
                    "TransferData attempt {Attempt}/{MaximumAttempts} failed; retrying sequence {Sequence}",
                    attempt, maximumAttempts, command[1]);
                await Task.Delay(TimeSpan.FromMilliseconds(100 * attempt), cancellationToken);
            }
        }
    }

    public override async Task<bool> VerifyAsync(FlashFile file, IProgress<FlashProgress>? progress = null, CancellationToken cancellationToken = default)
    {
        return await ExecuteOperationAsync(async () =>
        {
            if (file is not Simos18FlashFile simos18File)
                throw new ArgumentException("Invalid flash file type");

            OnStatusUpdated("Verifying flash...");
            TransitionTo(DeviceState.Verifying);
            await VerifyBlocksInternalAsync(simos18File, progress, cancellationToken);

            OnStatusUpdated("Verification completed successfully");
            TransitionTo(DeviceState.Connected);
        }, "Verifying Simos18 ECU", cancellationToken);
    }

    public override async Task<byte[]> ReadMemoryAsync(uint address, uint size, CancellationToken cancellationToken = default)
    {
        byte[] data = Array.Empty<byte>();
        await ExecuteOperationAsync(async () =>
        {
            data = await ReadMemoryInternalAsync(address, size, cancellationToken);
            OnStatusUpdated($"Read {data.Length} bytes successfully");
        }, "Reading memory", cancellationToken);
        return data;
    }

    public Task<Simos18FlashFile> ParseFlashFileAsync(
        string filePath,
        CancellationToken cancellationToken = default) =>
        _frfParser.ParseAsync(filePath, cancellationToken);

    public async Task<bool> RunExploitAsync(
        IProgress<FlashProgress>? progress = null,
        CancellationToken cancellationToken = default)
    {
        if (!_isConnected || _connectionParameters is null || _profile is null)
            throw new InvalidOperationException("Connect with an explicit ECU profile before running the workflow.");
        if (!_simulationMode && !_profile.ProtocolValidated)
            throw new InvalidOperationException(
                "Physical workflow execution requires a profile explicitly marked ProtocolValidated after bench validation.");

        Func<CancellationToken, Task> executionGuard = _simulationMode
            ? _ => Task.CompletedTask
            : token => PhysicalExecutionPolicy.ValidateAsync(
                _connectionParameters,
                _profile,
                _ecuInfo,
                GetVoltageMonitor(_connectionParameters),
                token);
        Func<CancellationToken, Task<byte[]?>>? loaderProvider = null;
        if (_simulationMode)
        {
            var simulatedLoader = _connectionParameters.CustomParams?.TryGetValue("SimulationLoader", out var configured) == true &&
                                  configured is byte[] bytes && bytes.Length > 0
                ? bytes
                : Enumerable.Range(0, 256).Select(index => (byte)index).ToArray();
            loaderProvider = _ => Task.FromResult<byte[]?>(simulatedLoader);
        }

        using var engine = new Simos18ExploitEngine(
            _loggerFactory.CreateLogger<Simos18ExploitEngine>(),
            _transport,
            _seedKey,
            _profile,
            executionGuard,
            new ReadBackLoaderTransferVerifier(),
            loaderProvider);

        return await ExecuteOperationAsync(async token =>
        {
            using var workflowCancellation = CancellationTokenSource.CreateLinkedTokenSource(token);
            await using var voltageSupervisor = _simulationMode
                ? null
                : new SupplyVoltageSupervisor(
                    GetVoltageMonitor(_connectionParameters),
                    _profile.MinimumSupplyVoltage,
                    TimeSpan.FromSeconds(1),
                    _logger,
                    _ => workflowCancellation.Cancel());
            voltageSupervisor?.Start(workflowCancellation.Token);
            LastWorkflowResult = await engine.PerformFullExploitAsync(progress, workflowCancellation.Token);
            if (voltageSupervisor is not null) await voltageSupervisor.StopAsync();
            if (!LastWorkflowResult.IsSuccess)
                throw new InvalidOperationException(
                    $"Workflow failed during {LastWorkflowResult.Stage}: {LastWorkflowResult.Message}",
                    LastWorkflowResult.Error);
        }, "Simos18 workflow", cancellationToken);
    }

    public override async Task<bool> WriteMemoryAsync(uint address, byte[] data, CancellationToken cancellationToken = default)
    {
        return await ExecuteOperationAsync(async () =>
        {
            OnStatusUpdated($"Writing {data.Length} bytes at 0x{address:X8}");

            await SecurityAccessInternalAsync(SecurityAccessType.SeedKey, cancellationToken);

            var block = new FlashBlock
            {
                StartAddress = address,
                EndAddress = address + (uint)data.Length - 1,
                Size = (uint)data.Length,
                Data = data
            };

            if (!await ProgramBlockAsync(block, cancellationToken))
                throw new InvalidOperationException("Write operation failed");

            OnStatusUpdated("Write completed successfully");
        }, "Writing memory", cancellationToken);
    }

    public override async Task<bool> SecurityAccessAsync(SecurityAccessType type, CancellationToken cancellationToken = default)
    {
        return await ExecuteOperationAsync(
            () => SecurityAccessInternalAsync(type, cancellationToken),
            "Security access", cancellationToken);
    }

    private async Task SecurityAccessInternalAsync(SecurityAccessType type, CancellationToken cancellationToken)
    {
        OnStatusUpdated($"Performing security access ({type})");
        if (type != SecurityAccessType.SeedKey)
            throw new NotSupportedException($"Security type {type} not supported");

        var seedLevel = _profile?.ProgrammingSeedSubFunction ?? 0x11;
        var keyLevel = _profile?.ProgrammingKeySubFunction ?? 0x12;
        var seedResponse = await _transport.SendAsync(new byte[] { 0x27, seedLevel }, cancellationToken);
        if (seedResponse.Length < 6 || seedResponse[0] != 0x67 || seedResponse[1] != seedLevel)
            throw new InvalidOperationException("Failed to get seed");

        var script = _ecuInfo.BootloaderIdentification.Contains("SCG", StringComparison.OrdinalIgnoreCase)
            ? Simos18SeedKeyAlgorithm.Simos1810Script
            : _ecuInfo.BootloaderIdentification.Contains("SC8", StringComparison.OrdinalIgnoreCase)
                ? Simos18SeedKeyAlgorithm.Simos18Script
                : throw new InvalidOperationException(
                    $"Unsupported bootloader identification '{_ecuInfo.BootloaderIdentification}'. Expected SC8 or SCG.");
        var key = _seedKey.CalculateSa2Key(seedResponse.AsSpan()[2..6], script);
        var keyResponse = await _transport.SendAsync(
            new byte[] { 0x27, keyLevel, key[0], key[1], key[2], key[3] }, cancellationToken);
        if (keyResponse.Length < 2 || keyResponse[0] != 0x67 || keyResponse[1] != keyLevel)
            throw new InvalidOperationException("Security access failed");
        OnStatusUpdated("Security access successful");
    }

    private async Task<byte[]> ReadMemoryInternalAsync(uint address, uint size, CancellationToken cancellationToken)
    {
        const int maxReadSize = 0x1000;
        var result = new List<byte>();
        var remaining = size;
        var currentAddress = address;
        while (remaining > 0)
        {
            var readSize = Math.Min(maxReadSize, (int)remaining);
            var chunk = await ReadMemoryChunkAsync(currentAddress, (uint)readSize, cancellationToken);
            if (chunk.Length != readSize)
                throw new InvalidDataException($"ECU returned {chunk.Length} bytes; expected {readSize} at 0x{currentAddress:X8}.");
            result.AddRange(chunk);
            currentAddress += (uint)readSize;
            remaining -= (uint)readSize;
        }
        return result.ToArray();
    }

    public override async Task<bool> DiagnosticSessionControlAsync(DiagnosticSessionType session, CancellationToken cancellationToken = default)
    {
        var command = new byte[] { 0x10, (byte)session };
        var response = await _transport.SendAsync(command, cancellationToken);
        return response != null && response.Length > 0 && response[0] == 0x50;
    }

    private async Task<byte[]> ReadMemoryChunkAsync(uint address, uint size, CancellationToken cancellationToken)
    {
        var command = new byte[10];
        command[0] = 0x23; // ReadMemoryByAddress
        command[1] = 0x44; // four-byte address and four-byte size
        command[2] = (byte)(address >> 24);
        command[3] = (byte)(address >> 16);
        command[4] = (byte)(address >> 8);
        command[5] = (byte)address;
        command[6] = (byte)(size >> 24);
        command[7] = (byte)(size >> 16);
        command[8] = (byte)(size >> 8);
        command[9] = (byte)size;

        var response = await _transport.SendAsync(command, cancellationToken);
        if (response.Length < 1 || response[0] != 0x63)
            throw new InvalidOperationException($"Read memory failed at 0x{address:X8}");

        return response[1..];
    }

    private async Task<byte[]> ReadDataByIdentifierAsync(uint did, CancellationToken cancellationToken)
    {
        var command = new byte[3];
        command[0] = 0x22;
        command[1] = (byte)((did >> 8) & 0xFF);
        command[2] = (byte)(did & 0xFF);

        var response = await _transport.SendAsync(command, cancellationToken);
        if (response != null && response.Length > 3 && response[0] == 0x62)
            return response[3..];

        return Array.Empty<byte>();
    }

    private async Task<bool> ECUResetAsync(CancellationToken cancellationToken)
    {
        var command = new byte[] { 0x11, 0x01 };
        var response = await _transport.SendAsync(command, cancellationToken);
        return response != null && response.Length > 0 && response[0] == 0x51;
    }

    private async Task TesterPresentAsync(CancellationToken cancellationToken)
    {
        var response = await _transport.SendAsync(new byte[] { 0x3E, 0x00 }, cancellationToken);
        if (response.Length < 2 || response[0] != 0x7E || response[1] != 0x00)
            throw new InvalidOperationException("ECU did not acknowledge TesterPresent.");
    }

    private byte[] CreateDownloadCommand(uint address, uint size)
    {
        var command = new byte[11];
        command[0] = 0x34;
        command[1] = 0x00; // uncompressed and unencrypted
        command[2] = 0x44; // four-byte address and four-byte size
        command[3] = (byte)(address >> 24);
        command[4] = (byte)(address >> 16);
        command[5] = (byte)(address >> 8);
        command[6] = (byte)address;
        command[7] = (byte)(size >> 24);
        command[8] = (byte)(size >> 16);
        command[9] = (byte)(size >> 8);
        command[10] = (byte)size;
        return command;
    }

    private byte[] CreateTransferCommand(byte[] data, int sequenceNumber)
    {
        var command = new byte[data.Length + 2];
        command[0] = 0x36;
        command[1] = (byte)(sequenceNumber & 0xFF);
        Array.Copy(data, 0, command, 2, data.Length);
        return command;
    }

    internal static int ParseMaximumTransferDataLength(ReadOnlySpan<byte> response)
    {
        if (response.Length < 3 || response[0] != 0x74)
            throw new InvalidDataException("Invalid RequestDownload response.");
        var lengthBytes = response[1] >> 4;
        if (lengthBytes is < 1 or > 4 || response.Length < 2 + lengthBytes)
            throw new InvalidDataException("Invalid maxNumberOfBlockLength encoding.");

        uint value = 0;
        for (var i = 0; i < lengthBytes; i++)
            value = (value << 8) | response[2 + i];
        if (value < 3 || value > 65_535)
            throw new InvalidDataException($"ECU supplied an invalid transfer length of {value}.");
        return (int)value;
    }

    private void ValidateCompatibility(Simos18FlashFile file)
    {
        if (file.Blocks.Count == 0)
            throw new InvalidDataException("The flash file contains no programmable blocks.");
        if (!file.Validation.IsValid)
            throw new InvalidDataException("The flash file did not pass structural and checksum validation.");

        var targetEcu = file.Simos18Header.TargetECU.Trim();
        if (!string.IsNullOrEmpty(targetEcu) &&
            !targetEcu.Contains("SIMOS18", StringComparison.OrdinalIgnoreCase))
            throw new InvalidDataException($"Flash target '{targetEcu}' is not a Simos18 ECU.");

        var targetHardware = file.Simos18Header.HardwareID.Trim();
        var actualHardware = _ecuInfo.HardwareNumber.Trim();
        if (!string.IsNullOrEmpty(targetHardware) && !string.IsNullOrEmpty(actualHardware) &&
            !string.Equals(targetHardware, actualHardware, StringComparison.OrdinalIgnoreCase))
            throw new InvalidOperationException(
                $"Flash hardware '{targetHardware}' does not match connected ECU '{actualHardware}'.");
    }

    private async Task EraseMemoryInternalAsync(Simos18FlashFile file, CancellationToken cancellationToken)
    {
        var routineId = _profile?.EraseRoutineId ?? throw new InvalidOperationException(
            "No validated ECU profile is configured; refusing to guess an erase routine.");

        foreach (var block in file.Blocks)
        {
            var command = new byte[13];
            command[0] = 0x31;
            command[1] = 0x01;
            command[2] = (byte)(routineId >> 8);
            command[3] = (byte)routineId;
            command[4] = 0x44;
            command[5] = (byte)(block.StartAddress >> 24);
            command[6] = (byte)(block.StartAddress >> 16);
            command[7] = (byte)(block.StartAddress >> 8);
            command[8] = (byte)block.StartAddress;
            command[9] = (byte)(block.Size >> 24);
            command[10] = (byte)(block.Size >> 16);
            command[11] = (byte)(block.Size >> 8);
            command[12] = (byte)block.Size;
            var response = await _transport.SendAsync(command, cancellationToken);
            if (response.Length < 4 || response[0] != 0x71 || response[1] != 0x01 ||
                response[2] != command[2] || response[3] != command[3])
                throw new InvalidOperationException($"Erase routine failed for block at 0x{block.StartAddress:X8}.");
        }
    }

    private async Task VerifyBlocksInternalAsync(
        Simos18FlashFile file,
        IProgress<FlashProgress>? progress,
        CancellationToken cancellationToken)
    {
        var totalBlocks = file.Blocks.Count;
        for (var index = 0; index < totalBlocks; index++)
        {
            var block = file.Blocks[index];
            var readData = await ReadMemoryInternalAsync(block.StartAddress, (uint)block.Data.Length, cancellationToken);
            if (!readData.AsSpan().SequenceEqual(block.Data))
            {
                var mismatch = 0;
                while (mismatch < readData.Length && mismatch < block.Data.Length &&
                       readData[mismatch] == block.Data[mismatch])
                    mismatch++;
                throw new InvalidDataException(
                    $"Verification failed in block {index + 1} at address 0x{block.StartAddress + (uint)mismatch:X8}.");
            }
            progress?.Report(new FlashProgress
            {
                OverallProgress = (float)(index + 1) / totalBlocks,
                CurrentOperationProgress = (float)(index + 1) / totalBlocks * 100,
                OperationName = $"Verifying block {index + 1}/{totalBlocks}",
                CurrentOperation = FlashOperation.Verifying,
                BlocksProcessed = index + 1,
                TotalBlocks = totalBlocks
            });
        }
    }

    private IFlashJournal? CreateJournal()
    {
        if (_connectionParameters?.CustomParams?.TryGetValue("JournalPath", out var configured) != true ||
            configured is not string path || string.IsNullOrWhiteSpace(path))
            return null;
        return new JsonFlashJournal(path);
    }

    private static ISupplyVoltageMonitor GetVoltageMonitor(DeviceConnectionParams parameters)
    {
        if (parameters.CustomParams?.TryGetValue("SupplyVoltageMonitor", out var configured) == true &&
            configured is ISupplyVoltageMonitor monitor)
            return monitor;
        return new ConfiguredSupplyVoltageMonitor(parameters);
    }

    private async Task JournalAsync(
        FlashOperation operation,
        string description,
        bool completed,
        CancellationToken cancellationToken,
        string? error = null)
    {
        if (_journal is null || _activePlan is null) return;
        var sequence = _activePlan.Steps.FirstOrDefault(step => step.Operation == operation)?.Sequence ?? 0;
        await _journal.AppendAsync(new FlashJournalEntry(
            _activePlan.Id, sequence, operation, description, DateTimeOffset.UtcNow, completed, error), cancellationToken);
    }

    protected override void Dispose(bool disposing)
    {
        if (disposing && !_disposed)
        {
            _transport.Dispose();
        }
        base.Dispose(disposing);
    }
}
