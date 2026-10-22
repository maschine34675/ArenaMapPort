using ArenaMapPort.Shared;
using SPTarkov.DI.Annotations;
using SPTarkov.Server.Core.DI;
using SPTarkov.Server.Core.Models.Eft.Common;
using SPTarkov.Server.Core.Utils;

namespace ArenaMapPort.Server;

/// <summary>
/// The server's one route of its own: <c>GET /arenamapport/status</c>, a read-only answer to "why are the Arena maps not offered?".
///
/// Vanilla SPT has no way to tell a client why a mod registered no location. The client of this mod notices that the maps are
/// missing from its location list but cannot see the server's reason, so it asks (see <see cref="ArenaStatusContract"/>, and
/// <see cref="ArenaServerStatus"/> for what is recorded and what is deliberately left out of the answer).
///
/// This is a static router in its own DI class, not part of <see cref="ArenaMapPortMod"/>, so it answers even when
/// <c>OnLoadAsync</c> returned at the Arena gate and registered nothing, which is exactly the state worth reporting. It changes
/// nothing on the server: the route reads the holder and sends one JSON line. The pattern (a static router at
/// <see cref="OnLoadOrder.Routers"/> + 1, the route unauthenticated) is the one the ArenaMode server mod already uses.
/// Host evidence: docs/progress/map-notices.md.
/// </summary>
[Injectable(TypePriority = OnLoadOrder.Routers + 1)]
public sealed class ArenaMapPortStatusRouter(JsonUtil jsonUtil) : StaticRouter(jsonUtil, GetRoutes())
{
    private static List<RouteAction> GetRoutes() =>
    [
        new RouteAction<EmptyRequestData>(
            ArenaStatusContract.StatusRoute,
            (url, info, sessionId, output, cancellationToken) => new ValueTask<string>(ArenaServerStatus.ToJson())),
    ];
}
