using System;
using System.Collections.Generic;
using UnityEngine;
using UnityEngine.Audio;

namespace ArenaMapPort.Client
{
    /// <summary>
    /// The imported ambient players, room tones and rain share two native parent buses.
    /// LocalGame resets these to -80 dB. Keep their neutral endpoint until the last
    /// owned player releases it, irrespective of teardown order. Other writers win.
    /// </summary>
    internal sealed class AmbientBusLease : IDisposable
    {
        sealed class Entry
        {
            internal AudioMixer Mixer;
            internal string Parameter;
            internal float Previous;
            internal int Users;
            internal bool Changed, Yielded, DeviceReset;
        }

        static readonly List<Entry> Entries = new List<Entry>();
        static bool _installed;
        Entry _entry;

        internal static AmbientBusLease Acquire(AudioMixer mixer, string parameter)
        {
            if (mixer == null || (parameter != "AmbientInVolume" && parameter != "AmbientOutVolume"))
                throw new ArgumentException("Only the native ambient parent buses may be leased");
            if (!_installed)
            {
                _installed = true;
                AudioSettings.OnAudioConfigurationChanged += _ => {
                    foreach (var item in Entries) item.DeviceReset = true;
                };
            }
            var entry = Entries.Find(e => e.Mixer == mixer && e.Parameter == parameter);
            if (entry == null)
            {
                if (!mixer.GetFloat(parameter, out float current) || float.IsNaN(current) || float.IsInfinity(current))
                    throw new InvalidOperationException("Native ambient parent unavailable: " + parameter);
                entry = new Entry { Mixer = mixer, Parameter = parameter, Previous = current };
                // Only replace the known native reset value. A non-neutral live mix is not ours.
                if (current <= -79.99f)
                {
                    if (!mixer.SetFloat(parameter, 0f))
                        throw new InvalidOperationException("Cannot open native ambient parent: " + parameter);
                    entry.Changed = true;
                }
                else if (!Mathf.Approximately(current, 0f)) entry.Yielded = true;
                Entries.Add(entry);
            }
            entry.Users++;
            return new AmbientBusLease { _entry = entry };
        }

        internal bool IsNeutral
        {
            get
            {
                var entry = _entry;
                if (entry == null || entry.Yielded || entry.Mixer == null) return false;
                if (!entry.Mixer.GetFloat(entry.Parameter, out float current)) { Yield(entry); return false; }
                if (entry.DeviceReset)
                {
                    entry.DeviceReset = false;
                    if (current <= -79.99f)
                    {
                        // Device reset is explicit evidence for re-opening the same native bus.
                        if (!entry.Mixer.SetFloat(entry.Parameter, 0f)) { Yield(entry); return false; }
                        entry.Changed = true;
                        current = 0f;
                    }
                }
                if (Mathf.Approximately(current, 0f)) return true;
                Yield(entry);
                return false;
            }
        }

        static void Yield(Entry entry)
        {
            if (entry.Yielded) return;
            entry.Yielded = true;
            Plugin.Log.LogDebug($"[AudioBus] {entry.Parameter} changed externally; preserving its mix");
        }

        public void Dispose()
        {
            var entry = _entry;
            _entry = null;
            if (entry == null || --entry.Users != 0) return;
            Entries.Remove(entry);
            if (!entry.Changed || entry.Yielded || entry.Mixer == null) return;
            if (entry.Mixer.GetFloat(entry.Parameter, out float current) && Mathf.Approximately(current, 0f))
            {
                if (!entry.Mixer.SetFloat(entry.Parameter, entry.Previous))
                    Plugin.Log.LogWarning($"[AudioBus] Could not restore {entry.Parameter}");
            }
        }
    }
}
