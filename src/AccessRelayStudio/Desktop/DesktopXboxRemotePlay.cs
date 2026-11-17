using System.Diagnostics;
using System.Security.Cryptography;
using System.Text;
using EliteScriptStudio.RemotePlay;
using Windows.Security.Credentials;

namespace EliteScriptStudio.Desktop;

internal sealed partial class DesktopVisionService
{
    private static readonly HttpClient XboxHttp = new(new HttpClientHandler { AllowAutoRedirect = false }) { Timeout = TimeSpan.FromSeconds(20) };
    private readonly XboxHomeAccount _xboxAccount = new(XboxHttp, new RefreshStore());
    private IReadOnlyList<XboxConsole> _consoles = [];
    private XboxDevicePrompt? _signInPrompt;
    private XboxConsole? _console;
    private XboxRemoteConnection? _xbox;
    private bool _direct, _signedIn;
    private string _windowSelection = "", _consoleMessage = "";

    private sealed class RefreshStore : IXboxRefreshStore
    {
        private const string Resource = "AccessRelayStudio.XboxHome";
        private const string User = "Microsoft account";
        public string? Read()
        {
            try { var credential = new PasswordVault().Retrieve(Resource, User); credential.RetrievePassword(); return credential.Password; }
            catch (Exception e) when (e.HResult == unchecked((int)0x80070490)) { return null; }
        }
        public void Save(string value) => new PasswordVault().Add(new PasswordCredential(Resource, User, value));
        public void Clear()
        {
            var vault = new PasswordVault();
            try { vault.Remove(vault.Retrieve(Resource, User)); }
            catch (Exception e) when (e.HResult == unchecked((int)0x80070490)) { }
        }
    }

    private string DirectSourceId => _console is null ? "" : "xhome-" + Convert.ToHexStringLower(SHA256.HashData(
        Encoding.UTF8.GetBytes("xhome-webrtc-bgra-v1\ndirect-gamepad-v1\n" + _console.Id)));
    private string SelectedSourceTitle => _direct ? _console?.Name ?? "Xbox Remote Play" : SelectedSource().Title;
    private object XboxSnapshot() => new
    {
        direct = _direct, signedIn = _signedIn, consoles = _consoles, consoleId = _console?.Id,
        signInCode = _signInPrompt?.Code, verificationPage = _signInPrompt?.VerificationUri.AbsoluteUri,
        connected = _xbox?.Peer is not null, message = _xbox?.Status ?? _consoleMessage, statistics = _xbox?.Stats
    };

    private void InvalidateSource(string id)
    {
        _selectedSourceId = id; _version++; ClearPreview(); _calibration = null;
        _settings = _settings with { CaptureSourceKey = _direct ? id : WindowSourceKey(id), TrainingCapture = _settings.TrainingCapture with { Configured = false } };
        _runtime()?.SetVisionArmed(false);
    }

    private async Task DisconnectXboxAsync()
    {
        if (_xbox is not { } connection) return;
        _luaController?.StopOutput();
        _xbox = null;
        await connection.CloseAsync();
        if (_direct) InvalidateSource("");
    }

    private async Task XboxActionAsync(string action, System.Text.Json.JsonElement args, CancellationToken ct)
    {
        RequireStopped();
        if (action == "captureMode")
        {
            string mode = args.Text("id");
            if (mode is not ("window" or "xbox")) throw new InvalidDataException("Choose a capture source.");
            bool direct = mode == "xbox";
            if (direct == _direct) return;
            if (direct)
            {
                _signedIn = _xboxAccount.HasAccount; _windowSelection = _selectedSourceId; _consoleMessage = "";
                _luaController?.StopOutput();
            }
            else await DisconnectXboxAsync();
            _direct = direct; InvalidateSource(direct ? "" : _windowSelection); RefreshSources();
            _notice = direct ? "Choose your Xbox console." : "Choose a gameplay window.";
            return;
        }
        if (!_direct) throw new InvalidOperationException("Select direct Xbox Remote Play first.");
        switch (action)
        {
            case "xboxSignIn":
                if (_xbox is not null) await DisconnectXboxAsync();
                await _xboxAccount.SignInAsync(prompt =>
                {
                    _signInPrompt = prompt;
                    if (prompt is not null) Process.Start(new ProcessStartInfo(prompt.VerificationUri.AbsoluteUri) { UseShellExecute = true });
                }, ct);
                _signedIn = true; _console = null; _consoles = [];
                goto case "xboxRefresh";
            case "xboxRefresh":
                try
                {
                    var service = await _xboxAccount.ServiceAsync(ct);
                    var consoles = await service.ConsolesAsync(ct);
                    ct.ThrowIfCancellationRequested(); _consoles = consoles;
                    if (_xbox is null) _console = consoles.FirstOrDefault(x => x.Id == _console?.Id) ?? consoles.FirstOrDefault();
                    _consoleMessage = _notice = consoles.Count == 0 ? "No home consoles were found for this account." : "Choose a console to connect.";
                }
                finally { _signedIn = _xboxAccount.HasAccount; }
                break;
            case "xboxConsole":
                if (_xbox is { Ended: false }) throw new InvalidOperationException("Disconnect before changing consoles.");
                _console = _consoles.FirstOrDefault(x => x.Id == args.Text("id")) ?? throw new InvalidOperationException("Refresh the console list.");
                break;
            case "xboxConnect":
                if (_console is null) throw new InvalidOperationException("Choose a console first.");
                if (_xbox is { Ended: false }) return;
                await DisconnectXboxAsync();
                var client = await _xboxAccount.ServiceAsync(ct);
                var connection = _xbox = new XboxRemoteConnection(client, _console.Id);
                using (ct.Register(connection.Cancel))
                {
                    try { await connection.Ready; ct.ThrowIfCancellationRequested(); }
                    catch { await DisconnectXboxAsync(); throw; }
                }
                InvalidateSource(DirectSourceId); _notice = "Xbox connected. Start capture to observe the stream.";
                break;
            case "xboxDisconnect": await DisconnectXboxAsync(); _consoleMessage = _notice = "Xbox disconnected."; break;
            case "xboxSignOut":
                await DisconnectXboxAsync(); _xboxAccount.SignOut(); _signedIn = false; _consoles = []; _console = null; InvalidateSource("");
                _consoleMessage = _notice = "Signed out of Xbox."; break;
            default: throw new InvalidDataException("Unknown Xbox Remote Play command.");
        }
    }
}
