#nullable disable
using System;
using System.Diagnostics;
using System.Net.Http;
using System.Reflection;
using System.Threading;
using ArenaMapPort.Shared;
using HarmonyLib;

namespace ArenaMapPort.Client
{
    /// <summary>
    /// The quiet question "why does the server not offer the Arena maps?" (<c>GET /arenamapport/status</c>, docs/progress/map-notices.md section 4.4).
    ///
    /// What vanilla does. SPT's client talks to its server through <c>SPT.Common.Http.RequestHandler</c>, whose <c>GetJson</c> retries three times and writes
    /// an Error line per attempt and two Info lines per call: a route an older server does not know (HTTP 404) would cost four Error lines in the log of a
    /// player who did nothing wrong. SPT offers a mod no way to ask its server a question quietly.
    ///
    /// What the port lacks. The reason the server registered no Arena location lives on the server (<c>ArenaServerStatus</c>); without it the main-menu notice
    /// could only say "the maps are not offered".
    ///
    /// What this class does. When the main-menu notice needs the server's reason (the client verified Arena, a registry map is not offered), <see cref="Start"/>
    /// sends one request on a background thread named <see cref="ThreadName"/>, by reflection, because this plugin has no reference to SPT's assemblies:
    /// <c>SPT.Common.Http.RequestHandler.HttpClient</c> (a public static field, a property as the fallback) is SPT's own <c>Client</c>, whose
    /// <c>CreateNewHttpRequest(HttpMethod, string)</c> builds the request with the server address and the session cookie and whose <c>HttpClient</c> property
    /// carries the self-signed-certificate bypass of SPT's local HTTPS. The request is sent with <c>SendAsync</c> under this class's own five-second token; the
    /// shared <c>HttpClient.Timeout</c> is never touched and <c>GetJson</c> is never used. 200 is decoded (<see cref="ServerStatusParser.DecodeBody"/>: SPT's server zlib-compresses
    /// its answers) and parsed; 404 is a server without the route (<see cref="ProbeState.NotFound"/>); a 200 whose document this client cannot read (another
    /// schema, not JSON) is <see cref="ProbeState.Unreadable"/>, which the notices treat like a server that did not say why; a timeout, another status or a
    /// transport error is <see cref="ProbeState.Failed"/>; no SPT HTTP types, or a type that fails to initialise, is <see cref="ProbeState.Unavailable"/>. The
    /// worker never logs and never touches Unity or EFT: it publishes a volatile <see cref="Result"/> and a one-line <see cref="Detail"/> that the game thread
    /// reads and logs. Nothing is cached across sessions and no request is made on the healthy path.
    ///
    /// Host test: docs/progress/map-notices.md (V3, a real 404 from the 0.2.3 server and the live answer of the new one; the reflection surface against
    /// SPT 4.1.6 is a "not verified" item there).
    /// </summary>
    internal sealed class ServerStatusClient
    {
        internal const int TimeoutSeconds = 5;
        internal const string ThreadName = "ArenaMapPort-StatusProbe";
        internal const string RequestHandlerTypeName = "SPT.Common.Http.RequestHandler";
        internal const string HttpClientMember = "HttpClient";
        internal const string CreateRequestMethod = "CreateNewHttpRequest";

        readonly string _handlerTypeName;
        readonly TimeSpan _timeout;
        readonly ManualResetEventSlim _done = new ManualResetEventSlim(false);
        volatile ProbeView _result = ProbeView.NotAsked;
        volatile string _detail;
        int _started;

        /// <summary>The probe of the plugin: SPT's own request handler, the five-second token.</summary>
        internal ServerStatusClient() : this(RequestHandlerTypeName, TimeSpan.FromSeconds(TimeoutSeconds))
        {
        }

        /// <summary>The same probe against another handler type name and timeout: for the tests only (their stand-ins of SPT's types); no setting reaches it.</summary>
        internal ServerStatusClient(string requestHandlerTypeName, TimeSpan timeout)
        {
            _handlerTypeName = requestHandlerTypeName;
            _timeout = timeout;
        }

        /// <summary>The probe's state: NotAsked before <see cref="Start"/>, Pending while the request runs, then the answer. Never blocks.</summary>
        internal ProbeView Result => _result;

        /// <summary>One line for the log once the probe finished (<c>GET /arenamapport/status -&gt; 200 in 12 ms</c>, <c>-&gt; 404</c>, <c>-&gt; timeout</c>, <c>-&gt; Type: message</c>); null before.</summary>
        internal string Detail => _detail;

        /// <summary>True once the request has finished (any outcome).</summary>
        internal bool Finished => _done.IsSet;

        /// <summary>Waits for the answer (tests).</summary>
        internal bool Wait(TimeSpan timeout) => _done.Wait(timeout);

