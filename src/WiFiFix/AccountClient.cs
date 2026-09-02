using System.Diagnostics;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;

namespace XAOCEN.ReWiFi;

internal sealed class AccountClient : IDisposable
{
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);
    private readonly HttpClient _httpClient;
    private readonly Uri _baseUri;

    public AccountClient(Uri? baseUri = null)
    {
        _baseUri = baseUri ?? new Uri(ProductInfo.AuthBaseUrl, UriKind.Absolute);
        _httpClient = new HttpClient
        {
            Timeout = TimeSpan.FromSeconds(15)
        };
    }

    public async Task<DeviceAuthorizationStart> StartDeviceAuthorizationAsync(string? devicePublicKey, CancellationToken cancellationToken)
    {
        var payload = new
        {
            productId = ProductInfo.AccountProductId,
            platform = ProductInfo.AccountPlatform,
            deviceName = ProductInfo.ProductName,
            devicePublicKey
        };
        using var response = await PostJsonAsync("/v1/auth/device/start", payload, cancellationToken).ConfigureAwait(false);
        using var document = await ReadJsonAsync(response, cancellationToken).ConfigureAwait(false);
        var root = document.RootElement;

        var deviceCode = GetString(root, "deviceCode", "device_code");
        var verificationUriComplete = GetString(root, "verificationUriComplete", "verification_uri_complete");
        if (string.IsNullOrWhiteSpace(deviceCode) || string.IsNullOrWhiteSpace(verificationUriComplete))
        {
            throw new AccountProtocolException("设备授权响应缺少必要字段。");
        }

        var expiresIn = GetInt(root, "expiresIn", "expires_in") ?? 900;
        var interval = GetInt(root, "interval") ?? 5;
        return new DeviceAuthorizationStart(
            deviceCode,
            verificationUriComplete,
            TimeSpan.FromSeconds(Math.Clamp(expiresIn, 30, 3600)),
            TimeSpan.FromSeconds(Math.Clamp(interval, 1, 60)));
    }

    public async Task<DeviceAuthorizationStatus> GetDeviceAuthorizationStatusAsync(
        string deviceCode,
        CancellationToken cancellationToken)
    {
        var payload = new { deviceCode };
        using var response = await PostJsonAsync("/v1/auth/device/status", payload, cancellationToken).ConfigureAwait(false);
        using var document = await ReadJsonAsync(response, cancellationToken).ConfigureAwait(false);
        var root = document.RootElement;
        var status = GetString(root, "status", "state", "authorizationStatus")?.Trim().ToLowerInvariant();
        var approved = GetBool(root, "approved", "authorized", "success") == true ||
                       status is "approved" or "authorized" or "complete" or "completed" or "success";
        var denied = GetBool(root, "denied", "rejected") == true ||
                     status is "denied" or "rejected" or "declined" or "access_denied";
        var expired = GetBool(root, "expired") == true ||
                      status is "expired" or "expired_token";
        var consumed = status is "consumed";
        return new DeviceAuthorizationStatus(approved, denied, expired, consumed, status);
    }

    public async Task<AccountTokenResponse> ExchangeDeviceTokenAsync(
        string deviceCode,
        CancellationToken cancellationToken)
    {
        var payload = new { deviceCode };
        using var response = await PostJsonAsync("/v1/auth/device/token", payload, cancellationToken).ConfigureAwait(false);
        using var document = await ReadJsonAsync(response, cancellationToken).ConfigureAwait(false);
        var root = document.RootElement;
        return ParseTokenResponse(document.RootElement, requireRefreshToken: true);
    }

    public async Task<AccountTokenResponse> RefreshDeviceSessionAsync(
        string refreshToken,
        CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(refreshToken))
        {
            throw new ArgumentException("刷新令牌不能为空。", nameof(refreshToken));
        }

        var payload = new { refreshToken };
        using var response = await PostJsonAsync("/v1/auth/device/refresh", payload, cancellationToken).ConfigureAwait(false);
        using var document = await ReadJsonAsync(response, cancellationToken).ConfigureAwait(false);
        return ParseTokenResponse(document.RootElement, requireRefreshToken: true);
    }

    public async Task LogoutDeviceSessionAsync(string refreshToken, CancellationToken cancellationToken)
    {
        await SendSessionCommandAsync("/v1/auth/device/logout", refreshToken, cancellationToken).ConfigureAwait(false);
    }

    public async Task RevokeDeviceSessionAsync(string refreshToken, CancellationToken cancellationToken)
    {
        await SendSessionCommandAsync("/v1/auth/device/revoke", refreshToken, cancellationToken).ConfigureAwait(false);
    }

    public async Task<OfflineLicenseCheckResponse> CheckOfflineLicenseAsync(
        string compactLicense,
        string devicePublicKey,
        CancellationToken cancellationToken)
    {
        var payload = new
        {
            compactLicense,
            devicePublicKey,
            productVersion = AppLogger.Version
        };
        using var response = await PostJsonAsync("/v1/auth/offline/check", payload, cancellationToken).ConfigureAwait(false);
        using var document = await ReadJsonAsync(response, cancellationToken).ConfigureAwait(false);
        return ParseOfflineLicenseCheckResponse(document.RootElement);
    }

    public async Task<string> RefreshOfflineLicenseAsync(
        string compactLicense,
        string devicePublicKey,
        CancellationToken cancellationToken)
    {
        var payload = new
        {
            compactLicense,
            devicePublicKey,
            productVersion = AppLogger.Version
        };
        using var response = await PostJsonAsync("/v1/auth/offline/refresh", payload, cancellationToken).ConfigureAwait(false);
        using var document = await ReadJsonAsync(response, cancellationToken).ConfigureAwait(false);
        var refreshedLicense = GetString(document.RootElement, "compactLicense", "license");
        if (string.IsNullOrWhiteSpace(refreshedLicense))
        {
            throw new AccountProtocolException("离线授权重新签发响应缺少 compactLicense。");
        }

        return refreshedLicense;
    }

    public async Task<AccountProfile> GetAccountAsync(string accessToken, CancellationToken cancellationToken)
    {
        using var request = new HttpRequestMessage(HttpMethod.Get, CreateUri("/v1/account/me"));
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", accessToken);
        using var response = await SendAsync(request, cancellationToken).ConfigureAwait(false);
        using var document = await ReadJsonAsync(response, cancellationToken).ConfigureAwait(false);
        var root = document.RootElement;
        return new AccountProfile(
            GetString(root, "id", "accountId", "userId"),
            GetString(root, "displayName", "name", "username"),
            GetString(root, "email"),
            GetBool(root, "emailVerified"),
            GetString(root, "status"),
            GetString(root, "locale"),
            GetString(root, "timezone"),
            GetBool(root, "deletionRequested"));
    }

    public async Task<IReadOnlyList<AccountEntitlement>> GetEntitlementsAsync(
        string accessToken,
        CancellationToken cancellationToken)
    {
        using var request = new HttpRequestMessage(HttpMethod.Get, CreateUri("/v1/account/entitlements"));
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", accessToken);
        using var response = await SendAsync(request, cancellationToken).ConfigureAwait(false);
        using var document = await ReadJsonAsync(response, cancellationToken).ConfigureAwait(false);
        var root = document.RootElement;
        var items = root.ValueKind == JsonValueKind.Array
            ? root.EnumerateArray()
            : root.TryGetProperty("entitlements", out var entitlements) && entitlements.ValueKind == JsonValueKind.Array
                ? entitlements.EnumerateArray()
                : throw new AccountProtocolException("权益响应缺少 entitlements 数组。");

        var result = new List<AccountEntitlement>();
        foreach (var item in items)
        {
            if (item.ValueKind != JsonValueKind.Object)
            {
                throw new AccountProtocolException("权益响应包含无效项目。");
            }

            result.Add(new AccountEntitlement(
                GetString(item, "entitlementId", "id"),
                GetString(item, "productId"),
                GetString(item, "licenseType"),
                GetString(item, "grantKind"),
                GetString(item, "source"),
                GetString(item, "status"),
                GetInt(item, "maxDevices") ?? 0,
                GetInt(item, "activeDevices") ?? 0,
                GetString(item, "validFrom"),
                GetString(item, "expiresAt"),
                GetString(item, "planCode"),
                GetBool(item, "autoRenew") ?? false,
                GetInt(item, "renewalPeriodDays"),
                GetString(item, "graceUntil"),
                GetString(item, "createdAt"),
                GetBool(item, "offlineAuthorizationAvailable") ?? false));
        }

        return result;
    }

    public async Task RegisterOnlineDeviceAsync(
        string accessToken,
        string devicePublicKey,
        CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(accessToken)) throw new ArgumentException("访问令牌不能为空。", nameof(accessToken));
        if (string.IsNullOrWhiteSpace(devicePublicKey)) throw new ArgumentException("设备公钥不能为空。", nameof(devicePublicKey));

        var payload = new
        {
            productId = ProductInfo.AccountProductId,
            platform = ProductInfo.AccountPlatform,
            deviceName = ProductInfo.ProductName,
            devicePublicKey
        };
        using var request = new HttpRequestMessage(HttpMethod.Post, CreateUri("/v1/account/device/register-online"))
        {
            Content = new StringContent(JsonSerializer.Serialize(payload, JsonOptions), Encoding.UTF8, "application/json")
        };
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", accessToken);
        using var response = await SendAsync(request, cancellationToken).ConfigureAwait(false);
    }

    private async Task<HttpResponseMessage> PostJsonAsync(string path, object payload, CancellationToken cancellationToken)
    {
        using var request = new HttpRequestMessage(HttpMethod.Post, CreateUri(path))
        {
            Content = new StringContent(JsonSerializer.Serialize(payload, JsonOptions), Encoding.UTF8, "application/json")
        };
        return await SendAsync(request, cancellationToken).ConfigureAwait(false);
    }

    private async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
    {
        request.Headers.TryAddWithoutValidation("X-Request-Id", Guid.NewGuid().ToString("N"));
        var response = await _httpClient.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, cancellationToken)
            .ConfigureAwait(false);
        if (!response.IsSuccessStatusCode)
        {
            var statusCode = (int)response.StatusCode;
            var responseRequestId = response.Headers.TryGetValues("x-request-id", out var requestIds)
                ? requestIds.FirstOrDefault()
                : null;
            string? code = null;
            string? message = null;
            try
            {
                using var document = await ReadJsonAsync(response, cancellationToken).ConfigureAwait(false);
                var root = document.RootElement;
                code = GetString(root, "code");
                message = GetString(root, "message");
                responseRequestId ??= GetString(root, "requestId", "request_id");
            }
            catch (JsonException)
            {
                // The status code remains the authoritative failure signal when a
                // gateway returns a non-JSON error page.
            }
            finally
            {
                response.Dispose();
            }

            throw new AccountHttpException(statusCode, code, message, responseRequestId);
        }

        return response;
    }

    private static async Task<JsonDocument> ReadJsonAsync(HttpResponseMessage response, CancellationToken cancellationToken)
    {
        await using var stream = await response.Content.ReadAsStreamAsync(cancellationToken).ConfigureAwait(false);
        return await JsonDocument.ParseAsync(stream, cancellationToken: cancellationToken).ConfigureAwait(false);
    }

    private Uri CreateUri(string path) => new(_baseUri, path);

    private async Task SendSessionCommandAsync(string path, string refreshToken, CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(refreshToken))
        {
            throw new ArgumentException("刷新令牌不能为空。", nameof(refreshToken));
        }

        var payload = new { refreshToken };
        using var response = await PostJsonAsync(path, payload, cancellationToken).ConfigureAwait(false);
    }

    private static AccountTokenResponse ParseTokenResponse(JsonElement root, bool requireRefreshToken)
    {
        var accessToken = GetString(root, "accessToken", "access_token");
        var refreshToken = GetString(root, "refreshToken", "refresh_token");
        if (string.IsNullOrWhiteSpace(accessToken) || (requireRefreshToken && string.IsNullOrWhiteSpace(refreshToken)))
        {
            throw new AccountProtocolException("令牌响应缺少访问令牌或刷新令牌。");
        }

        var expiresIn = GetInt(root, "expiresIn", "expires_in") ?? 3600;
        return new AccountTokenResponse(accessToken, refreshToken ?? string.Empty, TimeSpan.FromSeconds(Math.Clamp(expiresIn, 60, 86400)));
    }

    private static OfflineLicenseCheckResponse ParseOfflineLicenseCheckResponse(JsonElement root)
    {
        var valid = GetBool(root, "valid");
        if (valid is null)
        {
            throw new AccountProtocolException("离线检查响应缺少 valid 字段。");
        }

        return new OfflineLicenseCheckResponse(
            valid.Value,
            GetString(root, "productId"),
            GetString(root, "deviceId"),
            GetString(root, "licenseType"),
            GetBool(root, "checkRequired") ?? false,
            GetBool(root, "reauthorizationRequired") ?? false,
            GetString(root, "nextOnlineCheckAt"),
            GetString(root, "hardReauthorizeAt"));
    }

    private static string? GetString(JsonElement root, params string[] names)
    {
        foreach (var name in names)
        {
            if (root.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.String)
            {
                return value.GetString();
            }
        }

        return null;
    }

    private static int? GetInt(JsonElement root, params string[] names)
    {
        foreach (var name in names)
        {
            if (root.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.Number && value.TryGetInt32(out var number))
            {
                return number;
            }
        }

        return null;
    }

    private static bool? GetBool(JsonElement root, params string[] names)
    {
        foreach (var name in names)
        {
            if (root.TryGetProperty(name, out var value) && (value.ValueKind == JsonValueKind.True || value.ValueKind == JsonValueKind.False))
            {
                return value.GetBoolean();
            }
        }

        return null;
    }

    public void Dispose() => _httpClient.Dispose();
}

