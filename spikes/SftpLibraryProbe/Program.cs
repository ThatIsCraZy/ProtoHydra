using System.Reflection;

foreach (var type in typeof(FxSsh.SshServer).Assembly.GetTypes().OrderBy(type => type.FullName))
{
    var fullName = type.FullName ?? "";
    if (fullName.Contains("Sftp", StringComparison.OrdinalIgnoreCase)
        || fullName.Contains("Auth", StringComparison.OrdinalIgnoreCase)
        || fullName.Contains("Args", StringComparison.OrdinalIgnoreCase)
        || fullName.Contains("Command", StringComparison.OrdinalIgnoreCase))
    {
        Console.WriteLine($"{(type.IsPublic ? "public" : "nonpublic")} {fullName}");
    }
}

var names = new[]
{
    "FxSsh.SshServer",
    "FxSsh.Session",
    "FxSsh.Services.Session",
    "FxSsh.Services.ConnectionService",
    "FxSsh.Services.Channel",
    "FxSsh.Services.SessionChannel",
    "FxSsh.Services.UserauthArgs",
    "FxSsh.Services.CommandRequestedArgs",
    "FxSsh.Services.SubsystemRequestedArgs",
    "FxSsh.Services.SftpService",
    "FxSsh.Messages.SubsystemRequestMessage",
    "FxSsh.Messages.Connection.CommandRequestMessage",
    "SFTP.SFTPServer",
    "SFTP.ISFTPServer",
    "SFTP.ISFTPHandler",
    "SFTP.DefaultSFTPHandler",
    "SFTP.SFTPServerOptions"
};

var assemblies = AppDomain.CurrentDomain.GetAssemblies()
    .Concat(new[]
    {
        typeof(FxSsh.SshServer).Assembly,
        typeof(SFTP.SFTPServer).Assembly
    })
    .Distinct()
    .ToArray();

var flags = BindingFlags.Public | BindingFlags.Instance | BindingFlags.Static | BindingFlags.DeclaredOnly;
foreach (var name in names)
{
    var type = assemblies
        .Select(assembly => assembly.GetType(name, throwOnError: false))
        .FirstOrDefault(type => type is not null);
    if (type is null)
    {
        Console.WriteLine($"MISSING {name}");
        continue;
    }

    Console.WriteLine($"TYPE {type.FullName}");
    foreach (var constructor in type.GetConstructors(flags))
    {
        Console.WriteLine($"  CTOR {constructor}");
    }

    foreach (var eventInfo in type.GetEvents(flags))
    {
        Console.WriteLine($"  EVENT {eventInfo}");
    }

    foreach (var property in type.GetProperties(BindingFlags.Public | BindingFlags.Instance | BindingFlags.Static))
    {
        Console.WriteLine($"  PROP {property.PropertyType.FullName} {property.Name}");
    }

    foreach (var method in type.GetMethods(flags).Where(method => !method.IsSpecialName).OrderBy(method => method.Name))
    {
        Console.WriteLine($"  METHOD {method}");
    }
}
