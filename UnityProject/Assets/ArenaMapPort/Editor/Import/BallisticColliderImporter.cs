// Reconstruct native EFT material-hit data on its original collider owner. The
// extractor maps source presets by name; per-instance values remain authoritative.
using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Text.RegularExpressions;
using EFT.Ballistics;
using EFT.NetworkPackets;
using Newtonsoft.Json.Linq;
using UnityEngine;

namespace ArenaMapPort.Editor.Import
{
    public static class BallisticColliderImporter
    {
        static readonly string[] ValueFields =
        {
            "penetration_level", "penetration_chance", "ricochet_chance",
            "fragmentation_chance", "trajectory_deviation_chance", "trajectory_deviation"
        };

        /// <summary>
        /// Call after all ordinary physics components on owner exist. Does not
        /// activate the owner, change its colliders, or attach interactive behavior.
        /// </summary>
        public static BallisticCollider Attach(GameObject owner, JObject component)
        {
            if (owner == null || component == null)
                throw new InvalidDataException("BallisticCollider import requires an owner and component.");
            string context = "BallisticCollider on '" + owner.name + "': ";
            if (Text(component, "type", context) != "BallisticCollider" || Int(component, "schema_version", context) != 1)
                throw new InvalidDataException(context + "unsupported component schema.");
            bool enabled = Bool(component, "enabled", context);
            string materialName = Text(component, "material", context);
            MaterialType material;
            if (!Enum.TryParse(materialName, false, out material) || !Enum.IsDefined(typeof(MaterialType), material) ||
                !String.Equals(Enum.GetName(typeof(MaterialType), material), materialName, StringComparison.Ordinal))
                throw new InvalidDataException(context + "unknown canonical material '" + materialName + "'.");
            if ((int)material != Int(component, "target_material_id", context))
                throw new InvalidDataException(context + "installed target material enum differs from extraction contract.");

            int presetId = Int(component, "target_preset_id", context);
            JToken presetName = component["preset_name"];
            if (presetId == -900)
            {
                if (presetName == null || presetName.Type != JTokenType.Null)
                    throw new InvalidDataException(context + "legacy preset must carry explicit null name.");
            }
            else if (presetId < 0 || presetName == null || presetName.Type != JTokenType.String ||
                     !Regex.IsMatch((string)presetName, @"\A[A-Za-z][A-Za-z0-9_]*\z"))
            {
                throw new InvalidDataException(context + "invalid named target preset.");
            }

            var values = component["values"] as JObject;
            if (values == null)
                throw new InvalidDataException(context + "missing ballistic values.");
            var expected = new HashSet<string>(ValueFields, StringComparer.Ordinal);
            foreach (var property in values.Properties())
                if (!expected.Remove(property.Name))
                    throw new InvalidDataException(context + "unexpected ballistic value '" + property.Name + "'.");
            if (expected.Count != 0)
                throw new InvalidDataException(context + "incomplete ballistic values.");
            var numbers = new float[ValueFields.Length];
            for (int i = 0; i < numbers.Length; i++)
            {
                JToken token = values[ValueFields[i]];
                if (token.Type != JTokenType.Float && token.Type != JTokenType.Integer)
                    throw new InvalidDataException(context + "non-numeric " + ValueFields[i] + ".");
                double number = token.Value<double>();
                if (Double.IsNaN(number) || Double.IsInfinity(number) || number < 0 ||
                    number > (i == 0 ? Single.MaxValue : 1d))
                    throw new InvalidDataException(context + "out-of-domain " + ValueFields[i] + ".");
                numbers[i] = (float)number;
            }

            ValidateSource(component["source"] as JObject, materialName, presetId, context);
            if (owner.GetComponents<Collider>().Length == 0)
                throw new InvalidDataException(context + "original physics collider has not been attached.");
            if (owner.GetComponent<global::BaseBallistic>() != null)
                throw new InvalidDataException(context + "owner already carries ballistic behavior.");

            BallisticCollider ballistic = null;
            try
            {
                ballistic = owner.AddComponent<BallisticCollider>();
                if (ballistic == null)
                    throw new InvalidDataException(context + "native EFT component is not ready; retry the authoring phase on an Editor update tick.");
                ballistic.enabled = enabled;
                ballistic._presetId = presetId;
                ballistic.TypeOfMaterial = material;
                ballistic.PenetrationLevel = numbers[0];
                ballistic.PenetrationChance = numbers[1];
                ballistic.RicochetChance = numbers[2];
                ballistic.FragmentationChance = numbers[3];
                ballistic.TrajectoryDeviationChance = numbers[4];
                ballistic.TrajectoryDeviation = numbers[5];
                // Material-only scope: original stale Window IDs must not create
                // network-interactive objects. Real WindowBreaker authoring is separate.
                ballistic.HitType = EHitType.Default;
                ballistic.NetId = 0;
                // Awake may have run before assignment. It performs this exact
                // material-to-footstep association; serialized source sounds are stale.
                ballistic.Associate(material);
                return ballistic;
            }
            catch
            {
                if (ballistic != null) UnityEngine.Object.DestroyImmediate(ballistic);
                throw;
            }
        }

