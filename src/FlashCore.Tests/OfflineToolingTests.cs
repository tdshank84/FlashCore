using System.IO.Compression;
using System.Text.Json;
using FlashCore.Abstractions.Interfaces;
using FlashCore.Core.Artifacts;
using FlashCore.ECU.Simos18;
using FlashCore.ECU.QuickApps;
using Xunit;

namespace FlashCore.Tests;

public sealed class OfflineToolingTests
{
    [Fact]
    public async Task RecoveryPackageContainsHashedManifestAndAllowedArtifact()
    {
        var directory = CreateTemporaryDirectory();
        try
        {
            var plan = Path.Combine(directory, "plan.json");
            var output = Path.Combine(directory, "recovery.zip");
            await File.WriteAllTextAsync(plan, "{\"safe\":true}", TestContext.Current.CancellationToken);

            var manifest = await RecoveryPackage.CreateAsync(output,
                new("ECU-DEMO", "ABC", [new("flash-plan", plan)]), TestContext.Current.CancellationToken);

            Assert.Single(manifest.Entries);
            using var archive = ZipFile.OpenRead(output);
            Assert.NotNull(archive.GetEntry("manifest.json"));
            Assert.NotNull(archive.GetEntry("artifacts/flash-plan/plan.json"));
        }
        finally { Directory.Delete(directory, recursive: true); }
    }

    [Theory]
    [InlineData("BridgeLeg", Simos18TransportKind.BridgeLeg)]
    [InlineData("SocketCan", Simos18TransportKind.SocketCan)]
    [InlineData("Simulation", Simos18TransportKind.Simulation)]
    public void TransportFactoryHonorsExplicitSelection(string configured, Simos18TransportKind expected)
    {
        var parameters = new DeviceConnectionParams
        {
            PortName = "test",
            CustomParams = new Dictionary<string, object> { ["TransportKind"] = configured }
        };
        Assert.Equal(expected, Simos18TransportFactory.ResolveKind(parameters));
    }

    [Fact]
    public async Task RecoveryPackageRejectsUnknownArtifactKind()
    {
        var directory = CreateTemporaryDirectory();
        try
        {
            var file = Path.Combine(directory, "firmware.bin");
            await File.WriteAllBytesAsync(file, [1, 2, 3], TestContext.Current.CancellationToken);
            await Assert.ThrowsAsync<InvalidDataException>(() => RecoveryPackage.CreateAsync(
                Path.Combine(directory, "recovery.zip"),
                new("ECU", "ABC", [new("firmware", file)]),
                TestContext.Current.CancellationToken));
        }
        finally { Directory.Delete(directory, recursive: true); }
    }

    [Fact]
    public async Task GolfQuickAppCatalogLoadsAndRemainsNonExecutable()
    {
        var root = FindRepositoryRoot();
        var catalog = await QuickAppCatalogLoader.LoadAsync(
            Path.Combine(root, "data", "quick-apps", "vw-golf-mk7-us.json"),
            TestContext.Current.CancellationToken);

        Assert.Equal("US", catalog.Market);
        Assert.Equal("catalog-only", catalog.ExecutionPolicy);
        Assert.True(catalog.AppCount >= 150);
        Assert.Contains(catalog.Categories.SelectMany(category => category.Apps),
            app => app == "Traffic Jam Assist");
    }

    [Fact]
    public void QuickAppCatalogRejectsDuplicateNames()
    {
        var catalog = new QuickAppCatalog("1", "Golf", "US", 2015, 2021, "catalog-only", [],
            [new("One", ["Duplicate"]), new("Two", ["duplicate"])]);
        Assert.Contains(catalog.Validate(), error => error.Contains("Duplicate", StringComparison.Ordinal));
    }

    private static string FindRepositoryRoot()
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);
        while (directory is not null && !File.Exists(Path.Combine(directory.FullName, "FlashCore.sln")))
            directory = directory.Parent;
        return directory?.FullName ?? throw new DirectoryNotFoundException("Repository root was not found.");
    }

    private static string CreateTemporaryDirectory()
    {
        var path = Path.Combine(Path.GetTempPath(), $"flashcore-offline-{Guid.NewGuid():N}");
        Directory.CreateDirectory(path);
        return path;
    }
}
