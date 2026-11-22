#nullable disable
using System;
using System.Collections;
using System.Collections.Generic;
using System.IO;
using System.Reflection;
using System.Runtime.CompilerServices;
using Newtonsoft.Json;

namespace ArenaMapPort.Client
{
    /// <summary>
    /// DynamicMaps 1.3 prunes its catalogue on every scan and validates image paths before returning a definition.
    /// Merge our exact packages before the scan registers preferences, preserving native/addon choices and the active filter.
    /// The native definition type/serializer and UI refresh remain authoritative; no third-party files are written.
    /// </summary>
    internal sealed class DynamicMapsOwnedCatalog
    {
        const BindingFlags Public = BindingFlags.Public | BindingFlags.Instance | BindingFlags.DeclaredOnly;
        const BindingFlags Private = BindingFlags.NonPublic | BindingFlags.Instance | BindingFlags.DeclaredOnly;
        readonly DynamicMapAssets _assets;
        readonly Type _definition;
        readonly FieldInfo _definitions, _writeTimes;
        readonly PropertyInfo _layers, _image, _bounds, _names, _name, _selection, _addon, _imageBounds, _gameBounds;
        internal readonly MethodInfo Preferences;
        readonly Dictionary<object, Scan> _scans = new Dictionary<object, Scan>();
        readonly ConditionalWeakTable<object, Dictionary<string, CachedDefinition>> _caches = new ConditionalWeakTable<object, Dictionary<string, CachedDefinition>>();

        internal sealed class Scan
        {
            internal object Dropdown;
            internal Scan Parent;
            internal bool OwnsMaps;
        }

        sealed class CachedDefinition
        {
            internal object Value;
            internal DateTime DefinitionTime, ImageTime;
            internal long DefinitionLength, ImageLength;
            internal bool Matches(FileInfo definition, FileInfo image) => DefinitionTime == definition.LastWriteTimeUtc
                && DefinitionLength == definition.Length && ImageTime == image.LastWriteTimeUtc && ImageLength == image.Length;
        }

        internal DynamicMapsOwnedCatalog(DynamicMapAssets assets, Type dropdown, Type definition, Type layer)
        {
            _assets = assets;
            _definition = definition ?? throw new ArgumentNullException(nameof(definition));
            var read = definition.GetMethod("LoadFromPath", BindingFlags.Public | BindingFlags.Static,
                null, new[] { typeof(string), typeof(string) }, null);
            if (read?.ReturnType != definition) throw new MissingMethodException("MapDef.LoadFromPath(string,string)");
            _definitions = Field(dropdown, "_mapDefs", typeof(Dictionary<,>).MakeGenericType(typeof(string), definition));
            _writeTimes = Field(dropdown, "_writeTimes", typeof(Dictionary<string, DateTime>));
            _layers = Property(definition, "Layers", typeof(Dictionary<,>).MakeGenericType(typeof(string), layer));
            _image = Property(layer, "ImagePath", typeof(string), writable: true);
            _bounds = Property(definition, "Bounds");
            _names = Property(definition, "MapInternalNames", typeof(List<string>));
            _name = Property(definition, "DisplayName", typeof(string));
            _selection = Property(definition, "SelectionId", typeof(string), writable: true);
            _addon = Property(definition, "IsAddon", typeof(bool), writable: true);
            _imageBounds = Property(layer, "ImageBounds");
            _gameBounds = Property(layer, "GameBounds");
            Preferences = Method(dropdown, "RegisterMapPreferences", typeof(void), Type.EmptyTypes);
        }

        internal Scan BeginScan(object dropdown, string request)
        {
            _scans.TryGetValue(dropdown, out var parent);
            var scan = new Scan { Dropdown = dropdown, Parent = parent, OwnsMaps = request == "Maps" };
            _scans[dropdown] = scan;
            return scan;
        }

