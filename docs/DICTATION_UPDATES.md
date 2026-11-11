# Local dictation and signed updates

Development: `0.1.2-preview`. These changes are not in the published `0.1.1` installers.

## Dictation

Settings → Dictation downloads the multilingual **Whisper base** model (not base.en) and pinned whisper.cpp CPU engine only when the user chooses Download. No Python, browser engine, cloud transcription or resident speech service is required. Existing conversations remain usable during download. Recognition loads the model in a short-lived process only for each recording.

- Model: `ggerganov/whisper.cpp`, revision `5359861c739e955e79d9a303bcbc70fb988958b1`, `ggml-base.bin`, 147,951,465 bytes.
- Model SHA256: `60ed5bc3dd14eea856493d334349b405782ddcaf0028d4b5df4088345fba2efe`.
- Engine: upstream stable `v1.9.2`, CPU-only `whisper-bin-x64.zip` or `whisper-bin-Win32.zip`. A 32-bit client on 64-bit Windows can use the 64-bit speech process.
- x64 archive SHA256: `49dcc16de826f20bd53d44f947a1ae49dfa81f86cad67a64d80820cb192d674a`.
- x86 archive SHA256: `de170719aebcb4794d695d449e179002db1fe03b862f21f5c34b2909a7cf8f22`.

Downloads stream to temporary files, are size-bounded and checked against these pinned hashes before publication. Extraction uses a flat executable/DLL allowlist. Cancellation/failure leaves no partial file as an installed model. Settings can remove the selected runtime and model.

The message composer has Dictate / Finish recording. The microphone is opened only after that explicit action, using WinMM at 16 kHz, mono PCM. Choose the default microphone or another device and Russian, English, Kazakh or automatic detection independently of the interface language. Recording is bounded to two minutes. Transcription runs with at most four CPU threads, without GPU, and adds editable text to the current draft. It never sends the draft automatically. Chat/context switching is locked during recording and recognition so the text cannot land in another conversation. Downloading the model does not lock the chat.

Audio stays in memory during capture. During recognition a generated temporary WAV and transcript are removed in `finally`, including cancellation; abandoned known recording files are cleaned at the next application start. Audio, recognized text, subprocess output and microphone names are not included in technical logs or support archives. Cancelling recording discards it.

Engine compatibility is separate from client compatibility. On this Windows 11 x64 host the pinned native engine works. Windows 7/Server 2012, 32-bit OS, older instruction sets, microphone hardware and Russian/Kazakh accuracy still need real-machine acceptance checks. Unsupported engines show an error without changing the rest of the client. Third-party notices: [THIRD_PARTY_SPEECH.md](THIRD_PARTY_SPEECH.md).

## Updates

Settings → Updates offers automatic checks at startup, at most once per day, a manual check, optional preview releases, release notes, download/cancel and Install and restart. A network failure does not block startup. A separate unauthenticated HTTP client is used for GitHub/model downloads: no Arni session token is forwarded.

The client examines up to 30 recent releases from `ssadkov/arni-lite`. An eligible release must include `update.json`, a detached binary RSA/SHA256 `update.json.sig`, and the exact referenced installer. The public key is embedded in Core. The manifest specifies schema, SemVer, minimum Windows version and x86/x64 installer names/sizes/hashes. Downgrades, mismatched tags, unsupported OS/architecture and unauthenticated legacy releases are excluded. Download origins are constructed from the fixed repository; arbitrary URLs in release metadata are ignored. Signature/hash errors never start an installer.

Install and restart is enabled only while agent, dictation and sign-in operations are idle. Before shutdown, the client saves the current chat and a one-time restart draft. It rechecks the installer checksum, starts the per-user Inno Setup installer and exits normally. The `/ARNIUPDATE=1` installer waits up to 60 seconds for the app mutex to disappear, updates the current directory and restarts the client. It does not force-close a running agent or reboot Windows. Normal manual installation is unchanged. Installation failures rely on Inno Setup's normal handling; no custom transactional rollback is claimed.

### Publishing an updatable release

1. Build both architectures and installers with the same version as the application. Inno accepts `/DAppVersion=0.1.2-preview` and `/DAppNumericVersion=0.1.2` for preview filenames/version metadata.
2. Run `tools/New-UpdateManifest.ps1 -Version <version> -InstallerDirectory <directory>` on the signing machine.
3. Upload both exact installers, `update.json` and `update.json.sig` to the matching `v<version>` GitHub release. Mark previews as prereleases. Keep the signed minimum-Windows value accurate.

The initial RSA key was generated locally. Only `src/ArniLite.Core/UpdatesPublicKey.xml` belongs in source control. The private key is DPAPI-protected for the current Windows user in `%LOCALAPPDATA%\Arnion\ArniLite\ReleaseKeys\updates.dpapi`; it is not in the app, repository, logs or release assets. Signing currently requires this Windows user/machine; arrange a secure recoverable backup before relying on it for ongoing releases. Do not regenerate a key for each release: already installed clients trust the original public key. CI signing and key rotation are future release-infrastructure work.

The existing public 0.1.1 release has no signed update metadata and is intentionally skipped. This feature becomes useful to installed users once a release containing the updater is distributed and a subsequent signed release is published.

## Validation

- Tests cover signed release acceptance, tampering/wrong keys, version/tag matching, SemVer/channel/OS/architecture policy, bounded/hash-verified downloads, cancellation/cleanup, migrated preferences, restart drafts and dictation's context lock.
- Speech tests cover WAV headers, extraction allowlisting, local CLI arguments, language selection, silence avoidance and cleanup on success/cancellation.
- A native net48 smoke test downloaded/verified/extracted the pinned engine, verified the model, transcribed the public upstream JFK sample through `WhisperRecognizer` and `ProcessRunner`, and observed zero temporary recordings afterward. It also verified a manifest produced by the release signing script against the application's embedded key.
- UI checks and installer compilation are recorded with the completed implementation. Live microphone capture and a full replacement/restart of an installed client remain separate acceptance checks.
