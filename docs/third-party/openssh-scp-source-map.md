# OpenSSH SCP Source Map

Reference repository: `https://github.com/PowerShell/openssh-portable.git`

Branch: `latestw_all`

Commit: `e98b11e5de035cbd0eecbddc95a87da42f14671f`

## Mapped Areas

| C# area | OpenSSH source area | Notes |
|---|---|---|
| `ScpCommandParser` | `scp.c` option handling around remote command construction and server mode options | Strict parser accepts only `scp`, `-f`, `-t`, `-d`, `-r`, `-p`, `-v`, optional `--`, and one path. |
| `ScpServerSession.SendFileAsync` | `source()` around `C%04o %lld %s\n`, file data, ACK handling | MVP implements single regular file transfer. |
| `ScpServerSession.ReceiveFileAsync` | `sink()` handling initial ACK, `C` records, exact byte count, final ACK | MVP implements single regular file receive. |
| `ScpProtocolStream.ReadResponseAsync` | `response()` | Handles ACK `0x00`, warning `0x01`, fatal `0x02`. |
| `ScpProtocolStream.WriteErrorAsync` | `run_err()` | Sends SCP warning/fatal records with `scp: ` prefix. |

## Deliberately Unsupported In MVP

- Recursive directory upload/download.
- `T` timestamp records beyond rejection/documented non-support.
- Glob, tilde, variable, shell, or command expansion.
- General shell access.
- Special files, symlinks, hardlinks, devices, FIFOs, sockets, ACL transfer.
