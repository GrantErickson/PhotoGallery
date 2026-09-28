namespace PhotoGallery.Remote;

/// <summary>Another computer as typed: "GRANT-PC", "grant-pc:48000", "192.168.1.20", "[fe80::1]:47813" or a full https:// address.</summary>
public sealed record RemoteAddress(string Host, int Port)
{
    public static RemoteAddress? Parse(string? text)
    {
        text = text?.Trim().TrimEnd('/');
        if (string.IsNullOrEmpty(text)) return null;
        if (!text.Contains("://", StringComparison.Ordinal)) text = "https://" + text;
        if (!Uri.TryCreate(text, UriKind.Absolute, out var uri) || uri.Scheme != Uri.UriSchemeHttps || uri.Host.Length == 0
            || uri.AbsolutePath != "/" || uri.Query.Length > 0 || uri.UserInfo.Length > 0) return null;
        // Without a port: remote access's own, rather than https's usual 443.
        var port = uri.IsDefaultPort && !text.EndsWith(":443", StringComparison.Ordinal) ? RemoteServerOptions.DefaultPort : uri.Port;
        return new RemoteAddress(uri.Host, port);
    }

    public string Origin => $"https://{Host}:{Port}";

    /// <summary>How this computer is remembered (its accepted certificate, its saved passphrase).</summary>
    public string Key => $"{Host.ToLowerInvariant()}:{Port}";

    /// <summary>Whether an address (from the browser) is on this computer.</summary>
    public bool Owns(string? url) =>
        Uri.TryCreate(url, UriKind.Absolute, out var uri) && uri.Scheme == Uri.UriSchemeHttps && uri.Port == Port
        && string.Equals(uri.Host, Host, StringComparison.OrdinalIgnoreCase);
}
