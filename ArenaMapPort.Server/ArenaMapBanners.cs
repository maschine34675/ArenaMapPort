// PLAN-TWO-MAPS item 10 (docs/progress/two-maps-item-10-banners.md) — location-select banner
// routing pattern ported (MIT) from Manimal.Icebreaker.Server.IcebreakerMod's own banner-wiring
// block (ManimalIcebreaker-main, icebreaker-server/IcebreakerMod.cs:367-397) — see
// THIRD-PARTY-NOTICES.md.
using System;
using System.Collections.Generic;
using System.IO;
using ArenaMapPort.Shared;
using SPTarkov.Server.Core.Models.Common;
using SPTarkov.Server.Core.Models.Eft.Common;
using SPTarkov.Server.Core.Models.Spt.Tables;

namespace ArenaMapPort.Server;

/// <summary>
/// The location-select screen's one-per-map picture (client: <c>LocationInfoPanel.Set()</c> reads
/// only <c>location.Banners[0]</c>, EFT.UI/LocationInfoPanel.cs:167; <c>ImageUrl =&gt; "/files/" +
/// pic.path</c>, JsonType/LocationSettings.cs:153), built from Arena's own
/// Matchmaker_Banner_Equator / Matchmaker_Banner_Bay5 art (extracted read-only by
/// tools/extract/extract_banners.py, never committed).
///
/// Equator and Bay5 only, per the two-map release decision — deliberately NOT a field on
/// <see cref="ArenaMapDescriptor"/> (Shared/Client/UnityProject are out of scope for this pass;
/// keeping it here also means the twelve deferred maps need no descriptor change at all to stay
/// bannerless). <c>db/&lt;key&gt;/base.json</c> keeps shipping <c>Banners: []</c> byte-for-byte
/// unchanged for every map, including Equator/Bay5 — <see cref="Resolve"/> builds the runtime
/// <see cref="Banner"/> entry in memory instead, once per server start, and ONLY when the backing
/// image file already exists on disk under the mod's own deployed folder. A <c>Banners</c> entry
/// whose <c>pic.path</c> nothing serves is AGENTS.md's documented landmine — an "[UNHANDLED]"
/// server log line and an empty client error dialog at the location-select screen — so a missing
/// file must leave <c>Banners</c> exactly as empty as it is today, never a dangling entry: that
/// invariant is why this returns <c>null</c> instead of ever constructing a <see cref="Banner"/>
/// for a file that is not there.
/// </summary>
public static class ArenaMapBanners
{
    /// <summary>
    /// One descriptor per bannered map. <see cref="Id"/> ("arena_&lt;key&gt;_banner",
    /// docs/PLAN-TWO-MAPS.md §10) is a free string (SPTarkov.Server.Core.Models.Eft.Common.Banner.Id
    /// is <c>string?</c>, NOT a <see cref="MongoId"/> — do not confuse it with
    /// <see cref="ArenaMapDescriptor.IdField"/>) and doubles as half of the locale key pair the
    /// client's carousel reads ("&lt;id&gt; Name"/"&lt;id&gt; Description",
    /// MatchmakerBannersPanel.cs:164). <see cref="FileStem"/> is the basename (without extension)
    /// both <see cref="Resolve"/> and <c>tools/extract/extract_banners.py</c> key off of — the two
    /// must never drift from each other.
    /// </summary>
    public sealed record Descriptor(string Id, string FileStem);

    private static readonly IReadOnlyDictionary<string, Descriptor> ByMapKey = new Dictionary<string, Descriptor>
    {
        [ArenaMapRegistry.Equator.Key] = new Descriptor("arena_equator_banner", "arena_equator"),
        [ArenaMapRegistry.Bay5.Key] = new Descriptor("arena_bay5_banner", "arena_bay5"),
    };

    /// <summary>
    /// Candidate file extensions, tried in this order. extract_banners.py defaults to jpg
    /// (quality=95) but also supports <c>--ext png</c>; either decodes fine client-side (research
    /// pass, 2026-09-25: SPT's HttpServerHelper mime map and Unity's UnityWebRequestTexture both
    /// handle jpg and png, and vanilla itself ships a real mix of both extensions).
    /// </summary>
    private static readonly string[] Extensions = { "jpg", "jpeg", "png" };

    /// <summary>The banner descriptor for <paramref name="map"/>, or null for any of the twelve
    /// maps this pass does not touch.</summary>
    public static Descriptor? For(ArenaMapDescriptor map) => ByMapKey.GetValueOrDefault(map.Key);

