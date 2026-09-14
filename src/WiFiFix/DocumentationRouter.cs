using System.Diagnostics;

namespace XAOCEN.ReWiFi;

internal static class DocumentationRouter
{
    private static readonly TimeSpan AvailabilityTimeout = TimeSpan.FromSeconds(6);
    private static readonly HttpClient AvailabilityClient = CreateAvailabilityClient();

    public static async Task<bool> OpenAsync()
    {
        return await OpenWithFallbackAsync(
            ProductInfo.OnlineDocsUrl,
            "产品介绍与使用帮助",
            AboutPage.TryOpenLocal).ConfigureAwait(false);
    }

    public static bool OpenOnline() => TryOpenExternal(ProductInfo.OnlineDocsUrl, "打开在线产品文档");

    public static async Task OpenLegalAsync(string name)
    {
        if (name is not ("terms" or "privacy")) throw new ArgumentException("未知协议");
        var url = $"https://www.xaocen.studio/products/rewifi/{name}/";
        await OpenWithFallbackAsync(url, name == "terms" ? "用户协议" : "隐私说明", () => AboutPage.TryOpenLegal(name))
            .ConfigureAwait(false);
    }

    public static bool OpenLocal() => AboutPage.TryOpenLocal();

    private static async Task<bool> OpenWithFallbackAsync(string url, string title, Func<bool> openLocal)
    {
        if (await IsOnlineResourceAvailableAsync(url).ConfigureAwait(false) && TryOpenExternal(url, $"打开在线{title}"))
        {
            return true;
        }

        AppLogger.Info($"在线{title}不可达，回退到内置本地副本。");
        return openLocal();
    }

    private static async Task<bool> IsOnlineResourceAvailableAsync(string url)
    {
        try
        {
            using var request = new HttpRequestMessage(HttpMethod.Get, url);
            using var response = await AvailabilityClient.SendAsync(request, HttpCompletionOption.ResponseHeadersRead)
                .ConfigureAwait(false);
            return response.IsSuccessStatusCode;
        }
        catch (Exception ex)
        {
            AppLogger.Warning($"在线文档可用性检查失败：{ex.GetType().Name}");
            return false;
        }
    }

    private static HttpClient CreateAvailabilityClient()
    {
        var handler = new HttpClientHandler { UseProxy = true };
        var client = new HttpClient(handler) { Timeout = AvailabilityTimeout };
        client.DefaultRequestHeaders.UserAgent.ParseAdd("XAOCEN-ReWiFi/2.4");
        return client;
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
