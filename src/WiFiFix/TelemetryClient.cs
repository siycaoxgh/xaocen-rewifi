using System.Diagnostics;
using System.Net;
using System.Net.Http;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Text.RegularExpressions;

namespace XAOCEN.ReWiFi;

internal static class TelemetryBuild
{
#if TELEMETRY_TEST
    public static readonly bool IsEnabled = true;
    public static readonly bool IsTestBuild = true;
    public static readonly string EventsEndpoint = "https://telemetry-test.xaocen.studio/v1/telemetry/events";
    public static readonly string Channel = "test";
#else
    public static readonly bool IsEnabled = true;
    public static readonly bool IsTestBuild = false;
    public static readonly string EventsEndpoint = "https://auth.xaocen.studio/v1/telemetry/events";
    public static readonly string Channel = "release";
#endif

    public const string SchemaVersion = "1.0";
    public const string PolicyVersion = "2026-09-13";
}

internal static class TelemetryEventNames
{
    public const string FirstRun = "app.first_run";
    public const string Updated = "app.updated";
    public const string Start = "app.start";
    public const string SessionSummary = "app.session_summary";
    public const string FeatureUsed = "feature.used";
    public const string ActivationSuccess = "activation.success";
    public const string ActivationFailure = "activation.failure";
    public const string EntitlementChecked = "entitlement.checked";
    public const string CrashReported = "crash.reported";
    public const string OptOut = "telemetry.opt_out";
}

internal readonly record struct TelemetryConsentSnapshot(
    bool Analytics,
    bool Crash,
    bool AllUploadsDisabled,
    string PolicyVersion);

internal readonly record struct TelemetryStatusSnapshot(
    bool IsEnabled,
    bool IsTestBuild,
    bool AnalyticsConsent,
    bool CrashConsent,
    bool AllUploadsDisabled,
    int QueuedEvents,
    string InstanceId);

internal readonly record struct TelemetryFlushResult(
    int Queued,
    int Sent,
    int Remaining);

