// Spike: can a third-party app get the video half of an iPhone Live Photo from OneDrive personal?
// The sync client only downloads the still. Known undocumented route (abraunegg/onedrive#1670):
//   api.onedrive.com/v1.0/drives/{driveId}/items/{itemId}/content?format=video&ump=1
// This probes that and several Graph variants and reports what comes back.
//
// Usage: dotnet run -- <clientId> ["OneDrive-relative path to a Live Photo"]
using System.Net;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using Microsoft.Identity.Client;
using Microsoft.Identity.Client.Extensions.Msal;

if (args.Length < 1)
{
    Console.WriteLine("Usage: dotnet run -- <clientId> [\"Pictures/Camera Roll/2026/09/20260924_031147076_iOS.heic\"]");
    return 1;
}
var clientId = args[0];
var itemPath = args.Length > 1 ? args[1] : "Pictures/Camera Roll/2026/09/20260924_031147076_iOS.heic";
var outDir = Path.Combine(AppContext.BaseDirectory, "out");
Directory.CreateDirectory(outDir);

// --- Auth: personal Microsoft accounts only, system browser, cached so reruns are silent.
var app = PublicClientApplicationBuilder.Create(clientId)
    .WithAuthority("https://login.microsoftonline.com/consumers")
    .WithRedirectUri("http://localhost")
    .Build();
var cacheProps = new StorageCreationPropertiesBuilder("spike.msalcache.bin", outDir).Build();
(await MsalCacheHelper.CreateAsync(cacheProps)).RegisterCache(app.UserTokenCache);

string[] scopes = ["Files.Read"];
AuthenticationResult auth;
try
{
    auth = await app.AcquireTokenSilent(scopes, (await app.GetAccountsAsync()).FirstOrDefault()).ExecuteAsync();
}
catch (MsalUiRequiredException)
{
    auth = await app.AcquireTokenInteractive(scopes).WithUseEmbeddedWebView(false).ExecuteAsync();
}
Console.WriteLine($"Signed in as {auth.Account.Username}\n");

using var http = new HttpClient(new HttpClientHandler { AllowAutoRedirect = false });
http.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", auth.AccessToken);

var encodedPath = string.Join('/', itemPath.Split('/').Select(Uri.EscapeDataString));

// --- 1. Metadata: does either endpoint surface a livePhoto facet / different size?
var local = Path.Combine(Environment.GetEnvironmentVariable("OneDrive") ?? "", itemPath.Replace('/', '\\'));
if (File.Exists(local)) Console.WriteLine($"Local file size: {new FileInfo(local).Length:N0}");

var graphItem = await GetJson($"https://graph.microsoft.com/v1.0/me/drive/root:/{encodedPath}");
if (graphItem is null) return 2;
var itemId = graphItem.Value.GetProperty("id").GetString()!;
var driveId = graphItem.Value.GetProperty("parentReference").GetProperty("driveId").GetString()!;
Console.WriteLine($"driveId={driveId} itemId={itemId}\n");

await GetJson($"https://graph.microsoft.com/beta/me/drive/items/{itemId}");
await GetJson($"https://api.onedrive.com/v1.0/drives/{driveId}/items/{itemId}?select=id,name,size,file,photo,image&ump=1");

// --- 2. Content variants. Each result is saved and sniffed for image vs QuickTime/MP4.
string[] contentUrls =
[
    $"https://graph.microsoft.com/v1.0/me/drive/items/{itemId}/content",
    $"https://graph.microsoft.com/v1.0/me/drive/items/{itemId}/content?format=video",
    $"https://graph.microsoft.com/beta/me/drive/items/{itemId}/content?format=video",
    $"https://api.onedrive.com/v1.0/drives/{driveId}/items/{itemId}/content?ump=1",
    $"https://api.onedrive.com/v1.0/drives/{driveId}/items/{itemId}/content?format=video&ump=1",
];
var n = 0;
foreach (var url in contentUrls)
{
    await Probe(HttpMethod.Get, url, ++n);
    if (url.Contains("format=video")) await Probe(HttpMethod.Post, url, ++n);
}
return 0;

async Task<JsonElement?> GetJson(string url)
{
    Console.WriteLine($"GET {url}");
    using var resp = await http.GetAsync(url);
    var body = await resp.Content.ReadAsStringAsync();
    Console.WriteLine($"  -> {(int)resp.StatusCode} {resp.StatusCode}");
    if (!resp.IsSuccessStatusCode) { Console.WriteLine($"  {Trim(body, 400)}\n"); return null; }
    var json = JsonDocument.Parse(body).RootElement;
    foreach (var key in new[] { "size", "file", "photo", "image", "video", "livePhoto" })
        if (json.TryGetProperty(key, out var v)) Console.WriteLine($"  {key}: {v.GetRawText()}");
    Console.WriteLine();
    return json;
}

async Task Probe(HttpMethod method, string url, int index)
{
    Console.WriteLine($"{method} {url}");
    var target = url;
    HttpResponseMessage resp;
    using (var req = new HttpRequestMessage(method, target)) resp = await http.SendAsync(req);
    // Follow a redirect manually, without the bearer token (pre-authenticated download URLs).
    if (resp.StatusCode is HttpStatusCode.Found or HttpStatusCode.Redirect or HttpStatusCode.TemporaryRedirect
        && resp.Headers.Location is { } loc)
    {
        Console.WriteLine($"  -> {(int)resp.StatusCode} redirect");
        resp.Dispose();
        using var plain = new HttpClient();
        resp = await plain.GetAsync(loc);
    }
    using (resp)
    {
        var bytes = await resp.Content.ReadAsByteArrayAsync();
        var disp = resp.Content.Headers.ContentDisposition?.ToString();
        Console.WriteLine($"  -> {(int)resp.StatusCode} {resp.Content.Headers.ContentType} {bytes.Length:N0} bytes {disp}");
        if (!resp.IsSuccessStatusCode) { Console.WriteLine($"  {Trim(Encoding.UTF8.GetString(bytes), 400)}\n"); return; }
        Console.WriteLine($"  sniff: {Sniff(bytes)}");
        var file = Path.Combine(outDir, $"{index:00}.bin");
        await File.WriteAllBytesAsync(file, bytes);
        Console.WriteLine($"  saved {file}\n");
    }
}

static string Sniff(byte[] b)
{
    if (b.Length < 12) return "too small";
    var brand = Encoding.ASCII.GetString(b, 4, 8);
    var hasMoov = b.AsSpan().IndexOf("moov"u8) >= 0;
    return brand switch
    {
        _ when brand.StartsWith("ftypqt") => $"QuickTime video (moov={hasMoov})",
        _ when brand.StartsWith("ftypheic") || brand.StartsWith("ftypmif1") => $"HEIC image (moov={hasMoov})",
        _ when brand.StartsWith("ftyp") => $"ISO-BMFF '{brand}' (moov={hasMoov})",
        _ when b[0] == 0xFF && b[1] == 0xD8 => "JPEG",
        _ => $"unknown: {Convert.ToHexString(b, 0, 12)}",
    };
}

static string Trim(string s, int max) => s.Length <= max ? s : s[..max] + "...";