    /// <summary>What <see cref="Resolve"/> returns on a hit: the <see cref="Banner"/> entry to
    /// assign to <c>LocationBase.Banners</c>, the ImageRouter key to register it under (already
    /// prefixed "/files/", matching <c>ImageRouter.AddRoute</c>'s own expectation — it stores
    /// exactly the key it is given, see ImageRouter.cs:16-19), and the physical file path to serve
    /// for that key.</summary>
    public readonly record struct Resolved(Banner Banner, string RouteKey, string FilePath);

    /// <summary>
    /// Looks for <c>&lt;bannersDir&gt;/&lt;FileStem&gt;.{jpg,jpeg,png}</c> on disk and, on the first
    /// hit, returns the <see cref="Banner"/> entry to assign plus the ImageRouter key/physical path
    /// to register for it. Returns null — after calling <paramref name="warn"/> exactly once —
    /// when <paramref name="map"/> has no banner descriptor at all, or none of the candidate files
    /// exist; the caller must not create a <c>Banners</c> entry in either case (see class doc
    /// comment). Pure aside from the <see cref="File.Exists"/> probe, so a test can point
    /// <paramref name="bannersDir"/> at a temp directory without touching the real deploy tree.
    /// </summary>
    public static Resolved? Resolve(ArenaMapDescriptor map, string bannersDir, Action<string> warn)
    {
        var descriptor = For(map);
        if (descriptor is null)
        {
            return null;
        }

        foreach (var ext in Extensions)
        {
            var fileName = $"{descriptor.FileStem}.{ext}";
            var fullPath = Path.Combine(bannersDir, fileName);
            if (File.Exists(fullPath))
            {
                var picPath = $"banners/{fileName}";
                var banner = new Banner
                {
                    Id = descriptor.Id,
                    Picture = new Pic { File = fileName, Path = picPath, Rcid = "", Type = "banners" },
                };
                // The route KEY registered with ImageRouter must be extension-FREE: AddRoute
                // itself never strips one (ImageRouter.cs:16-19), but CanHandle/HandleAsync strip
                // the extension off the INCOMING request path before comparing (ImageRouter.cs:
                // 23,33 — fileUtil.StripExtension(context.Request.Path, keepPath: true)) — so a
                // route registered WITH the extension (as pic.path itself, and the client's
                // resulting "/files/"+pic.path request, both carry) would never match and this
                // banner would silently 404 through to the [UNHANDLED] fallback. Icebreaker's own
                // AddRoute call makes the same choice (SysPath.GetFileNameWithoutExtension(file),
                // IcebreakerMod.cs:390) — mirrored here as descriptor.FileStem, which is already
                // extension-free by construction.
                return new Resolved(banner, $"/files/banners/{descriptor.FileStem}", fullPath);
            }
        }

        warn($"{map.Key}: no banner image under {bannersDir} ({descriptor.FileStem}.{{jpg|jpeg|png}}) — " +
             "Banners left empty, location-select shows the client's default picture " +
             "(run tools/extract/extract_banners.py to populate it)");
        return null;
    }

    /// <summary>
    /// Stamps the two locale keys the client's banner carousel reads
    /// (<c>MatchmakerBannersPanel.TryCreateBanner</c>, MatchmakerBannersPanel.cs:164: builds
    /// <c>banner.id + " Name"</c> / <c>banner.id + " Description"</c>) — English text only for this
    /// pass, written into every language slot the same way <see cref="ArenaMapRegistration.ApplyLocale"/>
    /// already does for the map's own name/description. A no-op for a map with no banner
    /// descriptor, or for a null <paramref name="locale"/> (mirrors <c>ApplyLocale</c>'s own null
    /// handling so it can be passed to <c>LazyLoad{T}.AddTransformer</c> the same way).
    /// </summary>
    public static GlobalLocaleDictionary? ApplyLocale(GlobalLocaleDictionary? locale, ArenaMapDescriptor map)
    {
        if (locale is null)
        {
            return locale;
        }

        var descriptor = For(map);
        if (descriptor is null)
        {
            return locale;
        }

        if (!locale.ContainsKey(descriptor.Id + " Name"))
        {
            locale[descriptor.Id + " Name"] = map.LocaleName;
        }
        if (!locale.ContainsKey(descriptor.Id + " Description"))
        {
            locale[descriptor.Id + " Description"] = map.LocaleDescription;
        }
        return locale;
    }
}
