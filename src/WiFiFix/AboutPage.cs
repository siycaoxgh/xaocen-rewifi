using System.Diagnostics;
using System.Reflection;
using System.Text;

namespace XAOCEN.ReWiFi;

public static class AboutPage
{
    private const string AboutFileName = "XAOCEN-ReWiFi-介绍.html";
    private const string BannerFileName = "xaocen-rewifi.png";

    public static bool TryOpen()
    {
        try
        {
            Directory.CreateDirectory(AppConfig.DirectoryPath);

            var htmlPath = Path.Combine(AppConfig.DirectoryPath, AboutFileName);
            File.WriteAllText(htmlPath, ReadTextResource("About.html"), new UTF8Encoding(false));

            using var imageStream = OpenResource(BannerFileName);
            if (imageStream is not null)
            {
                var imagePath = Path.Combine(AppConfig.DirectoryPath, BannerFileName);
                using var output = File.Create(imagePath);
                imageStream.CopyTo(output);
            }

            Process.Start(new ProcessStartInfo
            {
                FileName = htmlPath,
                UseShellExecute = true
            });
            AppLogger.Info($"打开本地产品介绍页面：{htmlPath}");
            return true;
        }
        catch (Exception ex)
        {
            AppLogger.Error("打开本地产品介绍页面失败", ex);
            return false;
        }
    }

    private static string ReadTextResource(string fileName)
    {
        using var stream = OpenResource(fileName)
            ?? throw new FileNotFoundException($"找不到内置介绍页面：{fileName}");
        using var reader = new StreamReader(stream, Encoding.UTF8);
        return reader.ReadToEnd();
    }

    private static Stream? OpenResource(string fileName)
    {
        var resourceName = Assembly.GetExecutingAssembly()
            .GetManifestResourceNames()
            .FirstOrDefault(name => name.EndsWith($".{fileName}", StringComparison.OrdinalIgnoreCase));
        return resourceName is null ? null : Assembly.GetExecutingAssembly().GetManifestResourceStream(resourceName);
    }
}
