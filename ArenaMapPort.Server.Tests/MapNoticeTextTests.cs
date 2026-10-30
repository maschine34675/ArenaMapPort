#nullable disable
using System.Text.RegularExpressions;
using ArenaMapPort.Client;
using Newtonsoft.Json.Linq;
using Xunit;

namespace ArenaMapPort.Server.Tests;

/// <summary>
/// MapNoticeText: the ids and ref codes, the English master table and its German twin (parity, lints, the exact texts of the design), composition
/// (lead, reason, action, footer, caption), the sanitising and length rules, and the language choice.
/// </summary>
public sealed class MapNoticeTextTests
{
    static readonly string[] Languages = { "en", "de" };

    static readonly Regex Token = new(@"\{[A-Za-z][A-Za-z0-9]*\}");

    /// <summary>Every composed text of every id, lead, reason and action, in both languages.</summary>
    static IEnumerable<(string Language, string Description, MapNoticeMessage Message)> AllComposed()
    {
        foreach (string language in Languages)
            foreach (NoticeId id in MapNoticeText.AllIds)
            {
                foreach (NoticeLead lead in new[] { NoticeLead.Menu, NoticeLead.Attempt })
                {
                    MapNoticePlan plan = NoticeTestData.FullPlan(id, lead);
                    switch (id)
                    {
                        case NoticeId.ServerNoArena:
                            foreach (NoticeWhy why in Enum.GetValues<NoticeWhy>().Where(w => w.ToString().StartsWith("Server") && w != NoticeWhy.ServerNotRegistered))
                                foreach (NoticeAction action in Enum.GetValues<NoticeAction>().Where(a => a != NoticeAction.None))
                                {
                                    plan = NoticeTestData.FullPlan(id, lead);
                                    plan.Why = why;
                                    plan.Action = action;
                                    yield return (language, id + "/" + why + "/" + action + "/" + lead, MapNoticeText.Compose(plan, language));
                                }
                            continue;
                        case NoticeId.ClientOverrideBad:
                        case NoticeId.ClientFolderBad:
                            foreach (NoticeWhy why in new[] { NoticeWhy.FolderMissing, NoticeWhy.ExeMissing, NoticeWhy.ManifestBad })
                            {
                                plan = NoticeTestData.FullPlan(id, lead);
                                plan.Why = why;
                                yield return (language, id + "/" + why + "/" + lead, MapNoticeText.Compose(plan, language));
                            }
                            continue;
                        case NoticeId.ServerAllFailed:
                        case NoticeId.ServerSomeFailed:
                            foreach (NoticeWhy why in new[] { NoticeWhy.MapDataBad, NoticeWhy.RegistrationFailed })
                            {
                                plan = NoticeTestData.FullPlan(id, lead);
                                plan.Why = why;
                                yield return (language, id + "/" + why + "/" + lead, MapNoticeText.Compose(plan, language));
                            }
                            continue;
                        case NoticeId.NavMeshMissing:
                            foreach (NoticeWhy why in new[] { NoticeWhy.NavUnverified, NoticeWhy.NavFileMissing, NoticeWhy.NavDamaged, NoticeWhy.NavNoKey })
                            {
                                plan = NoticeTestData.FullPlan(id, lead);
                                plan.Why = why;
                                yield return (language, id + "/" + why + "/" + lead, MapNoticeText.Compose(plan, language));
                            }
                            continue;
                        case NoticeId.SelectionReminder:
                            foreach (ReminderGroup group in Enum.GetValues<ReminderGroup>().Where(g => g != ReminderGroup.None))
                            {
                                plan = NoticeTestData.FullPlan(id, lead);
                                plan.Reminder = group;
                                yield return (language, id + "/" + group + "/" + lead, MapNoticeText.Compose(plan, language));
                            }
                            continue;
                    }
                    yield return (language, id + "/" + lead, MapNoticeText.Compose(plan, language));
                }
            }
    }

    // ---- ids and refs -------------------------------------------------------------------------------------------

    [Fact]
    public void EveryIdHasABodyAndOneRefInBothLanguages()
    {
        foreach (string language in Languages)
            foreach (NoticeId id in MapNoticeText.AllIds)
                Assert.NotEmpty(MapNoticeText.TemplateLines(id, language));

        var refs = MapNoticeText.AllIds.Select(MapNoticeText.RefOf).ToList();
        Assert.Equal(refs.Count, refs.Distinct().Count());
        Assert.All(refs, code => Assert.Matches("^[A-Z_]+$", code));
    }

