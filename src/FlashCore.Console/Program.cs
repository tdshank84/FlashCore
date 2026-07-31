using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Spectre.Console;
using FlashCore.Abstractions.Interfaces;
using FlashCore.Abstractions.Models;
using FlashCore.ECU.Simos18;
using FlashCore.ECU.Simos18.Configuration;
using FlashCore.Core.Artifacts;
using FlashCore.Core.Transport;
using FlashCore.ECU.Simos18.Planning;
using FlashCore.ECU.QuickApps;
using System.Text.Json;

namespace FlashCore.Console;

class Program
{
    private static ServiceProvider _serviceProvider = null!;

    static async Task<int> Main(string[] args)
    {
        var services = new ServiceCollection();
        ConfigureServices(services);
        _serviceProvider = services.BuildServiceProvider();

        if (args.Length > 0) return await RunCommandAsync(args);

        DisplayBanner();

        while (true)
        {
            var choice = AnsiConsole.Prompt(
                new SelectionPrompt<string>()
                    .Title("[yellow]Select an option:[/]")
                    .PageSize(10)
                    .AddChoices([
                        "1. Flash Simos18 ECU",
                        "2. Read ECU Info",
                        "3. Verify Flash",
                        "4. Read Memory",
                        "5. Dry-run Flash Plan",
                        "6. Read-only HIL Check",
                        "7. Exit"
                    ]));

            switch (choice)
            {
                case "1. Flash Simos18 ECU":
                    await FlashSimos18ECU();
                    break;
                case "2. Read ECU Info":
                    await ReadECUInfo();
                    break;
                case "3. Verify Flash":
                    await VerifyFlash();
                    break;
                case "4. Read Memory":
                    await ReadMemory();
                    break;
                case "5. Dry-run Flash Plan":
                    await DryRunFlashPlan();
                    break;
                case "6. Read-only HIL Check":
                    await RunReadOnlyHilCheck();
                    break;
                case "7. Exit":
                    AnsiConsole.Markup("[green]Goodbye![/]");
                    return 0;
            }
        }
    }

    private static async Task<int> RunCommandAsync(string[] args)
    {
        try
        {
            switch (args[0].ToLowerInvariant())
            {
                case "--version" or "version":
                    System.Console.WriteLine("FlashCore 1.0.8");
                    return 0;
                case "recovery":
                    return await RunRecoveryCommandAsync(args[1..]);
                case "replay":
                    return await RunReplayCommandAsync(args[1..]);
                case "plan":
                    return await RunPlanCommandAsync(args[1..]);
                case "ecu":
                    return await RunEcuCommandAsync(args[1..]);
                case "apps":
                    return await RunAppsCommandAsync(args[1..]);
                case "help" or "--help" or "-h":
                    WriteCommandHelp();
                    return 0;
                default:
                    throw new ArgumentException($"Unknown command '{args[0]}'. Run 'flashcore help'.");
            }
        }
        catch (Exception exception) when (exception is not OperationCanceledException)
        {
            System.Console.Error.WriteLine($"Error: {exception.Message}");
            return 1;
        }
    }

    private static async Task<int> RunRecoveryCommandAsync(string[] args)
    {
        RequireArguments(args, 5,
            "recovery create <output.zip> <ecu-id> <firmware-sha256> <kind=path> [kind=path ...]");
        if (!string.Equals(args[0], "create", StringComparison.OrdinalIgnoreCase))
            throw new ArgumentException("Only 'recovery create' is supported.");
        var artifacts = args[4..].Select(ParseRecoveryArtifact).ToArray();
        var manifest = await RecoveryPackage.CreateAsync(args[1], new(args[2], args[3], artifacts));
        System.Console.WriteLine(JsonSerializer.Serialize(manifest, new JsonSerializerOptions { WriteIndented = true }));
        return 0;
    }

    private static async Task<int> RunReplayCommandAsync(string[] args)
    {
        RequireArguments(args, 2, "replay inspect <transcript.json>");
        if (!string.Equals(args[0], "inspect", StringComparison.OrdinalIgnoreCase))
            throw new ArgumentException("Only 'replay inspect' is supported.");
        using var replay = await TranscriptReplayTransport.LoadAsync(args[1]);
        System.Console.WriteLine($"Valid transcript with {replay.Remaining} exchanges.");
        return 0;
    }

