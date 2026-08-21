using System.Diagnostics;
using System.Net.NetworkInformation;
using System.Runtime.InteropServices;
using System.Text;

namespace XAOCEN.ReWiFi;

public enum WatcherStatus
{
    Normal,
    Abnormal,
    Recovering,
    CoolingDown,
    WifiDisconnected
}

public sealed class NetworkWatcher : IAsyncDisposable
{
    private readonly WifiController _wifiController;
    private readonly ConnectivityProbe _connectivityProbe;
    private readonly CancellationTokenSource _shutdown = new();
    private readonly object _sync = new();
    private AppConfig _config;
    private Task? _monitorTask;
    private DateTimeOffset? _failureStarted;
    private DateTimeOffset? _cooldownUntil;
    private DateTimeOffset _nextProbeAllowedUtc = DateTimeOffset.MinValue;
    private bool _recoveryRunning;
    private string? _lastStatusText;
    private TaskCompletionSource<bool> _networkSignal = CreateSignal();

    public NetworkWatcher(AppConfig config, WifiController wifiController, ConnectivityProbe connectivityProbe)
    {
        _config = config.Clone();
        _wifiController = wifiController;
        _connectivityProbe = connectivityProbe;
    }

    public event Action<WatcherStatus, string>? StatusChanged;
    public event Action<string>? NotificationRequested;

    public void Start()
    {
        if (_monitorTask is not null)
        {
            return;
        }

        NetworkChange.NetworkAvailabilityChanged += OnNetworkChanged;
        _monitorTask = MonitorLoopAsync(_shutdown.Token);
        AppLogger.Info("网络监听已启动，轮询间隔=2s。");
    }

    public void UpdateConfig(AppConfig config)
    {
        lock (_sync)
        {
            _config = config.Clone();
        }

        AppLogger.Info($"配置已更新：SSID={config.TargetSsid}; 网卡={config.AdapterName}; 异常等待={config.FailureDelaySeconds}s; 冷却={config.CooldownSeconds}s; 自动恢复={config.AutoRecovery}; 开机启动={config.AutoStart}; 连通性探测={config.EnableConnectivityProbe}; 探测超时={config.ConnectivityProbeTimeoutSeconds}s");
        SignalNetworkChange();
    }

    public Task TriggerManualRecoveryAsync() => TriggerRecoveryAsync(manual: true, cancellationToken: _shutdown.Token);

    public async ValueTask DisposeAsync()
    {
        NetworkChange.NetworkAvailabilityChanged -= OnNetworkChanged;
        _shutdown.Cancel();
        if (_monitorTask is not null)
        {
            try { await _monitorTask.ConfigureAwait(false); }
            catch (OperationCanceledException) { }
        }

        _shutdown.Dispose();
    }

    private async Task MonitorLoopAsync(CancellationToken cancellationToken)
    {
        while (!cancellationToken.IsCancellationRequested)
        {
            try
            {
                var signalTask = GetSignalTask();
                var delayTask = Task.Delay(TimeSpan.FromSeconds(2), cancellationToken);
                await Task.WhenAny(delayTask, signalTask).ConfigureAwait(false);
                ResetSignal(signalTask);
                await EvaluateAsync(cancellationToken).ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                break;
            }
            catch (Exception ex)
            {
                // Monitoring is best-effort; the next timer tick retries the Windows state read.
                AppLogger.Error("网络监控循环出现异常", ex);
            }
        }
    }