    [Fact]
    public void TheRefCodesAreTheOnesOfTheDesign()
    {
        var expected = new Dictionary<NoticeId, string>
        {
            [NoticeId.ClientNoArena] = "NO_ARENA", [NoticeId.ClientRegistryUnreadable] = "REGISTRY", [NoticeId.ClientOverrideBad] = "OVERRIDE_BAD",
            [NoticeId.ClientFolderBad] = "FOLDER_BAD", [NoticeId.ClientIncomplete] = "ARENA_INCOMPLETE", [NoticeId.ClientCheckFailed] = "CHECK_FAILED",
            [NoticeId.CheckPending] = "PENDING", [NoticeId.ClientCheckSlow] = "CHECK_SLOW", [NoticeId.ClientHookMissing] = "HOOK_MISSING",
            [NoticeId.ServerNoArena] = "SERVER_NO_ARENA", [NoticeId.ServerAllFailed] = "SERVER_ALL_FAILED", [NoticeId.ServerUnknown] = "SERVER_UNKNOWN",
            [NoticeId.ServerNoAnswer] = "SERVER_NO_ANSWER", [NoticeId.ServerSomeFailed] = "SERVER_PARTIAL_FAILED", [NoticeId.ServerSomeMissing] = "SERVER_PARTIAL",
            [NoticeId.InstallIncomplete] = "FILES_MISSING", [NoticeId.BundleBad] = "BUNDLE_BAD", [NoticeId.LoadNoCause] = "NO_CAUSE",
            [NoticeId.NavMeshMissing] = "NAVMESH", [NoticeId.MapFileDamaged] = "FILE_DAMAGED", [NoticeId.SelectionReminder] = "SELECTION",
            [NoticeId.KeyUpdated] = "KEY_UPDATED", [NoticeId.KeyArenaOlder] = "KEY_ARENA_OLDER", [NoticeId.KeyDiffers] = "KEY_DIFFERS",
            [NoticeId.KeyUnreadable] = "KEY_UNREADABLE", [NoticeId.KeyFileBad] = "KEY_FILE_BAD", [NoticeId.KeyInternal] = "KEY_INTERNAL",
        };
        Assert.Equal(MapNoticeText.AllIds.Count, expected.Count);
        foreach (var pair in expected)
            Assert.Equal(pair.Value, MapNoticeText.RefOf(pair.Key));
    }

    [Fact]
    public void OnlyTheFourShortStatesAreToasts()
    {
        var toasts = MapNoticeText.AllIds.Where(id => MapNoticeText.KindOf(id) == NoticeKind.Toast).ToHashSet();
        Assert.Equal(new[] { NoticeId.CheckPending, NoticeId.SelectionReminder, NoticeId.NavMeshMissing, NoticeId.MapFileDamaged }.ToHashSet(), toasts);
    }

    // ---- the tables ----------------------------------------------------------------------------------------------

    [Fact]
    public void EnglishAndGermanHaveTheSameLinesPlaceholdersAndSubTemplates()
    {
        foreach (NoticeId id in MapNoticeText.AllIds)
        {
            var en = MapNoticeText.TemplateLines(id, "en");
            var de = MapNoticeText.TemplateLines(id, "de");
            Assert.True(en.Count == de.Count, id + ": line count");
            for (int i = 0; i < en.Count; i++)
            {
                Assert.True(MapNoticeText.IsMenuOnlyLine(id, "en", i) == MapNoticeText.IsMenuOnlyLine(id, "de", i), id + ": menu-only flag of line " + i);
                Assert.True(MapNoticeText.PlaceholdersIn(en[i]).SetEquals(MapNoticeText.PlaceholdersIn(de[i])), id + ": placeholders of line " + i);
            }
        }

        var enSub = MapNoticeText.SubTemplates("en");
        var deSub = MapNoticeText.SubTemplates("de");
        Assert.Equal(enSub.Keys.OrderBy(k => k), deSub.Keys.OrderBy(k => k));
        foreach (var key in enSub.Keys)
            Assert.True(MapNoticeText.PlaceholdersIn(enSub[key]).SetEquals(MapNoticeText.PlaceholdersIn(deSub[key])), "sub-template " + key);
    }

    [Fact]
    public void EveryReasonLeadAndActionHasAnEnglishAndAGermanText()
    {
        var en = MapNoticeText.SubTemplates("en");
        foreach (NoticeWhy why in Enum.GetValues<NoticeWhy>().Where(w => w != NoticeWhy.None))
            Assert.True(en.ContainsKey("why:" + why), "reason " + why);
        foreach (NoticeAction action in Enum.GetValues<NoticeAction>().Where(a => a != NoticeAction.None))
            Assert.True(en.ContainsKey("action:" + action), "action " + action);
        foreach (NoticeLead lead in Enum.GetValues<NoticeLead>())
            Assert.True(en.ContainsKey("lead:" + lead), "lead " + lead);
        foreach (ReminderGroup group in Enum.GetValues<ReminderGroup>().Where(g => g != ReminderGroup.None))
        {
            Assert.True(en.ContainsKey("short:" + group), "short " + group);
            Assert.True(en.ContainsKey("fix:" + group), "fix " + group);
        }
    }