internal sealed class AccountSessionManager : IDisposable
{
    private readonly AccountClient _client;
    private readonly WindowsCredentialStore _credentialStore;
    private readonly Func<string>? _devicePublicKeyProvider;
    private readonly SemaphoreSlim _authorizationLock = new(1, 1);
    private CancellationTokenSource _shutdown = new();
    private AccountSession? _session;
    private IReadOnlyList<AccountEntitlement>? _entitlements;

    public AccountSessionManager(AccountClient? client = null, WindowsCredentialStore? credentialStore = null, Func<string>? devicePublicKeyProvider = null)
    {
        _client = client ?? new AccountClient();
        _credentialStore = credentialStore ?? new WindowsCredentialStore();
        _devicePublicKeyProvider = devicePublicKeyProvider;
    }

    public AccountSession? CurrentSession => _session;
    public IReadOnlyList<AccountEntitlement>? CurrentEntitlements => _entitlements;
    internal AccountClient Client => _client;
    public event Action<string>? StatusChanged;
    public event Action<DeviceAuthorizationProgress>? AuthorizationProgressChanged;

    public async Task<AccountProfile> AuthorizeAsync(CancellationToken cancellationToken = default)
    {
        await _authorizationLock.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            using var linked = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, _shutdown.Token);
            string? devicePublicKey = null;
            try
            {
                devicePublicKey = _devicePublicKeyProvider?.Invoke();
            }
            catch (Exception ex)
            {
                AppLogger.Warning($"无法读取稳定设备公钥，将继续进行账号授权但暂不登记产品设备：{ex.Message}");
            }

