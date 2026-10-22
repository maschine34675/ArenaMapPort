#nullable disable
using ArenaMapPort.Client;
using ArenaMapPort.Crypto;
using ArenaMapPort.Shared;

namespace ArenaMapPort.Server.Tests;

/// <summary>Values and helpers shared by the map-notice tests (texts, inputs, selector, scheduler, failures).</summary>
internal static class NoticeTestData
{
    internal const string Version = "0.3.0";
    internal const string ArenaPath = @"D:\Battlestate Games\Escape from Tarkov Arena";

    internal static readonly string[] RegistryKeys = { "arena_equator", "arena_bay5", "arena_airpit", "arena_bowl" };

    internal static IReadOnlyList<MapInfo> Maps => MapInfo.FromRegistry();

    internal static GateSnapshot Verified(string path = ArenaPath, string version = "0.6.1.0.48206") =>
        new(GateState.Verified, ArenaInstallCause.None, "registry", path, version, 0, null, null);

    internal static GateSnapshot NotVerified(ArenaInstallCause cause, string source = "registry", string path = null, int mismatches = 0,
        string reason = null) =>
        new(GateState.NotVerified, cause, source, path, null, mismatches, reason, null);

    internal static GateSnapshot State(GateState state, string failure = null) =>
        new(state, ArenaInstallCause.None, null, null, null, 0, null, failure);

    /// <summary>All registry locations, enabled.</summary>
    internal static List<OfferedLocation> AllOffered() => RegistryKeys.Select(key => new OfferedLocation(key, true)).ToList();

    internal static List<OfferedLocation> Offered(params string[] keys) => keys.Select(key => new OfferedLocation(key, true)).ToList();

    /// <summary>An input with the given gate; every registry map offered unless <paramref name="offered"/> says otherwise (null list = unknown).</summary>
    internal static MapAvailabilityInput Input(GateSnapshot gate, IEnumerable<OfferedLocation> offered, ProbeView probe = null,
        FileScan files = null, IReadOnlyList<string> hooks = null, bool enabled = true) =>
        MapNoticeInputs.Build(gate, Maps, offered, files, probe, hooks, Version, enabled);

    internal static MapAvailabilityInput Healthy() => Input(Verified(), AllOffered(), null, FileScan.Clean(4));

    internal static ServerStatusView Status(string gateState, string cause, string source = "none", string overrideFile = "absent",
        string serverVersion = "0.3.0", string manifestVersion = null, int mismatches = 0, params (string Key, string State, string Cause)[] maps) =>
        new(1, serverVersion, gateState, cause, source, manifestVersion, mismatches, overrideFile,
            maps.Select(map => new ServerMapStatus(map.Key, map.State, map.Cause)).ToList());

    internal static (string, string, string)[] MapsAll(string state, string cause) =>
        RegistryKeys.Select(key => (key, state, cause)).ToArray();

    internal static ProbeView Ok(ServerStatusView status) => new(ProbeState.Ok, status);

    /// <summary>A plan with every placeholder value a text may ask for, for the lint tests.</summary>
    internal static MapNoticePlan FullPlan(NoticeId id, NoticeLead lead = NoticeLead.Menu, string serverVersion = "0.3.0")
    {
        var plan = new MapNoticePlan(id)
        {
            Lead = lead,
            ServerVersion = serverVersion,
            Reminder = ReminderGroup.ArenaNotFound,
        };
        plan.Set("Map", "Bay 5").Set("maps", "Equator, Bay 5").Set("path", ArenaPath).Set("n", "12").Set("file", "arena_bay5_scenes.bundle")
            .Set("detail", "InvalidOperationException: boom").Set("inst", "0.6.1.0.48206").Set("built", "0.6.0.0.47752").Set("ver", Version)
            .Set("json", MapNoticeText.BuildJsonExample(ArenaPath));
        switch (id)
        {
            case NoticeId.ClientOverrideBad:
            case NoticeId.ClientFolderBad:
                plan.Why = NoticeWhy.FolderMissing;
                break;
            case NoticeId.ServerNoArena:
                plan.Why = NoticeWhy.ServerNotRegistered;
                plan.Action = NoticeAction.CreateOverride;
                break;
            case NoticeId.ServerAllFailed:
            case NoticeId.ServerSomeFailed:
                plan.Why = NoticeWhy.MapDataBad;
                break;
            case NoticeId.NavMeshMissing:
                plan.Why = NoticeWhy.NavFileMissing;
                break;
        }
        return plan;
    }

