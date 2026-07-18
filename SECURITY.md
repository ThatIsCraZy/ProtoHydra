# Security Policy

## Reporting a vulnerability

Please report suspected security issues privately via GitHub's
**"Report a vulnerability"** button under the repository's **Security** tab
(GitHub Security Advisories), rather than opening a public issue. You will
receive a response as soon as reasonably possible.

Please do not include real credentials, customer data, or other sensitive
information in reports.

## Scope note

FileHydra is a maintenance/lab tool that deliberately ships with an
**Accept-Any authentication policy** (every username/password is accepted, no
access control) and can create a **temporary, user-approved (elevated) Windows
Firewall allow rule** for its own listener ports. Both are documented, opt-in
maintenance features — not attempts to exploit vulnerabilities or circumvent
security controls. FileHydra is intended for controlled maintenance/device
networks, **not** for permanent or publicly reachable operation. Reports that
simply restate this documented behaviour are not considered vulnerabilities.

---

## Code signing policy

Release binaries are produced by a **reproducible, automated GitHub Actions
build** from this public source repository (see
[`.github/workflows/release.yml`](.github/workflows/release.yml)) and are code
signed through the **[SignPath Foundation](https://signpath.org/)** free code
signing program for open-source projects. The signing certificate is issued to
and vouched for by SignPath Foundation; private keys are held on SignPath's HSM
and are never exposed to the build.

**Team roles**

- **Author** — the project maintainer(s) who own all source code and build
  scripts in this repository.
- **Reviewer** — all external contributions (pull requests) are reviewed by a
  maintainer before being merged.
- **Approver** — every signing request is approved manually by a maintainer
  before a binary is signed and published.

For a single-maintainer project these roles are held by the same person; no
binary is signed without an explicit, manual approval of the corresponding
signing request.

**Privacy**

FileHydra collects **no** telemetry and transmits **no** usage or personal data.
All runtime data (certificates, SSH host key, logs, configuration) stays on the
local machine under `%LOCALAPPDATA%\FileHydra\`.

**Attribution**

Free code signing is provided by [SignPath.io](https://signpath.io/); the
certificate is issued by the [SignPath Foundation](https://signpath.org/).

**Verifying a release**

After a signed release is published, you can verify the signature on Windows
with:

```powershell
Get-AuthenticodeSignature .\FileHydra.exe | Format-List Status, SignerCertificate
```

A valid signature reports `Status: Valid` and a certificate issued for the
project by the SignPath Foundation CA.