        static void ValidateSource(JObject source, string material, int targetPreset, string context)
        {
            if (source == null)
                throw new InvalidDataException(context + "missing original component provenance.");
            string file = Text(source, "file", context);
            if (file != Path.GetFileName(file) || String.IsNullOrWhiteSpace(file))
                throw new InvalidDataException(context + "source file must be a basename.");
            foreach (string key in new[] { "component_id", "object_id" })
            {
                long value;
                string text = Text(source, key, context);
                if (!Regex.IsMatch(text, @"\A[1-9][0-9]*\z") ||
                    !Int64.TryParse(text, NumberStyles.None, CultureInfo.InvariantCulture, out value))
                    throw new InvalidDataException(context + "invalid source " + key + ".");
            }
            if (!Regex.IsMatch(Text(source, "raw_sha256", context), @"\A[0-9a-f]{64}\z"))
                throw new InvalidDataException(context + "invalid source raw hash.");
            int sourcePreset = Int(source, "preset_id", context);
            if ((sourcePreset == -900) != (targetPreset == -900) || (sourcePreset < 0 && sourcePreset != -900))
                throw new InvalidDataException(context + "invalid source preset mapping.");
            if (Int(source, "material_id", context) < 0)
                throw new InvalidDataException(context + "invalid source material.");
            int surface = Int(source, "surface_sound_id", context);
            if (surface != 0 && !(surface == 12 && material == "WoodThick"))
                throw new InvalidDataException(context + "unreviewed original surface sound.");
            int hit = Int(source, "hit_type_id", context), net = Int(source, "net_id", context);
            if (!((hit == 0 && net == 0) || (hit == 2 && net > 0 && material == "Glass")))
                throw new InvalidDataException(context + "unreviewed original hit/network identity.");
        }

        static string Text(JObject obj, string key, string context)
        {
            JToken token = obj[key];
            if (token == null || token.Type != JTokenType.String || String.IsNullOrEmpty((string)token))
                throw new InvalidDataException(context + "missing string '" + key + "'.");
            return (string)token;
        }

        static int Int(JObject obj, string key, string context)
        {
            JToken token = obj[key];
            if (token == null || token.Type != JTokenType.Integer)
                throw new InvalidDataException(context + "missing integer '" + key + "'.");
            long value = token.Value<long>();
            if (value < Int32.MinValue || value > Int32.MaxValue)
                throw new InvalidDataException(context + "integer out of range: " + key + ".");
            return (int)value;
        }

        static bool Bool(JObject obj, string key, string context)
        {
            JToken token = obj[key];
            if (token == null || token.Type != JTokenType.Boolean)
                throw new InvalidDataException(context + "missing boolean '" + key + "'.");
            return (bool)token;
        }
    }
}
