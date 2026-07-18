namespace DataService.Core.Events;

public enum TransferEventKind
{
    ListenerStarting,
    ListenerStarted,
    ListenerStopping,
    ListenerStopped,
    ListenerFaulted,
    ClientConnected,
    ClientDisconnected,
    AuthenticationAttempt,
    CommandReceived,
    DirectoryListed,
    DownloadStarted,
    DownloadCompleted,
    DownloadFailed,
    UploadStarted,
    UploadCompleted,
    UploadFailed,
    RequestRejected
}