    private static async Task<int> RunPlanCommandAsync(string[] args)
    {
        RequireArguments(args, 2, "plan <firmware.frf> <profile.json>");
        using var device = _serviceProvider.GetRequiredService<Simos18FlashDevice>();
        var file = await device.ParseFlashFileAsync(args[0]);
        var profile = await new Simos18ProfileLoader().LoadAsync(args[1], requireSignature: false);
        var report = new Simos18DryRunAnalyzer().Analyze(file, profile);
        System.Console.WriteLine(JsonSerializer.Serialize(report, new JsonSerializerOptions { WriteIndented = true }));
        return report.IsReady ? 0 : 2;
    }

    private static async Task<int> RunEcuCommandAsync(string[] args)
    {
        RequireArguments(args, 2, "ecu info <interface> [bridgeleg|socketcan|simulation]");
        if (!string.Equals(args[0], "info", StringComparison.OrdinalIgnoreCase))
            throw new ArgumentException("Only the read-only 'ecu info' command is supported.");
        using var device = _serviceProvider.GetRequiredService<Simos18FlashDevice>();
        var custom = new Dictionary<string, object> { ["ReadOnlyHilMode"] = true };
        if (args.Length > 2) custom["TransportKind"] = args[2];
        await device.ConnectAsync(new DeviceConnectionParams
        {
            PortName = args[1],
            BaudRate = 250000,
            Protocol = ProtocolType.CAN,
            CustomParams = custom
        });
        var info = await device.GetDeviceInfoAsync();
        System.Console.WriteLine(JsonSerializer.Serialize(info, new JsonSerializerOptions { WriteIndented = true }));
        await device.DisconnectAsync();
        return 0;
    }

    private static async Task<int> RunAppsCommandAsync(string[] args)
    {
        RequireArguments(args, 2, "apps <list|validate|inspect> <catalog.json> [category]");
        var catalog = await QuickAppCatalogLoader.LoadAsync(args[1]);
        switch (args[0].ToLowerInvariant())
        {
            case "validate":
                System.Console.WriteLine($"Valid catalog: {catalog.Vehicle}, {catalog.AppCount} apps, policy={catalog.ExecutionPolicy}.");
                return 0;
            case "inspect":
                System.Console.WriteLine(JsonSerializer.Serialize(new
                {
                    catalog.CatalogVersion,
                    catalog.Vehicle,
                    catalog.Market,
                    ModelYears = $"{catalog.FirstModelYear}-{catalog.LastModelYear}",
                    catalog.ExecutionPolicy,
                    catalog.CompatibilityKeys,
                    CategoryCount = catalog.Categories.Count,
                    catalog.AppCount
                }, new JsonSerializerOptions { WriteIndented = true }));
                return 0;
            case "list":
                var categories = args.Length > 2
                    ? catalog.Categories.Where(category => string.Equals(category.Name, args[2], StringComparison.OrdinalIgnoreCase))
                    : catalog.Categories;
                var selected = categories.ToArray();
                if (selected.Length == 0) throw new ArgumentException($"Unknown Quick App category '{args[2]}'.");
                foreach (var category in selected)
                {
                    System.Console.WriteLine(category.Name);
                    foreach (var app in category.Apps) System.Console.WriteLine($"  - {app}");
                }
                return 0;
            default:
                throw new ArgumentException($"Unknown apps command '{args[0]}'.");
        }
    }

    private static RecoveryArtifact ParseRecoveryArtifact(string value)
    {
        var separator = value.IndexOf('=');
        if (separator <= 0 || separator == value.Length - 1)
            throw new ArgumentException($"Recovery artifact '{value}' must use kind=path.");
        return new RecoveryArtifact(value[..separator], value[(separator + 1)..]);
    }

    private static void RequireArguments(string[] args, int count, string usage)
    {
        if (args.Length < count) throw new ArgumentException($"Usage: flashcore {usage}");
    }

    private static void WriteCommandHelp() => System.Console.WriteLine(
        """
        FlashCore 1.0.8 commands
          flashcore ecu info <interface> [bridgeleg|socketcan|simulation]
          flashcore apps list <catalog.json> [category]
          flashcore apps inspect <catalog.json>
          flashcore apps validate <catalog.json>
          flashcore plan <firmware.frf> <profile.json>
          flashcore replay inspect <transcript.json>
          flashcore recovery create <output.zip> <ecu-id> <firmware-sha256> <kind=path> [...]
        """);

