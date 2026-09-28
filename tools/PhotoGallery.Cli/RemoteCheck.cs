using System.Diagnostics;
using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;
using PhotoGallery.Remote;

namespace PhotoGallery.Cli;

/// <summary>
/// A smoke test of another computer's remote access, run from the computer that connects to it:
///   dotnet run --project tools/PhotoGallery.Cli -- remote-check GRANT-PC [--code "A1B2 C3D4 E5F6 7890"]
/// The passphrase comes from the PG_REMOTE_PASSPHRASE environment variable (without it, only the checks that need no
/// sign-in run). Never prints the passphrase or the session token, so the report can be shared. Read-only: it changes
/// nothing on the other computer (it signs in and out, and may make it fetch one Live Photo video).
/// </summary>
internal static class RemoteCheck
{
    private static int _failed;

    public static async Task<int> RunAsync(string[] args)
    {
        var computer = args.FirstOrDefault(a => !a.StartsWith("--", StringComparison.Ordinal));
        var expectedCode = Option(args, "--code");
        if (RemoteAddress.Parse(computer) is not { } address)
        {
            Console.WriteLine("Usage: remote-check <computer[:port]> [--code \"XXXX XXXX XXXX XXXX\"]   (passphrase in PG_REMOTE_PASSPHRASE)");
            return 2;
        }
        var passphrase = Environment.GetEnvironmentVariable("PG_REMOTE_PASSPHRASE");
        Console.WriteLine($"Remote access check: {address.Origin}   ({DateTime.Now:yyyy-MM-dd HH:mm}, from {Environment.MachineName})");

        // 1. Reach it, and see its certificate. From then on only that certificate is accepted.
        string? seen = null;
        var accepted = (string?)null;
        using var handler = new HttpClientHandler
        {
            UseCookies = false,
            ServerCertificateCustomValidationCallback = (_, certificate, _, _) =>
            {
                if (certificate is null) return false;
                var fingerprint = HostCertificate.Fingerprint(certificate);
                seen ??= fingerprint;
                return accepted is null || fingerprint == accepted;
            },
        };
        using var http = new HttpClient(handler) { BaseAddress = new Uri(address.Origin + "/"), Timeout = TimeSpan.FromSeconds(60) };

        var clock = Stopwatch.StartNew();
        JsonElement hello;
        try
        {
            hello = await http.GetFromJsonAsync<JsonElement>("api/hello");
        }
        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException or JsonException)
        {
            Fail("S1 reach", $"{ex.GetType().Name}: {ex.Message}. Is remote access on there, the port right, and the firewall open (private network)?");
            return 1;
        }
        accepted = seen;
        var code = HostCertificate.SecurityCode(seen!);
        Pass("S1 reach", $"\"{hello.GetProperty("name").GetString()}\" answered in {clock.ElapsedMilliseconds} ms, security code {code}");
        if (expectedCode is not null)
        {
            if (Normalize(expectedCode) == Normalize(code)) Pass("S2 security code", "matches the code given");
            else Fail("S2 security code", $"is {code}, expected {expectedCode}. Don't send the passphrase until this is explained.");
            if (Normalize(expectedCode) != Normalize(code)) return 1;
        }
        else
        {
            Info("S2 security code", "not checked (pass --code with the code shown in the host's Settings)");
        }

        // 2. What must be refused.
        await ExpectAsync("S3 no sign-in", http, new HttpRequestMessage(HttpMethod.Get, "api/items"), HttpStatusCode.Unauthorized);
        var foreign = new HttpRequestMessage(HttpMethod.Get, "api/hello");
        foreign.Headers.Host = "photos.example.com";
        await ExpectAsync("S4 other host name", http, foreign, HttpStatusCode.Forbidden);
        await ExpectAsync("S5 post from a form", http, new HttpRequestMessage(HttpMethod.Post, "api/login")
            { Content = JsonContent.Create(new { passphrase = "x" }) }, HttpStatusCode.Forbidden);

        if (string.IsNullOrEmpty(passphrase))
        {
            Info("S6+", "PG_REMOTE_PASSPHRASE isn't set: skipping the checks that sign in");
            return Summary();
        }