            var token = await _client.StartDeviceAuthorizationAsync(devicePublicKey, linked.Token).ConfigureAwait(false);
            ValidateVerificationUri(token.VerificationUriComplete);
            OpenVerificationUri(token.VerificationUriComplete);
            SetStatus("等待 XAOCEN Account 批准设备…");
            AppLogger.Info("XAOCEN Account 设备授权已发起。");

            var startedAt = DateTimeOffset.UtcNow;
            var expiresAt = startedAt.Add(token.ExpiresIn);
            var nextPollAt = startedAt.Add(token.PollInterval);
            var pollCount = 0;
            PublishAuthorizationProgress(new DeviceAuthorizationProgress(
                DeviceAuthorizationPhase.Waiting,
                startedAt,
                expiresAt,
                LastPollAt: null,
                NextPollAt: nextPollAt,
                PollCount: pollCount));

            while (DateTimeOffset.UtcNow < expiresAt)
            {
                await Task.Delay(token.PollInterval, linked.Token).ConfigureAwait(false);
                var status = await _client.GetDeviceAuthorizationStatusAsync(token.DeviceCode, linked.Token).ConfigureAwait(false);
                var lastPollAt = DateTimeOffset.UtcNow;
                pollCount++;
                if (status.Denied)
                {
                    PublishAuthorizationProgress(new DeviceAuthorizationProgress(
                        DeviceAuthorizationPhase.Denied,
                        startedAt,
                        expiresAt,
                        lastPollAt,
                        NextPollAt: null,
                        pollCount));
                    SetStatus("XAOCEN Account 授权已拒绝");
                    throw new AccountAuthorizationException("用户拒绝了设备授权。");
                }

                if (status.Expired || status.Consumed)
                {
                    PublishAuthorizationProgress(new DeviceAuthorizationProgress(
                        status.Consumed ? DeviceAuthorizationPhase.Consumed : DeviceAuthorizationPhase.Expired,
                        startedAt,
                        expiresAt,
                        lastPollAt,
                        NextPollAt: null,
                        pollCount));
                    SetStatus(status.Consumed ? "XAOCEN Account 授权码已使用" : "XAOCEN Account 授权已过期");
                    throw new AccountAuthorizationException(status.Consumed
                        ? "设备授权码已经使用，请重新发起授权。"
                        : "设备授权已过期，请重新发起授权。");
                }

                if (!status.Approved)
                {
                    nextPollAt = lastPollAt.Add(token.PollInterval);
                    PublishAuthorizationProgress(new DeviceAuthorizationProgress(
                        DeviceAuthorizationPhase.Waiting,
                        startedAt,
                        expiresAt,
                        lastPollAt,
                        nextPollAt,
                        pollCount));
                    continue;
                }

                PublishAuthorizationProgress(new DeviceAuthorizationProgress(
                    DeviceAuthorizationPhase.Approved,
                    startedAt,
                    expiresAt,
                    lastPollAt,
                    NextPollAt: null,
                    pollCount));
                SetStatus("设备已批准，正在获取账号信息…");
                var tokenResponse = await _client.ExchangeDeviceTokenAsync(token.DeviceCode, linked.Token).ConfigureAwait(false);
                var profile = await _client.GetAccountAsync(tokenResponse.AccessToken, linked.Token).ConfigureAwait(false);
                var entitlements = await TryGetEntitlementsAsync(tokenResponse.AccessToken, linked.Token).ConfigureAwait(false);
                if (!string.IsNullOrWhiteSpace(devicePublicKey))
                {
                    await _client.RegisterOnlineDeviceAsync(tokenResponse.AccessToken, devicePublicKey, linked.Token).ConfigureAwait(false);
                }
                _credentialStore.WriteRefreshToken(tokenResponse.RefreshToken);
                _session = new AccountSession(tokenResponse.AccessToken, tokenResponse.ExpiresIn, profile);
                _entitlements = entitlements;
                PublishAuthorizationProgress(new DeviceAuthorizationProgress(
                    DeviceAuthorizationPhase.Connected,
                    startedAt,
                    expiresAt,
                    lastPollAt,
                    NextPollAt: null,
                    pollCount));
                SetStatus("XAOCEN Account 已连接");
                AppLogger.Info("XAOCEN Account 在线扫码授权完成，账号信息验证成功。");
                return profile;
            }