    private static void ConfigureServices(ServiceCollection services)
    {
        services.AddLogging(configure =>
        {
            configure.ClearProviders();
            configure.AddConsole();
            configure.SetMinimumLevel(LogLevel.Information);
        });

        services.AddScoped<Simos18FlashDevice>();
        services.AddScoped<IFlashDevice>(sp => sp.GetRequiredService<Simos18FlashDevice>());
    }

    private static void DisplayBanner()
    {
        AnsiConsole.Write(
            new Panel(
                new Markup("[cyan]FlashCore v1.0.8 - Experimental Simos18 Research Tool[/]\n" +
                          "[yellow].NET 10 - Independent implementation[/]\n" +
                          "[green]BridgeLEG Transport | UDS Diagnostics[/]\n" +
                          "[blue]Hardware programming remains bench-validation only[/]")
                .Centered())
            {
                Border = BoxBorder.Double,
                Padding = new Padding(2, 1),
                Header = new PanelHeader("[red]⚠️  RESEARCH USE ONLY  ⚠️[/]")
            });
        AnsiConsole.WriteLine();
    }

    private static async Task FlashSimos18ECU()
    {
        AnsiConsole.Markup("[red]⚠️  WARNING: This will perform the Simos18 exploit[/]");
        AnsiConsole.Markup("\n[red]This can permanently damage your ECU if used incorrectly![/]\n");

        if (!AnsiConsole.Confirm("[yellow]Do you have a backup and recovery method?[/]")) return;
        if (!AnsiConsole.Confirm("[yellow]Are you sure you want to continue?[/]")) return;

        var filePath = AnsiConsole.Ask<string>("[cyan]Enter path to FRF file:[/]");
        if (!File.Exists(filePath))
        {
            AnsiConsole.Markup("[red]File not found![/]");
            return;
        }

        var defaultPort = OperatingSystem.IsWindows() ? "COM3" : "/dev/cu.SLAB_USBtoUART";
        var port = AnsiConsole.Ask<string>("[cyan]Enter Macchina A0 serial port:[/]", defaultPort);
        var eraseRoutineText = AnsiConsole.Ask<string>(
            "[cyan]Enter the validated erase routine ID for this ECU (for example 0xFF00):[/]");
        var hardwareNumber = AnsiConsole.Ask<string>("[cyan]Enter the exact ECU hardware number:[/]");
        var bootloaderIdentifier = AnsiConsole.Ask<string>("[cyan]Enter the exact bootloader identifier (SC8/SCG):[/]");
        var loaderAddressText = AnsiConsole.Ask<string>("[cyan]Enter the validated loader address (hex):[/]", "0x80000000");
        var sampleModeDidText = AnsiConsole.Ask<string>("[cyan]Enter the validated sample-mode DID (hex):[/]");
        var unlockLoaderPath = AnsiConsole.Ask<string>("[cyan]Enter the validated unlock-loader path:[/]");
        var supplyVoltage = AnsiConsole.Ask<decimal>("[cyan]Enter the measured supply voltage:[/]");
        var confirmation = AnsiConsole.Ask<string>(
            $"[red]Type '{PhysicalExecutionPolicy.ConfirmationText}' to enable this physical operation:[/]");
        var tracePath = AnsiConsole.Ask<string>("[cyan]Request/response trace path:[/]",
            ArtifactStorage.CreatePath("trace", "jsonl"));
        var journalPath = AnsiConsole.Ask<string>("[cyan]Recovery journal path:[/]",
            ArtifactStorage.CreatePath("journal", "json"));
        var profile = new Simos18EcuProfile
        {
            Name = $"{hardwareNumber}-{bootloaderIdentifier}",
            HardwareNumber = hardwareNumber,
            BootloaderIdentifier = bootloaderIdentifier,
            EraseRoutineId = ParseHexUInt16(eraseRoutineText),
            LoaderAddress = ParseHexUInt32(loaderAddressText),
            SampleModeDid = ParseHexUInt16(sampleModeDidText),
            UnlockLoaderPath = unlockLoaderPath
        };

        using var device = _serviceProvider.GetRequiredService<Simos18FlashDevice>();

        try
        {
            await AnsiConsole.Status()
                .Start("Connecting to Simos18 ECU...", async ctx =>
                {
                    var connected = await device.ConnectAsync(new DeviceConnectionParams
                    {
                        PortName = port,
                        BaudRate = 250000,
                        Protocol = ProtocolType.CAN,
                        CustomParams = new Dictionary<string, object>
                        {
                            ["EcuProfile"] = profile,
                            ["EnablePhysicalProgramming"] = true,
                            ["SafetyConfirmation"] = confirmation,
                            ["SupplyVoltage"] = supplyVoltage,
                            ["TracePath"] = tracePath,
                            ["JournalPath"] = journalPath
                        }
                    });

                    if (!connected) throw new Exception("Failed to connect to ECU");

                    var info = await device.GetDeviceInfoAsync();
                    ctx.Status = $"Connected to: {info.Model} (HW: {info.HardwareVersion})";
                    await Task.Delay(1000);

                    var flashFile = await device.ParseFlashFileAsync(filePath);

                    ctx.Status = "Flashing ECU...";

                    device.StatusUpdated += (s, status) =>
                    {
                        AnsiConsole.MarkupLine($"\n[blue]▶[/] {status}");
                    };

                    var progress = new Progress<FlashProgress>(p =>
                    {
                        AnsiConsole.Console.Write($"\r[{DateTime.Now:HH:mm:ss}] {p.OperationName}: {p.OverallProgress:P0}");
                    });

                    var success = await device.FlashAsync(flashFile, progress);

                    AnsiConsole.WriteLine();
                    if (success)
                    {
                        AnsiConsole.Markup("\n[green]✓ Flash completed successfully![/]");
                        AnsiConsole.Markup("\n[yellow]ECU will reset. Wait 10 seconds before starting the car.[/]");
                    }
                    else
                    {
                        AnsiConsole.Markup("\n[red]✗ Flash failed![/]");
                    }
                });
        }
        catch (Exception ex)
        {
            AnsiConsole.WriteException(ex);
        }

        AnsiConsole.Markup("\nPress any key to continue...");
        System.Console.ReadKey();
    }

