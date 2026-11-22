using ArenaMapPort.Client;
using Newtonsoft.Json;
using System.Runtime.CompilerServices;
using Definition = DynamicMaps.Data.MapDef;
using Layer = DynamicMaps.Data.MapLayerDef;

// Minimal independent API/lifecycle fixtures; production Install resolves these native type names.
namespace DynamicMaps.Data
{
    public sealed class MapDef
    {
        public string DisplayName { get; set; } = "";
        public object Bounds { get; set; } = new();
        public List<string> MapInternalNames { get; set; } = [];
        public Dictionary<string, Layer> Layers { get; set; } = new();
        public List<string> StaticMarkers { get; set; } = [];
        [JsonIgnore] public string SelectionId { get; set; } = "";
        [JsonIgnore] public bool IsAddon { get; set; }
        // If the old recursive/native-loader path is accidentally restored, the fixture fails.
        public static Definition LoadFromPath(string path, string? addon = null) => throw new InvalidOperationException("Native validation ran before owned path resolution");
    }

    public sealed class MapLayerDef
    {
        public string ImagePath { get; set; } = "";
        public object ImageBounds { get; set; } = new();
        public List<object> GameBounds { get; set; } = [];
    }

}

namespace DynamicMaps.UI.Controls
{
    public sealed class MapSelectDropdown
    {
        readonly Dictionary<string, Definition> _mapDefs = new();
        readonly Dictionary<string, DateTime> _writeTimes = new();
        public Dictionary<string, Definition> Definitions => _mapDefs;
        public Dictionary<string, DateTime> Times => _writeTimes;
        public string? Filter { get; set; }
        public List<Definition> Available { get; private set; } = [];
        public int RegisteredCount { get; private set; }
        public int Refreshes { get; private set; }
        public int PreferencePasses { get; private set; }
        public Dictionary<string, string> Preferred { get; } = new();
        public Definition? Current { get; private set; }
        public int MapLoads { get; private set; }
        public float Zoom { get; set; } = 1;
        public int Floor { get; set; }
        public bool ThrowInPreferences { get; set; }
        public Exception? NativeFailure { get; set; }
        [MethodImpl(MethodImplOptions.NoInlining)]
        public void LoadMapDefsFromPath(string request)
        {
            PruneExternal();
            RegisterMapPreferences();
            ChangeAvailableMapDefs(FilteredMapDefs());
        }
        [MethodImpl(MethodImplOptions.NoInlining)]
        void RegisterMapPreferences()
        {
            PreferencePasses++;
            if (NativeFailure != null) throw NativeFailure;
            if (ThrowInPreferences) throw new IOException("Native preference failure");
            RegisteredCount = _mapDefs.Count;
            // 1.3 Settings.RegisterMapPreference preserves a selected choice only while it is in the
            // newly bound choices. A late second registration cannot recover a choice reset to Automatic.
            foreach (var location in _mapDefs.Values.SelectMany(v => v.MapInternalNames).Distinct())
            {
                var choices = _mapDefs.Values.Where(v => v.MapInternalNames.Contains(location)).Select(v => v.DisplayName).ToArray();
                if (!Preferred.TryGetValue(location, out var selected) || !choices.Contains(selected)) Preferred[location] = "Automatic";
            }
        }
        IEnumerable<Definition> FilteredMapDefs() => _mapDefs.Values.Where(v => Filter == null || v.MapInternalNames.Contains(Filter));
        void ChangeAvailableMapDefs(IEnumerable<Definition> values) { Available = values.ToList(); Refreshes++; }
        internal void RefreshNative() => ChangeAvailableMapDefs(FilteredMapDefs());
        internal void Scan(DynamicMapsOwnedCatalog catalog, string request, Action<string, string> warn)
        {
            var scope = catalog.BeginScan(this, request);
            try
            {
                PruneExternal();
                catalog.PreparePreferences(this, warn);
                RegisterMapPreferences();
                ChangeAvailableMapDefs(FilteredMapDefs());
                if (Filter == null) return;
                Preferred.TryGetValue(Filter, out var preferred);
                var selection = Available.FirstOrDefault(v => v.DisplayName == preferred) ?? Available.FirstOrDefault();
                // 1.3 ModdedMapScreen.ChangeMap retains the view only for the same MapDef reference.
                if (selection != null && !ReferenceEquals(Current, selection))
                {
                    Current = selection;
                    MapLoads++;
                    Zoom = 1;
                    Floor = 0;
                }
            }
            finally { catalog.EndScan(scope); }
        }
        public void PruneExternal()
        {
            foreach (var key in _mapDefs.Keys.Where(Path.IsPathRooted).ToArray()) { _mapDefs.Remove(key); _writeTimes.Remove(key); }
        }
    }

}
