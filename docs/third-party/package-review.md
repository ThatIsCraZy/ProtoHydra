# Third-party package review

## Watson 7.0.15

- Purpose: HTTP/HTTPS listener library for `DataService.Protocols.Http`.
- Source: NuGet package `Watson` 7.0.15, repository `https://github.com/dotnet/WatsonWebserver`.
- License: MIT, from package `LICENSE.md`.
- Target framework: package includes `net10.0`.
- Server functionality: exposes `WatsonWebserver.Webserver` with configurable `WebserverSettings` and async request delegate.
- Stop/cancellation: adapter owns the server task, cancellation token source, and calls `Stop()`/`Dispose()`.
- Security check: covered by `dotnet list package --vulnerable --include-transitive`.

## FubarDev.FtpServer 3.1.2

- Purpose: FTP/FTPS listener and FTP command implementation for `DataService.Protocols.Ftp`.
- Source: NuGet packages `FubarDev.FtpServer` 3.1.2 and `FubarDev.FtpServer.FileSystem.DotNet` 3.1.2, repository `https://github.com/FubarDevelopment/FtpServer.git`.
- License: MIT, from package metadata.
- Target framework: packages include `netstandard2.1`, consumed from `net10.0`.
- Server functionality: exposes `IFtpServerHost`, DI registration, explicit `AUTH TLS`, passive data connection support, membership provider integration, and DotNet filesystem backend.
- Stop/cancellation: adapter owns a Generic Host, starts/stops `IFtpServerHost`, and disposes the host on stop/fault.
- Security check: `Newtonsoft.Json` is pinned directly to 13.0.4 to override FubarDev's vulnerable transitive 9.0.1 dependency; covered by `dotnet list package --vulnerable --include-transitive`.

## Tftp.Net 1.3.0

- Purpose: TFTP listener library for `DataService.Protocols.Tftp` and TFTP client library for integration tests.
- Source: NuGet package `Tftp.Net` 1.3.0, repository `https://github.com/Callisto82/tftp.net`.
- License: Microsoft Public License, from package metadata.
- Target framework: package includes `netstandard2.0`, consumed from `net10.0`.
- Server functionality: exposes `TftpServer` with RRQ/WRQ events and `ITftpTransfer.Start(Stream)` so packet handling remains in the library.
- Stop/cancellation: adapter disposes the server and cancels active transfers via the package transfer API.
- Security check: covered by `dotnet list package --vulnerable --include-transitive`.

## FxSsh 1.3.0 and SFTPServer 1.3.3

- Purpose: SSH listener/auth/channel handling for SFTP/SCP and SFTP-v3 subsystem handling for `DataService.Protocols.Ssh`.
- Source: NuGet packages `FxSsh` 1.3.0 (`https://github.com/Aimeast/FxSsh`) and `SFTPServer` 1.3.3 (`https://github.com/KeenSystemsNL/SFTPServer`).
- License: MIT for both packages, from package and repository metadata.
- Target framework: `FxSsh` targets `net8.0`; `SFTPServer` includes `net6.0` and `netstandard2.1`; both are consumed from `net10.0`.
- Server functionality: `FxSsh` exposes `SshServer`, password/public-key auth events, session channels, subsystem requests, and exec requests; `SFTPServer` exposes `SFTPServer.Run` over input/output streams plus `ISFTPHandler`.
- Runtime architecture: SFTP and legacy SCP share one FxSsh listener, one SSH host key, and one configured SSH port. The listener routes `subsystem:sftp` to the SFTP handler and `exec scp ...` to the managed SCP handler; disabling either frontend rejects only that channel request type.
- Integration result: active SFTP smoke test uses SSH.NET against a real listener for password auth, listing, file download, nested file download, and blocked root escape.
- SCP result: active SCP smoke test uses SSH.NET against a real listener for password auth, single-file download, single-file upload, and blocked root escape. A shared-listener smoke test verifies that SFTP subsystem and SCP exec requests work on the same TCP port. The classic SCP file-transfer state machine is implemented as managed code under the OpenSSH SCP exception and mapped in `docs/third-party/openssh-scp-source-map.md`.
- Security note: the built-in `DefaultSFTPHandler` is not used because its README warns about path traversal hardening. The adapter uses a rooted read-only handler backed by the shared `RootPathResolver`.
- Stop/cancellation: the shared listener stops `SshServer` only when both SFTP and SCP are disabled; per-protocol stops reject new channel requests for that protocol and cancel active sessions for that protocol.
- Security check: covered by `dotnet list package --vulnerable --include-transitive`.

## Application packages

- `Avalonia` 12.0.5, `Avalonia.Desktop` 12.0.5, `Avalonia.Themes.Fluent` 12.0.5: native desktop UI.
- `CommunityToolkit.Mvvm` 8.4.2: MVVM observable base types.
- `Microsoft.Extensions.Hosting` 10.0.9 and `Microsoft.Extensions.Options.ConfigurationExtensions` 10.0.9: Generic Host and options integration.
- `Serilog.Extensions.Logging` 10.0.0 and `Serilog.Sinks.File` 7.0.0: logging integration and file sink.
- `Microsoft.NET.Test.Sdk` 18.7.0, `coverlet.collector` 10.0.1, `xunit.runner.visualstudio` 3.1.5: test execution and coverage.

## Integration test clients

- `FluentFTP` 54.2.0: active FTP and FTPS client smoke tests. License: MIT. Repository: `https://github.com/robinrodricks/FluentFTP`.
- `Tftp.Net` 1.3.0: active TFTP client smoke tests. License: Microsoft Public License. Repository: `https://github.com/Callisto82/tftp.net`.
- `SSH.NET` 2025.1.0: active SFTP and SCP client smoke tests. License: MIT. Repository: `https://github.com/sshnet/SSH.NET`.

OpenSSH `scp -O` and WinSCP SCP-mode interoperability are still required before treating SCP as complete beyond the current SSH.NET-covered MVP surface.