    [Fact]
    public void NoComposedTextKeepsAPlaceholderToken()
    {
        foreach (var (language, description, message) in AllComposed())
            Assert.False(Token.IsMatch(message.Text), language + " " + description + ": " + message.Text);
    }

    [Fact]
    public void NoTextContainsTheForbiddenWordsOrAHexRun()
    {
        var hex = new Regex("[0-9a-fA-F]{32,}");
        foreach (var (language, description, message) in AllComposed())
        {
            string lower = message.Text.ToLowerInvariant();
            Assert.DoesNotContain("copy protection", lower);
            Assert.DoesNotContain("protect", lower);
            Assert.DoesNotContain("schutz", lower);
            Assert.False(hex.IsMatch(message.Text), language + " " + description);
        }
        foreach (string language in Languages)
            foreach (NoticeId id in MapNoticeText.AllIds)
                foreach (string line in MapNoticeText.TemplateLines(id, language))
                {
                    Assert.DoesNotContain("protect", line.ToLowerInvariant());
                    Assert.DoesNotMatch(hex, line);
                }
    }

    [Fact]
    public void NoTextHasALessThanSignAndEveryGreaterThanSignIsAMenuSeparator()
    {
        // The window renders rich text unescaped: "<" would start a tag. ">" alone is harmless, and the F12 path
        // "F12 > maschine-ArenaMapPort > General" is spelled with it, so only that spelling may remain.
        foreach (var (language, description, message) in AllComposed())
        {
            Assert.DoesNotContain("<", message.Text);
            foreach (Match match in Regex.Matches(message.Text, ">"))
            {
                Assert.True(match.Index > 0 && message.Text[match.Index - 1] == ' ' && match.Index + 1 < message.Text.Length && message.Text[match.Index + 1] == ' ',
                    language + " " + description + ": '>' at " + match.Index);
            }
        }
    }

    [Fact]
    public void HostileValuesCannotInjectRichTextOrPlaceholders()
    {
        foreach (string language in Languages)
        {
            MapNoticePlan plan = NoticeTestData.FullPlan(NoticeId.ClientFolderBad);
            plan.Set("path", "D:\\<b>evil</b>\\{ovr}\n<color=red>").Set("detail", "<size=99>{f12}</size>");
            string text = MapNoticeText.Compose(plan, language).Text;
            Assert.DoesNotContain("<", text);
            Assert.Contains("(b)evil(/b)", text);
            Assert.Contains("{ovr}", text); // a value is never expanded
            Assert.DoesNotContain("\n<", text);
        }
    }

    [Fact]
    public void KeyPhrasesAppearWhereTheDesignRequiresThem()
    {
        string Text(NoticeId id, string language = "en", Action<MapNoticePlan> tweak = null)
        {
            MapNoticePlan plan = NoticeTestData.FullPlan(id);
            tweak?.Invoke(plan);
            return MapNoticeText.Compose(plan, language).Text;
        }

        foreach (string language in Languages)
        {
            foreach (NoticeId id in new[] { NoticeId.ClientNoArena, NoticeId.ClientRegistryUnreadable, NoticeId.ClientOverrideBad, NoticeId.ClientCheckSlow })
                Assert.Contains("ArenaInstallPath", Text(id, language));
            Assert.Contains("arena-install.json", Text(NoticeId.ClientNoArena, language));
            Assert.Contains("ShowMapNotices", Text(NoticeId.ClientNoArena, language));
            Assert.Contains("ShowMapNotices", Text(NoticeId.ClientOverrideBad, language));
            foreach (NoticeId id in new[] { NoticeId.ServerUnknown, NoticeId.ServerNoAnswer, NoticeId.ServerAllFailed, NoticeId.ServerSomeMissing, NoticeId.ServerSomeFailed })
                Assert.Contains("[ArenaMapPort]", Text(id, language));
            Assert.Contains("[ArenaMapPort]", Text(NoticeId.ServerNoArena, language, plan => { plan.Why = NoticeWhy.ServerCheckFailed; plan.Action = NoticeAction.CheckFailed; }));
            Assert.Contains(@"SPT_Runtime\user\mods\ArenaMapPort\config\arena-install.json", Text(NoticeId.ServerNoArena, language));
            Assert.Contains("\"installPath\"", Text(NoticeId.ServerNoArena, language));
            Assert.Contains("[Arena]", Text(NoticeId.ClientCheckFailed, language));
            Assert.Contains("[BundleKeys]", Text(NoticeId.KeyInternal, language));
            Assert.Contains("[Bundles]", Text(NoticeId.BundleBad, language));
            Assert.Contains("[NavMesh]", Text(NoticeId.NavMeshMissing, language));
            Assert.Contains(MapNoticeText.SupportLink, Text(NoticeId.ClientCheckFailed, language));
        }
    }

