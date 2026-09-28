using System.Net.Http.Json;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using System.Text.Json;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;
using Microsoft.UI.Xaml.Navigation;
using Microsoft.Web.WebView2.Core;
using PhotoGallery.App.Services;
using PhotoGallery.Core;
using PhotoGallery.Remote;
using Windows.Security.Credentials;

namespace PhotoGallery.App.Pages;

/// <summary>
/// The library on another computer (its Remote access). The other computer made its own certificate, so nothing
/// vouches for it: the first time, its security code is shown to compare with the one in its Settings, and from then on
/// only that certificate is accepted, both here and in the browser view showing its web app. The passphrase is only
/// ever sent over a connection with that certificate.
/// </summary>
public sealed partial class RemotePage : Page
{
    private const string VaultResource = "Photo Gallery remote access";

    private RemoteAddress? _address;
    private string? _fingerprint;
    /// <summary>Given to the other computer's page once it has loaded, then forgotten.</summary>
    private string? _passphrase;
    private bool _remember;
    /// <summary>Signed in again with the saved passphrase after the other computer signed this one out (once, until it works).</summary>
    private bool _signedInAgain;
    private bool _hooked;
    private bool? _paneWasOpen;

    public RemotePage()
    {
        InitializeComponent();
        NavigationCacheMode = NavigationCacheMode.Required; // coming back finds the other computer's page where it was
        ActualThemeChanged += (_, _) =>
        {
            if (Web.CoreWebView2 is { } core) core.Profile.PreferredColorScheme = Scheme;
        };
    }

    /// <summary>Showing the other computer's library (the title bar's search box and Back then work on it).</summary>
    public bool IsBrowsing => BrowserPanel.Visibility == Visibility.Visible;

    public bool CanGoBack => IsBrowsing && Web.CoreWebView2?.CanGoBack == true;

    public void GoBack() => Web.CoreWebView2?.GoBack();

    public void Search(string text) => Run($"window.photoGallery && window.photoGallery.search({JsonSerializer.Serialize(text)})");

    public void ClearSearch() => Run("window.photoGallery && window.photoGallery.clearSearch()");

    private void Run(string script)
    {
        if (Web.CoreWebView2 is { } core && _address?.Owns(core.Source) == true) _ = core.ExecuteScriptAsync(script);
    }

    private CoreWebView2PreferredColorScheme Scheme => ActualTheme == ElementTheme.Dark ? CoreWebView2PreferredColorScheme.Dark : CoreWebView2PreferredColorScheme.Light;

    protected override async void OnNavigatedTo(NavigationEventArgs e)
    {
        if (IsBrowsing)
        {
            CollapsePane();
            return;
        }
        var settings = App.Services.Settings;
        ComputerBox.Text = settings.RemoteComputer ?? "";
        OpenAtStartBox.IsChecked = settings.RemoteOpenAtStart;
        var saved = RemoteAddress.Parse(settings.RemoteComputer) is { } address ? LoadPassphrase(address.Key) : null;
        ForgetButton.Visibility = saved is null ? Visibility.Collapsed : Visibility.Visible;
        if (saved is not null)
        {
            PassphraseBox.Password = saved;
            await ConnectAsync();
        }
        else
        {
            (ComputerBox.Text.Length == 0 ? (Control)ComputerBox : PassphraseBox).Focus(FocusState.Programmatic);
        }
    }

    protected override void OnNavigatedFrom(NavigationEventArgs e) => RestorePane();

    private void OnFieldKeyDown(object sender, Microsoft.UI.Xaml.Input.KeyRoutedEventArgs e)
    {
        if (e.Key != Windows.System.VirtualKey.Enter) return;
        e.Handled = true;
        _ = ConnectAsync();
    }

    private void OnConnect(object sender, RoutedEventArgs e) => _ = ConnectAsync();