    private async Task EvaluateAsync(CancellationToken cancellationToken)
    {
        if (_recoveryRunning)
        {
            return;
        }

        if (_cooldownUntil is { } cooldownUntil)
        {
            if (DateTimeOffset.UtcNow < cooldownUntil)
            {
                SetStatus(WatcherStatus.CoolingDown, "冷却中");
                return;
            }

            _cooldownUntil = null;
            _failureStarted = null;
        }

        var snapshot = NetworkSnapshot.Read(GetConfig().AdapterName);
        if (snapshot.IsHealthy)
        {
            _failureStarted = null;
            _nextProbeAllowedUtc = DateTimeOffset.MinValue;
            SetStatus(WatcherStatus.Normal, "网络正常");
            return;
        }

        var abnormalText = !snapshot.IsAdapterPresent
            ? "网络异常"
            : !snapshot.IsWifiConnected
                ? "Wi-Fi 未连接"
                : "Wi-Fi 已连接，但无 Internet";
        SetStatus(snapshot.IsAdapterPresent && !snapshot.IsWifiConnected
            ? WatcherStatus.WifiDisconnected
            : WatcherStatus.Abnormal, abnormalText);

        var config = GetConfig();
        if (!config.AutoRecovery)
        {
            return;
        }

        _failureStarted ??= DateTimeOffset.UtcNow;
        if (DateTimeOffset.UtcNow - _failureStarted.Value < TimeSpan.FromSeconds(config.FailureDelaySeconds))
        {
            return;
        }

        // Confirm once more after the delay before touching the adapter.
        var confirmedSnapshot = NetworkSnapshot.Read(config.AdapterName);
        if (!confirmedSnapshot.IsHealthy)
        {
            if (!confirmedSnapshot.IsAdapterPresent || !confirmedSnapshot.IsWifiConnected)
            {
                AppLogger.Info("确认 Wi-Fi 物理连接已断开，准备自动恢复。");
                await TriggerRecoveryAsync(manual: false, cancellationToken).ConfigureAwait(false);
                return;
            }

            if (!config.EnableConnectivityProbe)
            {
                AppLogger.Info("连通性探测已关闭，按 Windows Internet 状态执行自动恢复。");
                await TriggerRecoveryAsync(manual: false, cancellationToken).ConfigureAwait(false);
                return;
            }

            if (DateTimeOffset.UtcNow < _nextProbeAllowedUtc)
            {
                _failureStarted = null;
                return;
            }

            var probe = await _connectivityProbe
                .CheckAsync(config.ConnectivityProbeTimeoutSeconds, cancellationToken)
                .ConfigureAwait(false);
            AppLogger.Info($"连通性探测汇总：reachable={probe.ReachableCount}/{probe.Results.Count}");

            if (probe.AllReachable)
            {
                _failureStarted = null;
                _nextProbeAllowedUtc = DateTimeOffset.MinValue;
                SetStatus(WatcherStatus.Normal, "网络正常（外网可达）");
                return;
            }

            if (!probe.NoneReachable)
            {
                _failureStarted = null;
                _nextProbeAllowedUtc = DateTimeOffset.UtcNow.AddSeconds(30);
                SetStatus(WatcherStatus.Abnormal, "网络部分可达（可能是代理规则）");
                return;
            }

            _nextProbeAllowedUtc = DateTimeOffset.UtcNow.AddSeconds(30);
            AppLogger.Warning("Wi-Fi 仍连接，但 Google 和百度均不可达，执行一次 Wi-Fi 恢复。");
            await TriggerRecoveryAsync(manual: false, cancellationToken).ConfigureAwait(false);
        }
        else
        {
            _failureStarted = null;
            _nextProbeAllowedUtc = DateTimeOffset.MinValue;
            SetStatus(WatcherStatus.Normal, "网络正常");
        }
    }

    private async Task TriggerRecoveryAsync(bool manual, CancellationToken cancellationToken = default)
    {
        lock (_sync)
        {
            if (_recoveryRunning)
            {
                return;
            }

            _recoveryRunning = true;
        }

        try
        {
            var config = GetConfig();
            SetStatus(WatcherStatus.Recovering, "正在恢复");
            AppLogger.Info($"开始{(manual ? "手动" : "自动")}恢复：SSID={config.TargetSsid}; 网卡={config.AdapterName}");
            var result = await _wifiController.RecoverAsync(config, cancellationToken).ConfigureAwait(false);
            if (!result.Success && result.Notify && result.ErrorMessage is not null)
            {
                NotificationRequested?.Invoke(result.ErrorMessage);
            }

            AppLogger.Info(result.Success ? "Wi-Fi 恢复流程完成。" : $"Wi-Fi 恢复流程结束但未成功：{result.ErrorMessage}");

            _failureStarted = null;
            _cooldownUntil = DateTimeOffset.UtcNow.AddSeconds(config.CooldownSeconds);
            SetStatus(WatcherStatus.CoolingDown, "冷却中");
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            AppLogger.Warning("Wi-Fi 恢复流程被取消。");
            if (!manual)
            {
                _failureStarted = DateTimeOffset.UtcNow;
            }
        }
        catch (Exception ex)
        {
            AppLogger.Error("Wi-Fi 恢复流程发生异常", ex);
            NotificationRequested?.Invoke($"Wi-Fi 恢复失败：{ex.Message}");
            _cooldownUntil = DateTimeOffset.UtcNow.AddSeconds(GetConfig().CooldownSeconds);
            SetStatus(WatcherStatus.CoolingDown, "冷却中");
        }
        finally
        {
            lock (_sync) { _recoveryRunning = false; }
        }
    }