    private static async Task ReadECUInfo()
    {
        var port = AnsiConsole.Ask<string>("[cyan]Enter Macchina A0 serial port:[/]",
            OperatingSystem.IsWindows() ? "COM3" : "/dev/cu.SLAB_USBtoUART");

        using var device = _serviceProvider.GetRequiredService<Simos18FlashDevice>();

        try
        {
            await AnsiConsole.Status()
                .Start("Reading ECU information...", async ctx =>
                {
                    var connected = await device.ConnectAsync(new DeviceConnectionParams
                    {
                        PortName = port,
                        BaudRate = 250000,
                        Protocol = ProtocolType.CAN
                    });

                    if (!connected) throw new Exception("Failed to connect to ECU");

                    var info = await device.GetDeviceInfoAsync();

                    var table = new Table();
                    table.Border(TableBorder.Rounded);
                    table.Title("[cyan]Simos18 ECU Information[/]");

                    table.AddColumn("Property");
                    table.AddColumn("Value");

                    table.AddRow("Device ID", info.DeviceId);
                    table.AddRow("Manufacturer", info.Manufacturer);
                    table.AddRow("Model", info.Model);
                    table.AddRow("Hardware Version", info.HardwareVersion);
                    table.AddRow("Firmware Version", info.FirmwareVersion);
                    table.AddRow("Sample Mode", info.HardwareVersion.Contains("X13") ? "[green]Active[/]" : "[yellow]Inactive[/]");
                    table.AddRow("Last Connected", info.LastConnected.ToString("yyyy-MM-dd HH:mm:ss"));

                    AnsiConsole.Write(table);
                });
        }
        catch (Exception ex)
        {
            AnsiConsole.WriteException(ex);
        }

        AnsiConsole.Markup("\nPress any key to continue...");
        System.Console.ReadKey();
    }

