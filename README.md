# Arni Lite

Lightweight Windows client for Arni. This repository currently contains the G0 prototype: a .NET Framework 4.8 WPF window, diagnostics, the desktop sign-in client, a streaming text probe, and a non-interactive PowerShell runner.

The product specification is in `docs/ARNI_LITE_WINDOWS_SPEC.md`. Windows 7 SP1 and Windows Server 2012 have not been tested yet.

## Build

- .NET SDK 8 or newer
- .NET Framework 4.8 targeting pack
- Windows x64

```powershell
dotnet test ArniLite.sln -c Release
dotnet build ArniLite.sln -c Release
src\ArniLite.App\bin\Release\net48\ArniLite.exe --self-check docs\g0-self-check.txt
```

The self-check writes OS, Framework, TLS, DPAPI, PowerShell, and process-stop results. It does not print tokens.

## Sign-in

The client calls `POST /api/desktop/auth/start` on `https://api.arnion.ru` and opens the returned page in the external browser. That bridge lives in the backend and is not deployed with this commit. Until it is, the Yandex and Google buttons open the existing web sign-in pages so the browser path can still be checked.

The session token is stored with DPAPI for the current Windows user. It is not written to the on-screen log or the self-check report.

## Boundaries

`ArniLite.Core`, `ArniLite.Api`, and `ArniLite.Storage` do not reference WPF, the registry, DPAPI, or P/Invoke. Process launch, DPAPI, and OS detection are in `ArniLite.Windows`.
