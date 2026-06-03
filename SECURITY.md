# Security Policy

SharpADIDNS is a dual-use security and administration tool for AD-integrated
DNS. This policy covers vulnerabilities in this project itself. It does not
provide authorization to test, attack, or modify systems that you do not own or
do not have explicit permission to assess.

## Supported Versions

This project does not maintain long-term support branches. Security fixes are
handled for the latest tagged release and the `main` branch.

| Version | Security support |
| ------- | ---------------- |
| Latest tagged release | Supported |
| `main` branch | Supported on a best-effort basis |
| Older tags or forks | Not supported |

## Scope

In scope:

- Vulnerabilities in the SharpADIDNS source code.
- Vulnerabilities in official release artifacts published from this repository.
- Build, packaging, or documentation issues that could materially mislead users
  into unsafe operation.
- Supply-chain or repository-integrity concerns affecting this project.

Out of scope:

- Abuse of expected SharpADIDNS functionality against systems without
  authorization.
- Vulnerabilities in third-party environments, AD deployments, DNS
  configurations, C2 frameworks, or operator infrastructure.
- Requests for help bypassing detection, access controls, or organizational
  policy in environments where you are not authorized.
- Social engineering, physical attacks, spam, denial-of-service testing, or
  attacks against GitHub, maintainers, or project users.
- Theoretical reports without a plausible impact path.

## Reporting a Vulnerability

Please do not disclose vulnerability details in a public issue, pull request,
discussion, gist, social post, or chat transcript.

Preferred reporting channel:

- Use GitHub Private Vulnerability Reporting for this repository, if available:
  <https://github.com/RedteamNotes/SharpADIDNS/security/advisories/new>

Fallback reporting channel:

- If private reporting is not available, open a public issue titled
  `Security contact request` and include only a brief, non-sensitive summary.
  A maintainer can then arrange a private channel.

Please include:

- Affected version, tag, commit, or release artifact.
- A clear impact statement and the security boundary being crossed.
- Minimal reproduction steps or proof-of-concept details.
- Relevant logs, command output, or screenshots with secrets removed.
- Whether the issue is already publicly known or under active exploitation.
- Any suggested remediation, if you have one.

Do not include production credentials, tokens, private keys, customer data,
domain names, IP addresses, or other sensitive environmental details unless the
maintainer explicitly asks for them and a private channel has been agreed.

## Handling and Disclosure

Expected response targets:

- Acknowledgement: within 3 business days.
- Initial triage: within 7 business days.
- Fix plan: based on severity, exploitability, affected versions, and release
  complexity.

Target remediation windows after confirmation:

| Severity | Target |
| -------- | ------ |
| Critical | 14 days |
| High | 30 days |
| Medium | Next reasonable release |
| Low | Best effort |

For issues with broad user impact, the project may publish a GitHub Security
Advisory, request a CVE, or release coordinated mitigation guidance. Public
credit will be offered unless the reporter requests otherwise.

Please coordinate public disclosure with the maintainers. As a default, wait
until a fix or mitigation is available, or 90 days after the issue is confirmed,
whichever comes first, unless both sides agree to a different timeline.

## Safe Harbor

The project will not pursue legal action against good-faith security research
that:

- Targets only systems, accounts, repositories, and data you are authorized to
  test.
- Avoids privacy violations, data destruction, persistence, lateral movement,
  and service disruption.
- Uses the minimum testing necessary to demonstrate the issue.
- Reports the issue promptly and keeps details confidential during coordinated
  disclosure.
- Does not use the vulnerability for extortion, unauthorized access, or
  operational advantage.

This safe harbor applies only to this project and its maintainers. It cannot
bind third parties, employers, service providers, customers, or other legal
owners of affected systems.

## Secure Use Expectations

SharpADIDNS is intended for authorized security assessments, lab environments,
CTFs, and controlled administration work. Operators should prefer `--dry-run`
and `--backup-to` before write operations, keep engagement authorization and
change records, and avoid including secrets or customer-specific details in bug
reports.

The maintainers may decline reports or requests whose primary purpose is to
enable unauthorized operation, evade policy in third-party environments, or
provide offensive tasking support rather than improve the security of the
project.
