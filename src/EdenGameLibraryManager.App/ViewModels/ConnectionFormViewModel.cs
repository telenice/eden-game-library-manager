using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using EdenGameLibraryManager.Core.Interfaces;
using EdenGameLibraryManager.Core.Models;
using EdenGameLibraryManager.Core.Services;

namespace EdenGameLibraryManager.App.ViewModels;

/// <summary>
/// One server profile being edited: name, URL, credentials and "Test Connection". Used by the setup
/// wizard and by the profile list in Settings.
/// </summary>
public sealed partial class ConnectionFormViewModel : ViewModelBase
{
    private readonly IAeroFoilClient _client;

    public ConnectionFormViewModel(IAeroFoilClient client, ICredentialStore credentials, ServerProfile profile)
    {
        _client = client;
        Id = profile.Id;
        SupportsPasswordPersistence = credentials.SupportsPersistence;
        _profileName = profile.Name;
        _serverUrl = profile.ServerUrl;
        _username = profile.Username;
        _password = credentials.GetPassword(profile.Id) ?? string.Empty;
        _rememberPassword = profile.RememberPassword && credentials.SupportsPersistence;
        _allowInsecureHttp = profile.AllowInsecureHttp;
    }

    public string Id { get; }

    public bool SupportsPasswordPersistence { get; }

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(DisplayName))]
    private string _profileName;

    public string DisplayName => ToProfile(ServerUrl).DisplayName;

    /// <summary>The profile as it would be saved (password excluded).</summary>
    public ServerProfile ToProfile(string serverUrl) => new()
    {
        Id = Id,
        Name = ProfileName.Trim(),
        ServerUrl = serverUrl,
        Username = Username.Trim(),
        RememberPassword = RememberPassword,
        AllowInsecureHttp = AllowInsecureHttp,
    };

    public string PersistenceNote => SupportsPasswordPersistence
        ? (OperatingSystem.IsWindows() ? "Stored encrypted with Windows DPAPI for your user account."
            : OperatingSystem.IsMacOS() ? "Stored in your macOS login Keychain."
            : "Stored in your desktop keyring (Secret Service).")
        : "No secure keyring is available on this system (on Linux, install libsecret-tools), so the password is kept for this session only.";

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsHttpUrl), nameof(DisplayName))]
    private string _serverUrl;

    [ObservableProperty] private string _username;
    [ObservableProperty] private string _password;
    [ObservableProperty] private bool _rememberPassword;
    [ObservableProperty] private bool _allowInsecureHttp;
    [ObservableProperty] private bool _isTesting;
    [ObservableProperty] private string? _testMessage;
    [ObservableProperty] private bool? _testSucceeded;

    public bool IsHttpUrl => ServerUrl.TrimStart().StartsWith("http://", StringComparison.OrdinalIgnoreCase);
    public bool TestFailed => TestSucceeded == false;
    public bool TestPassed => TestSucceeded == true;

    partial void OnServerUrlChanged(string value) => ResetTest();
    partial void OnUsernameChanged(string value) => ResetTest();
    partial void OnPasswordChanged(string value) => ResetTest();
    partial void OnAllowInsecureHttpChanged(bool value) => ResetTest();

    partial void OnTestSucceededChanged(bool? value)
    {
        OnPropertyChanged(nameof(TestFailed));
        OnPropertyChanged(nameof(TestPassed));
    }

    /// <summary>Validates the URL and returns a connection, or sets <see cref="TestMessage"/> and returns null.</summary>
    public ServerConnection? TryBuildConnection()
    {
        try
        {
            var uri = UrlPolicy.NormalizeServerUrl(ServerUrl, AllowInsecureHttp);
            return new ServerConnection(uri, Username.Trim(), Password, AllowInsecureHttp);
        }
        catch (AeroFoilException ex)
        {
            TestMessage = ex.Message;
            TestSucceeded = false;
            return null;
        }
    }

    [RelayCommand]
    public async Task<bool> TestAsync()
    {
        var connection = TryBuildConnection();
        if (connection is null) return false;

        IsTesting = true;
        TestMessage = "Connecting…";
        TestSucceeded = null;
        try
        {
            var catalog = await Task.Run(() => _client.GetCatalogAsync(connection, CancellationToken.None));
            var baseGames = catalog.Items.Count(i => i.ContentType == ContentType.Base);
            var message = $"Connected. Authentication and catalogue access succeeded: {catalog.Items.Count} items " +
                          $"({baseGames} base games) via {catalog.Endpoint}.";
            if (catalog.Warnings.Count > 0) message += " " + string.Join(" ", catalog.Warnings);
            TestMessage = message;
            TestSucceeded = true;
            return true;
        }
        catch (AeroFoilException ex)
        {
            TestMessage = ex.Message;
            TestSucceeded = false;
            return false;
        }
        catch (Exception ex)
        {
            TestMessage = $"Unexpected error: {ex.Message}";
            TestSucceeded = false;
            return false;
        }
        finally
        {
            IsTesting = false;
        }
    }

    private void ResetTest()
    {
        TestSucceeded = null;
        TestMessage = null;
    }
}
