#nullable disable
using System.Collections.Concurrent;
using System.IO.Compression;
using System.Net;
using System.Net.Http;
using System.Net.Sockets;
using System.Text;
using ArenaMapPort.Client;
using ArenaMapPort.Shared;
using Xunit;
using StandIn = SPT.Common.Http;

namespace ArenaMapPort.Server.Tests;

/// <summary>One canned answer of <see cref="HttpStub"/>.</summary>
internal sealed record StubResponse(int Status, byte[] Body = null, int DelayMilliseconds = 0);

/// <summary>
/// A minimal HTTP/1.1 server on a loopback port: reads a request head, answers with the response its handler picks, closes the connection. It stands in for the
/// SPT server so the probe's real <see cref="HttpClient"/> path (reflection into the stand-in SPT client, request, status, body, compression) runs for real.
/// </summary>
internal sealed class HttpStub : IDisposable
{
    readonly TcpListener _listener;
    readonly Thread _accept;
    volatile bool _stop;

    internal readonly ConcurrentQueue<string> Heads = new();
    internal Func<string, StubResponse> Handler = _ => new StubResponse(404);

    internal HttpStub()
    {
        _listener = new TcpListener(IPAddress.Loopback, 0);
        _listener.Start();
        _accept = new Thread(AcceptLoop) { IsBackground = true, Name = "HttpStub" };
        _accept.Start();
    }

    internal string Address => "http://127.0.0.1:" + ((IPEndPoint)_listener.LocalEndpoint).Port;

    void AcceptLoop()
    {
        while (!_stop)
        {
            TcpClient client;
            try
            {
                client = _listener.AcceptTcpClient();
            }
            catch
            {
                return;
            }
            ThreadPool.QueueUserWorkItem(_ => Serve(client));
        }
    }

    void Serve(TcpClient client)
    {
        try
        {
            using (client)
            using (NetworkStream stream = client.GetStream())
            {
                var head = new StringBuilder();
                var buffer = new byte[1];
                while (!head.ToString().EndsWith("\r\n\r\n", StringComparison.Ordinal))
                {
                    if (stream.Read(buffer, 0, 1) <= 0)
                        return;
                    head.Append((char)buffer[0]);
                }
                string text = head.ToString();
                Heads.Enqueue(text);
                string path = text.Split(' ')[1];
                StubResponse response = Handler(path);
                if (response.DelayMilliseconds > 0)
                    Thread.Sleep(response.DelayMilliseconds);
                byte[] body = response.Body ?? Array.Empty<byte>();
                string reason = response.Status == 200 ? "OK" : response.Status == 404 ? "Not Found" : "Error";
                byte[] header = Encoding.ASCII.GetBytes("HTTP/1.1 " + response.Status + " " + reason + "\r\nContent-Length: " + body.Length + "\r\nConnection: close\r\n\r\n");
                stream.Write(header, 0, header.Length);
                stream.Write(body, 0, body.Length);
            }
        }
        catch
        {
            // a client that went away is the client's business
        }
    }

    public void Dispose()
    {
        _stop = true;
        try { _listener.Stop(); }
        catch { /* best effort */ }
    }
}

/// <summary>The probe against stand-ins of SPT's HTTP types and a loopback server: every outcome the notices read (docs/progress/map-notices.md, section 4.4).</summary>
public sealed class ServerStatusClientTests : IDisposable
{
    static readonly TimeSpan Patience = TimeSpan.FromSeconds(20);

    readonly HttpStub _server = new();

    public ServerStatusClientTests()
    {
        StandIn.RequestHandler.HttpClient = new StandIn.Client(_server.Address);
        StandIn.PropertyRequestHandler.HttpClient = new StandIn.Client(_server.Address);
        StandIn.Client.LastRequestThread = null;
    }

    public void Dispose()
    {
        _server.Dispose();
        StandIn.RequestHandler.HttpClient = null;
        StandIn.PropertyRequestHandler.HttpClient = null;
    }

    const string NotVerifiedJson =
        "{\"schema\":1,\"serverVersion\":\"0.3.0\",\"gate\":{\"state\":\"not_verified\",\"cause\":\"not_registered\",\"source\":\"none\"," +
        "\"manifestVersion\":null,\"mismatchCount\":0,\"overrideFile\":\"absent\"},\"maps\":[{\"key\":\"arena_equator\",\"state\":\"not_registered\",\"cause\":\"gate\"}]}";

    static byte[] Zlib(string text)
    {
        using var output = new MemoryStream();
        using (var zlib = new ZLibStream(output, CompressionLevel.Optimal, leaveOpen: true))
        {
            byte[] bytes = Encoding.UTF8.GetBytes(text);
            zlib.Write(bytes, 0, bytes.Length);
        }
        return output.ToArray();
    }