            PublishAuthorizationProgress(new DeviceAuthorizationProgress(
                DeviceAuthorizationPhase.Expired,
                startedAt,
                expiresAt,
                LastPollAt: null,
                NextPollAt: null,
                pollCount));
            SetStatus("XAOCEN Account 授权超时");
            throw new AccountAuthorizationException("设备授权等待超时，请重新发起授权。");
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            SetStatus("XAOCEN Account 授权已取消");
            throw;
        }
        finally
        {
            _authorizationLock.Release();
        }
    }

    public async Task<bool> TryRestoreAsync(CancellationToken cancellationToken = default)
    {
        await _authorizationLock.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            var refreshToken = _credentialStore.ReadRefreshToken();
            if (string.IsNullOrWhiteSpace(refreshToken))
            {
                return false;
            }

            using var linked = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, _shutdown.Token);
            try
            {
                var token = await _client.RefreshDeviceSessionAsync(refreshToken, linked.Token).ConfigureAwait(false);
                var profile = await _client.GetAccountAsync(token.AccessToken, linked.Token).ConfigureAwait(false);
                var entitlements = await TryGetEntitlementsAsync(token.AccessToken, linked.Token).ConfigureAwait(false);
                try
                {
                    var devicePublicKey = _devicePublicKeyProvider?.Invoke();
                    if (!string.IsNullOrWhiteSpace(devicePublicKey))
                    {
                        await _client.RegisterOnlineDeviceAsync(token.AccessToken, devicePublicKey, linked.Token).ConfigureAwait(false);
                    }
                }
                catch (Exception ex)
                {
                    AppLogger.Warning($"在线设备登记未完成，会话仍已恢复：{ex.Message}");
                }
                _credentialStore.WriteRefreshToken(token.RefreshToken);
                _session = new AccountSession(token.AccessToken, token.ExpiresIn, profile);
                _entitlements = entitlements;
                SetStatus("XAOCEN Account 会话已恢复");
                AppLogger.Info("XAOCEN Account 会话恢复成功。");
                return true;
            }
            catch (AccountHttpException ex) when (ex.StatusCode == 401)
            {
                _credentialStore.DeleteRefreshToken();
                _session = null;
                _entitlements = null;
                SetStatus("XAOCEN Account 会话已失效");
                AppLogger.Warning("XAOCEN Account 刷新令牌已失效，已清理本地凭据。");
                return false;
            }
        }
        finally
        {
            _authorizationLock.Release();
        }
    }

    public async Task LogoutAsync(CancellationToken cancellationToken = default)
    {
        await EndSessionAsync(revoke: false, cancellationToken).ConfigureAwait(false);
    }

    public async Task RevokeAsync(CancellationToken cancellationToken = default)
    {
        await EndSessionAsync(revoke: true, cancellationToken).ConfigureAwait(false);
    }

    private async Task EndSessionAsync(bool revoke, CancellationToken cancellationToken)
    {
        await _authorizationLock.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            var refreshToken = _credentialStore.ReadRefreshToken();
            if (!string.IsNullOrWhiteSpace(refreshToken))
            {
                try
                {
                    if (revoke)
                    {
                        await _client.RevokeDeviceSessionAsync(refreshToken, cancellationToken).ConfigureAwait(false);
                    }
                    else
                    {
                        await _client.LogoutDeviceSessionAsync(refreshToken, cancellationToken).ConfigureAwait(false);
                    }
                }
                catch (AccountHttpException ex) when (ex.StatusCode == 401)
                {
                    // The server already considers the credential invalid; local
                    // cleanup is still the correct terminal action.
                }
            }

            _credentialStore.DeleteRefreshToken();
            _session = null;
            _entitlements = null;
            SetStatus(revoke ? "XAOCEN Account 会话已撤销" : "XAOCEN Account 已退出");
        }
        finally
        {
            _authorizationLock.Release();
        }
    }

    public void Dispose()
    {
        _shutdown.Cancel();
        _shutdown.Dispose();
        _authorizationLock.Dispose();
        _client.Dispose();
    }

    private static void ValidateVerificationUri(string value)
    {
        if (!Uri.TryCreate(value, UriKind.Absolute, out var uri) ||
            uri.Scheme != Uri.UriSchemeHttps ||
            (!uri.Host.EndsWith(".xaocen.studio", StringComparison.OrdinalIgnoreCase) &&
             !string.Equals(uri.Host, "xaocen.studio", StringComparison.OrdinalIgnoreCase)))
        {
            throw new AccountProtocolException("服务端返回的授权地址不是受信任的 XAOCEN HTTPS 地址。");
        }
    }

    private static void OpenVerificationUri(string uri)
    {
        Process.Start(new ProcessStartInfo(uri) { UseShellExecute = true });
    }

    private void SetStatus(string status)
    {
        StatusChanged?.Invoke(status);
        AppLogger.Info($"账号状态：{status}");
    }

    private async Task<IReadOnlyList<AccountEntitlement>?> TryGetEntitlementsAsync(
        string accessToken,
        CancellationToken cancellationToken)
    {
        try
        {
            return await _client.GetEntitlementsAsync(accessToken, cancellationToken).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex)
        {
            AppLogger.Warning($"ReWiFi 权益查询暂时不可用：{ex.GetType().Name}");
            return null;
        }
    }

    private void PublishAuthorizationProgress(DeviceAuthorizationProgress progress)
    {
        try
        {
            AuthorizationProgressChanged?.Invoke(progress);
        }
        catch (Exception ex)
        {
            AppLogger.Warning($"账号授权进度界面更新失败：{ex.GetType().Name}");
        }
    }
}