        /// <summary>
        /// Starts the request once; a second call does nothing. Returns at once: the answer shows in <see cref="Result"/>. Never throws; a probe that cannot be
        /// started is <see cref="ProbeState.Unavailable"/>.
        /// </summary>
        internal void Start()
        {
            if (Interlocked.Exchange(ref _started, 1) != 0)
                return;
            _result = new ProbeView(ProbeState.Pending);
            try
            {
                var worker = new Thread(Run) { IsBackground = true, Name = ThreadName };
                worker.Start();
            }
            catch (Exception e)
            {
                Finish(new ProbeView(ProbeState.Unavailable), Route() + " -> " + e.GetType().Name + ": " + e.Message);
            }
        }

        static string Route() => "GET " + ArenaStatusContract.StatusRoute;

        // The exception that says what happened: reflection wraps in TargetInvocationException, a failed static constructor in TypeInitializationException, a task in AggregateException.
        static Exception Unwrap(Exception e)
        {
            while ((e is TargetInvocationException || e is TypeInitializationException || e is AggregateException) && e.InnerException != null)
                e = e.InnerException;
            return e;
        }

        void Finish(ProbeView result, string detail)
        {
            _detail = detail;
            _result = result;
            _done.Set();
        }

        // The worker. Everything that can throw is inside the one try; whatever it throws becomes a state.
        void Run()
        {
            try
            {
                Finish(Ask(out string detail), detail);
            }
            catch (Exception e)
            {
                Finish(new ProbeView(ProbeState.Failed), Route() + " -> " + e.GetType().Name + ": " + e.Message);
            }
        }

        ProbeView Ask(out string detail)
        {
            // ---- SPT's own client, by reflection -----------------------------------------------------------------------
            object client;
            MethodInfo create;
            HttpClient http;
            try
            {
                Type handler = AccessTools.TypeByName(_handlerTypeName);
                if (handler == null)
                {
                    detail = Route() + " -> no SPT HTTP client (" + _handlerTypeName + " not found)";
                    return new ProbeView(ProbeState.Unavailable);
                }

                FieldInfo field = AccessTools.Field(handler, HttpClientMember);
                if (field != null)
                {
                    client = field.GetValue(null);
                }
                else
                {
                    PropertyInfo property = AccessTools.Property(handler, HttpClientMember);
                    client = property?.GetValue(null);
                }
                if (client == null)
                {
                    detail = Route() + " -> no SPT HTTP client (" + _handlerTypeName + "." + HttpClientMember + " is not available)";
                    return new ProbeView(ProbeState.Unavailable);
                }

                Type clientType = client.GetType();
                create = AccessTools.Method(clientType, CreateRequestMethod, new[] { typeof(HttpMethod), typeof(string) });
                http = AccessTools.Property(clientType, HttpClientMember)?.GetValue(client) as HttpClient;
                if (create == null || http == null)
                {
                    detail = Route() + " -> no SPT HTTP client (" + clientType.Name + " lacks " + CreateRequestMethod + " or " + HttpClientMember + ")";
                    return new ProbeView(ProbeState.Unavailable);
                }
            }
            catch (Exception e)
            {
                // RequestHandler's static constructor throws without a backend address (TypeInitializationException): this client cannot ask.
                Exception inner = Unwrap(e);
                detail = Route() + " -> no SPT HTTP client (" + inner.GetType().Name + ": " + inner.Message + ")";
                return new ProbeView(ProbeState.Unavailable);
            }

            // ---- the request ----------------------------------------------------------------------------------------------
            var clock = Stopwatch.StartNew();
            try
            {
                using (var request = (HttpRequestMessage)create.Invoke(client, new object[] { HttpMethod.Get, ArenaStatusContract.StatusRoute }))
                using (var timeout = new CancellationTokenSource(_timeout))
                using (HttpResponseMessage response = http.SendAsync(request, timeout.Token).GetAwaiter().GetResult())
                {
                    int status = (int)response.StatusCode;
                    if (status == 404)
                    {
                        detail = Route() + " -> 404";
                        return new ProbeView(ProbeState.NotFound);
                    }
                    if (!response.IsSuccessStatusCode)
                    {
                        detail = Route() + " -> HTTP " + status;
                        return new ProbeView(ProbeState.Failed);
                    }

                    byte[] body = response.Content.ReadAsByteArrayAsync().GetAwaiter().GetResult();
                    if (!ServerStatusParser.TryParseBody(body, out ServerStatusView view, out string error))
                    {
                        detail = Route() + " -> 200 but unreadable (" + error + ")";
                        return new ProbeView(ProbeState.Unreadable);
                    }
                    detail = Route() + " -> 200 in " + clock.ElapsedMilliseconds + " ms";
                    return new ProbeView(ProbeState.Ok, view);
                }
            }
            catch (OperationCanceledException)
            {
                detail = Route() + " -> timeout";
                return new ProbeView(ProbeState.Failed);
            }
            catch (Exception e)
            {
                Exception inner = Unwrap(e);
                if (inner is OperationCanceledException)
                {
                    detail = Route() + " -> timeout";
                    return new ProbeView(ProbeState.Failed);
                }
                detail = Route() + " -> " + inner.GetType().Name + ": " + inner.Message;
                return new ProbeView(ProbeState.Failed);
            }
        }
    }
}