    private async Task ConnectAsync()
    {
        ConnectError.IsOpen = false;
        if (RemoteAddress.Parse(ComputerBox.Text) is not { } address)
        {
            ShowError("Enter the other computer's name (shown in its Settings → Remote access) or its address.");
            return;
        }
        var passphrase = PassphraseBox.Password;
        if (passphrase.Trim().Length == 0)
        {
            ShowError("Enter the passphrase set on the other computer.");
            return;
        }
        ConnectButton.IsEnabled = false;
        ConnectBusy.IsActive = true;
        try
        {
            var (name, fingerprint, problem) = await HelloAsync(address);
            if (problem is not null)
            {
                ShowError(problem);
                return;
            }
            if (!await TrustAsync(address, name!, fingerprint!)) return;

            var settings = App.Services.Settings;
            settings.RemoteComputer = ComputerBox.Text.Trim();
            settings.RemoteOpenAtStart = OpenAtStartBox.IsChecked == true;
            App.Services.SaveSettings();
            _remember = RememberBox.IsChecked == true;
            if (!_remember) ForgetPassphrase(address.Key);
            await OpenAsync(address, name!, fingerprint!, passphrase);
        }
        finally
        {
            ConnectButton.IsEnabled = true;
            ConnectBusy.IsActive = false;
        }
    }

    private void ShowError(string message)
    {
        ConnectError.Message = message;
        ConnectError.IsOpen = true;
    }

    /// <summary>Reaches the other computer and sees its certificate (nothing is sent but the question).</summary>
    private static async Task<(string? Name, string? Fingerprint, string? Problem)> HelloAsync(RemoteAddress address)
    {
        string? seen = null;
        using var handler = new HttpClientHandler
        {
            UseCookies = false,
            // Any certificate is let through, only to learn which it is; it's compared with the accepted one before anything else.
            ServerCertificateCustomValidationCallback = (_, certificate, _, _) =>
            {
                seen = certificate is null ? null : HostCertificate.Fingerprint(certificate);
                return certificate is not null;
            },
        };
        using var http = new HttpClient(handler) { Timeout = TimeSpan.FromSeconds(10) };
        var host = address.Host.Trim('[', ']');
        try
        {
            var hello = await http.GetFromJsonAsync<JsonElement>(address.Origin + "/api/hello");
            var name = hello.TryGetProperty("name", out var value) ? value.GetString() : null;
            return seen is null || string.IsNullOrWhiteSpace(name)
                ? (null, null, $"{host} answered, but not as Photo Gallery's remote access.")
                : (name, seen, null);
        }
        catch (HttpRequestException ex)
        {
            return (null, null, $"Couldn't reach {host} on port {address.Port} ({ex.Message}). Check that it's on and awake, that Remote access is " +
                                "turned on in its Settings, and that Windows let Photo Gallery use the network there (private networks).");
        }
        catch (TaskCanceledException)
        {
            return (null, null, $"{host} didn't answer on port {address.Port}. Check that Remote access is turned on in its Settings, and the port.");
        }
        catch (Exception ex) when (ex is JsonException or NotSupportedException)
        {
            return (null, null, $"{host} answered, but not as Photo Gallery's remote access.");
        }
    }

    /// <summary>
    /// The certificate this PC accepted for that computer before, or (the first time, or if it changed) the user's word
    /// that its security code matches the one on the other computer.
    /// </summary>
    private async Task<bool> TrustAsync(RemoteAddress address, string name, string fingerprint)
    {
        var pins = App.Services.Settings.RemotePins;
        if (pins.TryGetValue(address.Key, out var pinned) && pinned == fingerprint) return true;
        var changed = pinned is not null;
        var content = new StackPanel { Spacing = 14, MaxWidth = 460 };
        content.Children.Add(new TextBlock
        {
            TextWrapping = TextWrapping.Wrap,
            Text = changed
                ? $"{name}'s security code isn't the one it had before. That happens when Windows or Photo Gallery's certificate there was reset, " +
                  $"but it could also be another device pretending to be {name}. Only continue if {name} shows this same code, in Settings → Remote access."
                : $"This PC hasn't connected to {name} before. To be sure it's really {name}, check that it shows this same security code, " +
                  "in Settings → Remote access.",
        });
        content.Children.Add(new TextBlock
        {
            Text = HostCertificate.SecurityCode(fingerprint),
            FontFamily = new FontFamily("Consolas"),
            FontSize = 28,
            HorizontalAlignment = HorizontalAlignment.Center,
            IsTextSelectionEnabled = true,
        });
        var dialog = new ContentDialog
        {
            XamlRoot = XamlRoot,
            Title = changed ? "The security code changed" : $"Connect to {name}?",
            Content = content,
            PrimaryButtonText = "The codes match",
            CloseButtonText = "Cancel",
            DefaultButton = changed ? ContentDialogButton.Close : ContentDialogButton.Primary,
        };
        if (await dialog.ShowAsync() != ContentDialogResult.Primary) return false;
        pins[address.Key] = fingerprint;
        App.Services.SaveSettings();
        return true;
    }

