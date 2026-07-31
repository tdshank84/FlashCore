using System.IO.Compression;
using System.Text;
using FlashCore.Abstractions.Models;
using FlashCore.ECU.Simos18.Models;
using Microsoft.Extensions.Logging;

namespace FlashCore.ECU.Simos18.Parsers;

public class Simos18FRFParser
{
    private readonly ILogger<Simos18FRFParser> _logger;

    public Simos18FRFParser(ILogger<Simos18FRFParser> logger)
    {
        _logger = logger;
    }

    public async Task<Simos18FlashFile> ParseAsync(string filePath, CancellationToken cancellationToken = default)
    {
        _logger.LogInformation($"Parsing Simos18 FRF file: {filePath}");

        try
        {
            var fileBytes = await File.ReadAllBytesAsync(filePath, cancellationToken);

            var flashFile = new Simos18FlashFile
            {
                FilePath = filePath,
                FileName = Path.GetFileName(filePath),
                FileSize = fileBytes.Length,
                RawData = fileBytes,
                FileType = FlashFileType.FRF,
                Format = FlashFileFormat.Segmented,
                CreatedAt = File.GetCreationTime(filePath)
            };

            ParseFRFHeader(fileBytes, flashFile);
            ParseDataBlocks(fileBytes, flashFile);
            ExtractMetadata(flashFile);

            _logger.LogInformation($"Parsed {flashFile.FileName}: {flashFile.Blocks.Count} blocks");
            return flashFile;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, $"Error parsing FRF file: {filePath}");
            throw;
        }
    }

    private void ParseFRFHeader(byte[] data, Simos18FlashFile flashFile)
    {
        if (data.Length < 16)
            throw new InvalidDataException("Invalid FRF file: header is truncated.");

        var header = flashFile.Simos18Header;
        header.Magic = BitConverter.ToUInt32(data, 0);

        if (header.Magic != 0x465246)
            throw new InvalidDataException("Invalid FRF file: Magic not found");

        header.HeaderSize = BitConverter.ToUInt32(data, 4);
        header.DataSize = BitConverter.ToUInt32(data, 8);
        header.Version = BitConverter.ToUInt32(data, 12);
        if (header.HeaderSize < 16 || header.HeaderSize > data.Length)
            throw new InvalidDataException("Invalid FRF file: header size is outside the file.");
        if (header.DataSize > data.Length - header.HeaderSize)
            throw new InvalidDataException("Invalid FRF file: declared data size exceeds the file.");

        var nameStart = 16;
        var nameEnd = Array.IndexOf(data, (byte)0, nameStart);
        if (nameEnd > nameStart)
            flashFile.Metadata["OriginalFileName"] = Encoding.ASCII.GetString(data[nameStart..nameEnd]);

        var headerLimit = (int)header.HeaderSize;
        var offset = nameEnd >= 0 && nameEnd < headerLimit ? nameEnd + 1 : headerLimit;
        if (offset < headerLimit)
        {
            var targetEnd = Array.IndexOf(data, (byte)0, offset, headerLimit - offset);
            if (targetEnd > offset)
            {
                header.TargetECU = Encoding.ASCII.GetString(data[offset..targetEnd]);
                offset = targetEnd + 1;
            }

            var hwEnd = offset < headerLimit ? Array.IndexOf(data, (byte)0, offset, headerLimit - offset) : -1;
            if (hwEnd > offset)
            {
                header.HardwareID = Encoding.ASCII.GetString(data[offset..hwEnd]);
                offset = hwEnd + 1;
            }

            var dateEnd = offset < headerLimit ? Array.IndexOf(data, (byte)0, offset, headerLimit - offset) : -1;
            if (dateEnd > offset)
            {
                var dateStr = Encoding.ASCII.GetString(data[offset..dateEnd]);
                if (DateTime.TryParse(dateStr, out var date))
                    header.CreationDate = date;
            }
        }

        flashFile.Header = new FlashFileHeader
        {
            Magic = Encoding.ASCII.GetString(BitConverter.GetBytes(header.Magic)),
            Version = header.Version,
            HeaderSize = header.HeaderSize,
            DataSize = header.DataSize,
            TargetECU = header.TargetECU,
            CreationDate = header.CreationDate
        };
    }

    private void ParseDataBlocks(byte[] data, Simos18FlashFile flashFile)
    {
        var offset = (int)flashFile.Simos18Header.HeaderSize;
        var dataEnd = checked(offset + (int)flashFile.Simos18Header.DataSize);
        var blockCount = 0;

        while (offset < dataEnd)
        {
            if (offset + 16 > dataEnd)
                throw new InvalidDataException($"Invalid FRF file: block {blockCount + 1} header is truncated.");
            var blockHeader = new byte[16];
            Array.Copy(data, offset, blockHeader, 0, 16);
            offset += 16;

            var blockAddress = BitConverter.ToUInt32(blockHeader, 0);
            var blockSize = BitConverter.ToUInt32(blockHeader, 4);
            var blockChecksum = BitConverter.ToUInt32(blockHeader, 8);
            var blockType = BitConverter.ToUInt32(blockHeader, 12);

            if (blockSize == 0 || blockSize > int.MaxValue || blockSize > dataEnd - offset)
                throw new InvalidDataException($"Invalid FRF file: block {blockCount + 1} has an invalid size.");

            var blockData = new byte[blockSize];
            Array.Copy(data, offset, blockData, 0, (int)blockSize);
            offset += (int)blockSize;

            if (flashFile.Simos18Header.IsCompressed)
            {
                try { blockData = DecompressBlock(blockData); }
                catch { }
            }

            var calculatedChecksum = ComputeCrc32(blockData);
            if (blockChecksum != 0 && blockChecksum != calculatedChecksum)
                throw new InvalidDataException(
                    $"Invalid FRF file: block {blockCount + 1} CRC32 is 0x{calculatedChecksum:X8}, expected 0x{blockChecksum:X8}.");

            var block = new FlashBlock
            {
                StartAddress = blockAddress,
                EndAddress = blockAddress + (uint)blockData.Length - 1,
                Size = (uint)blockData.Length,
                Data = blockData,
                Checksum = blockChecksum == 0 ? string.Empty : blockChecksum.ToString("X8"),
                BlockType = DetermineBlockType(blockAddress, (BlockType)blockType)
            };

            flashFile.Blocks.Add(block);
            flashFile.DataBlocks.Add(new Simos18DataBlock
            {
                Address = blockAddress,
                Size = (uint)blockData.Length,
                Data = blockData,
                Checksum = blockChecksum,
                Type = (BlockType)blockType
            });

            blockCount++;
        }

        if (offset != dataEnd)
            throw new InvalidDataException("Invalid FRF file: parsed block data does not match the declared data size.");

        flashFile.BlockCount = blockCount;
        flashFile.Header.BlockCount = (uint)blockCount;
        flashFile.Validation.StructureValid = blockCount > 0;
        flashFile.Validation.ChecksumValid = true;
        flashFile.Validation.SignatureValid = !flashFile.Signature.IsSigned || flashFile.Signature.IsValid;
        flashFile.Validation.IsValid = flashFile.Validation.StructureValid &&
                                       flashFile.Validation.ChecksumValid &&
                                       flashFile.Validation.SignatureValid;
        flashFile.IsVerified = flashFile.Validation.IsValid;
        flashFile.Validation.ValidatedAt = DateTime.UtcNow;
    }

    private FlashBlockType DetermineBlockType(uint address, BlockType blockType)
    {
        if (blockType != BlockType.Reserved && blockType != 0)
        {
            return blockType switch
            {
                BlockType.Code => FlashBlockType.Code,
                BlockType.Calibration => FlashBlockType.Calibration,
                BlockType.Bootloader => FlashBlockType.Bootloader,
                BlockType.Signature => FlashBlockType.Signature,
                BlockType.EEPROM => FlashBlockType.EEPROM,
                BlockType.Flash => FlashBlockType.Flash,
                _ => FlashBlockType.Data
            };
        }

        if (address >= 0x80000000 && address < 0x90000000) return FlashBlockType.Code;
        if (address >= 0xA0000000 && address < 0xB0000000) return FlashBlockType.Calibration;
        if (address >= 0xF0000000 && address < 0xF1000000) return FlashBlockType.Bootloader;
        return FlashBlockType.Data;
    }

    private byte[] DecompressBlock(byte[] compressedData)
    {
        try
        {
            using var input = new MemoryStream(compressedData);
            using var output = new MemoryStream();
            using var gzip = new GZipStream(input, CompressionMode.Decompress);
            gzip.CopyTo(output);
            return output.ToArray();
        }
        catch { return compressedData; }
    }

    private void ExtractMetadata(Simos18FlashFile flashFile)
    {
        flashFile.Metadata["FileType"] = "Simos18 FRF";
        flashFile.Metadata["Version"] = flashFile.Simos18Header.Version.ToString();
        flashFile.Metadata["TargetECU"] = flashFile.Simos18Header.TargetECU;
        flashFile.Metadata["HardwareID"] = flashFile.Simos18Header.HardwareID;
        flashFile.Metadata["BlockCount"] = flashFile.Blocks.Count.ToString();
        flashFile.TargetECU = flashFile.Simos18Header.TargetECU;
        flashFile.TargetHW = flashFile.Simos18Header.HardwareID;

        flashFile.CalculateChecksum();
    }

    internal static uint ComputeCrc32(ReadOnlySpan<byte> data)
    {
        uint crc = 0xFFFFFFFF;
        foreach (var value in data)
        {
            crc ^= value;
            for (var bit = 0; bit < 8; bit++)
                crc = (crc >> 1) ^ ((crc & 1) == 1 ? 0xEDB88320u : 0u);
        }
        return ~crc;
    }
}