internal sealed class TelemetryClient : IDisposable
{
    private const int MaxQueue = 500;
    private const int MaxBatch = 100;
    private const int MaxPayloadBytes = 256 * 1024;
    private const int MaxPropertyValueLength = 512;

    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web)
    {
        WriteIndented = false,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull
    };

    private static readonly HashSet<string> BasicEventNames = new(StringComparer.Ordinal)
    {
        TelemetryEventNames.FirstRun,
        TelemetryEventNames.Start,
        TelemetryEventNames.Updated,
        TelemetryEventNames.ActivationSuccess
    };

    private static readonly IReadOnlyDictionary<string, HashSet<string>> AllowedProperties =
        new Dictionary<string, HashSet<string>>(StringComparer.Ordinal)
        {
            [TelemetryEventNames.FirstRun] = new(StringComparer.Ordinal) { "previousVersion", "installChannel" },
            [TelemetryEventNames.Updated] = new(StringComparer.Ordinal) { "previousVersion", "installChannel" },
            [TelemetryEventNames.Start] = new(StringComparer.Ordinal) { "launchSource" },
            [TelemetryEventNames.SessionSummary] = new(StringComparer.Ordinal) { "durationBucket", "featureCount", "completedCoreAction" },
            [TelemetryEventNames.FeatureUsed] = new(StringComparer.Ordinal) { "feature", "count" },
            [TelemetryEventNames.ActivationSuccess] = new(StringComparer.Ordinal) { "authorizationMode", "entitlementType", "networkState" },
            [TelemetryEventNames.ActivationFailure] = new(StringComparer.Ordinal) { "authorizationMode", "errorCode", "networkState" },
            [TelemetryEventNames.EntitlementChecked] = new(StringComparer.Ordinal) { "authorizationMode", "result", "errorCode" },
            [TelemetryEventNames.CrashReported] = new(StringComparer.Ordinal) { "exceptionType", "errorCode", "message", "stack", "phase", "native" },
            [TelemetryEventNames.OptOut] = new(StringComparer.Ordinal) { "scope" }
        };

    private static readonly Regex BearerTokenRegex = new(
        @"Bearer\s+[A-Za-z0-9._~+/=-]+",
        RegexOptions.Compiled | RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);
    private static readonly Regex WindowsPathRegex = new(
        @"[A-Z]:\\Users\\[^\\\r\n]+",
        RegexOptions.Compiled | RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);
    private static readonly Regex UnixPathRegex = new(
        @"(?:/home/|~/)[^\s\r\n]+",
        RegexOptions.Compiled | RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);
    private static readonly Regex EmailRegex = new(
        @"[\w.+-]+@[\w.-]+\.[A-Za-z]{2,}",
        RegexOptions.Compiled | RegexOptions.CultureInvariant);
    private static readonly Regex Ipv4Regex = new(
        @"(?<![\w])(?:\d{1,3}\.){3}\d{1,3}(?![\w])",
        RegexOptions.Compiled | RegexOptions.CultureInvariant);
    private static readonly Regex Ipv6Regex = new(
        @"(?<![\w])(?:[0-9A-Fa-f]{1,4}:){2,7}[0-9A-Fa-f]{0,4}(?![\w])",
        RegexOptions.Compiled | RegexOptions.CultureInvariant);
    private static readonly Regex MacRegex = new(
        @"(?<![\w])(?:[0-9A-Fa-f]{2}[:-]){5}[0-9A-Fa-f]{2}(?![\w])",
        RegexOptions.Compiled | RegexOptions.CultureInvariant);

    private readonly object _sync = new();
    private readonly TelemetryLocalStore _store = new();
    private readonly HttpClient _httpClient = new() { Timeout = TimeSpan.FromSeconds(8) };
    private readonly List<TelemetryEventDocument> _queue;
    private readonly HashSet<string> _sensitiveValues = new(StringComparer.OrdinalIgnoreCase);
    private TelemetryStateDocument _state;
    private string _sessionId = Guid.NewGuid().ToString();
    private Stopwatch _sessionTimer = Stopwatch.StartNew();
    private int _featureCount;
    private bool _completedCoreAction;
    private bool _sessionStarted;
    private bool _handlersInstalled;
    private bool _disposed;
    private int _flushInProgress;

    public event Action? StatusChanged;

    public TelemetryClient()
    {
        _state = _store.LoadState();
        _queue = _store.LoadQueue();
        ReconcileQueueWithConsent();
        ObserveCurrentVersion();
    }

    public TelemetryConsentSnapshot Consent
    {
        get
        {
            lock (_sync)
            {
                return new TelemetryConsentSnapshot(
                    _state.AnalyticsConsent,
                    _state.CrashConsent,
                    _state.AllUploadsDisabled,
                    _state.PolicyVersion);
            }
        }
    }

    public TelemetryStatusSnapshot GetStatus()
    {
        lock (_sync)
        {
            return new TelemetryStatusSnapshot(
                TelemetryBuild.IsEnabled,
                TelemetryBuild.IsTestBuild,
                _state.AnalyticsConsent,
                _state.CrashConsent,
                _state.AllUploadsDisabled,
                _queue.Count,
                _state.InstanceId);
        }
    }

    public string StatusText
    {
        get
        {
            var status = GetStatus();
            var endpointText = status.IsTestBuild
                ? "测试接收地址已配置"
                : status.IsEnabled ? "正式接收地址已配置" : "当前构建未启用遥测上传";
            var basicText = status.AllUploadsDisabled ? "已禁止" : status.IsEnabled ? "已启用" : "未发送";
            var enhancedText = status.AnalyticsConsent ? "已同意" : "未同意";
            var crashText = status.CrashConsent ? "已同意" : "未同意";
            var uploadText = status.AllUploadsDisabled ? "所有上传均已停止" : $"待上传：{status.QueuedEvents} 条";
            return $"{endpointText}\n基础统计：{basicText} · 增强分析：{enhancedText}\n崩溃报告：{crashText} · {uploadText}";
        }
    }

    public void AddSensitiveValue(string? value)
    {
        if (string.IsNullOrWhiteSpace(value) || value.Trim().Length < 2)
        {
            return;
        }

        lock (_sync)
        {
            _sensitiveValues.Add(value.Trim());
        }
    }

    public void InitializeSession()
    {
        lock (_sync)
        {
            ThrowIfDisposed();
            _sessionId = Guid.NewGuid().ToString();
            _sessionTimer = Stopwatch.StartNew();
            _featureCount = 0;
            _completedCoreAction = false;
            _sessionStarted = false;
            QueuePendingEventsUnsafe();
            _store.SaveState(_state);
        }

        NotifyStatusChanged();
        RequestFlush();
    }

    public void SetConsent(bool analytics, bool crash, bool allUploadsDisabled)
    {
        lock (_sync)
        {
            ThrowIfDisposed();
            var previousAnalytics = _state.AnalyticsConsent;
            var previousCrash = _state.CrashConsent;
            _state.AnalyticsConsent = analytics;
            _state.CrashConsent = crash;
            _state.AllUploadsDisabled = allUploadsDisabled;
            _state.PolicyVersion = TelemetryBuild.PolicyVersion;

            if (allUploadsDisabled)
            {
                _queue.Clear();
            }
            else
            {
                if (!analytics)
                {
                    _queue.RemoveAll(item => IsEnhancedEvent(item.EventName));
                }

                if (!crash)
                {
                    _queue.RemoveAll(item => item.EventName == TelemetryEventNames.CrashReported);
                }

                var scope = GetOptOutScope(previousAnalytics, previousCrash, analytics, crash);
                if (scope is not null)
                {
                    EnqueueUnsafe(TelemetryEventNames.OptOut, new Dictionary<string, object?> { ["scope"] = scope }, DateTimeOffset.UtcNow);
                }

                QueuePendingEventsUnsafe();
            }

            _store.SaveState(_state);
            _store.SaveQueue(_queue);
        }

        NotifyStatusChanged();
        RequestFlush();
    }

    public void ResetInstanceId()
    {
        lock (_sync)
        {
            ThrowIfDisposed();
            _state.InstanceId = Guid.NewGuid().ToString();
            _state.FirstObservedAt = DateTimeOffset.UtcNow;
            _state.FirstRunRecorded = false;
            _state.ActivationObservedAt = null;
            _state.ActivationNetworkState = null;
            _state.ActivationRecorded = false;
            _state.PendingPreviousVersion = null;
            _state.LastVersion = AppLogger.Version;
            _sessionId = Guid.NewGuid().ToString();
            _sessionTimer = Stopwatch.StartNew();
            _sessionStarted = false;
            _featureCount = 0;
            _completedCoreAction = false;
            _queue.Clear();
            _store.SaveState(_state);
            _store.SaveQueue(_queue);
        }

        NotifyStatusChanged();
    }

    public void ClearQueuedEvents()
    {
        lock (_sync)
        {
            ThrowIfDisposed();
            _queue.Clear();
            _store.SaveQueue(_queue);
        }

        NotifyStatusChanged();
    }

    public bool Track(string eventName, IReadOnlyDictionary<string, object?>? properties = null, DateTimeOffset? occurredAt = null)
    {
        var tracked = false;
        lock (_sync)
        {
            ThrowIfDisposed();
            if (!TelemetryBuild.IsEnabled || _state.AllUploadsDisabled || !AllowedProperties.ContainsKey(eventName))
            {
                return false;
            }

            if (eventName == TelemetryEventNames.CrashReported && !_state.CrashConsent)
            {
                return false;
            }

            if (IsEnhancedEvent(eventName) && !_state.AnalyticsConsent)
            {
                return false;
            }

            EnqueueUnsafe(eventName, properties, occurredAt ?? DateTimeOffset.UtcNow);
            tracked = true;
        }

        if (tracked)
        {
            NotifyStatusChanged();
        }

        return tracked;
    }

    public void RecordCoreActivation(string networkState)
    {
        lock (_sync)
        {
            ThrowIfDisposed();
            if (_state.ActivationObservedAt is null)
            {
                _state.ActivationObservedAt = DateTimeOffset.UtcNow;
                _state.ActivationNetworkState = networkState;
                QueuePendingEventsUnsafe();
                _store.SaveState(_state);
            }
        }

        RequestFlush();
    }

    public void RecordActivationFailure(string authorizationMode, string errorCode, string networkState)
    {
        if (Track(TelemetryEventNames.ActivationFailure, new Dictionary<string, object?>
            {
                ["authorizationMode"] = authorizationMode,
                ["errorCode"] = errorCode,
                ["networkState"] = networkState
            }))
        {
            RequestFlush();
        }
    }

    public void RecordEntitlementCheck(string authorizationMode, string result, string? errorCode = null)
    {
        if (Track(TelemetryEventNames.EntitlementChecked, new Dictionary<string, object?>
            {
                ["authorizationMode"] = authorizationMode,
                ["result"] = result,
                ["errorCode"] = errorCode
            }))
        {
            RequestFlush();
        }
    }

    public void MarkFeatureUsed(string feature, int count = 1)
    {
        if (Track(TelemetryEventNames.FeatureUsed, new Dictionary<string, object?>
            {
                ["feature"] = feature,
                ["count"] = count
            }))
        {
            lock (_sync)
            {
                _featureCount++;
                _completedCoreAction = true;
            }
        }
    }

    public bool ReportCrash(Exception exception, string phase, bool native = false)
    {
        var reported = Track(TelemetryEventNames.CrashReported, new Dictionary<string, object?>
        {
            ["exceptionType"] = exception.GetType().FullName ?? exception.GetType().Name,
            ["errorCode"] = exception is AccountHttpException accountException
                ? accountException.Code ?? $"http_{accountException.StatusCode}"
                : null,
            ["message"] = exception.Message,
            ["stack"] = exception.StackTrace,
            ["phase"] = phase,
            ["native"] = native
        });

        if (reported)
        {
            AppLogger.Warning($"已将脱敏崩溃信息加入本地遥测队列：阶段={phase}; 类型={exception.GetType().Name}");
            RequestFlush();
        }

        return reported;
    }

    public void InstallGlobalExceptionHandlers()
    {
        lock (_sync)
        {
            if (_handlersInstalled || _disposed)
            {
                return;
            }

            Application.ThreadException += OnThreadException;
            AppDomain.CurrentDomain.UnhandledException += OnUnhandledException;
            TaskScheduler.UnobservedTaskException += OnUnobservedTaskException;
            _handlersInstalled = true;
        }
    }

    public async Task<TelemetryFlushResult> FlushAsync(CancellationToken cancellationToken = default)
    {
        if (!TelemetryBuild.IsEnabled || string.IsNullOrWhiteSpace(TelemetryBuild.EventsEndpoint))
        {
            var remaining = GetStatus().QueuedEvents;
            return new TelemetryFlushResult(remaining, 0, remaining);
        }

        if (Interlocked.Exchange(ref _flushInProgress, 1) != 0)
        {
            var queued = GetStatus().QueuedEvents;
            return new TelemetryFlushResult(queued, 0, queued);
        }

        try
        {
            var initialCount = GetStatus().QueuedEvents;
            var sent = 0;
            while (!cancellationToken.IsCancellationRequested)
            {
                TelemetryEventDocument[] batch;
                lock (_sync)
                {
                    batch = _queue.Take(MaxBatch).ToArray();
                }

                if (batch.Length == 0)
                {
                    break;
                }

                var outcome = await SendBatchAsync(batch, cancellationToken).ConfigureAwait(false);
                if (outcome == TelemetrySendOutcome.TemporaryFailure)
                {
                    break;
                }

                lock (_sync)
                {
                    var ids = batch.Select(item => item.EventId).ToHashSet(StringComparer.OrdinalIgnoreCase);
                    _queue.RemoveAll(item => ids.Contains(item.EventId));
                    _store.SaveQueue(_queue);
                }

                NotifyStatusChanged();

                sent += batch.Length;
            }

            return new TelemetryFlushResult(initialCount, sent, GetStatus().QueuedEvents);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            var queued = GetStatus().QueuedEvents;
            return new TelemetryFlushResult(queued, 0, queued);
        }
        finally
        {
            Interlocked.Exchange(ref _flushInProgress, 0);
        }
    }

    public void RequestFlush()
    {
        if (!TelemetryBuild.IsEnabled || string.IsNullOrWhiteSpace(TelemetryBuild.EventsEndpoint))
        {
            return;
        }

        _ = FlushAsync();
    }

    public async Task ShutdownAsync(CancellationToken cancellationToken = default)
    {
        lock (_sync)
        {
            if (_disposed)
            {
                return;
            }

            if (_sessionStarted && !_state.AllUploadsDisabled && _state.AnalyticsConsent)
            {
                EnqueueUnsafe(TelemetryEventNames.SessionSummary, new Dictionary<string, object?>
                {
                    ["durationBucket"] = GetDurationBucket(_sessionTimer.Elapsed),
                    ["featureCount"] = _featureCount,
                    ["completedCoreAction"] = _completedCoreAction
                }, DateTimeOffset.UtcNow);
                _store.SaveQueue(_queue);
            }
        }

        if (TelemetryBuild.IsEnabled)
        {
            await FlushAsync(cancellationToken).ConfigureAwait(false);
        }
    }

    public void Dispose()
    {
        lock (_sync)
        {
            if (_disposed)
            {
                return;
            }

            if (_handlersInstalled)
            {
                Application.ThreadException -= OnThreadException;
                AppDomain.CurrentDomain.UnhandledException -= OnUnhandledException;
                TaskScheduler.UnobservedTaskException -= OnUnobservedTaskException;
                _handlersInstalled = false;
            }

            _disposed = true;
            _httpClient.Dispose();
        }
    }

    private void ObserveCurrentVersion()
    {
        lock (_sync)
        {
            if (string.IsNullOrWhiteSpace(_state.InstanceId))
            {
                _state.InstanceId = Guid.NewGuid().ToString();
            }

            _state.FirstObservedAt ??= DateTimeOffset.UtcNow;
            var currentVersion = AppLogger.Version;
            if (!string.IsNullOrWhiteSpace(_state.LastVersion) &&
                !string.Equals(_state.LastVersion, currentVersion, StringComparison.OrdinalIgnoreCase))
            {
                _state.PendingPreviousVersion = _state.LastVersion;
            }

            _state.LastVersion = currentVersion;
            _state.PolicyVersion = TelemetryBuild.PolicyVersion;
            _store.SaveState(_state);
        }
    }

    private void ReconcileQueueWithConsent()
    {
        lock (_sync)
        {
            var removed = _queue.RemoveAll(item =>
                _state.AllUploadsDisabled ||
                (item.EventName == TelemetryEventNames.CrashReported && !_state.CrashConsent) ||
                (IsEnhancedEvent(item.EventName) && !_state.AnalyticsConsent));
            if (removed > 0)
            {
                _store.SaveQueue(_queue);
            }
        }
    }

    private void QueuePendingEventsUnsafe()
    {
        if (!TelemetryBuild.IsEnabled || _state.AllUploadsDisabled)
        {
            return;
        }

        if (_state.FirstObservedAt is { } firstObservedAt && !_state.FirstRunRecorded)
        {
            EnqueueUnsafe(TelemetryEventNames.FirstRun, null, firstObservedAt);
            _state.FirstRunRecorded = true;
        }

        if (!string.IsNullOrWhiteSpace(_state.PendingPreviousVersion))
        {
            EnqueueUnsafe(TelemetryEventNames.Updated, null, DateTimeOffset.UtcNow);
            _state.PendingPreviousVersion = null;
        }

        if (_state.ActivationObservedAt is { } activationObservedAt && !_state.ActivationRecorded)
        {
            EnqueueUnsafe(TelemetryEventNames.ActivationSuccess, null, activationObservedAt);
            _state.ActivationRecorded = true;
        }

        if (!_sessionStarted)
        {
            EnqueueUnsafe(TelemetryEventNames.Start, null, DateTimeOffset.UtcNow);
            _sessionStarted = true;
        }
    }

    private void EnqueueUnsafe(string eventName, IReadOnlyDictionary<string, object?>? properties, DateTimeOffset occurredAt)
    {
        var consent = new TelemetryConsentDocument
        {
            Analytics = _state.AnalyticsConsent,
            Crash = _state.CrashConsent,
            PolicyVersion = _state.PolicyVersion
        };
        var eventDocument = new TelemetryEventDocument
        {
            SchemaVersion = TelemetryBuild.SchemaVersion,
            EventId = Guid.NewGuid().ToString(),
            EventName = eventName,
            OccurredAt = occurredAt.ToUniversalTime(),
            InstanceId = _state.InstanceId,
            SessionId = _sessionId,
            ProductId = ProductInfo.AccountProductId,
            Platform = ProductInfo.AccountPlatform,
            AppVersion = AppLogger.Version,
            OsVersion = eventName == TelemetryEventNames.CrashReported ? Environment.OSVersion.VersionString : null,
            Architecture = eventName == TelemetryEventNames.CrashReported
                ? System.Runtime.InteropServices.RuntimeInformation.ProcessArchitecture.ToString().ToLowerInvariant()
                : null,
            Channel = null,
            Consent = consent,
            Properties = IsBasicEvent(eventName) ? null : CleanProperties(eventName, properties)
        };

        _queue.Add(eventDocument);
        if (_queue.Count > MaxQueue)
        {
            _queue.RemoveRange(0, _queue.Count - MaxQueue);
        }

        _store.SaveQueue(_queue);
    }

    private Dictionary<string, object?>? CleanProperties(string eventName, IReadOnlyDictionary<string, object?>? properties)
    {
        if (properties is null || !AllowedProperties.TryGetValue(eventName, out var allowed))
        {
            return null;
        }

        var result = new Dictionary<string, object?>(StringComparer.Ordinal);
        foreach (var pair in properties)
        {
            if (!allowed.Contains(pair.Key) || result.Count >= 40)
            {
                continue;
            }

            if (pair.Value is null)
            {
                result[pair.Key] = null;
            }
            else if (pair.Value is string text)
            {
                result[pair.Key] = eventName == TelemetryEventNames.CrashReported
                    ? SanitizeText(text)
                    : Truncate(text);
            }
            else if (pair.Value is bool or byte or sbyte or short or ushort or int or uint or long or ulong or float or double or decimal)
            {
                result[pair.Key] = pair.Value;
            }
        }

        return result.Count == 0 ? null : result;
    }

    private string SanitizeText(string value)
    {
        var sanitized = value;
        foreach (var sensitiveValue in _sensitiveValues.OrderByDescending(item => item.Length))
        {
            sanitized = sanitized.Replace(sensitiveValue, "<network-redacted>", StringComparison.OrdinalIgnoreCase);
        }

        sanitized = BearerTokenRegex.Replace(sanitized, "Bearer <redacted>");
        sanitized = WindowsPathRegex.Replace(sanitized, "<user-path>");
        sanitized = UnixPathRegex.Replace(sanitized, "<user-path>");
        sanitized = EmailRegex.Replace(sanitized, "<email>");
        sanitized = Ipv4Regex.Replace(sanitized, "<ip>");
        sanitized = Ipv6Regex.Replace(sanitized, "<ip>");
        sanitized = MacRegex.Replace(sanitized, "<mac>");
        return Truncate(sanitized);
    }

    private static string Truncate(string value) => value.Length <= MaxPropertyValueLength
        ? value
        : value[..MaxPropertyValueLength];

    private async Task<TelemetrySendOutcome> SendBatchAsync(
        IReadOnlyList<TelemetryEventDocument> batch,
        CancellationToken cancellationToken)
    {
        var payload = new TelemetryBatchDocument
        {
            SchemaVersion = TelemetryBuild.SchemaVersion,
            Events = batch
        };
        var payloadJson = JsonSerializer.Serialize(payload, JsonOptions);
        if (Encoding.UTF8.GetByteCount(payloadJson) > MaxPayloadBytes && batch.Count > 1)
        {
            var midpoint = batch.Count / 2;
            var first = await SendBatchAsync(batch.Take(midpoint).ToArray(), cancellationToken).ConfigureAwait(false);
            if (first != TelemetrySendOutcome.Success)
            {
                return first;
            }

            return await SendBatchAsync(batch.Skip(midpoint).ToArray(), cancellationToken).ConfigureAwait(false);
        }

        for (var attempt = 0; attempt < 3; attempt++)
        {
            try
            {
                using var request = new HttpRequestMessage(HttpMethod.Post, TelemetryBuild.EventsEndpoint)
                {
                    Content = new StringContent(payloadJson, Encoding.UTF8, "application/json")
                };
                using var response = await _httpClient.SendAsync(request, cancellationToken).ConfigureAwait(false);
                var responseBody = await response.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(false);

                if (response.StatusCode == HttpStatusCode.Created)
                {
                    var result = JsonSerializer.Deserialize<TelemetryResponseDocument>(responseBody, JsonOptions);
                    if (result is null)
                    {
                        AppLogger.Warning("遥测服务返回 201，但响应格式无法解析；当前批次将丢弃。");
                        return TelemetrySendOutcome.PermanentFailure;
                    }

                    if (result.Rejected is { Count: > 0 })
                    {
                        var rejectionSummary = result.Rejected
                            .GroupBy(item => string.IsNullOrWhiteSpace(item.Code) ? "unknown" : item.Code.Trim(), StringComparer.OrdinalIgnoreCase)
                            .Select(group => $"{group.Key}={group.Count()}");
                        AppLogger.Warning($"遥测服务拒绝了 {result.Rejected.Count} 条事件；原因：{string.Join(", ", rejectionSummary)}；已按协议丢弃，不重复重试。");
                    }

                    return TelemetrySendOutcome.Success;
                }

                if (response.StatusCode == HttpStatusCode.RequestEntityTooLarge && batch.Count > 1)
                {
                    var midpoint = batch.Count / 2;
                    var first = await SendBatchAsync(batch.Take(midpoint).ToArray(), cancellationToken).ConfigureAwait(false);
                    if (first != TelemetrySendOutcome.Success)
                    {
                        return first;
                    }

                    return await SendBatchAsync(batch.Skip(midpoint).ToArray(), cancellationToken).ConfigureAwait(false);
                }

                if (IsRetryable(response.StatusCode))
                {
                    if (attempt == 2)
                    {
                        return TelemetrySendOutcome.TemporaryFailure;
                    }

                    await Task.Delay(GetRetryDelay(response, attempt), cancellationToken).ConfigureAwait(false);
                    continue;
                }

                AppLogger.Warning($"遥测服务拒绝请求：HTTP {(int)response.StatusCode}；当前批次将丢弃。");
                return TelemetrySendOutcome.PermanentFailure;
            }
            catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
            {
                return TelemetrySendOutcome.TemporaryFailure;
            }
            catch (HttpRequestException)
            {
                if (attempt == 2)
                {
                    return TelemetrySendOutcome.TemporaryFailure;
                }

                await Task.Delay(TimeSpan.FromSeconds(Math.Pow(2, attempt)) + TimeSpan.FromMilliseconds(Random.Shared.Next(0, 250)), cancellationToken).ConfigureAwait(false);
            }
        }

        return TelemetrySendOutcome.TemporaryFailure;
    }

    private void NotifyStatusChanged()
    {
        try
        {
            StatusChanged?.Invoke();
        }
        catch (Exception exception)
        {
            AppLogger.Warning($"遥测状态刷新通知失败：{exception.GetType().Name}");
        }
    }

    private static bool IsRetryable(HttpStatusCode statusCode) =>
        statusCode == HttpStatusCode.RequestTimeout ||
        (int)statusCode == 429 ||
        (int)statusCode >= 500;

    private static TimeSpan GetRetryDelay(HttpResponseMessage response, int attempt)
    {
        if (response.Headers.RetryAfter?.Delta is { } delta)
        {
            return TimeSpan.FromSeconds(Math.Clamp(delta.TotalSeconds, 0, 60));
        }

        return TimeSpan.FromSeconds(Math.Pow(2, attempt)) + TimeSpan.FromMilliseconds(Random.Shared.Next(0, 250));
    }

    private static bool IsBasicEvent(string eventName) => BasicEventNames.Contains(eventName);

    private static bool IsEnhancedEvent(string eventName) => eventName is
        TelemetryEventNames.SessionSummary or
        TelemetryEventNames.FeatureUsed or
        TelemetryEventNames.ActivationFailure or
        TelemetryEventNames.EntitlementChecked;

    private static string? GetOptOutScope(bool previousAnalytics, bool previousCrash, bool analytics, bool crash)
    {
        var analyticsDisabled = previousAnalytics && !analytics;
        var crashDisabled = previousCrash && !crash;
        return analyticsDisabled && crashDisabled ? "all"
            : analyticsDisabled ? "analytics"
            : crashDisabled ? "crash"
            : null;
    }

    private static string GetDurationBucket(TimeSpan duration) => duration.TotalSeconds switch
    {
        < 10 => "0-10s",
        < 60 => "10-60s",
        < 300 => "1-5m",
        < 1800 => "5-30m",
        _ => "30m+"
    };

    private void OnThreadException(object? sender, ThreadExceptionEventArgs e) => ReportCrash(e.Exception, "ui");

    private void OnUnhandledException(object? sender, UnhandledExceptionEventArgs e)
    {
        if (e.ExceptionObject is Exception exception)
        {
            ReportCrash(exception, "unhandled");
        }
    }

    private void OnUnobservedTaskException(object? sender, UnobservedTaskExceptionEventArgs e)
    {
        ReportCrash(e.Exception, "background-task");
        e.SetObserved();
    }

    private void ThrowIfDisposed()
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
    }

    private enum TelemetrySendOutcome
    {
        Success,
        TemporaryFailure,
        PermanentFailure
    }
}