    static ServerStatusClient Probe(string handler = ServerStatusClient.RequestHandlerTypeName, double seconds = 10) =>
        new(handler, TimeSpan.FromSeconds(seconds));

    static ServerStatusClient Run(ServerStatusClient probe)
    {
        probe.Start();
        Assert.True(probe.Wait(Patience), "the probe did not finish");
        return probe;
    }

    // ---- the answers ---------------------------------------------------------------------------------------------------------

    [Fact]
    public void BeforeStartNothingHappensAndAfterItThePendingStateShows()
    {
        var probe = Probe();
        Assert.Equal(ProbeState.NotAsked, probe.Result.State);
        Assert.False(probe.Finished);
        Assert.Null(probe.Detail);
        Assert.Empty(_server.Heads);

        _server.Handler = _ => new StubResponse(200, Encoding.UTF8.GetBytes(NotVerifiedJson), DelayMilliseconds: 300);
        probe.Start();
        Assert.Equal(ProbeState.Pending, probe.Result.State); // returns at once, the answer comes later
        Assert.True(probe.Wait(Patience));
        Assert.Equal(ProbeState.Ok, probe.Result.State);
    }

    [Fact]
    public void APlainJsonAnswerIsReadIntoTheStatusView()
    {
        _server.Handler = _ => new StubResponse(200, Encoding.UTF8.GetBytes(NotVerifiedJson));
        var probe = Run(Probe());

        Assert.Equal(ProbeState.Ok, probe.Result.State);
        ServerStatusView status = probe.Result.Status;
        Assert.Equal("not_verified", status.GateState);
        Assert.Equal("not_registered", status.GateCauseCode);
        Assert.Equal("0.3.0", status.ServerVersion);
        Assert.Equal("ok:not_verified/not_registered", probe.Result.LogCode);
        Assert.Matches(@"^GET /arenamapport/status -> 200 in \d+ ms$", probe.Detail);
    }

    [Fact]
    public void AZlibCompressedAnswerLikeSptsServerSendsIsInflated()
    {
        byte[] body = Zlib(NotVerifiedJson);
        Assert.Equal(0x78, body[0]); // the header SPT's server writes
        _server.Handler = _ => new StubResponse(200, body);

        var probe = Run(Probe());

        Assert.Equal(ProbeState.Ok, probe.Result.State);
        Assert.Equal("not_registered", probe.Result.Status.GateCauseCode);
    }

    [Fact]
    public void ARequestToARouteTheServerDoesNotKnowIsNotFoundWithoutRetries()
    {
        _server.Handler = _ => new StubResponse(404, Encoding.UTF8.GetBytes("Not Found"));

        var probe = Run(Probe());

        Assert.Equal(ProbeState.NotFound, probe.Result.State);
        Assert.Equal("404", probe.Result.LogCode);
        Assert.Equal("GET /arenamapport/status -> 404", probe.Detail);
        Assert.Single(_server.Heads); // SPT's GetJson would have asked four times
    }

    [Fact]
    public void AServerErrorIsAFailure()
    {
        _server.Handler = _ => new StubResponse(500, Encoding.UTF8.GetBytes("boom"));

        var probe = Run(Probe());

        Assert.Equal(ProbeState.Failed, probe.Result.State);
        Assert.Equal("GET /arenamapport/status -> HTTP 500", probe.Detail);
    }

    [Theory]
    [InlineData("not json at all")]
    [InlineData("{\"schema\":2,\"gate\":{\"state\":\"verified\"}}")] // a schema this client does not know
    [InlineData("{\"schema\":1}")]                                      // no gate
    public void AnUnreadableDocumentIsUnreadableNotAFailure(string body)
    {
        // design section 4.3: a missing or higher schema "reads as unavailable": the notice is the one for a server that did not say why, never "did not answer"
        _server.Handler = _ => new StubResponse(200, Encoding.UTF8.GetBytes(body));

        var probe = Run(Probe());

        Assert.Equal(ProbeState.Unreadable, probe.Result.State);
        Assert.Equal("unreadable", probe.Result.LogCode);
        Assert.StartsWith("GET /arenamapport/status -> 200 but unreadable (", probe.Detail);
    }

    [Fact]
    public void AnAnswerThatTakesLongerThanTheTokenIsATimeoutAndLeavesTheSharedTimeoutAlone()
    {
        _server.Handler = _ => new StubResponse(200, Encoding.UTF8.GetBytes(NotVerifiedJson), DelayMilliseconds: 5000);
        TimeSpan shared = StandIn.RequestHandler.HttpClient.HttpClient.Timeout;
        var clock = System.Diagnostics.Stopwatch.StartNew();

        var probe = Run(Probe(seconds: 0.4));

        Assert.True(clock.Elapsed < TimeSpan.FromSeconds(4), "the token cancelled the request, not the server's delay");
        Assert.Equal(ProbeState.Failed, probe.Result.State);
        Assert.Equal("GET /arenamapport/status -> timeout", probe.Detail);
        Assert.Equal(shared, StandIn.RequestHandler.HttpClient.HttpClient.Timeout); // the shared HttpClient.Timeout is never changed
    }

