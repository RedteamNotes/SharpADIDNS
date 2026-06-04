# Security Policy

SharpADIDNS is a dual-use tool for authorized work with AD-integrated DNS. This policy only covers security issues in this repository, its source code, and official release artifacts.

It does not authorize testing or modifying systems without explicit permission.

## Reporting

Please do not disclose security details in public issues, pull requests, discussions, gists, or social posts.

Use GitHub Private Vulnerability Reporting if available:

<https://github.com/RedteamNotes/SharpADIDNS/security/advisories/new>

If private reporting is not available, open a public issue titled `Security contact request` and include only a brief, non-sensitive summary.

Remove credentials, tokens, private keys, customer data, internal domain names, IP addresses, and other sensitive details before reporting.

## Scope

In scope: bugs in SharpADIDNS source code, official release artifacts, build or packaging issues, and repository integrity concerns.

Out of scope: unauthorized use of the tool, third-party AD or DNS misconfigurations, operator infrastructure issues, detection bypass requests, and reports without a clear impact path.

## Use

Use SharpADIDNS only in authorized assessments, labs, CTFs, or controlled administration work. Prefer `--dry-run` and `--backup-to` before write operations.
