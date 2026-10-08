# Security policy

## Reporting a vulnerability

Please report security problems privately, not in a public issue: use GitHub's "Report a vulnerability"
(private security advisory) on this repository. Include the version, the platform, what an attacker can do,
and how to reproduce it. You will get an answer as soon as possible; please allow time for a fix before
publishing details.

## Supported versions

Only the latest release receives fixes.

## Known limitations (not vulnerabilities)

- **NBNS has no authentication.** Any host that reaches UDP 137 can register names, and UDP source addresses can
  be forged. WinsAlt's registration policy, blocked names, rate limits and quotas reduce the risk; they cannot
  remove it. Use static mappings for names that must always resolve correctly.
- **The dashboard is plain HTTP.** Restrict who can reach it. A new installation signs in with admin / admin and
  asks for a new password at once.
- **Secrets in the data folder are stored in clear text**: the replication key (`replication.json`) and the API
  token (`settings.json`). The admin password is stored as a salted PBKDF2 hash (`auth.json`). Only
  administrators should be able to read the data folder.
- **Replication is authenticated (HMAC-SHA256 with a shared key) but not encrypted.**
- An `auth.json` that cannot be read locks every sign-in on purpose; deleting it restores admin / admin.
