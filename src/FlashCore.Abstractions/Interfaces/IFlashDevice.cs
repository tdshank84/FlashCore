using FlashCore.Abstractions.Models;

namespace FlashCore.Abstractions.Interfaces;

public interface IFlashDevice : IDisposable
{
    Task<bool> ConnectAsync(DeviceConnectionParams parameters);
    Task DisconnectAsync();
    Task<DeviceInfo> GetDeviceInfoAsync(CancellationToken cancellationToken = default);
    Task<bool> FlashAsync(FlashFile file, IProgress<FlashProgress>? progress = null, CancellationToken cancellationToken = default);
    Task<bool> VerifyAsync(FlashFile file, IProgress<FlashProgress>? progress = null, CancellationToken cancellationToken = default);
    Task<byte[]> ReadMemoryAsync(uint address, uint size, CancellationToken cancellationToken = default);
    Task<bool> WriteMemoryAsync(uint address, byte[] data, CancellationToken cancellationToken = default);
    Task<bool> SecurityAccessAsync(SecurityAccessType type, CancellationToken cancellationToken = default);
    Task<bool> DiagnosticSessionControlAsync(DiagnosticSessionType session, CancellationToken cancellationToken = default);
    event EventHandler<FlashProgress> ProgressUpdated;
    event EventHandler<string> StatusUpdated;
    event EventHandler<OperationCompletedEventArgs> OperationCompleted;
    bool IsConnected { get; }
    bool IsBusy { get; }
    DeviceState State { get; }
    OperationResult? LastOperationResult { get; }
    DeviceCapabilities Capabilities { get; }
    void CancelCurrentOperation();
}

public class DeviceConnectionParams
{
    public string PortName { get; set; } = string.Empty;
    public int BaudRate { get; set; } = 500000;
    public ProtocolType Protocol { get; set; } = ProtocolType.CAN;
    public Dictionary<string, object>? CustomParams { get; set; }
}

public class DeviceInfo
{
    public string DeviceId { get; set; } = string.Empty;
    public string Manufacturer { get; set; } = string.Empty;
    public string Model { get; set; } = string.Empty;
    public string FirmwareVersion { get; set; } = string.Empty;
    public string HardwareVersion { get; set; } = string.Empty;
    public DateTime LastConnected { get; set; }
}

public enum ProtocolType { CAN, KWP2000, J2534, DoIP, KLine, LIN }

public enum DiagnosticSessionType
{
    Default = 0x01,
    Programming = 0x02,
    Extended = 0x03,
    Safety = 0x04,
    Manufacturing = 0x05
}

public enum SecurityAccessType { SeedKey = 0x01, RSA = 0x02, ECDSA = 0x03, Custom = 0xFF }

public class DeviceCapabilities
{
    public bool SupportsUDS { get; set; }
    public bool SupportsKWP2000 { get; set; }
    public bool SupportsJ2534 { get; set; }
    public bool SupportsDoIP { get; set; }
    public bool SupportsSecurityAccess { get; set; }
    public bool SupportsBootloader { get; set; }
    public int MaxPacketSize { get; set; } = 4096;
    public List<string> SupportedECUs { get; set; } = new();
    public Dictionary<string, object> ExtendedCapabilities { get; set; } = new();
}
