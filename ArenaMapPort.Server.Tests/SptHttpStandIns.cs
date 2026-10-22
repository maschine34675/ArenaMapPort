#nullable disable
using System.Net.Http;

// Stand-ins for the part of SPT's client library (spt-common, SPT.Common.Http) that ServerStatusClient reaches by reflection. The real types live in a
// BepInEx plugin of SPT 4.1.6 (RequestHandler.HttpClient is a public static readonly field of SPT's Client, whose CreateNewHttpRequest builds the request with
// the server address and the session cookie); the shapes below are the ones the 4.1.6 decompile shows, plus the broken variants the probe must survive.
// They are public static state: only ServerStatusClientTests touches them, and xunit runs the tests of one class one after the other.
namespace SPT.Common.Http
{
    /// <summary>SPT's <c>Client</c>: the address, the session cookie, a request factory and the underlying <see cref="HttpClient"/>.</summary>
    public class Client
    {
        readonly string _address;

        public Client(string address)
        {
            _address = address;
        }

        public HttpClient HttpClient { get; } = new HttpClient();

        /// <summary>The managed name of the thread that built the last request (the probe must run on its own thread).</summary>
        public static string LastRequestThread;
        public static bool LastRequestThreadIsBackground;

        public HttpRequestMessage CreateNewHttpRequest(HttpMethod method, string path)
        {
            LastRequestThread = Thread.CurrentThread.Name;
            LastRequestThreadIsBackground = Thread.CurrentThread.IsBackground;
            return new HttpRequestMessage
            {
                Method = method,
                RequestUri = new Uri(_address + path),
                Headers = { { "Cookie", "PHPSESSID=standin-session" } },
            };
        }
    }

    /// <summary>The handler the probe finds: a public static field, like the real one (the real one is readonly; a test needs to swap the address).</summary>
    public static class RequestHandler
    {
        public static Client HttpClient;
    }

    /// <summary>The fallback shape: a public static property instead of a field.</summary>
    public static class PropertyRequestHandler
    {
        public static Client HttpClient { get; set; }
    }

    /// <summary>A handler whose static constructor throws, like the real one when SPT's launch arguments carry no backend address.</summary>
    public static class ThrowingRequestHandler
    {
        public static readonly Client HttpClient;

        static ThrowingRequestHandler()
        {
            throw new InvalidOperationException("no backend address in the launch arguments");
        }
    }

    /// <summary>A handler whose client was never created.</summary>
    public static class EmptyRequestHandler
    {
        public static Client HttpClient;
    }

    /// <summary>A handler whose "client" is not SPT's client class (a library that moved on).</summary>
    public static class UnfamiliarRequestHandler
    {
        public static object HttpClient = new object();
    }
}