    /// <summary>
    /// The release is ONE ZIP on Pixeldrain, like 0.3.0 (user decision 2026-10-30; a five-part form prepared on 2026-10-29 was dropped): the
    /// reinstall advice of the server and file texts names "the release ZIP" and nothing that sounds like a set of ZIP parts.
    /// </summary>
    [Fact]
    public void ReinstallAdviceNamesTheReleaseZipInBothLanguages()
    {
        NoticeId[] ids = { NoticeId.ServerUnknown, NoticeId.ServerAllFailed, NoticeId.ServerSomeMissing, NoticeId.ServerSomeFailed, NoticeId.InstallIncomplete };
        foreach (NoticeId id in ids)
        {
            string en = MapNoticeText.Compose(NoticeTestData.FullPlan(id), "en").Text;
            string de = MapNoticeText.Compose(NoticeTestData.FullPlan(id), "de").Text;
            Assert.Contains("release ZIP", en);
            Assert.Contains("Release-ZIP", de);
            // no leftover wording of a split release or of the intermediate "package" form
            foreach (string gone in new[] { "ZIP parts", "all its", "release package", "ZIP-Teile", "alle seine", "Release-Paket" })
            {
                Assert.DoesNotContain(gone, en);
                Assert.DoesNotContain(gone, de);
            }
        }
    }

    [Fact]
    public void TheGermanTextsAddressThePlayerInformally()
    {
        var formal = new Regex(@"\bSie\b|\bIhr(e|en|em|er|es)?\b|\bIhnen\b");
        foreach (var (language, description, message) in AllComposed().Where(c => c.Language == "de"))
            Assert.False(formal.IsMatch(message.Text), description + ": " + message.Text);
    }

    // ---- the exact English texts of the design ----------------------------------------------------------------------

    [Fact]
    public void ClientNoArenaReadsAsTheDesignHasIt()
    {
        MapNoticePlan plan = NoticeTestData.FullPlan(NoticeId.ClientNoArena, serverVersion: null);
        MapNoticeMessage message = MapNoticeText.Compose(plan, "en");
        Assert.Equal("ArenaMapPort", message.Caption);
        Assert.Equal(NoticeKind.Modal, message.Kind);
        Assert.Equal(
            "The Arena maps are not available: ArenaMapPort did not find Escape from Tarkov: Arena on this PC.\n" +
            "Install Arena with the Battlestate Games launcher (the maps are built from its files), then restart the game and the SPT server.\n" +
            "Arena installed but not found? Set its folder in F12 > maschine-ArenaMapPort > General > ArenaInstallPath and in the server file arena-install.json (see the README).\n" +
            "To hide this message: F12 > maschine-ArenaMapPort > General > ShowMapNotices.\n" +
            "ArenaMapPort 0.3.0 - ref NO_ARENA",
            message.Text);
    }

    [Fact]
    public void TheAnswerToAFailedAttemptUsesTheMapLeadAndDropsTheHideHint()
    {
        MapNoticePlan plan = NoticeTestData.FullPlan(NoticeId.ClientNoArena, NoticeLead.Attempt);
        string text = MapNoticeText.Compose(plan, "en").Text;
        Assert.StartsWith("Bay 5 could not be loaded: ArenaMapPort did not find Escape from Tarkov: Arena on this PC.\n", text);
        Assert.DoesNotContain("ShowMapNotices", text); // that setting never silences an answer to a failed attempt

        string german = MapNoticeText.Compose(plan, "de").Text;
        Assert.StartsWith("Bay 5 konnte nicht geladen werden: ArenaMapPort hat Escape from Tarkov: Arena auf diesem PC nicht gefunden.\n", german);
        Assert.DoesNotContain("ShowMapNotices", german);
        Assert.Contains("ShowMapNotices", MapNoticeText.Compose(NoticeTestData.FullPlan(NoticeId.ClientNoArena), "de").Text);
    }