internal readonly record struct DeviceAuthorizationStart(
    string DeviceCode,
    string VerificationUriComplete,
    TimeSpan ExpiresIn,
    TimeSpan PollInterval);

internal readonly record struct DeviceAuthorizationStatus(
    bool Approved,
    bool Denied,
    bool Expired,
    bool Consumed,
    string? Status);

internal enum DeviceAuthorizationPhase
{
    Waiting,
    Approved,
    Connected,
    Denied,
    Expired,
    Consumed
}

internal readonly record struct DeviceAuthorizationProgress(
    DeviceAuthorizationPhase Phase,
    DateTimeOffset StartedAt,
    DateTimeOffset ExpiresAt,
    DateTimeOffset? LastPollAt,
    DateTimeOffset? NextPollAt,
    int PollCount);

internal readonly record struct AccountTokenResponse(
    string AccessToken,
    string RefreshToken,
    TimeSpan ExpiresIn);

internal readonly record struct AccountProfile(
    string? Id,
    string? DisplayName,
    string? Email,
    bool? EmailVerified,
    string? Status,
    string? Locale,
    string? Timezone,
    bool? DeletionRequested);

internal readonly record struct AccountEntitlement(
    string? EntitlementId,
    string? ProductId,
    string? LicenseType,
    string? GrantKind,
    string? Source,
    string? Status,
    int MaxDevices,
    int ActiveDevices,
    string? ValidFrom,
    string? ExpiresAt,
    string? PlanCode,
    bool AutoRenew,
    int? RenewalPeriodDays,
    string? GraceUntil,
    string? CreatedAt,
    bool OfflineAuthorizationAvailable);

