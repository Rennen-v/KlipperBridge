# KlipperBridge

This project is licensed under the MIT License; see [LICENSE](LICENSE).

KlipperBridge is a small Windows tray application that provides a loopback endpoint for a QIDI Q2 printer running Fluidd and Moonraker. Its TCP transport selects a physical IPv4 Ethernet or Wi-Fi adapter and binds outbound connections to that adapter, which can preserve printer access when a VPN changes Windows' default route. Network policy may still block the connection.

## Platform and requirements

- Windows 10/11, x64.
- .NET 8 Desktop Runtime.
- A QIDI Q2 (or compatible Klipper/Moonraker printer) reachable over a permitted local network.
- The printer's Fluidd and Moonraker URLs configured locally.

## Configure

Before the first launch, edit `src/KlipperBridge/appsettings.json` and set `FluiddBaseUrl` and `MoonrakerBaseUrl` to the printer's addresses. The checked-in values use the example hostname `qidi-q2.local`; replace it if that name does not resolve on your network.

KlipperBridge listens only on `127.0.0.1:8765`. Do not expose this local proxy to the LAN.

## Build and run

From the repository root in PowerShell:

```powershell
dotnet restore .\src\KlipperBridge\KlipperBridge.csproj
dotnet build .\src\KlipperBridge\KlipperBridge.csproj --no-restore
dotnet run --project .\src\KlipperBridge\KlipperBridge.csproj
```

To publish a framework-dependent Windows executable:

```powershell
dotnet publish .\src\KlipperBridge\KlipperBridge.csproj -c Release -r win-x64 --self-contained false -o .\publish
```

The executable is `publish\KlipperBridge.exe`. The target PC needs the .NET 8 Desktop Runtime. Build output and published binaries are not tracked in Git.

## Tray controls

- Double-click the tray icon or choose **Open Fluidd** to open `http://127.0.0.1:8765/`.
- The menu shows Moonraker connectivity and can trigger an on-demand check.
- Green means Moonraker responded; gray means the printer did not respond; red indicates local proxy startup failure.
- **Run with Windows** writes a per-user startup value under `HKCU`, without administrator privileges.
- **Exit** stops the local HTTP host and releases the listener.

## OrcaSlicer

For Moonraker/Fluidd integration, set the host or API endpoint to `http://127.0.0.1:8765`. Keep OrcaSlicer on the same Windows user session as KlipperBridge. Exact field names can vary by OrcaSlicer version.

## Tests

```powershell
dotnet test .\src\KlipperBridge.Tests\KlipperBridge.Tests.csproj
```

The unit tests cover adapter selection, TCP connection lifecycle, timeout/cancellation handling, and proxy path routing. They do not connect to a real printer.

## Limitations

- Windows IPv4 only; the transport uses the Windows `IP_UNICAST_IF` socket option and binds the selected local address.
- A VPN or endpoint policy can still deny local traffic. KlipperBridge does not change routes, VPN settings, or network filters.
