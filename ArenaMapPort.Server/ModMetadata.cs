using SPTarkov.Server.Core.Models.Spt.Mod;

namespace ArenaMapPort.Server;

// Every SPT mod must contain exactly one IModMetadata implementation (see the
// interface's own doc comment) — the server uses it to identify the mod, gate SPT
// version compatibility, and order mod load (TypePriority first, ModGuid as
// tiebreaker). Shape matches ArenaMode.Server's own ModMetadata.cs.
public record ModMetadata : IModMetadata
{
    public string ModGuid { get; init; } = "com.maschine.arenamapport";
    public string Name { get; init; } = "ArenaMapPort";
    public string Author { get; init; } = "maschine";
    public List<string>? Contributors { get; init; }
    public SemanticVersioning.Version Version { get; init; } = new("0.1.0");
    public SemanticVersioning.Range SptVersion { get; init; } = new("~4.1.5");
    public bool HasPrepatcher { get; init; } = false;
    public List<string>? Incompatibilities { get; init; }
    public Dictionary<string, SemanticVersioning.Range>? ModDependencies { get; init; }
    public string? Url { get; init; }
    public string License { get; init; } = "MIT";
}