internal readonly record struct AccountSession(
    string AccessToken,
    TimeSpan ExpiresIn,
    AccountProfile Profile);

internal readonly record struct OfflineLicenseCheckResponse(
    bool Valid,
    string? ProductId,
    string? DeviceId,
    string? LicenseType,
    bool CheckRequired,
    bool ReauthorizationRequired,
    string? NextOnlineCheckAt,
    string? HardReauthorizeAt);

internal sealed class AccountHttpException : Exception
{
    public AccountHttpException(int statusCode, string? code, string? serverMessage, string? requestId)
        : base(BuildMessage(statusCode, code, serverMessage, requestId))
    {
        StatusCode = statusCode;
        Code = code;
        RequestId = requestId;
    }

    public int StatusCode { get; }
    public string? Code { get; }
    public string? RequestId { get; }

    private static string BuildMessage(int statusCode, string? code, string? serverMessage, string? requestId)
    {
        var message = string.IsNullOrWhiteSpace(serverMessage)
            ? $"XAOCEN Account 请求失败（HTTP {statusCode}）。"
            : serverMessage;
        if (!string.IsNullOrWhiteSpace(code))
        {
            message += $" [{code}]";
        }

        return string.IsNullOrWhiteSpace(requestId) ? message : $"{message}\n请求编号：{requestId}";
    }
}

internal sealed class AccountProtocolException : Exception
{
    public AccountProtocolException(string message) : base(message) { }
}

internal sealed class AccountAuthorizationException : Exception
{
    public AccountAuthorizationException(string message) : base(message) { }
}
