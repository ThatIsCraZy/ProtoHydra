# ADR: Managed Classic SCP Port

## Status

Accepted for MVP implementation.

## Context

SCP must run inside the .NET process without launching `scp.exe`, `sshd.exe`, shells, native OpenSSH code, P/Invoke, or custom SSH transport logic. SSH transport, key exchange, encryption, authentication, and channels are delegated to `FxSsh`.

The classic SCP file-transfer layer is implemented as managed C# above an authenticated `exec` channel. The reference source is `PowerShell/openssh-portable` branch `latestw_all` at commit `e98b11e5de035cbd0eecbddc95a87da42f14671f`.

## Decision

Use `FxSsh` for the shared SSH listener, host key, password/public-key accept-any authentication, and `exec` channel delivery. SFTP and SCP use the same TCP port and host key; the listener routes `subsystem:sftp` to the SFTP handler and `exec scp ...` to this managed SCP handler. The GUI keeps SFTP and legacy SCP independently switchable by allowing or rejecting those SSH channel request types.

Implement only the classic SCP/RCP wire layer in managed code:

- Parse strict `scp ... -f <path>` and `scp ... -t <path>` server commands.
- Support ACK `0x00`, warning/error `0x01`, fatal `0x02`, `C<mode> <size> <name>\n`, raw file data, and final ACK.
- Use the shared `RootPathResolver` for every requested path.
- Reject shell syntax, traversal, absolute paths, path separators in received file names, and unsupported options.

## MVP Scope

Implemented first:

- single-file download,
- single-file upload,
- root escape blocking,
- accept-any SSH password authentication,
- central transfer events.

Deferred:

- recursive `-r`,
- timestamp preservation `-p`,
- OpenSSH `scp -O` and WinSCP manual interoperability runs,
- directory messages `D`/`E`,
- resumability and transfer limits beyond bounded header parsing.

## Security Review Process

For later updates, compare `scp.c` changes against the locked commit:

```text
git -C reference/openssh-portable fetch origin latestw_all
git -C reference/openssh-portable log e98b11e5de035cbd0eecbddc95a87da42f14671f..origin/latestw_all -- scp.c
git -C reference/openssh-portable diff e98b11e5de035cbd0eecbddc95a87da42f14671f..origin/latestw_all -- scp.c
```