internal sealed class TelemetryLocalStore
{
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);
    private readonly string _directoryPath = Path.Combine(AppConfig.DirectoryPath, "telemetry");
    private readonly string _statePath;
    private readonly string _queuePath;

    public TelemetryLocalStore()
    {
        _statePath = Path.Combine(_directoryPath, "state.json");
        _queuePath = Path.Combine(_directoryPath, "queue.json");
    }

    public TelemetryStateDocument LoadState()
    {
        try
        {
            if (File.Exists(_statePath))
            {
                var state = JsonSerializer.Deserialize<TelemetryStateDocument>(File.ReadAllText(_statePath), JsonOptions);
                if (state is not null && Guid.TryParse(state.InstanceId, out _))
                {
                    return state;
                }
            }
        }
        catch
        {
            AppLogger.Warning("遥测本地状态无法读取，将创建新的匿名状态。");
        }

        return new TelemetryStateDocument
        {
            InstanceId = Guid.NewGuid().ToString(),
            PolicyVersion = TelemetryBuild.PolicyVersion
        };
    }

    public List<TelemetryEventDocument> LoadQueue()
    {
        try
        {
            if (File.Exists(_queuePath))
            {
                var queue = JsonSerializer.Deserialize<List<TelemetryEventDocument>>(File.ReadAllText(_queuePath), JsonOptions);
                if (queue is not null)
                {
                    return queue.Where(item => !string.IsNullOrWhiteSpace(item.EventId)).TakeLast(500).ToList();
                }
            }
        }
        catch
        {
            AppLogger.Warning("遥测本地队列无法读取，将从空队列开始。");
        }

        return new List<TelemetryEventDocument>();
    }

    public void SaveState(TelemetryStateDocument state)
    {
        try
        {
            AtomicWrite(_statePath, JsonSerializer.Serialize(state, JsonOptions));
        }
        catch
        {
            AppLogger.Warning("遥测本地状态保存失败；不会影响 ReWiFi 核心功能。");
        }
    }

    public void SaveQueue(IReadOnlyList<TelemetryEventDocument> queue)
    {
        try
        {
            AtomicWrite(_queuePath, JsonSerializer.Serialize(queue, JsonOptions));
        }
        catch
        {
            AppLogger.Warning("遥测本地队列保存失败；不会影响 ReWiFi 核心功能。");
        }
    }

    private void AtomicWrite(string path, string content)
    {
        Directory.CreateDirectory(_directoryPath);
        var tempPath = path + ".tmp";
        File.WriteAllText(tempPath, content, new UTF8Encoding(false));
        File.Move(tempPath, path, true);
    }
}