    [Fact]
    public void ServerNoArenaChoosesItsActionLinesByCause()
    {
        string Compose(NoticeWhy why, NoticeAction action)
        {
            MapNoticePlan plan = NoticeTestData.FullPlan(NoticeId.ServerNoArena);
            plan.Why = why;
            plan.Action = action;
            return MapNoticeText.Compose(plan, "en").Text;
        }

        string create = Compose(NoticeWhy.ServerNotRegistered, NoticeAction.CreateOverride);
        Assert.Equal(
            "The Arena maps are not offered: the SPT server could not verify Escape from Tarkov: Arena (no launcher entry for Arena on the server PC), although this game found it.\n" +
            @"If the server runs on this PC, create SPT_Runtime\user\mods\ArenaMapPort\config\arena-install.json (create the folder config if needed; the name must end in .json, not .json.txt) with this content:" + "\n" +
            "{\"installPath\": \"D:\\\\Battlestate Games\\\\Escape from Tarkov Arena\"}\n" +
            "Then restart the server. If it runs on another PC, ask whoever runs it.\n" +
            "ArenaMapPort 0.3.0 - ref SERVER_NO_ARENA, server 0.3.0",
            create);

        string ignored = Compose(NoticeWhy.ServerOverrideIgnored, NoticeAction.FixIgnored);
        Assert.Contains("(its arena-install.json was ignored: it is not valid JSON or has no \"installPath\" text value)", ignored);
        Assert.Contains(@"exists but could not be used, so it was ignored. Make it exactly like this, with every backslash in the path doubled (\\) or written as /:", ignored);
        Assert.DoesNotContain("is not valid JSON, so it was ignored", ignored); // the file can be ignored for other reasons than its syntax

        Assert.Contains("Fix the path in SPT_Runtime", Compose(NoticeWhy.ServerOverrideFolderMissing, NoticeAction.FixOverridePath));
        Assert.Contains("(its Arena files are incomplete or changed)", Compose(NoticeWhy.ServerFilesMismatch, NoticeAction.FilesMismatch));
        Assert.Contains("(its check failed unexpectedly)", Compose(NoticeWhy.ServerCheckFailed, NoticeAction.CheckFailed));
        Assert.Contains("(the server cannot read the Windows registry)", Compose(NoticeWhy.ServerRegistryUnreadable, NoticeAction.CreateOverride));
    }

    [Fact]
    public void TheToastsReadAsTheDesignHasThem()
    {
        Assert.Equal("ArenaMapPort: still checking your Arena installation (slow disk or unreachable path). The Arena maps load once it passes.",
            MapNoticeText.Compose(NoticeTestData.FullPlan(NoticeId.CheckPending), "en").Text);

        Assert.Equal("ArenaMapPort: the Arena maps are not available (Arena was not found on this PC). Install Arena with the launcher, or set F12 > ArenaInstallPath, then restart.",
            MapNoticeText.ComposeReminder(ReminderGroup.ArenaNotFound, "en").Text);
        Assert.Equal("ArenaMapPort: the Arena maps are not available (the Arena installation cannot be used). Repair Arena or fix F12 > ArenaInstallPath, then restart.",
            MapNoticeText.ComposeReminder(ReminderGroup.ClientUnusable, "en").Text);
        Assert.Equal("ArenaMapPort: the Arena maps are not available (the SPT server does not offer them). Reinstall the server's ArenaMapPort and restart the server.",
            MapNoticeText.ComposeReminder(ReminderGroup.ServerNoOffer, "en").Text);
        Assert.Equal("ArenaMapPort: the Arena maps are not available (the server could not verify Arena). Set or fix the server's arena-install.json (see the README), then restart it.",
            MapNoticeText.ComposeReminder(ReminderGroup.ServerNoVerify, "en").Text);
        Assert.Equal(@"ArenaMapPort: the Arena maps are not available (the check of your Arena installation did not finish). Restart the game; the [Arena] lines of BepInEx\LogOutput.log say more.",
            MapNoticeText.ComposeReminder(ReminderGroup.ClientCheck, "en").Text);
        Assert.Equal("ArenaMapPort: the Arena maps are not available (this ArenaMapPort build does not fit your game version). Install the ArenaMapPort release for your SPT version, then restart.",
            MapNoticeText.ComposeReminder(ReminderGroup.ClientBuild, "en").Text);
        Assert.Equal("ArenaMapPort: the Arena maps are not available (the SPT server did not answer). Restart the SPT server, then this game.",
            MapNoticeText.ComposeReminder(ReminderGroup.ServerNoAnswer, "en").Text);
        Assert.Equal("ArenaMapPort: the Arena maps are not available (the server's Arena installation cannot be used). Repair or update Arena on the server PC, then restart the server.",
            MapNoticeText.ComposeReminder(ReminderGroup.ServerArena, "en").Text);
        Assert.Equal("ArenaMapPort: the Arena maps are not available (the server's Arena check failed). Restart the SPT server. If this repeats, see its console.",
            MapNoticeText.ComposeReminder(ReminderGroup.ServerCheck, "en").Text);

        MapNoticePlan nav = NoticeTestData.FullPlan(NoticeId.NavMeshMissing);
        nav.Why = NoticeWhy.NavNoKey;
        Assert.Equal(@"ArenaMapPort: Bay 5: the navigation data could not be loaded (its unlock file could not be used); bots will not move. Reinstall the complete client folder. Details: BepInEx\LogOutput.log, lines [NavMesh].",
            MapNoticeText.Compose(nav, "en").Text);
        Assert.Equal(@"ArenaMapPort: a map file for Bay 5 reported damage during the raid (arena_bay5_scenes.bundle). Reinstall the complete client folder. Details: BepInEx\LogOutput.log, lines [Bundles].",
            MapNoticeText.Compose(NoticeTestData.FullPlan(NoticeId.MapFileDamaged), "en").Text);
        Assert.Null(MapNoticeText.Compose(NoticeTestData.FullPlan(NoticeId.CheckPending), "en").Caption);
    }

