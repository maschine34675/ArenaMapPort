using System;
using System.Collections;
using System.IO;

namespace ArenaMapPort.Client
{
    /// <summary>Exact two-file package paths; serialized image tokens remain compatible with older packs.</summary>
    internal sealed class DynamicMapAssets
    {
        internal static readonly string[] Maps = { "Equator", "Bay5", "AirPit" };
        internal readonly string Root;
        bool _loading;

        internal DynamicMapAssets(string pluginDirectory) => Root = Path.GetFullPath(Path.Combine(pluginDirectory, "dynamicmaps"));
        internal string DirectoryFor(string map) => Path.Combine(Root, "ArenaMapPort_" + map);
        internal string DefinitionFor(string map) => Path.Combine(DirectoryFor(map), "ArenaMapPort_" + map + ".jsonc");
        internal string ImageFor(string map) => Path.Combine(DirectoryFor(map), "Layers", "Ground.svg");

        internal bool OwnsDefinition(string definition)
        {
            foreach (var map in Maps)
                if (string.Equals(definition, DefinitionFor(map), StringComparison.OrdinalIgnoreCase)) return true;
            return false;
        }

        internal bool NormalizeGroundLayer(string definition, IDictionary layers, Func<object, string> read, Action<object, string> write)
        {
            if (!OwnsDefinition(definition) || layers == null || layers.Count != 1) return false;
            foreach (var layer in layers.Values)
            {
                if (layer == null) return false;
                var image = ResolveImage(definition, read(layer));
                if (image == null || !File.Exists(image)) return false;
                write(layer, image);
            }
            return true;
        }

        internal string ResolveImage(string definition, string image)
        {
            if (string.IsNullOrEmpty(definition) || !Path.IsPathRooted(definition)) return null;
            var fullPath = Path.GetFullPath(definition);
            foreach (var map in Maps)
                if (string.Equals(fullPath, DefinitionFor(map), StringComparison.OrdinalIgnoreCase)
                    && image == "Maps/ArenaMapPort_" + map + "/Layers/Ground.svg")
                    return ImageFor(map);
            return null;
        }

        internal void LoadAdditional(string request, Action<string> load, Action<string> reportMissing)
        {
            if (_loading || request != "Maps") return;
            _loading = true;
            try
            {
                foreach (var map in Maps)
                {
                    if (File.Exists(DefinitionFor(map)) && File.Exists(ImageFor(map))) load(DirectoryFor(map));
                    else reportMissing(map);
                }
            }
            finally { _loading = false; }
        }
    }
}
