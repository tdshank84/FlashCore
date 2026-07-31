using FlashCore.Abstractions.Models;

namespace FlashCore.ECU.Simos18.Models;

public class Simos18ECUInfo
{
    public string VIN { get; set; } = string.Empty;
    public string HardwareNumber { get; set; } = string.Empty;
    public string HardwareVersion { get; set; } = string.Empty;
    public string SoftwareVersion { get; set; } = string.Empty;
    public string Model { get; set; } = string.Empty;
    public string ASAMID { get; set; } = string.Empty;
    public string BootloaderIdentification { get; set; } = string.Empty;
    public bool IsSampleMode { get; set; }
    public Dictionary<string, string> AdditionalInfo { get; set; } = new();
}

public class Simos18FlashFile : FlashFile
{
    public Simos18FileHeader Simos18Header { get; set; } = new();
    public List<Simos18DataBlock> DataBlocks { get; set; } = new();
    public bool IsUnlockLoader { get; set; }
    public Dictionary<uint, string> AddressMappings { get; set; } = new();
    public List<string> CompatibilityTags { get; set; } = new();
}

public class Simos18FileHeader
{
    public uint Magic { get; set; }
    public uint Version { get; set; }
    public uint HeaderSize { get; set; }
    public uint DataSize { get; set; }
    public uint BlockCount { get; set; }
    public string TargetECU { get; set; } = string.Empty;
    public string HardwareID { get; set; } = string.Empty;
    public DateTime CreationDate { get; set; }
    public string CreatedBy { get; set; } = string.Empty;
    public bool IsCompressed { get; set; }
    public bool IsEncrypted { get; set; }
    public byte[] Signature { get; set; } = Array.Empty<byte>();
}

public class Simos18DataBlock
{
    public uint Address { get; set; }
    public uint Size { get; set; }
    public byte[] Data { get; set; } = Array.Empty<byte>();
    public uint Checksum { get; set; }
    public BlockType Type { get; set; }
    public string Name { get; set; } = string.Empty;
    public bool IsCompressed { get; set; }
    public bool IsEncrypted { get; set; }
    public Dictionary<string, string> Metadata { get; set; } = new();
}

public enum BlockType : byte
{
    Code = 0x01, Data = 0x02, Calibration = 0x03,
    Bootloader = 0x04, Signature = 0x05, Reserved = 0xFF,
    EEPROM = 0x06, Flash = 0x07
}