    [Fact]
    public void AServerThatIsNotListeningIsAFailureWithTheExceptionNamed()
    {
        var closed = new TcpListener(IPAddress.Loopback, 0);
        closed.Start();
        string address = "http://127.0.0.1:" + ((IPEndPoint)closed.LocalEndpoint).Port;
        closed.Stop();
        StandIn.RequestHandler.HttpClient = new StandIn.Client(address);

        var probe = Run(Probe());

        Assert.Equal(ProbeState.Failed, probe.Result.State);
        Assert.StartsWith("GET /arenamapport/status -> HttpRequestException: ", probe.Detail);
    }

    // ---- the request itself ----------------------------------------------------------------------------------------------------

    [Fact]
    public void TheRequestIsAGetOfTheStatusRouteWithTheSessionCookieFromSptsClientOnItsOwnBackgroundThread()
    {
        _server.Handler = _ => new StubResponse(200, Encoding.UTF8.GetBytes(NotVerifiedJson));

        Run(Probe());

        string head = Assert.Single(_server.Heads);
        Assert.StartsWith("GET /arenamapport/status HTTP/1.1\r\n", head);
        Assert.Contains("Cookie: PHPSESSID=standin-session", head); // built by SPT's CreateNewHttpRequest, not by this plugin
        Assert.Equal(ServerStatusClient.ThreadName, StandIn.Client.LastRequestThread);
        Assert.True(StandIn.Client.LastRequestThreadIsBackground);
        Assert.Equal("/arenamapport/status", ArenaStatusContract.StatusRoute);
    }

    [Fact]
    public void StartingTwiceAsksOnce()
    {
        _server.Handler = _ => new StubResponse(200, Encoding.UTF8.GetBytes(NotVerifiedJson));
        var probe = Probe();

        probe.Start();
        probe.Start();
        Assert.True(probe.Wait(Patience));
        probe.Start();

        Assert.Single(_server.Heads);
    }

    [Fact]
    public void ThePropertyShapeOfTheHandlerWorksToo()
    {
        _server.Handler = _ => new StubResponse(200, Encoding.UTF8.GetBytes(NotVerifiedJson));

        var probe = Run(Probe("SPT.Common.Http.PropertyRequestHandler"));

        Assert.Equal(ProbeState.Ok, probe.Result.State);
    }

    // ---- no SPT HTTP client -------------------------------------------------------------------------------------------------------

    [Theory]
    [InlineData("SPT.Common.Http.NoSuchHandler", "not found")]
    [InlineData("SPT.Common.Http.ThrowingRequestHandler", "no backend address in the launch arguments")]
    [InlineData("SPT.Common.Http.EmptyRequestHandler", "is not available")]
    [InlineData("SPT.Common.Http.UnfamiliarRequestHandler", "lacks CreateNewHttpRequest or HttpClient")]
    public void AMissingOrBrokenSptClientIsUnavailableAndNoRequestIsMade(string handler, string why)
    {
        var probe = Run(Probe(handler));

        Assert.Equal(ProbeState.Unavailable, probe.Result.State);
        Assert.Equal("no_http", probe.Result.LogCode);
        Assert.StartsWith("GET /arenamapport/status -> no SPT HTTP client (", probe.Detail);
        Assert.Contains(why, probe.Detail);
        Assert.Empty(_server.Heads);
    }

    [Fact]
    public void TheProbeNeverLogsAndNeverTouchesTheNoticeEnvironment()
    {
        string source = File.ReadAllText(Path.Combine(ClientDirectory(), "ServerStatusClient.cs"));
        Assert.DoesNotContain("Plugin.Log", source); // the worker never logs: the game thread reads Detail and logs
        Assert.DoesNotMatch(@"\bGetJson(Async)?\(", source);   // SPT's retrying helper is never called
        Assert.DoesNotContain("new HttpClient", source);       // SPT's client is reused: cookie and certificate bypass come with it
        Assert.DoesNotMatch(@"\.Timeout\s*=", source);        // the shared timeout is never assigned
        Assert.DoesNotContain("UnityEngine", source);
    }

    static string ClientDirectory()
    {
        for (var dir = new DirectoryInfo(AppContext.BaseDirectory); dir != null; dir = dir.Parent)
        {
            string path = Path.Combine(dir.FullName, "ArenaMapPort.Client");
            if (File.Exists(Path.Combine(path, "Plugin.cs")))
                return path;
        }
        throw new DirectoryNotFoundException("ArenaMapPort.Client");
    }
}
