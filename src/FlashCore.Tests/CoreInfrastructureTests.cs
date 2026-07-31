using System.Text;
using FlashCore.Abstractions.Models;
using FlashCore.Core;
using FlashCore.Core.Checksums;
using FlashCore.Core.Journaling;
using FlashCore.Core.Planning;
using FlashCore.Core.Validation;
using Xunit;

namespace FlashCore.Tests;

public sealed class CoreInfrastructureTests
{
    [Fact]
    public void ChecksumService_CalculatesKnownVectors()
    {
        var service = new ChecksumService();
        var data = Encoding.ASCII.GetBytes("123456789");

        Assert.Equal("CBF43926", service.Calculate(data, ChecksumAlgorithm.Crc32));
        Assert.Equal("15E2B0D3C33891EBB0F1EF609EC419420C20E320CE94C65FBC8C3312448EB225",
            service.Calculate(data));
        Assert.True(service.Verify(data, "CBF43926", ChecksumAlgorithm.Crc32));
        Assert.False(service.Verify(data, "00000000", ChecksumAlgorithm.Crc32));
    }

    [Fact]
    public void PreflightValidator_RejectsOverlappingBlocks()
    {
        var file = CreateVerifiedFile(
            new FlashBlock { StartAddress = 0x1000, Size = 4, Data = [1, 2, 3, 4] },
            new FlashBlock { StartAddress = 0x1003, Size = 2, Data = [5, 6] });

        var result = new FlashPreflightValidator(new ChecksumService())
            .Validate(file, new PreflightContext("Simos18", "HW1"));

        Assert.False(result.IsValid);
        Assert.Contains(result.Errors, error => error.Contains("overlaps", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public void PreflightValidator_AcceptsConsistentFile()
    {
        var checksumService = new ChecksumService();
        var block = new FlashBlock { StartAddress = 0x1000, Size = 4, Data = [1, 2, 3, 4] };
        block.Checksum = checksumService.Calculate(block.Data, ChecksumAlgorithm.Crc32);
        var file = CreateVerifiedFile(block);
        file.Checksum = checksumService.Calculate(file.RawData);

        var result = new FlashPreflightValidator(checksumService)
            .Validate(file, new PreflightContext("Simos18", "HW1"));

        Assert.True(result.IsValid);
        Assert.Empty(result.Errors);
    }

    [Fact]
    public void DeviceStateMachine_EnforcesSafeTransitions()
    {
        var machine = new DeviceStateMachine();
        machine.TransitionTo(DeviceState.Connecting);
        machine.TransitionTo(DeviceState.Connected);
        machine.TransitionTo(DeviceState.Identified);

        Assert.Equal(DeviceState.Identified, machine.State);
        Assert.Throws<InvalidOperationException>(() => machine.TransitionTo(DeviceState.Erasing));
    }

    [Fact]
    public async Task OperationCoordinator_RejectsConcurrentOperation()
    {
        using var coordinator = new OperationCoordinator();
        var started = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var first = coordinator.ExecuteAsync("first", async token =>
        {
            started.SetResult();
            await release.Task.WaitAsync(token);
        }, TestContext.Current.CancellationToken);
        await started.Task;

        var second = await coordinator.ExecuteAsync(
            "second", _ => Task.CompletedTask, TestContext.Current.CancellationToken);
        release.SetResult();
        var firstResult = await first;

        Assert.True(firstResult.IsSuccess);
        Assert.False(second.IsSuccess);
        Assert.Equal(OperationErrorCode.Busy, second.Error?.Code);
    }

    [Fact]
    public async Task OperationCoordinator_CancelsAndRetries()
    {
        using var coordinator = new OperationCoordinator();
        var attempts = 0;
        var retried = await coordinator.ExecuteAsync("retry", _ =>
        {
            attempts++;
            return attempts == 1 ? Task.FromException(new IOException("transient")) : Task.CompletedTask;
        }, TestContext.Current.CancellationToken, maxAttempts: 2, isRetryable: exception => exception is IOException);

        using var cancellation = new CancellationTokenSource();
        cancellation.Cancel();
        var cancelled = await coordinator.ExecuteAsync("cancel", token => Task.Delay(100, token), cancellation.Token);

        Assert.True(retried.IsSuccess);
        Assert.Equal(2, attempts);
        Assert.False(cancelled.IsSuccess);
        Assert.Equal(OperationErrorCode.Cancelled, cancelled.Error?.Code);
    }

    [Fact]
    public async Task OperationCoordinator_ClassifiesTimeoutAndDiagnosticResponse()
    {
        using var coordinator = new OperationCoordinator();
        var timedOut = await coordinator.ExecuteAsync(
            "timeout",
            token => Task.Delay(TimeSpan.FromSeconds(1), token),
            TestContext.Current.CancellationToken,
            TimeSpan.FromMilliseconds(10));
        var negativeResponse = await coordinator.ExecuteAsync(
            "diagnostic",
            _ => Task.FromException(new DiagnosticNegativeResponseException(0x34, 0x37, "delay required")),
            TestContext.Current.CancellationToken);

        Assert.Equal(OperationErrorCode.TimedOut, timedOut.Error?.Code);
        Assert.Equal(OperationErrorCode.NegativeResponse, negativeResponse.Error?.Code);
        Assert.Equal<byte?>((byte)0x37, negativeResponse.Error?.NegativeResponseCode);
        Assert.True(negativeResponse.Error?.IsRetryable);
    }

    [Fact]
    public async Task JsonFlashJournal_RoundTripsEntries()
    {
        var path = Path.Combine(Path.GetTempPath(), $"flashcore-journal-{Guid.NewGuid():N}.json");
        try
        {
            var journal = new JsonFlashJournal(path);
            var entry = new FlashJournalEntry("plan", 1, FlashOperation.PreFlash, "validated",
                DateTimeOffset.UtcNow, true);

            await journal.AppendAsync(entry, TestContext.Current.CancellationToken);
            var entries = await journal.ReadAsync(TestContext.Current.CancellationToken);

            var saved = Assert.Single(entries);
            Assert.Equal(entry.PlanId, saved.PlanId);
            Assert.True(saved.Completed);
        }
        finally
        {
            if (File.Exists(path)) File.Delete(path);
        }
    }

    [Fact]
    public void FlashPlan_OrdersSafetyCriticalSteps()
    {
        var file = CreateVerifiedFile(new FlashBlock { StartAddress = 0x1000, Size = 1, Data = [1] });
        var plan = FlashPlan.Create(file, "ABC");

        Assert.Equal(FlashOperation.PreFlash, plan.Steps[0].Operation);
        Assert.Equal(FlashOperation.SecurityAccess, plan.Steps[1].Operation);
        Assert.Equal(FlashOperation.Erasing, plan.Steps[2].Operation);
        Assert.Equal(FlashOperation.Finalizing, plan.Steps[^1].Operation);
    }

    private static FlashFile CreateVerifiedFile(params FlashBlock[] blocks) => new()
    {
        RawData = [1],
        IsVerified = true,
        TargetECU = "Simos18",
        TargetHW = "HW1",
        Blocks = [.. blocks]
    };
}
