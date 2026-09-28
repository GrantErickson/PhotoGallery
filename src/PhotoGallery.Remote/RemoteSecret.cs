using System.Security.Cryptography;
using System.Text;

namespace PhotoGallery.Remote;

/// <summary>
/// The passphrase as the host keeps it: a salted PBKDF2 key, never the passphrase itself. Nothing derived from it is
/// ever sent, so it can only be guessed online, where <see cref="LoginThrottle"/> slows guessing down.
/// </summary>
public sealed record RemoteSecret(string Salt, int Iterations, string Key)
{
    public const int DefaultIterations = 600_000;
    public const int MinLength = 8;

    public static RemoteSecret Create(string passphrase, int iterations = DefaultIterations)
    {
        var salt = RandomNumberGenerator.GetBytes(16);
        return new(Convert.ToBase64String(salt), iterations, Convert.ToBase64String(Derive(passphrase, salt, iterations)));
    }

    /// <summary>The key for a passphrase. Surrounding spaces don't count, and accented letters match however they were typed.</summary>
    public static byte[] Derive(string passphrase, byte[] salt, int iterations) =>
        Rfc2898DeriveBytes.Pbkdf2(Encoding.UTF8.GetBytes(Normalize(passphrase)), salt, iterations, HashAlgorithmName.SHA256, 32);

    public static string Normalize(string passphrase) => passphrase.Trim().Normalize(NormalizationForm.FormC);

    public bool Verify(string passphrase)
    {
        try
        {
            return CryptographicOperations.FixedTimeEquals(Derive(passphrase, Convert.FromBase64String(Salt), Iterations), Convert.FromBase64String(Key));
        }
        catch (FormatException)
        {
            return false;
        }
    }
}
