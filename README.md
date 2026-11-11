# Arni Lite

Lightweight Windows client for Arni, built with WPF and .NET Framework 4.8. It includes project-based conversations, a local agent, streaming responses, account limits, file previews and per-user x86/x64 installers.

The product specification is in `docs/ARNI_LITE_WINDOWS_SPEC.md`. Windows 7 SP1 and Windows Server 2012 have not been tested yet.

## Build

- .NET SDK 8 or newer
- .NET Framework 4.8 targeting pack
- Windows x64

```powershell
dotnet test ArniLite.sln -c Release -m:1 -nr:false
dotnet build ArniLite.sln -c Release -m:1 -nr:false
src\ArniLite.App\bin\Release\net48\ArniLite.exe --self-check docs\g0-self-check.txt
```

The self-check writes OS, Framework, TLS, DPAPI, PowerShell, and process-stop results. It does not print tokens.

## Sign-in

The single sign-in button calls `POST /api/desktop/auth/start` on `https://api.arnion.ru` and opens the returned page in the external browser. Select a provider there and enter the code shown in Arni Lite. The client receives the session through the desktop sign-in bridge.

The session token is stored with DPAPI for the current Windows user. It is not written to the on-screen log or the self-check report.

See [the workspace interface](docs/WORKSPACE_UI.md) for navigation, keyboard shortcuts and validation details, and [release 0.1.2](docs/releases/0.1.2.md) for this release's changes.

Version `0.1.2` adds [language switching, support logs and native media previews](docs/LANGUAGES_MEDIA.md).

It also includes [optional local Whisper base dictation and signed update checks](docs/DICTATION_UPDATES.md). The speech model/engine download only when requested; installation of an application update requires the user's Install and restart button. Future macOS work on Avalonia is tracked in [tasks](docs/TASKS.md).

## Boundaries

`ArniLite.Core`, `ArniLite.Api`, and `ArniLite.Storage` do not reference WPF, the registry, DPAPI, or P/Invoke. Process launch, DPAPI, and OS detection are in `ArniLite.Windows`.
