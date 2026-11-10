# ADR 001: WPF on .NET Framework 4.8

Date: 2026-11-10. Status: accepted for G0, not yet proven on Windows 7 or Server 2012.

The G0 client is a `net48` WPF application. AvalonEdit 6.3.1.120 is referenced because that package still ships a `net462` assembly. The chat surface in this prototype is a plain text log. Markdown rendering is not part of G0.

Shared code is `netstandard2.0` so a later Linux client can reuse it. That split is a boundary, not a promise to ship Linux.

.NET 8/10 was rejected for the executable. Those runtimes do not meet the Windows 7 SP1 and Server 2012 target. This decision stays provisional until the same x64 package is launched on those systems.
