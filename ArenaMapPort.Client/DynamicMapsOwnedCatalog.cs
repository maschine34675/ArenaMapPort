#nullable disable
using System;
using System.Collections;
using System.Collections.Generic;
using System.IO;
using System.Reflection;
using Newtonsoft.Json;

namespace ArenaMapPort.Client
{
    /// <summary>
    /// DynamicMaps 1.3 prunes its catalogue on every scan and validates image paths before returning a definition.
    /// Merge only our exact packages after that scan, preserving all native/addon entries and the active filter.
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
        readonly MethodInfo _preferences, _filtered, _refresh;

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
            var sequence = typeof(IEnumerable<>).MakeGenericType(definition);
            _preferences = Method(dropdown, "RegisterMapPreferences", typeof(void), Type.EmptyTypes);
            _filtered = Method(dropdown, "FilteredMapDefs", sequence, Type.EmptyTypes);
            _refresh = Method(dropdown, "ChangeAvailableMapDefs", typeof(void), new[] { sequence });
        }

        internal void Merge(object dropdown, string request, Action<string, string> warn)
        {
            if (request != "Maps") return;
            var definitions = (IDictionary)_definitions.GetValue(dropdown);
            var times = (IDictionary)_writeTimes.GetValue(dropdown);
            foreach (var map in DynamicMapAssets.Maps)
            {
                var path = _assets.DefinitionFor(map);
                // Own-key removal also handles deleted/invalid packs on refresh. Never clear the native catalogue.
                definitions.Remove(path);
                times.Remove(path);
                try
                {
                    if (!File.Exists(path) || !File.Exists(_assets.ImageFor(map)))
                        throw new InvalidDataException("Missing owned map definition or SVG.");
                    var value = Read(path, map);
                    var time = File.GetLastWriteTimeUtc(path);
                    definitions[path] = value;
                    times[path] = time;
                }
                catch (Exception ex) { warn(map, "Rejected owned " + map + " overview: " + ex.GetBaseException().Message); }
            }
            // Both calls are native: preserve addon preferences, sorting and the current location filter.
            _preferences.Invoke(dropdown, null);
            _refresh.Invoke(dropdown, new[] { _filtered.Invoke(dropdown, null) });
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