        // 3. Sign in (the token stays in memory; it's never printed).
        clock.Restart();
        using var login = new HttpRequestMessage(HttpMethod.Post, "api/login") { Content = JsonContent.Create(new { passphrase }) };
        login.Headers.Add(RemoteServer.ScriptHeader, "1");
        using var signedIn = await http.SendAsync(login);
        if (!signedIn.IsSuccessStatusCode)
        {
            Fail("S6 sign in", $"{(int)signedIn.StatusCode}: {await ErrorOf(signedIn)}");
            return Summary();
        }
        var token = (await signedIn.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("token").GetString()!;
        http.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", token);
        Pass("S6 sign in", $"{clock.ElapsedMilliseconds} ms (the passphrase check itself takes a few hundred ms)");

        // 4. The lists.
        var (ids, dates, flags) = await ItemsAsync(http, "S7 timeline", "api/items");
        var photos = ids.Where((_, i) => (flags[i] & 1) == 0).ToList();
        var videos = ids.Where((_, i) => (flags[i] & 1) == 1).ToList();
        var live = ids.Where((_, i) => (flags[i] & 2) == 2).ToList();
        Info("S7 timeline", $"{photos.Count:N0} photos, {videos.Count:N0} videos, {live.Count:N0} Live Photos; newest {Date(dates.FirstOrDefault())}, oldest {Date(dates.LastOrDefault())}");
        await ItemsAsync(http, "S8 search (best matches)", "api/items?section=search&q=birthday%20cake");
        await ItemsAsync(http, "S9 search (exact words)", "api/items?section=search&q=birthday&exact=1&sort=newest");
        await ItemsAsync(http, "S10 favorites", "api/items?section=favorites");
        var day = DateTime.Today;
        await ItemsAsync(http, "S11 on this day", $"api/items?section=onthisday&day={day.Month}-{day.Day}");
        await CountAsync(http, "S12 people", "api/people");
        await CountAsync(http, "S13 albums", "api/albums");

        // 5. One item's details, thumbnails, photos at screen size, a video and a Live Photo.
        if (ids.Count > 0)
        {
            clock.Restart();
            using var details = await http.GetAsync($"api/media/{ids[0]}");
            if (details.IsSuccessStatusCode) Pass("S14 details", $"{clock.ElapsedMilliseconds} ms");
            else Fail("S14 details", $"{(int)details.StatusCode}");
            await ThumbnailsAsync(http, ids.Take(60).ToList());
        }
        await DisplayAsync(http, photos.Take(5).ToList());
        if (videos.Count > 0) await RangeAsync(http, "S17 video", $"api/media/{videos[0]}/video");
        else Info("S17 video", "no videos");
        if (live.Count > 0) await RangeAsync(http, "S18 Live Photo video", $"api/media/{live[0]}/motion", allowConflict: true);
        else Info("S18 Live Photo video", "no Live Photos");

        // 6. Sign out: the token stops working.
        using var logout = new HttpRequestMessage(HttpMethod.Post, "api/logout");
        logout.Headers.Add(RemoteServer.ScriptHeader, "1");
        (await http.SendAsync(logout)).Dispose();
        await ExpectAsync("S19 signed out", http, new HttpRequestMessage(HttpMethod.Get, "api/items"), HttpStatusCode.Unauthorized);
        return Summary();
    }

    private static async Task<(List<long> Ids, List<long> Dates, List<long> Flags)> ItemsAsync(HttpClient http, string name, string path)
    {
        var clock = Stopwatch.StartNew();
        using var response = await http.GetAsync(path);
        var bytes = await response.Content.ReadAsByteArrayAsync();
        if (!response.IsSuccessStatusCode)
        {
            Fail(name, $"{(int)response.StatusCode}");
            return ([], [], []);
        }
        using var json = JsonDocument.Parse(bytes);
        var root = json.RootElement;
        List<long> Read(string property) => root.GetProperty(property).EnumerateArray().Select(e => e.GetInt64()).ToList();
        var ids = Read("ids");
        var pictures = root.TryGetProperty("pictures", out var p) && p.ValueKind == JsonValueKind.True ? " (pictures matched too)" : "";
        var encoding = response.Content.Headers.ContentEncoding.FirstOrDefault() ?? "not compressed";
        Pass(name, $"{ids.Count:N0} items in {clock.ElapsedMilliseconds} ms, {bytes.Length / 1024.0:N0} KB after decompression ({encoding}){pictures}");
        return (ids, Read("dates"), Read("flags"));
    }

    private static async Task CountAsync(HttpClient http, string name, string path)
    {
        var clock = Stopwatch.StartNew();
        try
        {
            var rows = await http.GetFromJsonAsync<JsonElement>(path);
            Pass(name, $"{rows.GetArrayLength():N0} in {clock.ElapsedMilliseconds} ms");
        }
        catch (HttpRequestException ex)
        {
            Fail(name, ex.Message);
        }
    }