    /// <summary>A key failure record with the fields a resolver would set.</summary>
    internal static MapLoadFailure KeyFailure(string mapKey, ArenaMapPort.Crypto.KeyFailure cause, string installed = "0.6.1.0.48206",
        string built = "0.6.0.0.47752") =>
        new()
        {
            MapKey = mapKey, Kind = MapLoadFailureKind.Key, KeyCause = cause, InstalledVersion = installed, BuiltFor = built,
            File = mapKey + ".json", Detail = "detail",
        };
}

/// <summary>An in-memory <see cref="IFileProbe"/>: the paths in <see cref="Files"/> exist, those in <see cref="AmpbFiles"/> start with the AMPB magic.</summary>
internal sealed class FakeFileProbe : IFileProbe
{
    internal readonly HashSet<string> Files = new(StringComparer.OrdinalIgnoreCase);
    internal readonly HashSet<string> AmpbFiles = new(StringComparer.OrdinalIgnoreCase);
    internal readonly List<string> Asked = new();

    public bool Exists(string path)
    {
        Asked.Add(path);
        return Files.Contains(path);
    }

    public bool IsAmpb(string path) => AmpbFiles.Contains(path);

    /// <summary>Everything the scan looks for exists, for every map of <paramref name="maps"/>; the preset bundles are AMPB when <paramref name="encrypted"/>.</summary>
    internal static FakeFileProbe Complete(string pluginDir, IEnumerable<MapInfo> maps, bool encrypted)
    {
        var probe = new FakeFileProbe();
        foreach (MapInfo map in maps)
        {
            string preset = Path.Combine(pluginDir, "bundles", map.PresetFile);
            probe.Files.Add(preset);
            probe.Files.Add(Path.Combine(pluginDir, "bundles", map.ScenesFile));
            probe.Files.Add(Path.Combine(pluginDir, "bundles", map.NavMeshFile));
            probe.Files.Add(Path.Combine(pluginDir, map.SidecarFile));
            if (encrypted)
            {
                probe.AmpbFiles.Add(preset);
                probe.Files.Add(Path.Combine(pluginDir, "keys", map.Key + ".json"));
            }
        }
        return probe;
    }
}

/// <summary>
/// A scriptable <see cref="INoticeEnvironment"/>: every predicate is a settable property, every action is recorded, and the clock only moves when a test
/// moves it.
/// </summary>
internal sealed class FakeNoticeEnvironment : INoticeEnvironment
{
    internal sealed record ShownModal(string Caption, string Text, bool Scrolled, ModalTicket Ticket);

    public double Now { get; set; } = 100;
    public bool NoticesEnabled { get; set; } = true;
    public string Language { get; set; } = "en";
    public string ClientVersion { get; set; } = NoticeTestData.Version;
    public IReadOnlyList<MapInfo> Maps { get; set; } = NoticeTestData.Maps;
    public bool MenuIsCurrent { get; set; } = true;
    public bool SelectionIsCurrent { get; set; }
    public bool ModalPossible { get; set; } = true;
    /// <summary>What <see cref="INoticeEnvironment.ModalBlocker"/> answers (the sub-check name); empty = none.</summary>
    public string ModalBlocker { get; set; } = "";
    /// <summary>What <see cref="INoticeEnvironment.MessageWindowOpen"/> answers; true unless a test says the window vanished.</summary>
    public bool MessageWindowOpen { get; set; } = true;
    public bool ToastPossible { get; set; } = true;
    public bool InStartedRaid { get; set; }
    public string CurrentRaidMapKey { get; set; }
    public bool MatchingCanceledByPlayer { get; set; }
    public IReadOnlyList<string> SkippedHooks { get; set; } = new string[0];