    private static async Task VerifyFlash()
    {
        var port = AnsiConsole.Ask<string>("[cyan]Enter Macchina A0 serial port:[/]",
            OperatingSystem.IsWindows() ? "COM3" : "/dev/cu.SLAB_USBtoUART");
        var filePath = AnsiConsole.Ask<string>("[cyan]Enter path to FRF file to verify:[/]");

        if (!File.Exists(filePath))
        {
            AnsiConsole.Markup("[red]File not found![/]");
            return;
        }

        using var device = _serviceProvider.GetRequiredService<Simos18FlashDevice>();

        try
        {
            await AnsiConsole.Status()
                .Start("Verifying flash...", async ctx =>
                {
                    var connected = await device.ConnectAsync(new DeviceConnectionParams
                    {
                        PortName = port,
                        BaudRate = 250000,
                        Protocol = ProtocolType.CAN
                    });

                    if (!connected) throw new Exception("Failed to connect to ECU");

                    var flashFile = await device.ParseFlashFileAsync(filePath);

                    ctx.Status = "Verifying...";
                    var success = await device.VerifyAsync(flashFile);

                    if (success)
                        AnsiConsole.Markup("\n[green]✓ Verification passed![/]");
                    else
                        AnsiConsole.Markup("\n[red]✗ Verification failed![/]");
                });
        }
        catch (Exception ex)
        {
            AnsiConsole.WriteException(ex);
        }

        AnsiConsole.Markup("\nPress any key to continue...");
        System.Console.ReadKey();
    }

    private static async Task ReadMemory()
    {
        var port = AnsiConsole.Ask<string>("[cyan]Enter Macchina A0 serial port:[/]",
            OperatingSystem.IsWindows() ? "COM3" : "/dev/cu.SLAB_USBtoUART");
        var address = AnsiConsole.Ask<uint>("[cyan]Enter address in hex (e.g., 0x80000000):[/]");
        var size = AnsiConsole.Ask<uint>("[cyan]Enter size in bytes:[/]");

        using var device = _serviceProvider.GetRequiredService<Simos18FlashDevice>();

        try
        {
            await AnsiConsole.Status()
                .Start("Reading memory...", async ctx =>
                {
                    var connected = await device.ConnectAsync(new DeviceConnectionParams
                    {
                        PortName = port,
                        BaudRate = 250000,
                        Protocol = ProtocolType.CAN
                    });

                    if (!connected) throw new Exception("Failed to connect to ECU");

                    ctx.Status = $"Reading 0x{size:X} bytes at 0x{address:X8}...";

                    var data = await device.ReadMemoryAsync(address, size);

                    ctx.Status = $"Read {data.Length} bytes";

                    var hexDump = HexDump(data, address);
                    AnsiConsole.WriteLine(hexDump);
                });
        }
        catch (Exception ex)
        {
            AnsiConsole.WriteException(ex);
        }

        AnsiConsole.Markup("\nPress any key to continue...");
        System.Console.ReadKey();
    }

    private static async Task RunSimos18Exploit()
    {
        AnsiConsole.MarkupLine("[red]⚠ This operation can permanently damage or disable the ECU.[/]");
        AnsiConsole.MarkupLine("[yellow]Use only on hardware you own and have a tested recovery method for.[/]");

        if (!AnsiConsole.Confirm("[yellow]Is the vehicle/bench connected to a regulated power supply?[/]"))
            return;
        if (!AnsiConsole.Confirm("[yellow]Do you have a verified backup and recovery method?[/]"))
            return;
        if (!AnsiConsole.Confirm("[red]Run the full Simos18 exploit chain now?[/]", false))
            return;

        var defaultPort = OperatingSystem.IsWindows() ? "COM3" : "/dev/cu.SLAB_USBtoUART";
        var port = AnsiConsole.Ask("[cyan]Enter Macchina A0 serial port:[/]", defaultPort);

        using var device = _serviceProvider.GetRequiredService<Simos18FlashDevice>();
        device.StatusUpdated += (_, status) =>
            AnsiConsole.MarkupLine($"[blue]▶[/] {Markup.Escape(status)}");

        try
        {
            var connected = await device.ConnectAsync(new DeviceConnectionParams
            {
                PortName = port,
                BaudRate = 250000,
                Protocol = ProtocolType.CAN
            });
            if (!connected)
                throw new InvalidOperationException("Failed to connect to the Macchina A0 or ECU.");

            var progress = new Progress<FlashProgress>(p =>
                AnsiConsole.MarkupLine(
                    $"[cyan]{Markup.Escape(p.OperationName)}[/] [green]{p.OverallProgress:P0}[/]"));

            var success = await device.RunExploitAsync(progress);
            AnsiConsole.MarkupLine(success
                ? "[green]✓ Exploit chain completed and sample mode was verified.[/]"
                : "[red]✗ Exploit chain failed. Do not cycle power until ECU state and recovery options are assessed.[/]");
        }
        catch (Exception ex)
        {
            AnsiConsole.WriteException(ex);
        }
        finally
        {
            await device.DisconnectAsync();
        }

        AnsiConsole.Markup("\nPress any key to continue...");
        System.Console.ReadKey();
    }