    private async Task OpenAsync(RemoteAddress address, string name, string fingerprint, string passphrase)
    {
        CoreWebView2 core;
        try
        {
            core = await WebViewHost.StartAsync(Web);
        }
        catch (Exception ex)
        {
            ShowError($"The browser view didn't start: {ex.Message}");
            return;
        }
        if (!_hooked)
        {
            _hooked = true;
            core.ServerCertificateErrorDetected += OnCertificateError;
            core.NavigationStarting += OnNavigationStarting;
            core.NavigationCompleted += OnNavigationCompleted;
            core.NewWindowRequested += OnNewWindowRequested;
            core.WebMessageReceived += OnWebMessage;
            core.HistoryChanged += (_, _) => App.MainWindow.RefreshBackButton();
        }
        (_address, _fingerprint, _passphrase, _signedInAgain) = (address, fingerprint, passphrase, false);
        core.Profile.PreferredColorScheme = Scheme;
        ConnectedText.Text = $"Photos on {name}";
        ConnectPanel.Visibility = Visibility.Collapsed;
        BrowserPanel.Visibility = Visibility.Visible;
        CollapsePane();
        core.Navigate(address.Origin + "/?embedded=1");
    }

    /// <summary>Only the accepted certificate, and only for that computer: anything else is refused.</summary>
    private void OnCertificateError(CoreWebView2 sender, CoreWebView2ServerCertificateErrorDetectedEventArgs e)
    {
        e.Action = _address?.Owns(e.RequestUri) == true && _fingerprint is { } accepted && FingerprintOf(e.ServerCertificate) == accepted
            ? CoreWebView2ServerCertificateErrorAction.AlwaysAllow
            : CoreWebView2ServerCertificateErrorAction.Cancel;
    }

    private static string? FingerprintOf(CoreWebView2Certificate certificate)
    {
        try
        {
            using var parsed = X509Certificate2.CreateFromPem(certificate.ToPemEncoding());
            return HostCertificate.Fingerprint(parsed);
        }
        catch (CryptographicException)
        {
            return null;
        }
    }

    /// <summary>The view stays on the other computer; links elsewhere (the map) open in the usual browser.</summary>
    private void OnNavigationStarting(CoreWebView2 sender, CoreWebView2NavigationStartingEventArgs e)
    {
        if (_address?.Owns(e.Uri) == true || e.Uri.StartsWith("about:", StringComparison.Ordinal)) return;
        e.Cancel = true;
        OpenOutside(e.Uri);
    }

    private void OnNewWindowRequested(CoreWebView2 sender, CoreWebView2NewWindowRequestedEventArgs e)
    {
        e.Handled = true;
        OpenOutside(e.Uri);
    }

    private static void OpenOutside(string url)
    {
        if (Uri.TryCreate(url, UriKind.Absolute, out var uri) && (uri.Scheme == Uri.UriSchemeHttps || uri.Scheme == Uri.UriSchemeHttp))
            _ = Windows.System.Launcher.LaunchUriAsync(uri);
    }

    private void OnNavigationCompleted(CoreWebView2 sender, CoreWebView2NavigationCompletedEventArgs e)
    {
        if (_address?.Owns(sender.Source) != true) return;
        if (!e.IsSuccess)
        {
            ConnectedText.Text = $"Couldn't open the photos ({e.WebErrorStatus}). Try Reload.";
            return;
        }
        // Signs in through the page itself, so the page gets its own session (it does nothing if already signed in).
        if (_passphrase is { } passphrase) _ = sender.ExecuteScriptAsync($"window.photoGallery && window.photoGallery.signIn({JsonSerializer.Serialize(passphrase)})");
    }

    private void OnWebMessage(CoreWebView2 sender, CoreWebView2WebMessageReceivedEventArgs e)
    {
        if (_address?.Owns(e.Source) != true || _address is not { } address) return;
        try
        {
            using var message = JsonDocument.Parse(e.WebMessageAsJson);
            var root = message.RootElement;
            if (root.TryGetProperty("signedOut", out _))
            {
                // Signed out over there (it restarted, or the session ran out): sign in again with the saved passphrase,
                // unless a sign-in is already on its way.
                if (_passphrase is null && !_signedInAgain && LoadPassphrase(address.Key) is { } saved)
                {
                    _signedInAgain = true;
                    _passphrase = saved;
                    _ = sender.ExecuteScriptAsync($"window.photoGallery && window.photoGallery.signIn({JsonSerializer.Serialize(saved)})");
                }
                return;
            }
            if (!root.TryGetProperty("signedIn", out var signedIn)) return;
            if (signedIn.GetBoolean())
            {
                if (_remember && _passphrase is { } passphrase) SavePassphrase(address.Key, passphrase);
                _signedInAgain = false;
            }
            _passphrase = null; // the page asks for it itself from here on
        }
        catch (JsonException)
        {
        }
    }

