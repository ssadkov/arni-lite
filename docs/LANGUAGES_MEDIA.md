# Languages, diagnostics and media preview

Included in **0.1.2**, following the **0.1.1** release.

## Interface and preferences

The Settings window offers Russian, English and Kazakh. Language changes update live labels, account limits and model prices without recreating the main window or calling a model. Conversation contents and file contents remain user data.

Strings are maintained in three matching resource catalogs in `ArniLite.Core`. WPF observes a shared localization binding; Core, Api and Storage remain independent of WPF. Language and text wrapping are stored in `preferences.json` in the application's data directory. A missing or damaged preferences file falls back to Russian and enabled wrapping.

Text preview wraps by default. The preview's checkbox and Settings control the same saved preference. Disabling wrapping restores horizontal scrolling for code and tables.

## Version and support logs

Settings → About shows the assembly's version and architecture. Settings → Logs shows technical events and exports a ZIP that users can send manually to support.

The archive has a fixed allowlist: environment/version information, the current structured event log and its rotated predecessor. It excludes the token store, conversation histories, tool journal, legacy crash log, file contents and exception messages. Logs record event/phase, exception type, HRESULT and HTTP status when available. Logs and export reads are bounded.

## Audio and video

Recognized local media files open in a native WPF player with play/pause, stop, seeking, volume and an explicit external-player action. Selecting a file does not start playback. Closing or hiding the preview releases the player. Media files are passed to the native streaming decoder rather than read into the text preview buffer; the text preview size limit does not apply to them.

Decoding depends on the Windows media components and codecs available on the machine. Unsupported formats show a localized explanation and can be opened externally. No codec pack, browser runtime or new media library is installed by this client.

## Validation

- 33 Core/Api/Storage tests and 10 WPF tests passed: catalog parity, saved preferences and corruption recovery, live localization without network requests, privacy of diagnostic archives, media classification and no automatic playback, plus the existing agent/account regressions.
- Windows Computer Use verified Russian/English/Kazakh settings, About version, logs, and successful ZIP export.
- A separate native WPF decoder check on this Windows 11 machine opened generated PCM WAV and H.264 MP4 samples, observed advancing playback time and seeking, and called pause/stop/close without errors.
- Windows 7, Server 2012, RDP, high DPI and additional codecs are still separate compatibility checks.

## HTML and agent browsing

HTML remains a text preview in this iteration. A browser for the agent to navigate/read/interact with pages is a separate feature from rendering a generated HTML file. A future modern-Windows browser integration can be optional and loaded only when needed; the legacy Windows client must retain its lightweight startup path.
