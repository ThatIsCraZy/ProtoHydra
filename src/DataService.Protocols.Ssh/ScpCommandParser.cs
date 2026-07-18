namespace DataService.Protocols.Ssh;

internal static class ScpCommandParser
{
    public static ScpCommand Parse(string command)
    {
        var args = Split(command);
        if (args.Count < 3 || args[0] != "scp")
        {
            throw new InvalidOperationException("Only scp server commands are accepted.");
        }

        var download = false;
        var upload = false;
        var recursive = false;
        var preserveTimes = false;
        var targetShouldBeDirectory = false;
        string? path = null;

        for (var index = 1; index < args.Count; index++)
        {
            var arg = args[index];
            if (arg == "--")
            {
                if (++index >= args.Count)
                {
                    throw new InvalidOperationException("Missing SCP path.");
                }

                path = args[index];
                if (index != args.Count - 1)
                {
                    throw new InvalidOperationException("SCP command contains trailing arguments.");
                }

                break;
            }

            if (arg.StartsWith('-') && arg.Length > 1)
            {
                foreach (var option in arg.Skip(1))
                {
                    switch (option)
                    {
                        case 'f':
                            download = true;
                            break;
                        case 't':
                            upload = true;
                            break;
                        case 'r':
                            recursive = true;
                            break;
                        case 'p':
                            preserveTimes = true;
                            break;
                        case 'd':
                            targetShouldBeDirectory = true;
                            break;
                        case 'v':
                        case 'q':
                            break;
                        default:
                            throw new InvalidOperationException($"Unsupported SCP option: -{option}");
                    }
                }

                continue;
            }

            path = arg;
            if (index != args.Count - 1)
            {
                throw new InvalidOperationException("SCP command contains trailing arguments.");
            }
        }

        if (download == upload)
        {
            throw new InvalidOperationException("SCP command must specify exactly one of -f or -t.");
        }

        if (string.IsNullOrWhiteSpace(path))
        {
            throw new InvalidOperationException("SCP path is required.");
        }

        return new ScpCommand(
            download,
            upload,
            recursive,
            preserveTimes,
            targetShouldBeDirectory,
            path);
    }

    private static List<string> Split(string command)
    {
        var args = new List<string>();
        var current = new List<char>();
        char? quote = null;

        foreach (var ch in command)
        {
            if (quote is not null)
            {
                if (ch == quote)
                {
                    quote = null;
                }
                else
                {
                    current.Add(ch);
                }

                continue;
            }

            if (ch is '\'' or '"')
            {
                quote = ch;
                continue;
            }

            if (char.IsWhiteSpace(ch))
            {
                Flush();
            }
            else
            {
                current.Add(ch);
            }
        }

        if (quote is not null)
        {
            throw new InvalidOperationException("Unterminated quote in SCP command.");
        }

        Flush();
        return args;

        void Flush()
        {
            if (current.Count == 0)
            {
                return;
            }

            args.Add(new string([.. current]));
            current.Clear();
        }
    }
}
