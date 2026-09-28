using System.Collections.Concurrent;
using System.Net;
using System.Security.Cryptography;
using System.Text;

namespace PhotoGallery.Remote;

/// <summary>
/// Signed-in computers: a random token each, kept (hashed) in memory only, so restarting the host or changing the
/// passphrase signs everyone out.
/// </summary>
internal sealed class RemoteSessions
{
    public static readonly TimeSpan Lifetime = TimeSpan.FromDays(30);

    private readonly ConcurrentDictionary<string, DateTime> _expiry = new();

    public string Create()
    {
        var token = Convert.ToBase64String(RandomNumberGenerator.GetBytes(32)).Replace('+', '-').Replace('/', '_').TrimEnd('=');
        _expiry[Hash(token)] = DateTime.UtcNow + Lifetime;
        return token;
    }

    /// <summary>Whether the token is signed in; each use pushes its expiry out again.</summary>
    public bool Check(string? token)
    {
        if (string.IsNullOrEmpty(token) || token.Length > 100) return false;
        var key = Hash(token);
        if (!_expiry.TryGetValue(key, out var expires)) return false;
        if (expires < DateTime.UtcNow)
        {
            _expiry.TryRemove(key, out _);
            return false;
        }
        _expiry[key] = DateTime.UtcNow + Lifetime;
        return true;
    }

    public void Remove(string? token)
    {
        if (!string.IsNullOrEmpty(token)) _expiry.TryRemove(Hash(token), out _);
    }

    public void Clear() => _expiry.Clear();

    private static string Hash(string token) => Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(token)));
}

/// <summary>
/// Slows down passphrase guessing: after 5 wrong tries from an address it has to wait a minute, doubling each time
/// after that (up to an hour); and only one passphrase is checked at a time, so guesses can't tie up the processor.
/// </summary>
internal sealed class LoginThrottle(TimeProvider? clock = null)
{
    public const int FreeTries = 5;

    private readonly TimeProvider _clock = clock ?? TimeProvider.System;
    private readonly ConcurrentDictionary<IPAddress, (int Failures, DateTimeOffset LockedUntil)> _state = new();

    public SemaphoreSlim Gate { get; } = new(1);

    /// <summary>How long this address must wait before trying again, if it must.</summary>
    public TimeSpan? RetryAfter(IPAddress address)
    {
        if (!_state.TryGetValue(Normalize(address), out var state)) return null;
        var wait = state.LockedUntil - _clock.GetUtcNow();
        return wait > TimeSpan.Zero ? wait : null;
    }

    public void Failed(IPAddress address) => _state.AddOrUpdate(Normalize(address), _ => (1, DateTimeOffset.MinValue), (_, s) =>
    {
        var failures = s.Failures + 1;
        if (failures < FreeTries) return (failures, s.LockedUntil);
        var minutes = Math.Min(60, Math.Pow(2, failures - FreeTries));
        return (failures, _clock.GetUtcNow().AddMinutes(minutes));
    });

    public void Succeeded(IPAddress address) => _state.TryRemove(Normalize(address), out _);

    private static IPAddress Normalize(IPAddress address) => address.IsIPv4MappedToIPv6 ? address.MapToIPv4() : address;
}
