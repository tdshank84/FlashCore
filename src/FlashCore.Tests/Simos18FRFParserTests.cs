using System.Buffers.Binary;
using FlashCore.Abstractions.Models;
using FlashCore.ECU.Simos18.Parsers;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace FlashCore.Tests;

public class Simos18FRFParserTests
{
    [Fact]
    public async Task ParseAsync_ParsesValidatedBlock()
    {
        var bytes = new byte[68];
        BinaryPrimitives.WriteUInt32LittleEndian(bytes.AsSpan(0, 4), 0x465246);
        BinaryPrimitives.WriteUInt32LittleEndian(bytes.AsSpan(4, 4), 48);
        BinaryPrimitives.WriteUInt32LittleEndian(bytes.AsSpan(8, 4), 20);
        BinaryPrimitives.WriteUInt32LittleEndian(bytes.AsSpan(12, 4), 1);
        "test.frf\0SIMOS18\0HW1\0"u8.CopyTo(bytes.AsSpan(16));
        BinaryPrimitives.WriteUInt32LittleEndian(bytes.AsSpan(48, 4), 0x80000000);
        BinaryPrimitives.WriteUInt32LittleEndian(bytes.AsSpan(52, 4), 4);
        BinaryPrimitives.WriteUInt32LittleEndian(bytes.AsSpan(56, 4), 0xB63CFBCD);
        BinaryPrimitives.WriteUInt32LittleEndian(bytes.AsSpan(60, 4), 1);
        new byte[] { 1, 2, 3, 4 }.CopyTo(bytes, 64);

        var path = Path.GetTempFileName();
        try
        {
            await File.WriteAllBytesAsync(path, bytes, TestContext.Current.CancellationToken);
            var parser = new Simos18FRFParser(NullLogger<Simos18FRFParser>.Instance);
            var result = await parser.ParseAsync(path, TestContext.Current.CancellationToken);

            Assert.Single(result.Blocks);
            Assert.Equal(0x80000000u, result.Blocks[0].StartAddress);
            Assert.Equal(FlashBlockType.Code, result.Blocks[0].BlockType);
            Assert.Equal(new byte[] { 1, 2, 3, 4 }, result.Blocks[0].Data);
        }
        finally
        {
            File.Delete(path);
        }
    }

    [Fact]
    public async Task ParseAsync_RejectsTruncatedHeader()
    {
        var path = Path.GetTempFileName();
        try
        {
            await File.WriteAllBytesAsync(path, [0x46, 0x52, 0x46], TestContext.Current.CancellationToken);
            var parser = new Simos18FRFParser(NullLogger<Simos18FRFParser>.Instance);
            await Assert.ThrowsAsync<InvalidDataException>(
                () => parser.ParseAsync(path, TestContext.Current.CancellationToken));
        }
        finally
        {
            File.Delete(path);
        }
    }

    [Fact]
    public async Task ParseAsync_RejectsInvalidBlockChecksum()
    {
        var bytes = new byte[68];
        BinaryPrimitives.WriteUInt32LittleEndian(bytes.AsSpan(0, 4), 0x465246);
        BinaryPrimitives.WriteUInt32LittleEndian(bytes.AsSpan(4, 4), 48);
        BinaryPrimitives.WriteUInt32LittleEndian(bytes.AsSpan(8, 4), 20);
        BinaryPrimitives.WriteUInt32LittleEndian(bytes.AsSpan(12, 4), 1);
        "test.frf\0SIMOS18\0HW1\0"u8.CopyTo(bytes.AsSpan(16));
        BinaryPrimitives.WriteUInt32LittleEndian(bytes.AsSpan(48, 4), 0x80000000);
        BinaryPrimitives.WriteUInt32LittleEndian(bytes.AsSpan(52, 4), 4);
        BinaryPrimitives.WriteUInt32LittleEndian(bytes.AsSpan(56, 4), 0xDEADBEEF);
        BinaryPrimitives.WriteUInt32LittleEndian(bytes.AsSpan(60, 4), 1);
        new byte[] { 1, 2, 3, 4 }.CopyTo(bytes, 64);

        var path = Path.GetTempFileName();
        try
        {
            await File.WriteAllBytesAsync(path, bytes, TestContext.Current.CancellationToken);
            var parser = new Simos18FRFParser(NullLogger<Simos18FRFParser>.Instance);
            var exception = await Assert.ThrowsAsync<InvalidDataException>(
                () => parser.ParseAsync(path, TestContext.Current.CancellationToken));
            Assert.Contains("CRC32", exception.Message);
        }
        finally
        {
            File.Delete(path);
        }
    }

    [Fact]
    public async Task ParseAsync_RejectsDeterministicMalformedCorpus()
    {
        var directory = Path.Combine(Path.GetTempPath(), $"flashcore-frf-corpus-{Guid.NewGuid():N}");
        Directory.CreateDirectory(directory);
        var random = new Random(1808);
        var parser = new Simos18FRFParser(NullLogger<Simos18FRFParser>.Instance);
        try
        {
            for (var index = 0; index < 64; index++)
            {
                var bytes = new byte[random.Next(0, 256)];
                random.NextBytes(bytes);
                if (bytes.Length >= 4) BinaryPrimitives.WriteUInt32LittleEndian(bytes, 0x465246);
                var path = Path.Combine(directory, $"case-{index:D2}.frf");
                await File.WriteAllBytesAsync(path, bytes, TestContext.Current.CancellationToken);
                await Assert.ThrowsAnyAsync<Exception>(
                    () => parser.ParseAsync(path, TestContext.Current.CancellationToken));
            }
        }
        finally
        {
            Directory.Delete(directory, recursive: true);
        }
    }
}