    [Fact]
    public void TheKeyTextsNameTheArenaVersionsInTheDesignsWords()
    {
        Assert.Equal(
            "Bay 5 cannot be opened: Arena 0.6.1.0.48206 is installed, but this ArenaMapPort was built for Arena 0.6.0.0.47752, and files it needs have changed.\n" +
            "Install the newest ArenaMapPort release, which includes the matching unlock files, then restart the game.\n" +
            "ArenaMapPort 0.3.0 - ref KEY_UPDATED",
            MapNoticeText.Compose(NoticeTestData.FullPlan(NoticeId.KeyUpdated, serverVersion: null), "en").Text);
        Assert.Equal(
            "Bay 5 cannot be opened: this ArenaMapPort was built for Arena 0.6.0.0.47752, but Arena 0.6.1.0.48206 is installed.\n" +
            "Update Arena in the Battlestate Games launcher, then restart the game.\n" +
            "ArenaMapPort 0.3.0 - ref KEY_ARENA_OLDER",
            MapNoticeText.Compose(NoticeTestData.FullPlan(NoticeId.KeyArenaOlder, serverVersion: null), "en").Text);
        Assert.Equal(
            "Bay 5 did not start, and ArenaMapPort recorded no cause for it.\n" +
            @"Check BepInEx\LogOutput.log (lines marked [Arena], [Bundles], [BundleKeys], [NavMesh]) and the game's Logs folder (the errors.log of the newest session), and send them to https://github.com/maschine34675/ArenaMapPort/issues." + "\n" +
            "ArenaMapPort 0.3.0 - ref NO_CAUSE",
            MapNoticeText.Compose(NoticeTestData.FullPlan(NoticeId.LoadNoCause, serverVersion: null), "en").Text);
    }

    // ---- composition rules ----------------------------------------------------------------------------------------------

    [Fact]
    public void AModalGetsTheCaptionAndAFooterWithTheClientVersionAndTheRef()
    {
        foreach (string language in Languages)
            foreach (NoticeId id in MapNoticeText.AllIds.Where(id => MapNoticeText.KindOf(id) == NoticeKind.Modal))
            {
                MapNoticePlan plan = NoticeTestData.FullPlan(id);
                plan.ServerVersion = null;
                MapNoticeMessage message = MapNoticeText.Compose(plan, language);
                Assert.Equal("ArenaMapPort", message.Caption);
                Assert.Equal(MapNoticeText.RefOf(id), message.Ref);
                Assert.EndsWith("\nArenaMapPort 0.3.0 - ref " + MapNoticeText.RefOf(id), message.Text);
            }
    }

    [Fact]
    public void TheFooterAddsTheServerVersionWhenTheProbeAnswered()
    {
        MapNoticePlan plan = NoticeTestData.FullPlan(NoticeId.ServerUnknown);
        plan.ServerVersion = "0.2.3";
        Assert.EndsWith("\nArenaMapPort 0.3.0 - ref SERVER_UNKNOWN, server 0.2.3", MapNoticeText.Compose(plan, "en").Text);
        plan.ServerVersion = "  ";
        Assert.EndsWith("\nArenaMapPort 0.3.0 - ref SERVER_UNKNOWN", MapNoticeText.Compose(plan, "en").Text);
    }

    [Fact]
    public void ToastsStartWithTheProductNameAndHaveNoFooterOrCaption()
    {
        foreach (string language in Languages)
            foreach (NoticeId id in MapNoticeText.AllIds.Where(id => MapNoticeText.KindOf(id) == NoticeKind.Toast))
            {
                MapNoticeMessage message = MapNoticeText.Compose(NoticeTestData.FullPlan(id), language);
                Assert.StartsWith("ArenaMapPort: ", message.Text);
                Assert.DoesNotContain("\n", message.Text);
                Assert.DoesNotContain("- ref", message.Text);
                Assert.Null(message.Caption);
                Assert.False(message.Scrolled);
            }
    }