        internal void EndScan(Scan scan)
        {
            if (scan == null) return;
            if (!_scans.TryGetValue(scan.Dropdown, out var active) || !ReferenceEquals(active, scan)) return;
            if (scan.Parent == null) _scans.Remove(scan.Dropdown);
            else _scans[scan.Dropdown] = scan.Parent;
        }

        internal void PreparePreferences(object dropdown, Action<string, string> warn)
        {
            if (!_scans.TryGetValue(dropdown, out var scan) || !scan.OwnsMaps) return;
            var definitions = (IDictionary)_definitions.GetValue(dropdown);
            var times = (IDictionary)_writeTimes.GetValue(dropdown);
            var cache = _caches.GetOrCreateValue(dropdown);
            foreach (var map in DynamicMapAssets.Maps)
            {
                var path = _assets.DefinitionFor(map);
                // Own-key removal also handles deleted/invalid packs on refresh. Never clear the native catalogue.
                definitions.Remove(path);
                times.Remove(path);
                try
                {
                    var definition = new FileInfo(path);
                    var image = new FileInfo(_assets.ImageFor(map));
                    if (!definition.Exists || !image.Exists)
                        throw new InvalidDataException("Missing owned map definition or SVG.");
                    // Native scans prune our keys before getting here. Keep unchanged object identity in a weak,
                    // per-dropdown cache so reopening the map does not unload its view or reset zoom/floor state.
                    cache.TryGetValue(path, out var cached);
                    if (cached == null || !cached.Matches(definition, image))
                    {
                        cached = new CachedDefinition { Value = Read(path, map), DefinitionTime = definition.LastWriteTimeUtc,
                            DefinitionLength = definition.Length, ImageTime = image.LastWriteTimeUtc, ImageLength = image.Length };
                        cache[path] = cached;
                    }
                    definitions[path] = cached.Value;
                    times[path] = cached.DefinitionTime;
                }
                catch (Exception ex)
                {
                    cache.Remove(path);
                    warn(map, "Rejected owned " + map + " overview: " + ex.GetBaseException().Message);
                }
            }
            // Return to the FIRST native preference registration. Its following native dropdown refresh uses
            // the complete catalogue, so another addon for the same location cannot reset an owned selection.
        }

        object Read(string path, string map)
        {
            var value = JsonConvert.DeserializeObject(File.ReadAllText(path), _definition);
            if (value == null || _bounds.GetValue(value) == null || _names.GetValue(value) == null
                || string.IsNullOrWhiteSpace(_name.GetValue(value) as string))
                throw new InvalidDataException("Incomplete map definition.");
            var layers = _layers.GetValue(value) as IDictionary;
            if (!_assets.NormalizeGroundLayer(path, layers, layer => _image.GetValue(layer) as string,
                (layer, image) => _image.SetValue(layer, image)))
                throw new InvalidDataException("Expected one owned ground layer with its SVG present.");
            foreach (var layer in layers.Values)
                if (_imageBounds.GetValue(layer) == null || _gameBounds.GetValue(layer) == null)
                    throw new InvalidDataException("Incomplete map layer.");
            // A portable, stable native choice identity; it is not a path DynamicMaps subsequently opens.
            _selection.SetValue(value, "ArenaMapPort/" + map);
            _addon.SetValue(value, true);
            return value;
        }

        static FieldInfo Field(Type type, string name, Type expected)
        {
            var member = type?.GetField(name, Private);
            if (member?.FieldType != expected) throw new MissingFieldException(type?.FullName, name);
            return member;
        }

        static PropertyInfo Property(Type type, string name, Type expected = null, bool writable = false)
        {
            var member = type?.GetProperty(name, Public);
            if (member == null || member.GetGetMethod() == null || (expected != null && member.PropertyType != expected)
                || (writable && member.GetSetMethod() == null)) throw new MissingMemberException(type?.FullName, name);
            return member;
        }

        static MethodInfo Method(Type type, string name, Type returns, Type[] arguments)
        {
            var member = type?.GetMethod(name, Private, null, arguments, null);
            if (member?.ReturnType != returns) throw new MissingMethodException(type?.FullName, name);
            return member;
        }
    }
}
