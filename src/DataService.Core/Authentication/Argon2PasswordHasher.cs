using System.Security.Cryptography;
using System.Text;
using Konscious.Security.Cryptography;

namespace DataService.Core.Authentication;

/// <summary>
/// Argon2id password hashing using the PHC string format
/// ($argon2id$v=19$m=...,t=...,p=...$salt$hash) so parameters travel with each
/// hash and can be raised later without invalidating existing users.
/// </summary>
public static class Argon2PasswordHasher
{
    // RFC 9106 second recommended option (64 MiB, 3 iterations, 1 lane) —
    // deliberately memory-hard, still fast enough for interactive logins.
    private const int MemoryKibibytes = 65536;
    private const int Iterations = 3;
    private const int Parallelism = 1;
    private const int SaltLength = 16;
    private const int HashLength = 32;

    public static string Hash(string password)
    {
        ArgumentException.ThrowIfNullOrEmpty(password);

        var salt = RandomNumberGenerator.GetBytes(SaltLength);
        var hash = Compute(password, salt, MemoryKibibytes, Iterations, Parallelism, HashLength);
        return
            $"$argon2id$v=19$m={MemoryKibibytes},t={Iterations},p={Parallelism}" +
            $"${Convert.ToBase64String(salt)}${Convert.ToBase64String(hash)}";
    }

    public static bool Verify(string password, string encodedHash)
    {
        // Empty passwords can never be hashed (see Hash), so they never match.
        if (string.IsNullOrEmpty(password) || string.IsNullOrWhiteSpace(encodedHash))
        {
            return false;
        }

        try
        {
            var parts = encodedHash.Split('$', StringSplitOptions.RemoveEmptyEntries);
            if (parts.Length != 5 || parts[0] != "argon2id" || parts[1] != "v=19")
            {
                return false;
            }

            int memory = 0, iterations = 0, parallelism = 0;
            foreach (var parameter in parts[2].Split(','))
            {
                var pair = parameter.Split('=');
                if (pair.Length != 2 || !int.TryParse(pair[1], out var value) || value <= 0)
                {
                    return false;
                }

                switch (pair[0])
                {
                    case "m": memory = value; break;
                    case "t": iterations = value; break;
                    case "p": parallelism = value; break;
                    default: return false;
                }
            }

            if (memory == 0 || iterations == 0 || parallelism == 0)
            {
                return false;
            }

            var salt = Convert.FromBase64String(parts[3]);
            var expected = Convert.FromBase64String(parts[4]);
            var actual = Compute(password, salt, memory, iterations, parallelism, expected.Length);
            return CryptographicOperations.FixedTimeEquals(expected, actual);
        }
        catch (FormatException)
        {
            return false;
        }
    }

    private static byte[] Compute(
        string password,
        byte[] salt,
        int memoryKibibytes,
        int iterations,
        int parallelism,
        int hashLength)
    {
        using var argon2 = new Argon2id(Encoding.UTF8.GetBytes(password))
        {
            Salt = salt,
            MemorySize = memoryKibibytes,
            Iterations = iterations,
            DegreeOfParallelism = parallelism
        };
        return argon2.GetBytes(hashLength);
    }
}
