# G0 checks

This machine is Windows 10.0.26300, .NET Framework release 534296 (4.8.1 or later). The self-check on 2026-11-10 did not open the main window. Windows 7 SP1 and Windows Server 2012 were not available.

| Check | Result |
| --- | --- |
| Build `net48` WPF executable | Pass. AvalonEdit 6.3.1.120 resolved to `lib/net462` |
| Unit tests for SSE and sign-in links | Pass, 7 tests |
| OS version | Pass. `RtlGetVersion` reported 10.0.26300, so the manifest was not virtualized to 6.2 |
| TLS to `https://api.arnion.ru/api/models` | Pass. HTTP 200 with certificate validation left on. TLS policy stayed at the OS default |
| DPAPI round trip | Pass. The self-check token was removed afterwards |
| PowerShell `-Command -` via stdin | Pass. Exit 0, `arni-lite-ok` and Cyrillic `проверка` were decoded |
| Job object stop | Pass on this OS. A `ping` child was cancelled and no survivors were listed |
| Self-check private bytes | 34,541,568. This is the self-check process, not an idle window after 60 seconds |
| Windows 7 SP1 | Not run |
| Windows Server 2012 | Not run |
| Windows 10 / 11 as separate images | Not run. This OS build is 26300 only |
| Live desktop sign-in | Not run. The bridge is implemented in `arni-backend` and is not deployed |
| Live model stream | Not run |
| Same package on every target OS | Not run |

`docs/g0-self-check.txt` is local output and is gitignored. Do not paste tokens into this table.