    private void OnReload(object sender, RoutedEventArgs e) => Web.CoreWebView2?.Reload();

    private async void OnDisconnect(object sender, RoutedEventArgs e)
    {
        if (Web.CoreWebView2 is { } core && _address is { } address && _fingerprint is { } fingerprint)
        {
            // Signs this PC out there too, over a connection with the accepted certificate only.
            var cookies = await core.CookieManager.GetCookiesAsync(address.Origin);
            if (cookies.FirstOrDefault(c => c.Name == RemoteServer.CookieName)?.Value is { } session)
                await SignOutAsync(address, fingerprint, session);
            core.CookieManager.DeleteCookies(RemoteServer.CookieName, address.Origin);
            core.Navigate("about:blank");
        }
        (_address, _fingerprint, _passphrase) = (null, null, null);
        BrowserPanel.Visibility = Visibility.Collapsed;
        ConnectPanel.Visibility = Visibility.Visible;
        ForgetButton.Visibility = RemoteAddress.Parse(ComputerBox.Text) is { } typed && LoadPassphrase(typed.Key) is not null
            ? Visibility.Visible
            : Visibility.Collapsed;
        if (ForgetButton.Visibility == Visibility.Collapsed) PassphraseBox.Password = "";
        RestorePane();
        App.MainWindow.RefreshBackButton();
    }

    private static async Task SignOutAsync(RemoteAddress address, string fingerprint, string session)
    {
        using var handler = new HttpClientHandler
        {
            UseCookies = false,
            ServerCertificateCustomValidationCallback = (_, certificate, _, _) => certificate is not null && HostCertificate.Fingerprint(certificate) == fingerprint,
        };
        using var http = new HttpClient(handler) { Timeout = TimeSpan.FromSeconds(5) };
        using var request = new HttpRequestMessage(HttpMethod.Post, address.Origin + "/api/logout");
        request.Headers.Authorization = new System.Net.Http.Headers.AuthenticationHeaderValue("Bearer", session);
        request.Headers.Add(RemoteServer.ScriptHeader, "1");
        try
        {
            using var _ = await http.SendAsync(request);
        }
        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException)
        {
            // It forgets the session within 30 days anyway, or when it restarts.
        }
    }

    private void OnForget(object sender, RoutedEventArgs e)
    {
        if (RemoteAddress.Parse(ComputerBox.Text) is { } address) ForgetPassphrase(address.Key);
        if (RemoteAddress.Parse(App.Services.Settings.RemoteComputer) is { } saved) ForgetPassphrase(saved.Key);
        PassphraseBox.Password = "";
        ForgetButton.Visibility = Visibility.Collapsed;
    }

    // ---------- The menu pane: folded away while the other computer's page (with its own menu) shows ----------

    private void CollapsePane()
    {
        if (_paneWasOpen is not null) return;
        _paneWasOpen = App.MainWindow.IsPaneOpen;
        App.MainWindow.IsPaneOpen = false;
    }

    private void RestorePane()
    {
        if (_paneWasOpen is not { } open) return;
        _paneWasOpen = null;
        App.MainWindow.IsPaneOpen = open;
    }

    // ---------- The saved passphrase: Windows' Credential Manager, per computer ----------

    private static string? LoadPassphrase(string key)
    {
        try
        {
            var credential = new PasswordVault().Retrieve(VaultResource, key);
            credential.RetrievePassword();
            return credential.Password;
        }
        catch (COMException)
        {
            return null; // none saved
        }
    }

    private static void SavePassphrase(string key, string passphrase)
    {
        try
        {
            new PasswordVault().Add(new PasswordCredential(VaultResource, key, passphrase));
        }
        catch (COMException ex)
        {
            Log.Error("Couldn't save the remote access passphrase", ex);
        }
    }

    private static void ForgetPassphrase(string key)
    {
        try
        {
            var vault = new PasswordVault();
            vault.Remove(vault.Retrieve(VaultResource, key));
        }
        catch (COMException)
        {
        }
    }
}
