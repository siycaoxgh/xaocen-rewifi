using Microsoft.VisualStudio.TestTools.UnitTesting;
using XAOCEN.ReWiFi;

namespace WiFiFix.Tests;

[TestClass]
public sealed class WifiControllerTests
{
    [TestMethod]
    public void IsAdministrator_LoadsWindowsSecurityPrincipalAssembly()
    {
        try
        {
            _ = WifiController.IsAdministrator();
        }
        catch (Exception ex)
        {
            Assert.Fail($"Windows 权限检测不应抛出异常，实际异常：{ex}");
        }
    }
}
