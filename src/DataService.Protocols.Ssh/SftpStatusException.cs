using SFTP.Enums;
using SFTP.Exceptions;

namespace DataService.Protocols.Ssh;

internal sealed class SftpStatusException : HandlerException
{
    public SftpStatusException(Status status)
        : base(status)
    {
    }
}
