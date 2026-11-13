# Planned work

- [ ] macOS Lite client on **Avalonia**, reusing Core/Api/Storage. Start with projects, streaming chat and one local command; measure startup, memory and responsiveness on a real Mac before expanding. Implement Keychain storage, shell/process cancellation, native dialogs/media, signing/notarization and updates as platform integrations. Keep the legacy Windows WPF client supported.
- [ ] Optional browser controlled by the agent on modern Windows; HTML rendering alone does not satisfy this task.
- [ ] Remote MCP client: one `streamable-http` URL plus an optional `Authorization` header. Session is `initialize`, `notifications/initialized`, `tools/list`, confirmed `tools/call`. No stdio, OAuth, or Lite-as-server. Contract and pass bar are in `docs/AGENT_TOOLS.md`.
