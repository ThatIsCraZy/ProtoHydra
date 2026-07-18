namespace DataService.Protocols.Ssh;

internal sealed record ScpCommand(
    bool Download,
    bool Upload,
    bool Recursive,
    bool PreserveTimes,
    bool TargetShouldBeDirectory,
    string Path);
