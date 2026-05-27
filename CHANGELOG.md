# Changelog

All notable changes to this project. Format roughly follows [Keep a Changelog](https://keepachangelog.com/); versions follow semver intent (pre-1.0, so breaking changes may land in minor bumps -- they are explicitly marked **Breaking**).

Each release also has detailed notes attached on the [Releases page](https://github.com/RedteamNotes/SharpADIDNS/releases).

## [0.5.4] - 2026-05-27

Audit-grade polish: stdout discipline, JSON dry-run, per-verb help, correlation_id.

### Added
- `--c2 --dry-run` now emits a JSON receipt with `"result":"would_do"` instead of `[dry-run]` text (the previous behavior corrupted the JSON-only-on-stdout invariant under `--c2`).
- `correlation_id` field (process-scoped GUID via `Guid.NewGuid().ToString("D")`) is the first field in every top-level JSON line: action receipts, dry-run receipts, `query`/`enum`/`list-zones`/`script_summary`/backup. Lets operators group all output from one execute-assembly invocation in downstream processing.
- `--continue-on-error` -- pure alias for `--script-on-error continue` (long form kept).
- `--flag=value` GNU syntax accepted alongside `--flag value` (space form). First-`=` split; values containing `=` (base64 padding, DN strings) survive intact.
- Per-verb `--help` drill-in: `SharpADIDNS.exe add --help` (or `enum --help`, etc.) prints a verb-specific USAGE line + only the sections relevant to that verb, with a footer pointing at the full reference and `docs/RECIPES.md`.
- Tests: new `TestCorrelationIdShape` verifies the GUID format and stability. 19 unit tests total (was 18).

### Changed
- `--filter-type` is now accumulative: `--filter-type A --filter-type AAAA` is equivalent to `--filter-type A,AAAA`. CSV form unchanged.
- EXAMPLES block in `--help` reflowed: max line went from 247 to 87 columns; added the recommended Sliver C2 invocation pattern; dropped redundant CNAME / AAAA-LDAPS examples (full versions stay in README); EXAMPLES header now points at `docs/RECIPES.md`.

### Fixed
- `Logger.Info` / `Logger.Ok` / `Logger.Verbose` route to **stderr** when `opt.Format == "json"` (set via `Logger.JsonMode` in `Program.Main`). Previously `Backup.Snapshot`, `Replication.CheckBeforeAction`, and `DispatchAction` verbose lines could leak human-readable text onto stdout under `--c2`, corrupting the JSON receipt stream.
- `RunScript` exception net broadened to catch `COMException` (the generic, non-AD-specific COM fault that fires when LDAP path DNS lookup fails before any AD-specific error code) and a final `Exception` net. Without this, `--continue-on-error` aborted the entire script on the first non-`DirectoryServicesCOMException`.

## [0.5.0] - 2026-05-27

Batch mode + tool-side OPSEC depth on top of the v0.4.0 C2 baseline.

### Added
- `--script "stmt1; stmt2; ..."` -- single `execute-assembly` invocation runs N actions. Statements `;`-separated; each is action + flags applied on top of outer flags. Outer cannot also specify a top-level action.
- `--script-on-error halt|continue` -- per-statement failure behavior (default `halt`).
- `--mimic-aging` -- set the `dnsRecord` Timestamp field (offset 20, U32 LE, hours since 1601-01-01 UTC) to "now" instead of `0` (static). Defeats the `Timestamp=0 in dynamic-update zone` IOC documented in the Detection surface section. No effect on `--raw` (caller owns that blob). `BuildHeader` + all `Build*` methods got a `uint timestamp = 0` parameter.
- `--set-owner <SID|name>` -- after `add` (create/replace/append), call `node.ObjectSecurity.SetOwner` with `SecurityIdentifier` (when value starts with `S-1-`) or `NTAccount`. Receipt's `set_owner` field reports `{requested, result, applied_to_sid, error}`. Failure is non-fatal -- the record write stays, the owner change is reported as failed. Requires `WriteOwner` on the node.
- `docs/RECIPES.md` -- 8 cookbook-style end-to-end scenarios (DNS recon, single-record-add-with-rollback, stealth add, wildcard injection, SRV relay via `--append`, batch via `--script`, engagement cleanup from JSONL backup, DACL pre-flight check).
- Tests: `TestAgingTimestampNow` + `TestBuildA with mimic-aging timestamp`. 18 unit tests total (was 16).

### Changed
- `RunQuery` sets `node.Options.SecurityMasks = Owner | Group | Dacl` before bind, then calls `RefreshCache` with the explicit attribute list. Batches the per-property LDAP roundtrips into one search. No new flag.

## [0.4.0] - 2026-05-27

First-class support for in-memory execution via Sliver `execute-assembly`. Re-centers the OPSEC threat model: Tier-1 protections (which assumed local-shell context with Sysmon EID 1 visibility) become FUD under `execute-assembly` (assembly args go via CLR reflection, not the sacrificial process's command line), while disk writes, C2 channel bandwidth, and sacrificial-process spawns become the primary surface.

### Added
- `--c2` umbrella flag. Implies (each overridable): `--allow-cleartext-password --yes --no-color --quiet --format json --backup-to -`. Applied in `Program.Main` immediately after `Options.Parse`.
- `--password-base64 <b64>` -- UTF-8 password decoded from base64. The only password source that survives Sliver multi-layer command parsing without escape gymnastics when the password contains `'`, `"`, `$`, or spaces. (`--password-stdin` is unusable in `execute-assembly`; `--password-env` requires setting env on the sacrificial process.)
- `--backup-to -` (stdout sentinel) -- JSONL snapshot emitted on stdout instead of a file. No disk artifact. The JSON line carries a top-level `_type:"backup"` marker. Suppressed in `--format json` mode (receipt already carries `previous_state`).
- JSON receipts for `add` / `disable` / `remove` under `--format json`: structured single-line `{action, result, operation, dn, zone, name, record, previous_state, reverse}` replacing the `[+]` human lines. `previous_state.records_base64` is canonical for restoration. `reverse` is a single-command undo for `add` create only; `null` for replace/append/disable/remove (multi-step undo via `previous_state.records_base64` + `add --raw ... --force`). `reverse` deliberately omits `--username`/`--password*` to avoid leaking credentials into the receipt.
- README section **Using via Sliver `execute-assembly`** -- recommended invocation pattern, receipt schema, sacrificial-process choice guidance, pitfalls (stdin sources don't work, env vars hard to set, argfile pointless on target, file backups land in sacrificial CWD), and explicit "what `--c2` does NOT change" (DC audit + MDI still fire).

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

[0.5.4]: https://github.com/RedteamNotes/SharpADIDNS/releases/tag/v0.5.4
[0.5.0]: https://github.com/RedteamNotes/SharpADIDNS/releases/tag/v0.5.0
[0.4.0]: https://github.com/RedteamNotes/SharpADIDNS/releases/tag/v0.4.0
[0.3.0]: https://github.com/RedteamNotes/SharpADIDNS/releases/tag/v0.3.0
[0.2.0]: https://github.com/RedteamNotes/SharpADIDNS/releases/tag/v0.2.0
[0.1.0]: https://github.com/RedteamNotes/SharpADIDNS/releases/tag/v0.1.0
[0.0.5]: https://github.com/RedteamNotes/SharpADIDNS/releases/tag/v0.0.5
[0.0.4]: https://github.com/RedteamNotes/SharpADIDNS/releases/tag/v0.0.4
[0.0.3]: https://github.com/RedteamNotes/SharpADIDNS/releases/tag/v0.0.3
[0.0.1]: https://github.com/RedteamNotes/SharpADIDNS/releases/tag/v0.0.1