internal sealed class TelemetryStateDocument
{
    public string InstanceId { get; set; } = Guid.NewGuid().ToString();
    public bool AnalyticsConsent { get; set; }
    public bool CrashConsent { get; set; }
    public bool AllUploadsDisabled { get; set; }
    public string PolicyVersion { get; set; } = TelemetryBuild.PolicyVersion;
    public DateTimeOffset? FirstObservedAt { get; set; }
    public bool FirstRunRecorded { get; set; }
    public string? LastVersion { get; set; }
    public string? PendingPreviousVersion { get; set; }
    public DateTimeOffset? ActivationObservedAt { get; set; }
    public string? ActivationNetworkState { get; set; }
    public bool ActivationRecorded { get; set; }
}

internal sealed class TelemetryConsentDocument
{
    public bool Analytics { get; set; }
    public bool Crash { get; set; }
    public string PolicyVersion { get; set; } = TelemetryBuild.PolicyVersion;
}

internal sealed class TelemetryEventDocument
{
    public string SchemaVersion { get; set; } = TelemetryBuild.SchemaVersion;
    public string EventId { get; set; } = Guid.NewGuid().ToString();
    public string EventName { get; set; } = string.Empty;
    public DateTimeOffset OccurredAt { get; set; }
    public string InstanceId { get; set; } = string.Empty;
    public string SessionId { get; set; } = string.Empty;
    public string ProductId { get; set; } = ProductInfo.AccountProductId;
    public string Platform { get; set; } = ProductInfo.AccountPlatform;
    public string AppVersion { get; set; } = string.Empty;
    public string? OsVersion { get; set; }
    public string? Architecture { get; set; }
    public string? Channel { get; set; }
    public TelemetryConsentDocument Consent { get; set; } = new();
    public Dictionary<string, object?>? Properties { get; set; }
}

internal sealed class TelemetryBatchDocument
{
    public string SchemaVersion { get; set; } = TelemetryBuild.SchemaVersion;
    public IReadOnlyList<TelemetryEventDocument> Events { get; set; } = Array.Empty<TelemetryEventDocument>();
}

internal sealed class TelemetryResponseDocument
{
    public string? RequestId { get; set; }
    public int Accepted { get; set; }
    public int Duplicates { get; set; }
    public List<TelemetryRejectedEventDocument>? Rejected { get; set; }
}

internal sealed class TelemetryRejectedEventDocument
{
    public string EventId { get; set; } = string.Empty;
    public string Code { get; set; } = string.Empty;
}