    public GateSnapshot Gate { get; set; } = NoticeTestData.Verified();
    public bool OfferedAvailable { get; set; } = true;
    public List<OfferedLocation> Locations { get; set; } = NoticeTestData.AllOffered();
    public FileScan Scan { get; set; } = FileScan.Clean(4);
    public int ScanCalls { get; private set; }
    public IReadOnlyList<MapInfo> LastScanScope { get; private set; }

    public ProbeView Probe { get; set; } = ProbeView.NotAsked;
    public int ProbeStarts { get; private set; }
    /// <summary>Runs inside <see cref="StartProbe"/> (set <see cref="Probe"/> to Pending, or to a result at once).</summary>
    public Action WhenProbeStarts { get; set; }

    public readonly List<ShownModal> Modals = new();
    public readonly List<string> Toasts = new();
    public readonly List<string> Debugs = new();
    public readonly List<string> Infos = new();
    public readonly List<string> Warnings = new();

    /// <summary>Decides what the next <see cref="ShowModal"/> returns; null = a plain pending ticket.</summary>
    public Func<ModalTicket, ModalTicket> ModalOverride { get; set; }
    public Exception ShowModalThrows { get; set; }
    public Exception ShowToastThrows { get; set; }
    public bool ToastResult { get; set; } = true;
    public bool MenuPredicateThrows { get; set; }
    public bool ModalPredicateThrows { get; set; }
    /// <summary>Makes the clock (<see cref="INoticeEnvironment.Now"/>, the one unguarded read of a tick) throw.</summary>
    public bool NowThrows { get; set; }

    // calls that must not happen while idle
    public int NowReads { get; private set; }
    public int MenuReads { get; private set; }

    double INoticeEnvironment.Now
    {
        get
        {
            NowReads++;
            if (NowThrows)
                throw new InvalidOperationException("clock");
            return Now;
        }
    }

    bool INoticeEnvironment.MenuIsCurrent
    {
        get
        {
            MenuReads++;
            if (MenuPredicateThrows)
                throw new InvalidOperationException("menu predicate");
            return MenuIsCurrent;
        }
    }

    bool INoticeEnvironment.ModalPossible
    {
        get
        {
            if (ModalPredicateThrows)
                throw new InvalidOperationException("modal predicate");
            return ModalPossible;
        }
    }

    public GateSnapshot ReadGate() => Gate;

    public bool TryReadOffered(out IReadOnlyList<OfferedLocation> locations)
    {
        locations = OfferedAvailable ? Locations : null;
        return OfferedAvailable;
    }

    public FileScan ScanFiles(IReadOnlyList<MapInfo> scope)
    {
        ScanCalls++;
        LastScanScope = scope;
        return Scan;
    }

    public void StartProbe()
    {
        ProbeStarts++;
        WhenProbeStarts?.Invoke();
    }

    public ModalTicket ShowModal(string caption, string text, bool scrolled)
    {
        if (ShowModalThrows != null)
            throw ShowModalThrows;
        var ticket = new ModalTicket(Now);
        ModalTicket result = ModalOverride != null ? ModalOverride(ticket) : ticket;
        Modals.Add(new ShownModal(caption, text, scrolled, result));
        return result;
    }

    public bool ShowToast(string text)
    {
        if (ShowToastThrows != null)
            throw ShowToastThrows;
        Toasts.Add(text);
        return ToastResult;
    }

    public void Debug(string message) => Debugs.Add(message);
    public void Info(string message) => Infos.Add(message);
    public void Warning(string message) => Warnings.Add(message);

    /// <summary>The ticket of the newest modal.</summary>
    internal ModalTicket LastTicket => Modals[^1].Ticket;
}
