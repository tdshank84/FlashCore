# FlashCore

[![Build](https://github.com/tdshank84/FlashCore/actions/workflows/build.yml/badge.svg)](https://github.com/tdshank84/FlashCore/actions/workflows/build.yml)
![Version](https://img.shields.io/badge/version-1.0.8-blue)

FlashCore is an experimental .NET 10 console application for communicating with
Volkswagen Simos18 ECUs through a Macchina A0.

FlashCore is an independent research implementation. Published SA2 and Simos18
research used by this project is credited in [THIRD_PARTY_NOTICES.md](THIRD_PARTY_NOTICES.md).

The project version is fixed at **1.0.8** and will only change when explicitly
requested by the repository owner.

## Supported CAN hardware

FlashCore requires a compatible CAN interface to communicate with an ECU.

The Macchina A0 running BridgeLEG firmware is currently the only interface
implemented and tested by this project:

- **Macchina A0 with BridgeLEG** — Connects over USB and delegates CAN and
  ISO-TP framing to the device. Install the
  [BridgeLEG firmware](https://github.com/Switchleg1/esp32-isotp-ble-bridge/tree/BridgeLEG/main).

The following transport foundations are also available but require compatible
hardware, operating-system support, and driver validation:

- **Raspberry Pi with a Seeed Studio CAN-FD HAT** — Uses the Linux CAN_ISOTP
  SocketCAN transport and can serve as a dedicated bench or recovery tool.
- **Tactrix OpenPort 2.0 and compatible J2534 interfaces** — Use the injectable
  J2534 channel transport. Windows generally provides the simplest driver
  support; Linux and macOS require compatible third-party drivers such as
  [tdshank84/j2534](https://github.com/tdshank84/j2534).

Other J2534 devices may eventually work through the same transport layer.
Successful flashing requires precise ISO-TP transmit timing, including
`stmin_tx` configuration. Interfaces or drivers that cannot control this timing
may support diagnostics but fail during sustained flash transfers.

The stock A0 ELM-style firmware is not supported. The host serial link runs at
250000 baud; BridgeLEG handles 500 kbit/s CAN and ISO-TP on the device. Vehicle
CAN is expected on OBD-II pins 6 and 14.

Programming is fail-closed: FlashCore requires an explicitly supplied erase
routine identifier that has been validated for the connected ECU/bootloader.
The exploit also requires a raw `FL_8V0906259H__0001.unlock.bin` loader and a
matching `.sha256` sidecar; placeholder loader data is never generated.

## Build and test

```sh
dotnet restore FlashCore.sln
dotnet test FlashCore.sln
dotnet run --project src/FlashCore.Console
```

On macOS, the serial port usually resembles `/dev/cu.SLAB_USBtoUART`. On
Windows, it resembles `COM3`.

## Core safeguards

`FlashCore.Core` provides a device state machine, atomic operation coordination,
cancellation and timeout handling, retry classification, structured operation
results, SHA-256/CRC32 checksums, flash preflight validation, immutable flash
plans, and optional JSON recovery journals. Transport implementations use the
shared `ITransport` contract.

To persist a journal for a programming session, provide a writable path through
`DeviceConnectionParams.CustomParams["JournalPath"]`. The journal records the
completed safety-critical stages and programmed blocks.

Physical programming is fail-closed. It requires an explicit `Simos18EcuProfile`
that matches the live hardware and bootloader identifiers, the exact confirmation
text `ENABLE PHYSICAL ECU PROGRAMMING`, a measured supply voltage above the
profile minimum, and validated erase-routine, loader-address, and sample-mode DID
values. `CustomParams["TracePath"]` enables a JSON-lines request/response trace.
During programming, FlashCore schedules TesterPresent messages, retries transient
TransferData failures according to the profile, reads every programmed block back,
and refuses to reset the ECU when verification fails.

Security-access levels are configured separately for SBOOT, bootloader, and
programming stages. Physical workflow execution additionally requires a profile
marked `ProtocolValidated`; simulation does not bypass the physical policy. Trace
files redact security-access and TransferData payloads by default, are bounded and
rotated, and use owner-only permissions on Unix systems. A live voltage provider
can be supplied through `CustomParams["SupplyVoltageMonitor"]`; otherwise the
explicitly configured voltage is polled throughout the destructive operation.

ECU profiles can be stored as JSON and optionally verified with a trusted RSA
signature; the schema is in `schemas/simos18-profile.schema.json`. The console
provides a dry-run flash-plan command and a read-only HIL mode that blocks UDS
erase, security-access, download, and transfer services. Strict transcript replay
supports deterministic offline testing from approved request/response captures.
Runtime traces and journals default to the operating system's local application
data directory, redact VIN/security/transfer content, and support retention limits.

The build treats enabled .NET analyzer warnings as errors, audits transitive NuGet
dependencies, and runs CodeQL on pushes, pull requests, and a weekly schedule.
Version tags and manual release-workflow runs produce framework-dependent Windows,
Linux, Raspberry Pi/Linux ARM64, and macOS artifacts.

## Command line

Running `FlashCore.Console` without arguments opens the interactive interface.
Offline and read-only automation is also available:

```text
flashcore ecu info <interface> [bridgeleg|socketcan|simulation]
flashcore apps list <catalog.json> [category]
flashcore apps inspect <catalog.json>
flashcore apps validate <catalog.json>
flashcore plan <firmware.frf> <profile.json>
flashcore replay inspect <transcript.json>
flashcore recovery create <output.zip> <ecu-id> <firmware-sha256> <kind=path> [...]
```

Recovery packages accept only `ecu-info`, `flash-plan`, `trace`, `profile`,
`journal`, and `checksums` artifact kinds. They include a versioned JSON manifest
with the SHA-256 and length of every file. Firmware images and secret/key material
are deliberately excluded.

## Quick App catalog

`data/quick-apps/vw-golf-mk7-us.json` contains the working catalog for the
US-market Volkswagen Golf Mk7/Mk7.5 model years 2015–2021. It records more than
150 feature names across access, lighting, mirrors, dashboard, driver assistance,
comfort, infotainment, powertrain, and workshop categories. The schema is
`schemas/quick-app-catalog.schema.json`.

Catalog entries are intentionally `catalog-only`: they do not contain proprietary
third-party coding sequences and cannot write to an ECU. An executable profile
must identify exact control-unit addresses, part numbers, software versions,
original values, requested values, rollback values, and independent validation
evidence. FlashCore will not infer or guess these values.

## Safety

The parser and transport have offline tests, but actual ECU programming has not
been bench-validated by this repository. Use a regulated power supply, maintain
a recovery method, and validate read-only ECU identification before attempting
any programming operation.
