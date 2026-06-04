# Security Policy

SharpADIDNS is a dual-use security and administration tool for AD-integrated DNS. This policy covers security issues in the SharpADIDNS project itself, not the use of the tool against third-party environments.

## Supported Versions

Security fixes are handled for the latest tagged release and the `main` branch on a best-effort basis.

Older tags, modified copies, and forks are not supported.

## Scope

In scope:

- Vulnerabilities in the SharpADIDNS source code.
- Issues in official release artifacts published from this repository.
- Build, packaging, or documentation issues that could cause unsafe or misleading use.
- Repository or release integrity concerns affecting this project.

Out of scope:

- Abuse of expected SharpADIDNS functionality in unauthorized environments.
- Vulnerabilities in Active Directory, DNS, C2 frameworks, operator infrastructure, or third-party deployments.
- Requests to bypass detection, access controls, or organizational policy.
- Social engineering, denial-of-service testing, spam, or attacks against GitHub, maintainers, or users.
- Theoretical reports without a clear impact path.

## Reporting a Vulnerability

Please do not disclose vulnerability details in a public issue, pull request, discussion, gist, social post, or chat transcript.

Preferred reporting channel:

- Use GitHub Private Vulnerability Reporting for this repository, if available:
  <https://github.com/RedteamNotes/SharpADIDNS/security/advisories/new>

Fallback reporting channel:

- If private reporting is not available, open a public issue titled `Security contact request` and include only a brief, non-sensitive summary.

Please include:

- Affected version, tag, commit, or release artifact.
- A clear impact statement.
- Minimal reproduction steps.
- Relevant logs or command output with secrets removed.
- Any suggested remediation, if available.

Do not include production credentials, tokens, private keys, customer data, internal domain names, internal IP addresses, or other sensitive environmental details unless a private channel has been agreed.

## Handling

Reports are reviewed on a best-effort basis. Valid issues may be fixed in `main`, included in the next release, or documented with mitigation guidance depending on impact and complexity.

The project may decline reports that primarily enable unauthorized operation, policy bypass, or offensive tasking support rather than improving the security of SharpADIDNS itself.

## Safe Use

SharpADIDNS is intended for authorized security assessments, lab environments, CTFs, and controlled administration work.

Operators should prefer `--dry-run` and `--backup-to` before write operations, keep engagement authorization and change records, and avoid including sensitive environment details in bug reports.
