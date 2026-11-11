# Workspace UI

An alternative native WPF shell for Arni Lite, based on `main` at `8ab60dc`.
The executable still targets .NET Framework 4.8; no runtime UI dependencies were added.

## Layout and behavior

- One searchable sidebar groups chats by their project folder. Groups collapse; the selected chat is highlighted.
- **New chat** starts a general conversation without a project. The **+** beside a project starts a conversation in that project.
- Opening an existing chat restores its saved project. A missing project is reported rather than retaining another chat's working directory.
- The compact header shows the current chat and project. Model selection lives beside the composer. Arni always works as an agent, including conversations without a project; it can answer ordinary questions without calling a tool. There is no separate chat mode.
- **Files** opens an optional, resizable panel. On narrower windows, it temporarily takes the place of the sidebar.
- File previews are local and read-only, limited to 1 MiB / 500 lines. They do not call a model or launch a file association. This iteration lists the first 80 entries at the project root; nested folders can be opened in Explorer.
- Chat and navigation lists use WPF container virtualization/recycling. Streaming UI updates are batched every 60 ms.
- **Stop** cancels the active agent turn and preserves received text. Model and project changes are disabled during a turn.
- The existing sign-in bridge and session approval setting remain available. The account card shows identity, account type, free steps and applicable balance / 5-hour / weekly limits. It refreshes after a turn or manually, independently of catalog loading, and retains the last snapshot if a refresh fails.
- Known OpenRouter endpoints refused for Arni are excluded from model selection. A new harness restriction clears that model without retrying on a different model; the user sees an explanation in Russian.
- Tool details are collapsed by default; code blocks in normal answers stay visible.

## Keyboard

| Shortcut | Action |
| --- | --- |
| Ctrl+N | New general chat |
| Ctrl+K | Find a chat |
| Ctrl+B | Toggle project navigation |
| Ctrl+Shift+E | Toggle files |
| Enter | Send |
| Shift+Enter | New line |

## Build and validation

```powershell
dotnet build src/ArniLite.App/ArniLite.App.csproj -c Release -m:1 -nr:false -o dist/workspace-ui
dotnet test tests/ArniLite.UnitTests/ArniLite.UnitTests.csproj -c Release -m:1 -nr:false
dotnet test tests/ArniLite.AppTests/ArniLite.AppTests.csproj -c Release -m:1 -nr:false
```

The WPF tests use a dispatcher, isolated temporary histories, a fake HTTP handler and a process runner that rejects execution. They cover project restoration, general/project chat creation, search in collapsed groups, local preview, cancellation with partial history, and avoiding duplicate streamed text across a tool round.

Manual checks use Windows Computer Use. Sending paid requests, real sign-in, old Windows versions, high DPI, RDP and before/after performance benchmarks are outside these checks. Compatibility with those environments remains a release gate.

Validation on 2026-11-11: Release build passed, 24 core/API/storage tests and 8 WPF session tests passed. Added regressions cover nullable account budgets, remaining free / paid limits, independent account and catalog loading, quota refresh after a turn, tools in a general conversation, and rejected models without automatic fallback. Computer Use confirmed the new-chat screen, selected conversation, project restoration, chat search, opening/closing the file panel and local text preview. Window resizing through Computer Use did not produce a smaller captured window, so narrow-window behavior still needs a manual check.

The account fix was also checked with the existing signed-in session: the email, free account type, remaining free steps and local reset time render successfully. No inference request was submitted during this check.