    private AppConfig GetConfig()
    {
        lock (_sync) { return _config.Clone(); }
    }

    private void OnNetworkChanged(object? sender, NetworkAvailabilityEventArgs e) => SignalNetworkChange();

    private void SignalNetworkChange()
    {
        lock (_sync)
        {
            _networkSignal.TrySetResult(true);
        }
    }

    private Task GetSignalTask()
    {
        lock (_sync) { return _networkSignal.Task; }
    }

    private void ResetSignal(Task signalTask)
    {
        lock (_sync)
        {
            if (ReferenceEquals(_networkSignal.Task, signalTask))
            {
                _networkSignal = CreateSignal();
            }
        }
    }

    private static TaskCompletionSource<bool> CreateSignal() =>
        new(TaskCreationOptions.RunContinuationsAsynchronously);

    private void SetStatus(WatcherStatus status, string text)
    {
        if (string.Equals(_lastStatusText, text, StringComparison.Ordinal))
        {
            return;
        }

        _lastStatusText = text;
        AppLogger.Info($"状态：{text}");
        StatusChanged?.Invoke(status, text);
    }
}

internal readonly record struct NetworkSnapshot(
    bool IsHealthy,
    bool IsAdapterPresent,
    bool IsWifiConnected,
    bool IsInternetAvailable)
{
    public static NetworkSnapshot Read(string adapterName)
    {
        var adapter = NetworkInterface.GetAllNetworkInterfaces().FirstOrDefault(x =>
            string.Equals(x.Name, adapterName, StringComparison.OrdinalIgnoreCase) ||
            string.Equals(x.Description, adapterName, StringComparison.OrdinalIgnoreCase));
        if (adapter is null)
        {
            return new NetworkSnapshot(false, false, false, false);
        }

        var wifiConnected = adapter.NetworkInterfaceType == NetworkInterfaceType.Wireless80211 &&
                            adapter.OperationalStatus == OperationalStatus.Up;
        var internetAvailable = WindowsAdapterConnectivityReader.IsInternetAvailable(adapter.Name);
        return new NetworkSnapshot(internetAvailable && wifiConnected, true, wifiConnected, internetAvailable);
    }
}

internal static class WindowsAdapterConnectivityReader
{
    private static readonly object Sync = new();
    private static string? _cachedAdapterName;
    private static DateTimeOffset _cacheUntilUtc;
    private static bool _cachedResult;

    public static bool IsInternetAvailable(string adapterName)
    {
        lock (Sync)
        {
            if (string.Equals(_cachedAdapterName, adapterName, StringComparison.OrdinalIgnoreCase) &&
                DateTimeOffset.UtcNow < _cacheUntilUtc)
            {
                return _cachedResult;
            }
        }

        var result = ReadFromWindows(adapterName);
        lock (Sync)
        {
            _cachedAdapterName = adapterName;
            _cachedResult = result;
            _cacheUntilUtc = DateTimeOffset.UtcNow.AddSeconds(3);
        }

        return result;
    }

    private static bool ReadFromWindows(string adapterName)
    {
        try
        {
            var escapedName = adapterName.Replace("'", "''", StringComparison.Ordinal);
            var script = $"$p=Get-NetConnectionProfile -InterfaceAlias '{escapedName}' -ErrorAction SilentlyContinue; if ($p -and ($p | Where-Object {{ $_.IPv4Connectivity -eq 'Internet' -or $_.IPv6Connectivity -eq 'Internet' }})) {{ '1' }} else {{ '0' }}";
            var startInfo = new ProcessStartInfo
            {
                FileName = "powershell.exe",
                UseShellExecute = false,
                CreateNoWindow = true,
                RedirectStandardOutput = true,
                RedirectStandardError = true
            };
            startInfo.ArgumentList.Add("-NoProfile");
            startInfo.ArgumentList.Add("-NonInteractive");
            startInfo.ArgumentList.Add("-ExecutionPolicy");
            startInfo.ArgumentList.Add("Bypass");
            startInfo.ArgumentList.Add("-EncodedCommand");
            startInfo.ArgumentList.Add(Convert.ToBase64String(Encoding.Unicode.GetBytes(script)));

            using var process = new Process { StartInfo = startInfo };
            if (!process.Start() || !process.WaitForExit(2500))
            {
                try { if (!process.HasExited) process.Kill(entireProcessTree: true); } catch { }
                AppLogger.Warning($"无法读取网卡 Internet 状态：{adapterName}");
                return false;
            }

            var output = process.StandardOutput.ReadToEnd().Trim();
            return output == "1";
        }
        catch (Exception ex)
        {
            AppLogger.Warning($"读取网卡 Internet 状态失败：{adapterName}; {ex.Message}");
            return false;
        }
    }
}

