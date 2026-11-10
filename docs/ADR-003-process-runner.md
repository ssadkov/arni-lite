# ADR 003: PowerShell runner and process stop

Date: 2026-11-10. Status: accepted for G0. Encoding and Windows 7 job behavior are not fully proven.

G0 runs Windows PowerShell as `powershell.exe -NoLogo -NoProfile -NonInteractive -Command -` and writes the command to stdin. `-EncodedCommand` is not used. `ExecutionPolicy Bypass` is not used. The G0 window does not expose a free-form command box; the check button runs a fixed command.

The process is created suspended, placed in a Win32 job with kill-on-close, then resumed. If job assignment fails, the runner still starts the command and says so. Stop then walks parent process IDs and terminates the recorded tree. That fallback can miss a process that reparents itself, so the result lists any survivors instead of claiming the tree is gone.

Stdout and stderr are read together and capped. The selected folder is not a sandbox: a shell command runs as the current user. G0 does not ask the model to run commands yet, so the permission dialog from the specification is not in this build.

A job object does not contain hostile code. Children can still escape through services or scheduled tasks. The UI must keep saying that Stop may leave children when the job was not assigned.
