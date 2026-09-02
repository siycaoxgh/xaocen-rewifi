namespace XAOCEN.ReWiFi;

internal sealed class OfflineAuthorizationManager
{
    private readonly OfflineLicenseService _licenseService;
    private readonly AccountClient _accountClient;
    private readonly OfflineLicenseStore _licenseStore;

    public OfflineAuthorizationManager(
        AccountClient accountClient,
        OfflineLicenseService? licenseService = null,
        OfflineLicenseStore? licenseStore = null)
    {
        _accountClient = accountClient;
        _licenseService = licenseService ?? new OfflineLicenseService();
        _licenseStore = licenseStore ?? new OfflineLicenseStore();
    }

    public bool HasStoredLicense => !string.IsNullOrWhiteSpace(_licenseStore.Read());

    public OfflineAuthorizationDecision? LastDecision { get; private set; }

    public event Action? StateChanged;

    public string GetDevicePublicKey() => _licenseService.GetOrCreateDevicePublicKey();

    public OfflineLicenseValidationResult ImportLicense(string compactLicense)
    {
        var result = _licenseService.ValidateCompactLicense(compactLicense, _licenseService.GetDeviceKeyHash());
        LastDecision = null;
        if (result.IsValid)
        {
            _licenseStore.Write(compactLicense);
        }

        StateChanged?.Invoke();

        return result;
    }

    public OfflineLicenseValidationResult ValidateStoredLicense()
    {
        var compactLicense = _licenseStore.Read();
        return compactLicense is null
            ? OfflineLicenseValidationResult.Fail(OfflineLicenseValidationStatus.Malformed, "当前没有离线授权文件。")
            : _licenseService.ValidateCompactLicense(compactLicense, _licenseService.GetDeviceKeyHash());
    }

    public async Task<OfflineAuthorizationDecision> EvaluateAsync(CancellationToken cancellationToken = default)
    {
        var compactLicense = _licenseStore.Read();
        if (string.IsNullOrWhiteSpace(compactLicense))
        {
            return Remember(OfflineAuthorizationDecision.NoLicense());
        }

        var devicePublicKey = _licenseService.GetOrCreateDevicePublicKey();
        var deviceKeyHash = _licenseService.GetDeviceKeyHash();
        var localResult = _licenseService.ValidateCompactLicense(compactLicense, deviceKeyHash);
        if (!localResult.IsValid)
        {
            return Remember(OfflineAuthorizationDecision.Rejected(localResult));
        }

        try
        {
            var remote = await _accountClient.CheckOfflineLicenseAsync(compactLicense, devicePublicKey, cancellationToken)
                .ConfigureAwait(false);
            if (!remote.Valid)
            {
                return Remember(OfflineAuthorizationDecision.Rejected(
                    OfflineLicenseValidationResult.Fail(OfflineLicenseValidationStatus.PolicyExpired, "服务器拒绝了当前离线授权。")));
            }

            if (remote.ReauthorizationRequired)
            {
                return Remember(OfflineAuthorizationDecision.Rejected(
                    OfflineLicenseValidationResult.Fail(OfflineLicenseValidationStatus.ReauthorizationRequired, "离线授权需要联网重新授权。")));
            }

            if (remote.CheckRequired)
            {
                var refreshed = await _accountClient.RefreshOfflineLicenseAsync(compactLicense, devicePublicKey, cancellationToken)
                    .ConfigureAwait(false);
                var refreshedResult = _licenseService.ValidateCompactLicense(refreshed, deviceKeyHash);
                if (!refreshedResult.IsValid)
                {
                    return Remember(OfflineAuthorizationDecision.Rejected(refreshedResult));
                }

                _licenseStore.Write(refreshed);
            }

            return Remember(OfflineAuthorizationDecision.Online(localResult, remote));
        }
        catch (AccountHttpException ex) when (ex.StatusCode is >= 500 or 429)
        {
            return Remember(OfflineAuthorizationDecision.Offline(localResult, "Account 服务暂时不可用，已使用本地离线校验。"));
        }
        catch (HttpRequestException)
        {
            return Remember(OfflineAuthorizationDecision.Offline(localResult, "当前无法连接 Account，已使用本地离线校验。"));
        }
        catch (TaskCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            return Remember(OfflineAuthorizationDecision.Offline(localResult, "Account 请求超时，已使用本地离线校验。"));
        }
    }

    private OfflineAuthorizationDecision Remember(OfflineAuthorizationDecision decision)
    {
        LastDecision = decision;
        StateChanged?.Invoke();
        return decision;
    }
}

internal sealed class OfflineLicenseStore
{
    private const string FileName = "offline-license.xaocen-license";

    public string FilePath => Path.Combine(AppConfig.DirectoryPath, FileName);

    public string? Read()
    {
        try
        {
            return File.Exists(FilePath) ? File.ReadAllText(FilePath).Trim() : null;
        }
        catch (IOException)
        {
            return null;
        }
        catch (UnauthorizedAccessException)
        {
            return null;
        }
    }

    public void Write(string compactLicense)
    {
        Directory.CreateDirectory(AppConfig.DirectoryPath);
        File.WriteAllText(FilePath, compactLicense.Trim() + Environment.NewLine);
    }
}

internal enum OfflineAuthorizationMode
{
    NoLicense,
    Online,
    Offline,
    Rejected
}

internal sealed record OfflineAuthorizationDecision(
    OfflineAuthorizationMode Mode,
    OfflineLicenseValidationResult LocalResult,
    OfflineLicenseCheckResponse? OnlineResult,
    string Message)
{
    public bool IsAllowed => Mode is OfflineAuthorizationMode.Online or OfflineAuthorizationMode.Offline;

    public static OfflineAuthorizationDecision NoLicense() => new(
        OfflineAuthorizationMode.NoLicense,
        OfflineLicenseValidationResult.Fail(OfflineLicenseValidationStatus.Malformed, "当前没有离线授权文件。"),
        null,
        "当前没有离线授权文件。");

    public static OfflineAuthorizationDecision Offline(OfflineLicenseValidationResult localResult, string message) => new(
        OfflineAuthorizationMode.Offline, localResult, null, message);

    public static OfflineAuthorizationDecision Online(
        OfflineLicenseValidationResult localResult,
        OfflineLicenseCheckResponse onlineResult) => new(
        OfflineAuthorizationMode.Online,
        localResult,
        onlineResult,
        "Account 在线校验通过。");

    public static OfflineAuthorizationDecision Rejected(OfflineLicenseValidationResult localResult) => new(
        OfflineAuthorizationMode.Rejected, localResult, null, localResult.Message);
}