internal static class NetworkListManagerReader
{
    private const int NlmConnectivityIpv4Internet = 0x40;
    private const int NlmConnectivityIpv6Internet = 0x400;

    public static bool IsInternetAvailable(Guid adapterId)
    {
        try
        {
            var manager = (INetworkListManager)new NetworkListManager();
            try
            {
                var hresult = manager.GetNetworkConnections(out var connections);
                if (hresult < 0 || connections is null)
                {
                    return false;
                }

                try
                {
                    while (true)
                    {
                        var connectionBuffer = new nint[1];
                        uint fetched = 0;
                        var nextResult = connections.Next(1, connectionBuffer, ref fetched);
                        if (fetched > 0 && connectionBuffer[0] != 0)
                        {
                            var connection = (INetworkConnection)Marshal.GetObjectForIUnknown(connectionBuffer[0]);
                            try
                            {
                                if (connection.GetAdapterId(out var connectionAdapterId) >= 0 &&
                                    connectionAdapterId == adapterId &&
                                    connection.GetConnectivity(out var connectivity) >= 0)
                                {
                                    if ((connectivity & (NlmConnectivityIpv4Internet | NlmConnectivityIpv6Internet)) != 0)
                                    {
                                        return true;
                                    }
                                }
                            }
                            finally
                            {
                                Marshal.FinalReleaseComObject(connection);
                                Marshal.Release(connectionBuffer[0]);
                            }
                        }

                        if (nextResult < 0 || fetched == 0)
                        {
                            break;
                        }
                    }

                    return false;
                }
                finally
                {
                    Marshal.FinalReleaseComObject(connections);
                }
            }
            finally
            {
                Marshal.FinalReleaseComObject(manager);
            }
        }
        catch
        {
            // If NLM is temporarily unavailable, prefer the conservative local
            // adapter state instead of falsely declaring no Internet and cycling Wi-Fi.
            AppLogger.Warning("Network List Manager 暂时不可用，回退到 Windows 本地网卡状态。");
            return false;
        }
    }

    [ComImport, Guid("DCB00C01-570F-4A9B-8D69-199FDBA5723B")]
    private class NetworkListManager { }

    [ComImport, Guid("DCB00000-570F-4A9B-8D69-199FDBA5723B"), InterfaceType(ComInterfaceType.InterfaceIsDual)]
    private interface INetworkListManager
    {
        [PreserveSig]
        int GetNetworks(uint flags, out nint networks);

        [PreserveSig]
        int GetNetwork(Guid networkId, out nint network);

        [PreserveSig]
        int GetNetworkConnections([MarshalAs(UnmanagedType.Interface)] out IEnumNetworkConnections connections);

        [PreserveSig]
        int GetNetworkConnection(Guid connectionId, out nint connection);

        [PreserveSig]
        int IsConnectedToInternet([MarshalAs(UnmanagedType.VariantBool)] out bool connected);

        [PreserveSig]
        int IsConnected([MarshalAs(UnmanagedType.VariantBool)] out bool connected);

        [PreserveSig]
        int GetConnectivity(out int connectivity);
    }

    [ComImport, Guid("DCB00006-570F-4A9B-8D69-199FDBA5723B"), InterfaceType(ComInterfaceType.InterfaceIsDual)]
    private interface IEnumNetworkConnections
    {
        [PreserveSig]
        int Next(uint count,
            [Out, MarshalAs(UnmanagedType.LPArray, SizeParamIndex = 0)] nint[] connections,
            ref uint fetched);
    }

    [ComImport, Guid("DCB00005-570F-4A9B-8D69-199FDBA5723B"), InterfaceType(ComInterfaceType.InterfaceIsDual)]
    private interface INetworkConnection
    {
        [PreserveSig]
        int GetNetwork(out nint network);

        [PreserveSig]
        int IsConnectedToInternet([MarshalAs(UnmanagedType.VariantBool)] out bool connected);

        [PreserveSig]
        int IsConnected([MarshalAs(UnmanagedType.VariantBool)] out bool connected);

        [PreserveSig]
        int GetConnectivity(out int connectivity);

        [PreserveSig]
        int GetConnectionId(out Guid connectionId);

        [PreserveSig]
        int GetAdapterId(out Guid adapterId);

        [PreserveSig]
        int GetDomainType(out int domainType);
    }
}
