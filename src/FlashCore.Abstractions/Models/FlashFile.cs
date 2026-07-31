using System.Security.Cryptography;

namespace FlashCore.Abstractions.Models;

public class FlashFile
{
    public string FilePath { get; set; } = string.Empty;
    public string FileName { get; set; } = string.Empty;
    public long FileSize { get; set; }
    public string Checksum { get; set; } = string.Empty;
    public FlashFileType FileType { get; set; }
    public FlashFileFormat Format { get; set; }
    public List<FlashBlock> Blocks { get; set; } = new();
    public Dictionary<string, string> Metadata { get; set; } = new();
    public byte[] RawData { get; set; } = Array.Empty<byte>();
    public bool IsVerified { get; set; }
    public FlashFileHeader Header { get; set; } = new();
    public FlashFileSignature Signature { get; set; } = new();
    public FlashFileSecurity Security { get; set; } = new();
    public int BlockCount { get; set; }
    public DateTime CreatedAt { get; set; }
    public string CreatedBy { get; set; } = string.Empty;
    public string TargetECU { get; set; } = string.Empty;
    public string TargetHW { get; set; } = string.Empty;
    public string TargetSW { get; set; } = string.Empty;
    public List<string> Dependencies { get; set; } = new();
    public FlashFileValidation Validation { get; set; } = new();

    public void CalculateChecksum()
    {
        Checksum = Convert.ToHexString(SHA256.HashData(RawData));
    }

    public bool ValidateChecksum()
    {
        var calculated = Convert.ToHexString(SHA256.HashData(RawData));
        return string.Equals(calculated, Checksum, StringComparison.OrdinalIgnoreCase);
    }
}

public enum FlashFileType { Binary, Hex, SRecord, IntelHex, MotorolaSRecord, FRF, ODX, VBF }
public enum FlashFileFormat { Raw, Segmented, Compressed, Encrypted, Signed, Packed }

public class FlashFileHeader
{
    public string Magic { get; set; } = string.Empty;
    public uint Version { get; set; }
    public uint HeaderSize { get; set; }
    public uint DataSize { get; set; }
    public uint BlockCount { get; set; }
    public string CreatedBy { get; set; } = string.Empty;
    public DateTime CreationDate { get; set; }
    public string TargetECU { get; set; } = string.Empty;
    public string SoftwareVersion { get; set; } = string.Empty;
}

public class FlashFileSignature
{
    public bool IsSigned { get; set; }
    public string SignatureType { get; set; } = string.Empty;
    public byte[] SignatureData { get; set; } = Array.Empty<byte>();
    public string CertificateName { get; set; } = string.Empty;
    public bool IsValid { get; set; }
}

public class FlashBlock
{
    public uint StartAddress { get; set; }
    public uint EndAddress { get; set; }
    public uint Size { get; set; }
    public byte[] Data { get; set; } = Array.Empty<byte>();
    public bool IsCompressed { get; set; }
    public bool IsEncrypted { get; set; }
    public string Checksum { get; set; } = string.Empty;
    public FlashBlockType BlockType { get; set; }
    public FlashBlockSecurity Security { get; set; } = new();
    public Dictionary<string, string> Metadata { get; set; } = new();
}

public enum FlashBlockType { Code, Data, Calibration, Bootloader, Signature, Reserved, EEPROM, Flash, RAM, Configuration }

public class FlashBlockSecurity
{
    public bool IsProtected { get; set; }
    public string ProtectionType { get; set; } = string.Empty;
    public byte[] Key { get; set; } = Array.Empty<byte>();
    public bool IsVerified { get; set; }
}

public class FlashFileSecurity
{
    public bool IsEncrypted { get; set; }
    public string EncryptionType { get; set; } = string.Empty;
    public bool IsSigned { get; set; }
    public string SignatureType { get; set; } = string.Empty;
    public byte[] PublicKey { get; set; } = Array.Empty<byte>();
    public byte[] Certificate { get; set; } = Array.Empty<byte>();
}

public class FlashFileValidation
{
    public bool IsValid { get; set; }
    public List<string> Errors { get; set; } = new();
    public List<string> Warnings { get; set; } = new();
    public bool ChecksumValid { get; set; }
    public bool SignatureValid { get; set; }
    public bool StructureValid { get; set; }
    public DateTime ValidatedAt { get; set; }
}

public class FlashProgress
{
    public float OverallProgress { get; set; }
    public float CurrentOperationProgress { get; set; }
    public string OperationName { get; set; } = string.Empty;
    public FlashOperation CurrentOperation { get; set; }
    public long BytesProcessed { get; set; }
    public long TotalBytes { get; set; }
    public int BlocksProcessed { get; set; }
    public int TotalBlocks { get; set; }
    public TimeSpan ElapsedTime { get; set; }
    public TimeSpan EstimatedRemainingTime { get; set; }
    public string StatusMessage { get; set; } = string.Empty;
    public Dictionary<string, object> CustomData { get; set; } = new();
}

public enum FlashOperation { None, Connecting, Identifying, SecurityAccess, Erasing, Programming, Verifying, Finalizing, Bootloader, PreFlash, PostFlash }