    private static async Task ThumbnailsAsync(HttpClient http, List<long> ids)
    {
        var clock = Stopwatch.StartNew();
        int ok = 0, missing = 0;
        long bytes = 0;
        await Parallel.ForEachAsync(ids, new ParallelOptions { MaxDegreeOfParallelism = 12 }, async (id, ct) =>
        {
            using var response = await http.GetAsync($"api/media/{id}/thumb", ct);
            if (response.IsSuccessStatusCode)
            {
                Interlocked.Add(ref bytes, (await response.Content.ReadAsByteArrayAsync(ct)).Length);
                Interlocked.Increment(ref ok);
            }
            else Interlocked.Increment(ref missing);
        });
        var line = $"{ok} of {ids.Count} newest in {clock.ElapsedMilliseconds} ms ({(ok > 0 ? bytes / ok / 1024 : 0)} KB each)";
        if (missing == 0) Pass("S15 thumbnails", line);
        else Fail("S15 thumbnails", line + $", {missing} missing");
    }

    private static async Task DisplayAsync(HttpClient http, List<long> ids)
    {
        if (ids.Count == 0)
        {
            Info("S16 photos at screen size", "no photos");
            return;
        }
        var times = new List<long>();
        var failures = 0;
        foreach (var id in ids)
        {
            var clock = Stopwatch.StartNew();
            using var response = await http.GetAsync($"api/media/{id}/display?size=2560");
            if (response.IsSuccessStatusCode && response.Content.Headers.ContentType?.MediaType == "image/jpeg")
            {
                await response.Content.ReadAsByteArrayAsync();
                times.Add(clock.ElapsedMilliseconds);
            }
            else failures++;
        }
        var line = times.Count > 0 ? $"{times.Count} photos, {times.Average():N0} ms average, slowest {times.Max()} ms" : "none";
        if (failures == 0) Pass("S16 photos at screen size", line);
        else Fail("S16 photos at screen size", line + $", {failures} failed (a cloud-only file, or a codec missing on the host?)");
    }

    private static async Task RangeAsync(HttpClient http, string name, string path, bool allowConflict = false)
    {
        var clock = Stopwatch.StartNew();
        using var request = new HttpRequestMessage(HttpMethod.Get, path);
        request.Headers.Range = new RangeHeaderValue(0, 65535);
        using var response = await http.SendAsync(request);
        if (response.StatusCode == HttpStatusCode.PartialContent)
            Pass(name, $"206 in {clock.ElapsedMilliseconds} ms, {response.Content.Headers.ContentType?.MediaType}, {response.Content.Headers.ContentRange}");
        else if (allowConflict && response.StatusCode == HttpStatusCode.Conflict)
            Info(name, "the video is only in OneDrive and the host's OneDrive web session isn't connected: " + await ErrorOf(response));
        else
            Fail(name, $"{(int)response.StatusCode}: {await ErrorOf(response)}");
    }

    private static async Task ExpectAsync(string name, HttpClient http, HttpRequestMessage request, HttpStatusCode expected)
    {
        using (request)
        using (var response = await http.SendAsync(request))
        {
            if (response.StatusCode == expected) Pass(name, $"refused with {(int)expected}, as it should be");
            else Fail(name, $"answered {(int)response.StatusCode}, expected {(int)expected}");
        }
    }

    private static async Task<string> ErrorOf(HttpResponseMessage response)
    {
        try
        {
            var body = await response.Content.ReadFromJsonAsync<JsonElement>();
            return body.TryGetProperty("error", out var error) ? error.GetString() ?? "" : "";
        }
        catch (Exception ex) when (ex is JsonException or NotSupportedException)
        {
            return "";
        }
    }

    private static string Date(long unixLocal) => unixLocal == 0 ? "-" : DateTime.UnixEpoch.AddSeconds(unixLocal).ToString("yyyy-MM-dd");

    private static string? Option(string[] args, string name)
    {
        var at = Array.IndexOf(args, name);
        return at >= 0 && at + 1 < args.Length ? args[at + 1] : null;
    }

    private static string Normalize(string code) => new(code.Where(char.IsLetterOrDigit).Select(char.ToUpperInvariant).ToArray());

    private static void Pass(string name, string detail) => Console.WriteLine($"PASS  {name}: {detail}");

    private static void Info(string name, string detail) => Console.WriteLine($"INFO  {name}: {detail}");

    private static void Fail(string name, string detail)
    {
        _failed++;
        Console.WriteLine($"FAIL  {name}: {detail}");
    }

    private static int Summary()
    {
        Console.WriteLine(_failed == 0 ? "All checks passed." : $"{_failed} check(s) failed.");
        return _failed == 0 ? 0 : 1;
    }
}
