using System.Net;
using System.Net.Http;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using XAOCEN.ReWiFi;

namespace WiFiFix.Tests;

[TestClass]
public sealed class ConnectivityProbeTests
{
    private static readonly Uri[] TestUris =
    [
        new Uri("https://google.test/generate_204"),
        new Uri("https://baidu.test/")
    ];

    [TestMethod]
    public void ProductionHandler_DisablesSystemProxy()
    {
        using var handler = ConnectivityProbe.CreateDirectHandler();

        Assert.IsFalse(handler.UseProxy);
        Assert.IsNull(handler.Proxy);
    }

    [TestMethod]
    public async Task ProxyEnvironmentDoesNotChangeDirectProbe()
    {
        var oldHttpProxy = Environment.GetEnvironmentVariable("HTTP_PROXY");
        var oldHttpsProxy = Environment.GetEnvironmentVariable("HTTPS_PROXY");
        var oldAllProxy = Environment.GetEnvironmentVariable("ALL_PROXY");
        try
        {
            Environment.SetEnvironmentVariable("HTTP_PROXY", "http://127.0.0.1:7897");
            Environment.SetEnvironmentVariable("HTTPS_PROXY", "http://127.0.0.1:7897");
            Environment.SetEnvironmentVariable("ALL_PROXY", "http://127.0.0.1:7897");

            using var handler = new StubHandler(HttpStatusCode.NoContent, HttpStatusCode.OK);
            var probe = new ConnectivityProbe(handler, TestUris);
            var result = await probe.CheckAsync(1, CancellationToken.None);

            Assert.IsTrue(result.AllReachable);
            Assert.AreEqual(2, handler.Requests.Count);
        }
        finally
        {
            Environment.SetEnvironmentVariable("HTTP_PROXY", oldHttpProxy);
            Environment.SetEnvironmentVariable("HTTPS_PROXY", oldHttpsProxy);
            Environment.SetEnvironmentVariable("ALL_PROXY", oldAllProxy);
        }
    }

    [TestMethod]
    public async Task NoInternet_ProducesNoneReachableAndRequestsRecovery()
    {
        using var handler = new StubHandler(new HttpRequestException("network unavailable"));
        var probe = new ConnectivityProbe(handler, TestUris);

        var result = await probe.CheckAsync(1, CancellationToken.None);

        Assert.IsTrue(result.NoneReachable);
        Assert.IsTrue(NetworkWatcher.ShouldRecoverAfterConnectivityProbe(result));
    }

    private sealed class StubHandler : HttpMessageHandler
    {
        private readonly Queue<Func<HttpResponseMessage>> _responses;

        public StubHandler(params HttpStatusCode[] statuses)
        {
            _responses = new Queue<Func<HttpResponseMessage>>(
                statuses.Select(status => (Func<HttpResponseMessage>)(() => new HttpResponseMessage(status))));
        }

        public StubHandler(HttpRequestException exception)
        {
            _responses = new Queue<Func<HttpResponseMessage>>(
                [() => throw exception, () => throw exception]);
        }

        public List<Uri> Requests { get; } = [];

        protected override Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request, CancellationToken cancellationToken)
        {
            Requests.Add(request.RequestUri!);
            return Task.FromResult(_responses.Dequeue()());
        }
    }
}
