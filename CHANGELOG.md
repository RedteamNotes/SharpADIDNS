# Changelog

All notable changes to this project. Format roughly follows [Keep a Changelog](https://keepachangelog.com/); versions follow semver intent (pre-1.0, so breaking changes may land in minor bumps -- they are explicitly marked **Breaking**).

Each release also has detailed notes attached on the [Releases page](https://github.com/RedteamNotes/SharpADIDNS/releases).

## [0.3.0] - 2026-05-26

Tier 3: engineering quality + replication awareness + ergonomics.

### Added
- `tests/Tests.cs` -- 16 unit tests for the pure-function surface (`Bin`, `DnsRecord` builders/decoders, `DNS_COUNT_NAME` edges, tombstone FILETIME, `Json.Escape`). No AD required.
- `.github/workflows/build.yml` -- CI on `windows-latest`: build, run tests, upload artifact; on tag push, attach `SharpADIDNS.exe` to the matching GitHub Release.
- `--show-pdc` and `--require-pdc` -- detect the PDC emulator FSMO holder; optionally refuse to write to a non-PDC replica.
- `--append` -- keep all existing records on a node and add one more. Mutually exclusive with `--force`; refuses on tombstoned nodes.
- `@argfile.txt` argument-file expansion -- any token starting with `@` is a path, file is whitespace-tokenized (`#` line comments) and spliced into argv.
- `--color` / `--no-color` -- ANSI color on `[+]` / `[*]` / `[v]` / `[!]` / `[-]` markers; default auto-detects via `Console.IsOutputRedirected`.

### Changed
- `release/SharpADIDNS.exe` is no longer tracked in the repo. CI is the canonical builder; tagged releases get the CI-built binary attached automatically.

## [0.2.0] - 2026-05-26

Tier 2: functional surface parity with Powermad / dnstool.py.

### Added
- `list-zones` action -- enumerate `dnsZone` objects across the three Microsoft DNS partitions (`DomainDnsZones` / `ForestDnsZones` / `System`).
- Native `SRV` / `MX` / `PTR` record builders (`--type SRV --srv-priority N --srv-weight N --srv-port N`, `--type MX --mx-pref N`, `--type PTR`). No more `--raw` for these types.
- `--format json` for `enum` / `query` / `list-zones`. Single-line JSON on stdout; logger output suppressed in JSON mode so stdout stays valid.
- `query` now prints owner + DACL summary (explicit + inherited ACE counts; explicit ACEs by default, `-v` expands inherited).
- Enum filters: `--filter-type A,AAAA,...`, `--filter-name <glob>` (case-insensitive, `*` and `?`), `--only-tombstoned` / `--no-tombstoned`.

### Changed
- `SummaryLine` and `Decode` for `dnsRecord` now render structured SRV (`priority weight port target`) and MX (`preference exchange`) instead of `<N bytes>`.

## [0.1.0] - 2026-05-26

Tier 1: "safe to use in serious engagements" threshold reached.

### Added
- Credential input safety:
  - `--password-stdin` -- read from stdin
  - `--password-env <VAR>` -- read from env var
  - Interactive masked prompt when `--username` is given without a source
  - `--allow-cleartext-password` silences the warning when `--password <pwd>` is used
- `--dry-run` for `add` / `disable` / `remove` -- bind read-only, print the planned operation, no AD writes.
- `--backup-to <file>` -- before each destructive op, append a JSON line with the existing state. Restore via `add --raw <base64> --force`.
- High-risk confirmation prompts (skip with `-y` / `--yes`). Triggers: any `remove`, `add --name "*"`, `add --name wpad|isatap`, `add --force` on a tombstoned node.
- README **Detection surface** section: Windows event IDs (5136 / 5137 / 5141 / 4662 / 4624), Microsoft Defender for Identity detections, SIEM patterns, object-level IOCs, replication visibility, OPSEC checklist.

### Changed
- `--password <pwd>` now emits a warning about its visibility in process listings unless `--allow-cleartext-password` is set.

### Security
- Closes the practical credential-on-argv leak that was the biggest OPSEC issue in v0.0.x.

## [0.0.5] - 2026-05-26

### Added
- `Program.Version` constant -- single source of truth for the release version.
- `-V` / `--version` flag -- prints `SharpADIDNS v<ver>` and exits 0.
- `--help` header now reads `SharpADIDNS v<ver>  --  ...` so the binary self-identifies.

## [0.0.4] - 2026-05-25

### Added
- Project URL and contact line in the `--help` header.

### Changed
- **Breaking**: `--domain-dn` flag renamed to `--dn` across the parser, help text, and examples. Wrapper scripts must be updated. No alias is provided.

## [0.0.3] - 2026-05-25

### Changed
- Example identifiers tightened: `fileserver` -> `sccm`, `dc01` -> `dc`, `alice` -> `redpen`, `P@ss` -> `RedteamN0t3s.` in both README and embedded `--help` examples.
- README examples rewritten using POSIX backslash (`\`) line continuation, one fenced block per example with a one-line prose intro. The earlier PowerShell-backtick layout was reverted because it didn't paste cleanly into bash/zsh.
- `--username 'redteamnotes\redpen'` is single-quoted in examples so the backslash survives bash/zsh parsing.

## [0.0.1] - 2026-05-25

### Added
- Initial release.
- Actions: `enum`, `query`, `add`, `disable`, `remove`.
- Native record builders: `A`, `AAAA`, `CNAME`, `TXT`, plus raw base64 blob injection.
- `DNS_RPC_RECORD` and `DNS_COUNT_NAME` encoders/decoders implemented per [MS-DNSP].
- LDAP / LDAPS bind, current-process token or explicit `--username` / `--password`.
- Distinct exit codes (0 / 1 / 2 / 3 / 4) for scripting.
- Single-file .NET Framework 4.x executable, no third-party dependencies, built with the in-box `csc.exe`.

[0.3.0]: https://github.com/RedteamNotes/SharpADIDNS/releases/tag/v0.3.0
[0.2.0]: https://github.com/RedteamNotes/SharpADIDNS/releases/tag/v0.2.0
[0.1.0]: https://github.com/RedteamNotes/SharpADIDNS/releases/tag/v0.1.0
[0.0.5]: https://github.com/RedteamNotes/SharpADIDNS/releases/tag/v0.0.5
[0.0.4]: https://github.com/RedteamNotes/SharpADIDNS/releases/tag/v0.0.4
[0.0.3]: https://github.com/RedteamNotes/SharpADIDNS/releases/tag/v0.0.3
[0.0.1]: https://github.com/RedteamNotes/SharpADIDNS/releases/tag/v0.0.1