    [Fact]
    public void AModalLongerThan480CharactersIsFlaggedForTheScrolledWindowAndNoneIsAbsurdlyLong()
    {
        int scrolled = 0;
        foreach (var (language, description, message) in AllComposed().Where(c => c.Message.Kind == NoticeKind.Modal))
        {
            Assert.Equal(message.Text.Length > 480, message.Scrolled);
            Assert.True(message.Text.Length <= 760, language + " " + description + " is " + message.Text.Length + " characters");
            if (message.Scrolled)
                scrolled++;
        }
        // the ordinary window carries the shorter texts; the scrolled window is the exception (the longest reasons and the German table)
        Assert.True(scrolled < AllComposed().Count(c => c.Message.Kind == NoticeKind.Modal) / 2, "most modal texts fit the ordinary window");

        // the design's worst case: a long path in the JSON example always goes to the scrolled window
        MapNoticePlan plan = NoticeTestData.FullPlan(NoticeId.ServerNoArena);
        plan.Set("json", MapNoticeText.BuildJsonExample(@"D:\" + new string('x', 200)));
        Assert.True(MapNoticeText.Compose(plan, "en").Scrolled);
    }

    [Fact]
    public void TheMostCommonNoticeTheNoArenaTextSitsNearTheLimitAndGoesToTheScrolledWindowWhenOver()
    {
        // ClientNoArena with the F12 path spelled out twice is the design's longest client text (about 500 characters with the footer):
        // the host run (V2) measures whether the ordinary window clips it; until then the rule of the design decides.
        foreach (string language in Languages)
        {
            MapNoticeMessage message = MapNoticeText.Compose(NoticeTestData.FullPlan(NoticeId.ClientNoArena, serverVersion: null), language);
            Assert.InRange(message.Text.Length, 400, 700);
            Assert.Equal(message.Text.Length > MapNoticeText.ModalLimit, message.Scrolled);
        }
    }

    [Fact]
    public void ADynamicValueIsMadeSafeAndCutBeforeItIsSubstituted()
    {
        Assert.Equal("a(b)c d", MapNoticeText.Sanitize("a<b>c\nd"));
        Assert.Equal("x  y", MapNoticeText.Sanitize("  x\t\ty \r\n"));
        Assert.Null(MapNoticeText.Sanitize(null));

        MapNoticePlan plan = NoticeTestData.FullPlan(NoticeId.ClientCheckFailed);
        plan.Set("detail", new string('d', 200));
        string text = MapNoticeText.Compose(plan, "en").Text;
        Assert.Contains("(" + new string('d', 77) + "...)", text);
        Assert.DoesNotContain(new string('d', 78), text);

        plan = NoticeTestData.FullPlan(NoticeId.ClientFolderBad);
        plan.Set("path", @"D:\Games\" + new string('p', 100) + @"\Arena");
        text = MapNoticeText.Compose(plan, "en").Text;
        Match match = Regex.Match(text, @"lists Arena at (.*?), but");
        Assert.True(match.Success);
        Assert.Equal(60, match.Groups[1].Value.Length);
        Assert.StartsWith(@"D:\Games\", match.Groups[1].Value);
        Assert.EndsWith(@"\Arena", match.Groups[1].Value);
        Assert.Contains("...", match.Groups[1].Value);
    }

    [Fact]
    public void ShortenMiddleAndTruncateKeepTheirLimits()
    {
        Assert.Equal("short", MapNoticeText.ShortenMiddle("short", 60));
        string shortened = MapNoticeText.ShortenMiddle(new string('a', 30) + new string('b', 100) + new string('c', 30), 60);
        Assert.Equal(60, shortened.Length);
        Assert.Equal(new string('a', 28) + "..." + new string('c', 29), shortened);
        Assert.Equal("abc", MapNoticeText.Truncate("abc", 3));
        Assert.Equal("ab...", MapNoticeText.Truncate("abcdefgh", 5));
        Assert.Null(MapNoticeText.Truncate(null, 5));
    }

    [Fact]
    public void AValueThePlanLacksReadsAsAQuestionMarkNeverAsAToken()
    {
        var plan = new MapNoticePlan(NoticeId.InstallIncomplete);
        string text = MapNoticeText.Compose(plan, "en").Text;
        Assert.Contains("files for ? are incomplete (missing: ?, for example ?)", text);
        Assert.EndsWith("ArenaMapPort ? - ref FILES_MISSING", text);
        Assert.DoesNotMatch(Token, text);

        // a reason or action that was not chosen reads "?" too
        text = MapNoticeText.Compose(new MapNoticePlan(NoticeId.ServerNoArena), "en").Text;
        Assert.Contains("(?)", text);
        Assert.DoesNotMatch(Token, text);
    }

    [Fact]
    public void TheJsonExampleDoublesTheBackslashesAndParsesBackToThePath()
    {
        string path = @"D:\Battlestate Games\Escape from Tarkov Arena";
        string json = MapNoticeText.BuildJsonExample(path);
        Assert.Equal("{\"installPath\": \"D:\\\\Battlestate Games\\\\Escape from Tarkov Arena\"}", json);
        Assert.Equal(path, JObject.Parse(json).Value<string>("installPath"));

        Assert.Equal(@"\\server\share", JObject.Parse(MapNoticeText.BuildJsonExample(@"\\server\share")).Value<string>("installPath"));
        Assert.Equal("a\"b", JObject.Parse(MapNoticeText.BuildJsonExample("a\"b")).Value<string>("installPath"));
        Assert.Contains("installPath", MapNoticeText.BuildJsonExample(null));
        Assert.Contains("installPath", MapNoticeText.BuildJsonExample("  "));
    }

    [Fact]
    public void TheJsonExampleIsOneValueItsBracesAreNeverReadAsPlaceholders()
    {
        MapNoticePlan plan = NoticeTestData.FullPlan(NoticeId.ServerNoArena);
        string text = MapNoticeText.Compose(plan, "en").Text;
        Assert.Contains("{\"installPath\":", text);
        Assert.DoesNotMatch(Token, text);
    }

    // ---- fallback and reminder -----------------------------------------------------------------------------------------------

    [Fact]
    public void TheFallbackToastIsTheReminderOfRowsOneToThirteenAndTheProblemSentenceOtherwise()
    {
        MapNoticePlan withGroup = NoticeTestData.FullPlan(NoticeId.ClientNoArena);
        withGroup.Reminder = ReminderGroup.ArenaNotFound;
        Assert.Equal(MapNoticeText.ComposeReminder(ReminderGroup.ArenaNotFound, "en").Text, MapNoticeText.ComposeFallbackToast(withGroup, "en").Text);

        MapNoticePlan without = NoticeTestData.FullPlan(NoticeId.ServerSomeMissing);
        without.Reminder = ReminderGroup.None;
        MapNoticeMessage toast = MapNoticeText.ComposeFallbackToast(without, "en");
        Assert.Equal(NoticeKind.Toast, toast.Kind);
        Assert.StartsWith("ArenaMapPort: The SPT server does not offer Equator, Bay 5, which this game knows.", toast.Text);
        Assert.DoesNotContain("\n", toast.Text);
        Assert.True(toast.Text.Length <= MapNoticeText.ToastPrefix.Length + 200);

        MapNoticePlan alreadyToast = NoticeTestData.FullPlan(NoticeId.NavMeshMissing);
        Assert.Equal(MapNoticeText.Compose(alreadyToast, "en").Text, MapNoticeText.ComposeFallbackToast(alreadyToast, "en").Text);
        Assert.Throws<ArgumentNullException>(() => MapNoticeText.ComposeFallbackToast(null, "en"));
        Assert.Throws<ArgumentNullException>(() => MapNoticeText.Compose(null, "en"));
    }

    [Fact]
    public void AnUnknownReminderGroupReadsAsTheClientUnusableOne()
    {
        Assert.Equal(MapNoticeText.ComposeReminder(ReminderGroup.ClientUnusable, "en").Text, MapNoticeText.ComposeReminder(ReminderGroup.None, "en").Text);
    }

    // ---- language -----------------------------------------------------------------------------------------------------------

    [Theory]
    [InlineData("ge", "de")]
    [InlineData("GE", "de")]
    [InlineData(" ge ", "de")]
    [InlineData("de", "de")]
    [InlineData("de-DE", "de")]
    [InlineData("de_AT", "de")]
    [InlineData("ge-CH", "de")]
    [InlineData("german", "de")]
    [InlineData("en", "en")]
    [InlineData("fr", "en")]
    [InlineData("ru", "en")]
    [InlineData("cz", "en")]
    [InlineData("", "en")]
    [InlineData(null, "en")]
    [InlineData("  ", "en")]
    [InlineData("deadbeef", "en")]
    public void TheLanguageFollowsEftsLocaleCodeAndFallsBackToEnglish(string code, string expected)
    {
        Assert.Equal(expected, MapNoticeText.LanguageFor(code));
    }

    [Fact]
    public void AnUnknownLanguageComposesInEnglish()
    {
        MapNoticePlan plan = NoticeTestData.FullPlan(NoticeId.ClientNoArena);
        string english = MapNoticeText.Compose(plan, "en").Text;
        Assert.Equal(english, MapNoticeText.Compose(plan, null).Text);
        Assert.Equal(english, MapNoticeText.Compose(plan, "fr").Text);
        Assert.NotEqual(english, MapNoticeText.Compose(plan, "de").Text);
        Assert.Contains("Installiere Arena mit dem Battlestate-Games-Launcher", MapNoticeText.Compose(plan, "de").Text);
    }

    [Fact]
    public void PlaceholderPatternMatchesLettersOnly()
    {
        Assert.Equal(new[] { "Map", "maps", "ver" }, MapNoticeText.PlaceholdersIn("{Map} {maps} {ver} {1} {a-b} {\"x\": 1}"));
        Assert.Empty(MapNoticeText.PlaceholdersIn(null));
    }
}
