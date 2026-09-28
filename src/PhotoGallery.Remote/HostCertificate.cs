using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;

namespace PhotoGallery.Remote;

/// <summary>
/// The host's own HTTPS certificate: self-signed, made once and kept in the user's certificate store (its private key
/// protected by Windows). Nothing vouches for it, so browsers warn once; Photo Gallery checks it with the passphrase.
/// </summary>
public static class HostCertificate
{
    private const string FriendlyName = "Photo Gallery remote access";

    public static X509Certificate2 GetOrCreate()
    {
        using var store = new X509Store(StoreName.My, StoreLocation.CurrentUser);
        store.Open(OpenFlags.ReadWrite);
        var existing = store.Certificates
            .Where(c => c.FriendlyName == FriendlyName && c.HasPrivateKey && c.NotAfter > DateTime.Now.AddDays(30))
            .OrderByDescending(c => c.NotAfter)
            .FirstOrDefault();
        if (existing is not null) return existing;

        var created = Create(Environment.MachineName, X509KeyStorageFlags.UserKeySet | X509KeyStorageFlags.PersistKeySet);
        created.FriendlyName = FriendlyName;
        store.Add(created);
        return created;
    }

    /// <summary>A certificate whose key lives only as long as the object (tests).</summary>
    public static X509Certificate2 CreateTemporary(string name = "localhost") => Create(name, X509KeyStorageFlags.Exportable);

    private static X509Certificate2 Create(string name, X509KeyStorageFlags flags)
    {
        using var key = ECDsa.Create(ECCurve.NamedCurves.nistP256);
        var request = new CertificateRequest($"CN={name}, O=Photo Gallery", key, HashAlgorithmName.SHA256);
        var names = new SubjectAlternativeNameBuilder();
        names.AddDnsName(name);
        if (!name.Equals("localhost", StringComparison.OrdinalIgnoreCase)) names.AddDnsName("localhost");
        request.CertificateExtensions.Add(names.Build());
        request.CertificateExtensions.Add(new X509BasicConstraintsExtension(false, false, 0, true));
        request.CertificateExtensions.Add(new X509KeyUsageExtension(X509KeyUsageFlags.DigitalSignature, true));
        request.CertificateExtensions.Add(new X509EnhancedKeyUsageExtension([new Oid("1.3.6.1.5.5.7.3.1")], false)); // server authentication
        using var made = request.CreateSelfSigned(DateTimeOffset.Now.AddDays(-1), DateTimeOffset.Now.AddYears(10));
        // Windows' TLS can't use an ephemeral key: going through PFX puts it in a key store.
        return X509CertificateLoader.LoadPkcs12(made.Export(X509ContentType.Pfx), null, flags);
    }

    /// <summary>SHA-256 of the certificate, in hex (what browsers show as its fingerprint).</summary>
    public static string Fingerprint(X509Certificate certificate) => Convert.ToHexString(SHA256.HashData(certificate.GetRawCertData()));

    /// <summary>The fingerprint's start in groups of four, short enough to compare by eye.</summary>
    public static string SecurityCode(string fingerprint) =>
        string.Join(" ", Enumerable.Range(0, 4).Select(i => fingerprint.Substring(i * 4, 4)));
}