    private static string HexDump(byte[] data, uint startAddress)
    {
        var sb = new System.Text.StringBuilder();
        var bytesPerLine = 16;

        for (int i = 0; i < data.Length; i += bytesPerLine)
        {
            var address = startAddress + (uint)i;
            sb.Append($"{address:X8}  ");

            for (int j = 0; j < bytesPerLine; j++)
            {
                if (i + j < data.Length)
                    sb.Append($"{data[i + j]:X2} ");
                else
                    sb.Append("   ");
            }

            sb.Append("  ");

            for (int j = 0; j < bytesPerLine; j++)
            {
                if (i + j < data.Length)
                {
                    var c = (char)data[i + j];
                    sb.Append(char.IsControl(c) ? '.' : c);
                }
                else
                {
                    sb.Append(' ');
                }
            }

            sb.AppendLine();
        }

        return sb.ToString();
    }

    private static async Task DryRunFlashPlan()
    {
        var filePath = AnsiConsole.Ask<string>("[cyan]FRF file path:[/]");
        var profilePath = AnsiConsole.Ask<string>("[cyan]JSON ECU profile path:[/]");
        var requireSignature = AnsiConsole.Confirm("[yellow]Require a trusted profile signature?[/]", true);
        string? publicKey = null;
        if (requireSignature)
        {
            var publicKeyPath = AnsiConsole.Ask<string>("[cyan]Trusted RSA public-key PEM path:[/]");
            publicKey = await File.ReadAllTextAsync(publicKeyPath);
        }
        using var device = _serviceProvider.GetRequiredService<Simos18FlashDevice>();
        var profile = await new Simos18ProfileLoader().LoadAsync(profilePath, requireSignature, publicKey);
        var file = await device.ParseFlashFileAsync(filePath);
        var report = new Simos18DryRunAnalyzer().Analyze(file, profile);
        AnsiConsole.MarkupLine(report.IsReady ? "[green]Dry run passed.[/]" : "[red]Dry run failed.[/]");
        AnsiConsole.MarkupLine($"Profile: [cyan]{Markup.Escape(report.Profile)}[/]");
        AnsiConsole.MarkupLine($"SHA-256: [cyan]{report.FileSha256}[/]");
        AnsiConsole.MarkupLine($"Bytes: [cyan]{report.TotalBytes}[/]; Steps: [cyan]{report.Plan.Steps.Count}[/]");
        foreach (var error in report.Errors) AnsiConsole.MarkupLine($"[red]Error:[/] {Markup.Escape(error)}");
        foreach (var warning in report.Warnings) AnsiConsole.MarkupLine($"[yellow]Warning:[/] {Markup.Escape(warning)}");
    }

    private static async Task RunReadOnlyHilCheck()
    {
        var port = AnsiConsole.Ask<string>("[cyan]CAN/serial interface:[/]",
            OperatingSystem.IsWindows() ? "COM3" : "/dev/cu.SLAB_USBtoUART");
        using var device = _serviceProvider.GetRequiredService<Simos18FlashDevice>();
        await device.ConnectAsync(new DeviceConnectionParams
        {
            PortName = port,
            BaudRate = 250000,
            Protocol = ProtocolType.CAN,
            CustomParams = new Dictionary<string, object> { ["ReadOnlyHilMode"] = true }
        });
        var info = await device.GetDeviceInfoAsync();
        AnsiConsole.MarkupLine($"[green]Read-only HIL check passed:[/] {Markup.Escape(info.Model)} / {Markup.Escape(info.HardwareVersion)}");
        await device.DisconnectAsync();
    }

    private static ushort ParseHexUInt16(string value)
    {
        var text = value.StartsWith("0x", StringComparison.OrdinalIgnoreCase) ? value[2..] : value;
        return ushort.TryParse(text, System.Globalization.NumberStyles.HexNumber, null, out var parsed)
            ? parsed
            : throw new ArgumentException($"'{value}' is not a valid 16-bit hexadecimal value.");
    }

    private static uint ParseHexUInt32(string value)
    {
        var text = value.StartsWith("0x", StringComparison.OrdinalIgnoreCase) ? value[2..] : value;
        return uint.TryParse(text, System.Globalization.NumberStyles.HexNumber, null, out var parsed)
            ? parsed
            : throw new ArgumentException($"'{value}' is not a valid 32-bit hexadecimal value.");
    }
}
