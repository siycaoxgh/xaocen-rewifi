using System.Diagnostics;

namespace XAOCEN.ReWiFi;

internal static class DocumentationRouter
{
    private static readonly TimeSpan AvailabilityTimeout = TimeSpan.FromSeconds(2.5);

    public static async Task<bool> OpenAsync()
    {
        if (await IsOnlineDocumentationAvailableAsync().ConfigureAwait(false))
        {
            return TryOpenExternal(ProductInfo.OnlineDocsUrl, "打开在线产品文档") || AboutPage.TryOpenLocal();
        }

        AppLogger.Info("在线产品文档不可达，回退到本地产品介绍页面。");
        return AboutPage.TryOpenLocal();
    }

    public static bool OpenOnline() => TryOpenExternal(ProductInfo.OnlineDocsUrl, "打开在线产品文档");

    public static async Task OpenLegalAsync(string name)
    {
        if (name is not ("terms" or "privacy")) throw new ArgumentException("未知协议");
        var url = $"https://www.xaocen.studio/products/rewifi/{name}/";
        try
        {
            using var client = new HttpClient { Timeout = AvailabilityTimeout };
            using var response = await client.GetAsync(url, HttpCompletionOption.ResponseHeadersRead);
            if (response.IsSuccessStatusCode && TryOpenExternal(url, "打开产品协议")) return;
        }
        catch (HttpRequestException) { }
        catch (TaskCanceledException) { }
        AboutPage.TryOpenLegal(name);
    }

    public static bool OpenLocal() => AboutPage.TryOpenLocal();

    private static async Task<bool> IsOnlineDocumentationAvailableAsync()
    {
        try
        {
            using var handler = new HttpClientHandler { UseProxy = true };
            using var client = new HttpClient(handler) { Timeout = AvailabilityTimeout };
            using var response = await client.GetAsync(ProductInfo.OnlineDocsUrl, HttpCompletionOption.ResponseHeadersRead)
                .ConfigureAwait(false);
            return response.IsSuccessStatusCode;
        }
        catch (Exception ex)
        {
            AppLogger.Warning($"在线产品文档可用性检查失败：{ex.GetType().Name}");
            return false;
        }
    }

    private static bool TryOpenExternal(string url, string action)
    {
        try
        {
            Process.Start(new ProcessStartInfo(url) { UseShellExecute = true });
            AppLogger.Info($"{action}。");
            return true;
        }
        catch (Exception ex)
        {
            AppLogger.Error($"{action}失败。", ex);
            return false;
        }
    }
}
