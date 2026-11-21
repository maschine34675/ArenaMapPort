#nullable disable
using System.Text.RegularExpressions;
using Xunit;

namespace ArenaMapPort.Server.Tests;

/// <summary>
/// Source pins of the native grass (PLAN-YARD item 23; design-final.md section 5 "Source pins": they replace the unverifiable "grep for BSG bytes"; stage 2, 2026-11-21): the four model files stay Unity-free and do
/// no file access, no file of the grass touches Arena's installation or its gate or exports, hashes or caches bytes, the keys stay non-public keys that default ON, and the glue files keep the rules of the lead's
/// brief (Settings not Config, no DontDestroyOnLoad, no Info line, the worker touches neither Unity nor the log, files open read-only with sharing, a map's refusal is no session latch). The GPU glue itself is
/// host-checked only.
/// </summary>
public sealed class NativeGrassSourcePinTests
{
    static readonly string[] ModelFiles = { "NativeGrassModel.cs", "NativeGrassArtModel.cs", "NativeGrassColorModel.cs", "NativeGrassPackModel.cs" };

    static string ClientDir()
    {
        for (var dir = new DirectoryInfo(AppContext.BaseDirectory); dir != null; dir = dir.Parent)
        {
            string path = Path.Combine(dir.FullName, "ArenaMapPort.Client");
            if (File.Exists(Path.Combine(path, "Plugin.cs"))) return path;
        }
        throw new DirectoryNotFoundException("ArenaMapPort.Client");
    }

    static string Source(string file) => File.ReadAllText(Path.Combine(ClientDir(), file)).Replace("\r\n", "\n");

    static bool Exists(string file) => File.Exists(Path.Combine(ClientDir(), file));

    /// <summary>Every client file of the grass, model and glue alike.</summary>
    static string[] GrassFiles() => Directory.GetFiles(ClientDir(), "NativeGrass*.cs").Select(Path.GetFileName).OrderBy(n => n, StringComparer.Ordinal).ToArray();

    /// <summary>The code without comments and string literals, for rules about identifiers.</summary>
    static string Code(string source)
    {
        string noBlock = Regex.Replace(source, @"/\*.*?\*/", string.Empty, RegexOptions.Singleline);
        string noLine = Regex.Replace(noBlock, @"//[^\n]*", string.Empty);
        return Regex.Replace(noLine, "\"(\\\\.|[^\"\\\\])*\"", "\"\"");
    }

    [Fact]
    public void TheFourModelFilesAndTheArtSourceExistAndAreLinkedIntoTheTests()
    {
        string csproj = File.ReadAllText(Path.Combine(ClientDir(), "..", "ArenaMapPort.Server.Tests", "ArenaMapPort.Server.Tests.csproj"));
        foreach (string file in ModelFiles)
        {
            Assert.True(Exists(file), file);
            Assert.Contains("..\\ArenaMapPort.Client\\" + file, csproj);
        }
        Assert.Contains("..\\ArenaMapPort.Client\\NativeGrassArtSource.cs", csproj);
    }

    [Theory]
    [InlineData("NativeGrassModel.cs")]
    [InlineData("NativeGrassArtModel.cs")]
    [InlineData("NativeGrassColorModel.cs")]
    [InlineData("NativeGrassPackModel.cs")]
    public void TheModelFilesHaveNoUnityNoBepInExNoLogAndNoFileAccess(string file)
    {
        string code = Code(Source(file));
        foreach (string banned in new[] { "UnityEngine", "BepInEx", "HarmonyLib", "Debug.Log", "Logger", "Plugin.", "LogDebug", "LogWarning", "LogError", "File.", "FileStream", "Directory.", "Path.", "System.IO", "System.Threading", "Task.", "Marshal.", "Resources.", "GameObject", "MonoBehaviour" })
            Assert.DoesNotContain(banned, code);
    }

    [Theory]
    [InlineData("NativeGrassModel.cs")]
    [InlineData("NativeGrassArtModel.cs")]
    [InlineData("NativeGrassColorModel.cs")]
    [InlineData("NativeGrassPackModel.cs")]
    public void TheModelFilesTouchNothingThatReadsTheClockOrRandomOrTheEnvironment(string file)
    {
        string code = Code(Source(file));
        foreach (string banned in new[] { "DateTime", "Environment.", "Random", "Stopwatch", "Guid" })
            Assert.DoesNotContain(banned, code);
    }

    [Fact]
    public void NoGrassFileExportsHashesOrCachesBytes()
    {
        foreach (string file in GrassFiles())
        {
            string code = Code(Source(file));
            foreach (string banned in new[]
            {
                "File.Write", "File.Create", "File.Append", "File.Copy", "File.Move", "WriteAllBytes", "WriteAllText", "FileMode.Create", "FileMode.CreateNew", "FileMode.OpenOrCreate", "FileMode.Append", "FileMode.Truncate",
                "FileAccess.Write", "FileAccess.ReadWrite", "EncodeToPNG", "EncodeToJPG", "EncodeToEXR", "EncodeToTGA", "GetRawTextureData", "ReadPixels", "GetPixelData", "persistentDataPath", "temporaryCachePath",
                "Paths.ConfigPath", "Paths.CachePath", "Path.GetTempPath", "SHA256", "SHA1", "MD5", "ComputeHash", "HashAlgorithm", "Sha256",
            })
                Assert.True(!code.Contains(banned), file + " contains " + banned);
        }
    }

    [Fact]
    public void OnlyTheGlueChoosesTheCryptoProviderAndOnlyOnce()
    {
        // the provider is chosen on the game thread (the resolver's own one-time Warning wording) and handed to the worker; nothing else of the grass names the provider class
        foreach (string file in GrassFiles())
        {
            string code = Code(Source(file));
            int count = Regex.Matches(code, @"CryptoProviders\.").Count;
            Assert.Equal(file == "NativeGrassRepairs.cs" ? 1 : 0, count);
        }
        Assert.Contains("CryptoProviders.Select(text => Plugin.Log.LogWarning(BundleKeyResolver.BundlesTag + \" \" + text))", Source("NativeGrassRepairs.cs"));
    }

    [Fact]
    public void NoGrassFileTouchesTheArenaInstallationOrItsGate()
    {
        // stage 2: the grass is read from its own staged bundle; Arena's installation, its folders, the gate and the Arena file layer are not the grass code's business any more
        foreach (string file in GrassFiles())
        {
            string text = Source(file);
            string code = Code(text);
            foreach (string banned in new[] { "ArenaInstallation", "ArenaInstallationGate", "ArenaInstallationResult", "GateState", "IArenaFiles", "DirectoryArenaFiles", "InstallPath", "KeyPath", "OpenArenaFiles" })
                Assert.False(code.Contains(banned, StringComparison.Ordinal), file + " must not contain " + banned);
            foreach (string banned in new[] { "EscapeFromTarkovArena_Data", "assets.resS", "ConsistencyInfo" })
                Assert.False(text.Contains(banned, StringComparison.Ordinal), file + " must not name Arena's file " + banned);
        }
    }

    [Fact]
    public void TheStage1ConstantsOfArenasFilesAreGone()
    {
        foreach (string file in GrassFiles())
        {
            string code = Code(Source(file));
            foreach (string gone in new[] { "AssetsSize", "StreamSize", "StreamPath", "AssetsPath", "NormalTextureOffset", "TexArrayOffset", "NormalArrayOffset", "StreamTailPadding", "NativeGrassMapRow", "NativeGrassTable", "CheckFileSizes", "PlacementRelativePath", "ExpectedBytes" })
                Assert.False(code.Contains(gone, StringComparison.Ordinal), file + " must not contain " + gone);
        }
    }

    [Fact]
    public void NoGrassFileWritesAnArenaPathOrADistPath()
    {
        foreach (string file in GrassFiles())
        {
            string text = Source(file);
            Assert.DoesNotContain("dist/", text.Replace("distance", string.Empty).Replace("distinct", string.Empty), StringComparison.Ordinal);
            Assert.DoesNotContain("maschine-ArenaMapPort/", text);
        }
    }

    [Fact]
    public void TheGrassKeysAreNotPublic()
    {
        // nothing of the grass is a player option: its keys are in neither list, and the README and the packer's texts do not name them
        foreach (string file in new[] { "CompactConfiguration.cs", "MapConfigVisibility.cs", "PlayerOptions.cs" })
        {
            string text = Source(file);
            foreach (string key in new[] { "\"Grass\"", "GrassDrawing", "GrassArt", "GrassUpload", "NativeGrass", NativeGrassKeysName })
                Assert.DoesNotContain(key, text);
        }
        string root = Path.Combine(ClientDir(), "..");
        foreach (string text in new[] { File.ReadAllText(Path.Combine(root, "README.md")), File.ReadAllText(Path.Combine(root, "tools", "release", "make_release_zips.py")) })
        {
            Assert.DoesNotContain("GrassDrawing", text);
            Assert.DoesNotContain("GrassUpload", text);
            Assert.DoesNotContain("NativeGrassKeys", text);
        }
    }

    const string NativeGrassKeysName = "NativeGrassKeys";

    [Fact]
    public void TheGrassKeyDefaultsToOnAndTheStage1KeysAreNeverBoundAgain()
    {
        string model = Source("NativeGrassModel.cs");
        Assert.Contains("internal const bool GrassDefault = true;", model);
        Assert.Contains("internal const string Grass = \"Grass\";", model);
        Assert.Contains("internal const string ArtSourceDefault = \"Pack\";", model);
        if (Exists("NativeGrassRepairs.cs"))
        {
            string glue = Source("NativeGrassRepairs.cs");
            Assert.Contains("NativeGrassKeys.Grass, NativeGrassKeys.GrassDefault", glue);
            foreach (string old in new[] { "\"NativeGrass\"", "\"NativeGrassDrawing\"", "\"NativeGrassArt\"", "\"NativeGrassUpload\"", "NativeGrassKeys.Mode", "ModeDefault" })
                Assert.DoesNotContain(old, glue);
        }
        // the four stage-1 names appear in one place only: the model's list of retired keys
        foreach (string file in GrassFiles())
        {
            if (file == "NativeGrassModel.cs") continue;
            string text = Source(file);
            foreach (string old in new[] { "\"NativeGrass\"", "\"NativeGrassDrawing\"", "\"NativeGrassArt\"", "\"NativeGrassUpload\"" })
                Assert.DoesNotContain(old, text);
        }
    }

    [Fact]
    public void TheGrassBundleIsStagedAsAHardInputOfAFullBuildAndNeverSeenByTheUnityFsValidator()
    {
        string csproj = Source("ArenaMapPort.Client.csproj");
        foreach (string key in new[] { "arena_bowl", "arena_iceberg", "arena_yard" })
        {
            Assert.Contains("<ArenaMapPortGrassBundles Include=\"$(ArenaMapPortGrassBuildDir)\\" + key + "_grass.bundle\"><MapKey>" + key + "</MapKey></ArenaMapPortGrassBundles>", csproj);
            Assert.Contains("@(ArenaMapPortGrassBundles->WithMetadataValue('MapKey','" + key + "')->'--bundle &quot;%(FullPath)&quot;', ' ')", csproj);
        }
        Assert.Equal(3, Regex.Matches(csproj, "<ArenaMapPortGrassBundles Include=").Count);
        Assert.Equal(3, Regex.Matches(csproj, @"@\(ArenaMapPortGrassBundles->WithMetadataValue").Count);       // the stage commands of the three grass maps only
        Assert.Contains("work\\build\\grass</ArenaMapPortGrassBuildDir>", csproj);
        // a missing pack stops a full build with the Error that names the extractor; a DllOnly build never gets there (the whole target's condition)
        string error = Between(csproj, "<Error Text=\"[ArenaMapPort] expected grass bundle missing", "/>");
        Assert.Contains("python tools/extract/extract_grass.py pack --map all", error);
        Assert.Contains("Condition=\"!Exists('%(ArenaMapPortGrassBundles.Identity)')\"", Between(csproj, "<Error Text=\"[ArenaMapPort] expected grass bundle missing", "<Error Text=\"[ArenaMapPort] expected Equator"));
        Assert.Contains("Condition=\"'$(Configuration)'=='Release' And '$(ArenaMapPortDllOnly)'!='true'\"", Between(csproj, "<Target Name=\"ArenaMapPortStageWpOutputs\"", ">"));
        // validate_bundle_blocks.py reads UnityFS headers: only the map bundles are given to it
        string validator = csproj.Split('\n').Single(l => l.Contains("validate_bundle_blocks.py", StringComparison.Ordinal) && l.Contains("<Exec", StringComparison.Ordinal));
        Assert.Contains("@(ArenaMapPortBundles->", validator);
        Assert.DoesNotContain("Grass", validator);
        Assert.Contains("staged %(ArenaMapPortGrassBundles.Filename)", csproj);
    }

    [Fact]
    public void ThePluginWarnsAboutAMissingGrassBundleLikeAnyRequiredFileWithoutMakingTheMapUnavailable()
    {
        string plugin = Source("Plugin.cs");
        string banner = Between(plugin, "void LogStartupBanner()", "void OnDestroy()");
        Assert.Contains("if (map.HasNativeGrass)", banner);
        Assert.Contains("LogExpectedFile(Path.Combine(bundlesDir, map.GrassBundleFileName));", banner);
        // the same helper, hence the same one Warning per file; the map's own availability code never names the file
        Assert.Contains("Required map file is missing", Between(plugin, "void LogExpectedFile(", "}\n    }"));
        // the grass glue logs a missing pack at Debug only (it was warned about here)
        string refuse = Between(Source("NativeGrassRepairs.cs"), "static void RefuseMap(", "static void ReportFailures()");
        Assert.Contains("pack-missing", refuse);
    }

    // ---- the glue files, when they exist -----------------------------------------------------------------------------------------------------------------------------------

    sealed class GlueFactAttribute : FactAttribute
    {
        public GlueFactAttribute(string file)
        {
            if (!File.Exists(Path.Combine(ClientDir(), file))) Skip = file + " does not exist yet";
        }
    }

    [GlueFact("NativeGrassRepairs.cs")]
    public void TheRepairNeverUsesConfigDontDestroyOnLoadOrAnInfoLine()
    {
        string code = Code(Source("NativeGrassRepairs.cs"));
        Assert.DoesNotContain("Instance.Config", code);
        Assert.DoesNotContain("DontDestroyOnLoad", code);
        Assert.DoesNotContain("LogInfo(", code);
        Assert.DoesNotContain("LogMessage(", code);
        Assert.Contains("Settings.Bind", code);
        Assert.DoesNotContain("PlayerOptions", code);
    }

    [GlueFact("NativeGrassArtSource.cs")]
    public void TheArtSourceNeverTouchesUnityOrTheLogAndOpensTheFileReadOnlyWithSharing()
    {
        string code = Code(Source("NativeGrassArtSource.cs"));
        foreach (string banned in new[] { "UnityEngine", "LogDebug", "LogWarning", "LogError", "LogInfo", "Plugin.", "Debug.", "FileAccess.Write", "FileAccess.ReadWrite", "BundleKeyResolver", "BundleHost", "ArenaMapRaid" })
            Assert.DoesNotContain(banned, code);
        Assert.Contains("FileAccess.Read", code);
        Assert.Contains("FileShare.ReadWrite | FileShare.Delete", code);
    }

    [GlueFact("NativeGrassArtSource.cs")]
    public void TheKeyIsAskedOnTheCallersThreadCopiedAndClearedByTheWorker()
    {
        string source = Source("NativeGrassArtSource.cs");
        string code = Code(source);
        string start = Between(code, "internal static GrassPreparation Start(", "static GrassPreparation Done(");
        string load = Between(code, "internal static GrassInputs Load(", "static bool Gone(");
        // the resolver is asked inside Start (the game thread) and never inside Load (the worker); the worker gets a COPY
        Assert.Contains("resolveKey(mapKey, out shared)", start);
        Assert.DoesNotContain("resolveKey", load);
        Assert.Contains("(byte[])shared.Clone()", start);
        Assert.Contains("provider()", start);
        // Load clears the copy it was given in a finally and right after the stream took it, and nothing in it logs or throws
        Assert.Equal(2, Regex.Matches(load, @"Clear\(key\);").Count);
        Assert.Contains("catch (AmpbException e)", load);
        Assert.Contains("catch (Exception e)", load);
        Assert.Contains("e.GetType().Name", load);               // the exception type only, never a message
        Assert.DoesNotContain("e.Message", code);
        // the key never reaches a string or a field
        Assert.DoesNotContain("static byte[]", code);
    }

    [GlueFact("NativeGrassArtSource.cs")]
    public void TheWorkerReadsOnlyThroughThePackReaderAndTheDecryptingStream()
    {
        string code = Code(Source("NativeGrassArtSource.cs"));
        string load = Between(code, "internal static GrassInputs Load(", "static bool Gone(");
        foreach (string needed in new[] { "new AmpbReadStream(file, mapKey, key, provider, 4, false)", "NativeGrassPack.Parse(header, got, pack.Length, mapKey, out layout)", "NativeGrassPlacement.Parse(records, records.Length, layout.SliceCount)", "NativeGrassArt.CheckBlocks(data, range)" })
            Assert.Contains(needed, load);
        Assert.Contains("Ampb.IsAmpb(file)", Between(code, "internal static GrassPreparation Start(", "static GrassPreparation Done("));
        Assert.Contains("TaskCreationOptions.LongRunning", code);
    }

    // ---- the glue's rules (the lead's brief of 2026-11-21): idle cost, entry points, build order, teardown, worker, wiring -------------------------------------------------------------

    static string Between(string text, string start, string end)
    {
        int a = text.IndexOf(start, StringComparison.Ordinal);
        Assert.True(a >= 0, "missing: " + start);
        int b = text.IndexOf(end, a + start.Length, StringComparison.Ordinal);
        Assert.True(b > a, "missing end marker: " + end);
        return text.Substring(a, b - a);
    }

    static string Repairs() => Code(Source("NativeGrassRepairs.cs"));

    [GlueFact("NativeGrassRepairs.cs")]
    public void TheIdleTickIsOneStaticReadAndEverythingElseIsBehindIt()
    {
        string tick = Between(Repairs(), "internal static void Tick()", "static void OnUnexpected(");
        string body = tick.Substring(tick.IndexOf('{') + 1).TrimStart();
        Assert.StartsWith("if (!_hot || _tickBroken) return;", body);
        Assert.Contains("TickCore();", tick);
        Assert.Contains("catch (Exception", tick);   // no exception ever reaches EFT through LateUpdate
    }

    [GlueFact("NativeGrassRepairs.cs")]
    public void ThePerFrameReadsHaveNoSceneSearchNoLogAndNoAllocation()
    {
        string code = Repairs();
        foreach ((string name, string start, string end) in new[]
        {
            ("TickCore", "static void TickCore()", "static bool AllScenesLoaded()"),
            ("AllScenesLoaded", "static bool AllScenesLoaded()", "static void ReadCameras()"),
            ("ReadCameras", "static void ReadCameras()", "static bool IsGameStopping()"),
            ("IsGameStopping", "static bool IsGameStopping()", "static void ReadInputs()"),
        })
        {
            string part = Between(code, start, end);
            foreach (string banned in new[] { "FindObjectsOfType", "FindObjectOfType", "FindObjectsByType", "GetRootGameObjects", "GetComponentsInChildren", "FindObjectsOfTypeAll", "GameObject.Find", " new ", "Plugin.Log" })
                Assert.False(part.Contains(banned, StringComparison.Ordinal), name + " must not contain " + banned);
        }
        // the input reader allocates nothing and logs only the one 'worker started' line
        string inputs = Between(code, "static void ReadInputs()", "static string PluginDirectory()");
        Assert.DoesNotContain(" new ", inputs);
        Assert.Single(System.Text.RegularExpressions.Regex.Matches(inputs, "Log(Debug|Warning|Error)"));
        // the phase-change line sits behind an early return: nothing is formatted while the state stays
        string change = Between(code, "static void LogPhaseChange()", "static void DoBuild()");
        Assert.True(change.IndexOf("return;", StringComparison.Ordinal) < change.IndexOf("LogDebug(", StringComparison.Ordinal));
    }

    [GlueFact("NativeGrassRepairs.cs")]
    public void EveryEntryPointHasItsOwnTryCatch()
    {
        string code = Repairs();
        foreach ((string name, string start, string end) in new[]
        {
            ("Install", "internal static void Install()", "static void Reflag()"),
            ("Reflag", "static void Reflag()", "static void UpdateHot()"),
            ("OnSceneLoaded", "static void OnSceneLoaded(", "static void OnSceneUnloaded("),
            ("OnSceneUnloaded", "static void OnSceneUnloaded(", "internal static void Tick()"),
            ("Tick", "internal static void Tick()", "static void OnUnexpected("),
            ("DoBuild", "static void DoBuild()", "static void BuildCore("),
        })
        {
            string part = Between(code, start, end);
            Assert.True(part.Contains("try", StringComparison.Ordinal) && part.Contains("catch (Exception", StringComparison.Ordinal), name + " needs a try/catch");
        }
    }

    [GlueFact("NativeGrassRepairs.cs")]
    public void AFailureLatchesTearsDownAndNeverEscapes()
    {
        string code = Repairs();
        string doBuild = Between(code, "static void DoBuild()", "static void BuildCore(");
        Assert.Contains("_state.Fail(", doBuild);
        Assert.Contains("TearDownNow(GrassTeardownReason.Failure)", doBuild);
        Assert.DoesNotContain("throw", doBuild);
        string unexpected = Between(code, "static void OnUnexpected(", "static void TickCore()");
        Assert.Contains("_state.Fail(", unexpected);
        Assert.Contains("TearDownNow(", unexpected);
        // the one Warning of the session comes from the state machine's own once-only flag
        Assert.Contains("TakeWarningDue(", code);
        string report = Between(code, "static void ReportFailures()", "static void LogPhaseChange()");
        Assert.Single(System.Text.RegularExpressions.Regex.Matches(report, @"LogWarning\("));
        Assert.True(report.IndexOf("TakeWarningDue(", StringComparison.Ordinal) < report.IndexOf("LogWarning(", StringComparison.Ordinal));
    }

    [GlueFact("NativeGrassRepairs.cs")]
    public void TheBuildOrderIsTheDesignsOrder()
    {
        string build = Between(Repairs(), "static void BuildCore(long start)", "static Shader ResolveShader(");
        string[] order =
        {
            "ResolveShader(", "ResolveNoise(", "UploadTextures(", "MakeMaterial(shader", "MakePrefab(", "b.Holder.SetActive(false)", "MoveGameObjectToScene(b.Holder", "AddManager(b.Holder, false)",
            "AddManager(b.Holder, true)", "b.Holder.SetActive(true)", "CheckManagerWokeUp(b.Normal", "DefineGameObjectAsPrefabPrototypeAtRuntime(b.Normal", "DefineGameObjectAsPrefabPrototypeAtRuntime(b.Optic",
            "ConfigurePrototype(proto)", "Feed(b, b.Normal", "Feed(b, b.Optic", "RecordClones(b)", "FixCommandBuffers(b)",
        };
        int last = -1;
        foreach (string step in order)
        {
            int at = build.IndexOf(step, StringComparison.Ordinal);
            Assert.True(at >= 0, "missing build step " + step);
            Assert.True(at > last, step + " is out of order");
            last = at;
        }
    }

    [GlueFact("NativeGrassRepairs.cs")]
    public void TheFeedFollowsGrassInitializationsCallOrderForEachManager()
    {
        string feed = Between(Repairs(), "static void Feed(", "static void RecordClones(");
        string[] order = { "SetColorBuffers(", "InitializeWithMatrix4x4Array(", "SetInstanceCount(", "DefineAndAddVariationFromArray(" };
        int last = -1;
        foreach (string call in order)
        {
            int at = feed.IndexOf(call, StringComparison.Ordinal);
            Assert.True(at > last, call);
            last = at;
        }
        Assert.Contains("NativeGrassValues.IndexBufferName", feed);
    }

    [GlueFact("NativeGrassRepairs.cs")]
    public void TheCommandBufferRuleRemovesStaleBuffersThenReattachesBothManagers()
    {
        string code = Repairs();
        string fix = Between(code, "static string FixCommandBuffers(", "static string ScanCommandBuffers(");
        Assert.True(fix.IndexOf("ScanCommandBuffers(b, true", StringComparison.Ordinal) < fix.IndexOf("InitializeCameraData()", StringComparison.Ordinal));
        Assert.Contains("b.Normal.InitializeCameraData();", fix);
        Assert.Contains("b.Optic.InitializeCameraData();", fix);
        Assert.Contains("camera.RemoveCommandBuffer(", Between(code, "static void ScanCamera(", "static bool SameBuffer("));
        Assert.Contains("NativeGrassValues.CommandBufferNames", code);
        // teardown removes the same set from both cameras
        // (since the review of 2026-11-21: from every camera the build or a replacement touched, not only the two recorded ones)
        string teardownPart = Between(code, "static void TearDownNow(", "static int RemoveNamedBuffers(");
        Assert.Contains("RemoveNamedBuffers(camera)", teardownPart);
        string collect = Between(code, "static void CollectTeardownCameras(", "static int RemoveNamedBuffers(");
        foreach (string source in new[] { "b.NormalCamera", "b.OpticCamera", "manager.Camera", "current.Camera", "current.OpticCameraManager.Camera", "hiZOcclusionGenerator" })
            Assert.Contains(source, collect);
    }

    [GlueFact("NativeGrassRepairs.cs")]
    public void OnlyTheRealShaderReachesAManager()
    {
        string code = Repairs();
        Assert.Single(System.Text.RegularExpressions.Regex.Matches(code, "Shader\\.Find\\("));   // the documented fallback of ResolveShader only
        string resolve = Between(code, "static Shader ResolveShader(", "static Texture2D ResolveNoise(");
        Assert.Contains("found.name != NativeGrassValues.ShaderName", resolve);
        Assert.True(resolve.IndexOf("bindings-row", StringComparison.Ordinal) < resolve.IndexOf("Shader.Find(", StringComparison.Ordinal));
        string material = Between(code, "static Material MakeMaterial(", "static void SetTexture(");
        Assert.Contains("new Material(shader)", material);
        // the clones are checked after the feed: a Standard clone is refused and the build fails
        string clones = Between(code, "static void RecordClones(", "static string DescribeInstances(");
        Assert.Contains("clone.shader.name != NativeGrassValues.ShaderName", clones);
        Assert.Contains("throw new InvalidOperationException", clones);
    }

    [GlueFact("NativeGrassRepairs.cs")]
    public void TeardownDestroysBothManagersTogetherAndIsCalledFromTheUnloadPath()
    {
        string code = Repairs();
        string teardown = Between(code, "static void TearDownNow(", "static int RemoveNamedBuffers(");
        Assert.Contains("DestroyObject(b.Normal)", teardown);
        Assert.Contains("DestroyObject(b.Optic)", teardown);
        Assert.StartsWith("Build b = _build;", teardown.Substring(teardown.IndexOf('{') + 1).TrimStart());   // idempotent: a second call finds nothing
        Assert.Contains("ours = !b.HadHiZNormal;", teardown);
        Assert.Contains("ours = !b.HadHiZOptic;", teardown);
        Assert.Contains("ours = managerHiZ.Contains(hiZ);", teardown);   // a replacement camera: only a component one of our managers pointed at
        foreach (string created in new[] { "b.Prototype", "b.PrefabRoot", "b.Holder", "b.MaterialLod0", "b.MaterialLod1", "b.MeshLod0", "b.MeshLod1", "b.TexArray", "b.NormalArray", "b.NormalTexture", "b.ProceduralNoise" })
            Assert.Contains("DestroyObject(" + created, teardown);
        Assert.Contains("foreach (Material clone in b.Clones)", teardown);
        // the unload path ticks at once instead of waiting for the next frame
        string unload = Between(code, "static void OnSceneUnloaded(", "internal static void Tick()");
        Assert.Contains("TickCore();", unload);
        Assert.Contains("SceneManager.sceneUnloaded += OnSceneUnloaded;", code);
        // and every teardown reason of the state machine goes through the same function
        string tickCore = Between(code, "static void TickCore()", "static bool AllScenesLoaded()");
        Assert.Contains("TearDownNow(_state.TeardownReason)", tickCore);
    }

    [GlueFact("NativeGrassRepairs.cs")]
    public void TheHolderLivesInTheMapsGrassSceneAndIsNeverPersistent()
    {
        string code = Repairs();
        Assert.Contains("SceneManager.MoveGameObjectToScene(b.Holder, grassScene);", code);
        Assert.Contains("SceneManager.MoveGameObjectToScene(root, scene);", code);
        Assert.Contains("map.GrassSceneName", code);
        Assert.DoesNotContain("DontDestroyOnLoad", code);
        Assert.DoesNotContain("HideFlags.DontSave", code);
    }

    [GlueFact("NativeGrassRepairs.cs")]
    public void TheTickNeverCallsAnythingThatBlocksTheGameThread()
    {
        string code = Repairs();
        Assert.DoesNotContain("EnsureVerified", code);
        Assert.DoesNotContain("WaitForResult", code);
        Assert.DoesNotContain(".Wait(", code);
        Assert.Contains("GrassPreparation.Start(", code);
        // the worker's result is polled, with a watchdog
        Assert.Contains("WorkerTimeoutSeconds", code);
        // the glue itself reads no file: the art source does, on its worker (it only builds the path of the bundle)
        foreach (string banned in new[] { "File.", "FileStream", "ReadAllBytes", "Directory.", "StreamReader", "Stream " })
            Assert.False(code.Contains(banned, StringComparison.Ordinal), "NativeGrassRepairs.cs must not contain " + banned);
    }

    [GlueFact("NativeGrassRepairs.cs")]
    public void TheKeyComesFromTheOneResolverTheMapsBundlesUse()
    {
        string code = Repairs();
        // a non-blocking peek (a cache hit only): the grass never reaches the gate or derives on the game thread
        Assert.Contains("BundleKeyResolver.Default.TryPeekKey(mapKey, out key)", code);
        Assert.Single(System.Text.RegularExpressions.Regex.Matches(code, @"TryPeekKey\("));
        Assert.DoesNotContain("TryGetKey(", code.Replace("BundleKeyResolver.TryGetKey", ""));
        // handed to the art source as a method group: the game thread asks, the worker never does
        Assert.Contains("GrassPreparation.Start(map.Key, path, DiskGrassPackFiles.Instance, ResolveKey, SelectProvider, _artKind)", code);
        Assert.Contains("Path.Combine(Path.Combine(PluginDirectory(), \"bundles\"), map.GrassBundleFileName)", Source("NativeGrassRepairs.cs"));
        // the key is never put in a log line, a field or a string
        string resolve = Between(code, "static bool ResolveKey(", "static ICryptoProvider SelectProvider()");
        Assert.DoesNotContain("Log", resolve);
        Assert.DoesNotContain("ToString", resolve);
    }

    [GlueFact("NativeGrassRepairs.cs")]
    public void AMapsRefusalIsPerMapAndNeverASessionLatch()
    {
        string code = Repairs();
        string raw = Source("NativeGrassRepairs.cs");
        string refuse = Between(raw, "static void RefuseMap(", "static void ReportFailures()");
        Assert.DoesNotContain("_state.Fail(", refuse);                       // no session latch
        Assert.Contains("_refused[key] = reason;", refuse);
        Assert.Contains("DropPreparation();", refuse);
        Assert.Contains("Reflag();", refuse);
        // exactly one Warning for the map, and a missing pack is Debug only
        Assert.Single(System.Text.RegularExpressions.Regex.Matches(refuse, @"LogWarning\("));
        Assert.True(refuse.IndexOf("if (reason == \"pack-missing\")", StringComparison.Ordinal) < refuse.IndexOf("LogWarning(", StringComparison.Ordinal));
        // the tick routes a worker refusal there, and the flags read the per-map record
        Assert.Contains("RefuseMap(_in.InputError);", Between(code, "static void TickCore()", "static bool AllScenesLoaded()"));
        Assert.Contains("_refused.ContainsKey(owned.Key)", Between(code, "static void Reflag()", "static void UpdateHot()"));
        // only an exception of the build or the tick latches the session
        Assert.Equal(2, System.Text.RegularExpressions.Regex.Matches(code, @"_state\.Fail\(").Count);
        // a later raid of a refused map says so at Debug
        Assert.Contains("its pack was refused earlier in this session", Between(raw, "static void OnSceneLoaded(", "static void OnSceneUnloaded("));
    }

    [GlueFact("NativeGrassRepairs.cs")]
    public void TheArtBuffersAreDroppedRightAfterTheUpload()
    {
        string code = Repairs();
        string upload = Between(code, "static string UploadTextures(", "static string DescribeArray(");
        Assert.True(upload.IndexOf("CreatePlainTexture(", StringComparison.Ordinal) < upload.IndexOf("inputs.DropArt();", StringComparison.Ordinal));
        string doBuild = Between(code, "static void DoBuild()", "static void BuildCore(");
        Assert.Contains("DropPreparation();", doBuild);
    }

    [GlueFact("NativeGrassRepairs.cs")]
    public void TheSeasonNeverReachesTheGrass()
    {
        string code = Repairs();
        foreach (string banned in new[] { "Seasons.", "ESeason", "SetSeasonsMaterials", "SeasonsController", "SeasonsMaterialsFixer", "ESeasonStatus" })
            Assert.False(code.Contains(banned, StringComparison.Ordinal), "NativeGrassRepairs.cs must not read the season: " + banned);
        Assert.Contains("NativeGrassColorModel.SummerLists", code);
    }

    [GlueFact("NativeGrassRepairs.cs")]
    public void ThePluginWiresTheInstallAndTheTickOnceAndTheNoticesTickStaysLast()
    {
        string plugin = Code(Source("Plugin.cs"));
        Assert.Single(System.Text.RegularExpressions.Regex.Matches(plugin, @"NativeGrassRepairs\.Install\(\);"));
        Assert.Single(System.Text.RegularExpressions.Regex.Matches(plugin, @"NativeGrassRepairs\.Tick\(\);"));
        string late = Between(plugin, "void LateUpdate()", "void Awake()");
        Assert.True(late.IndexOf("NativeGrassRepairs.Tick();", StringComparison.Ordinal) < late.IndexOf("MapNotices.Tick();", StringComparison.Ordinal));
        Assert.True(late.TrimEnd().EndsWith("MapNotices.Tick();\n        }") || late.Replace("\r\n", "\n").TrimEnd().EndsWith("MapNotices.Tick();\n        }"), "MapNotices.Tick() must stay the last statement");
        // no Harmony hook is needed: the tick and two scene events carry the whole lifecycle
        foreach (string file in new[] { "HarmonyPatches.cs" })
            Assert.DoesNotContain("NativeGrass", Source(file));
    }

    [GlueFact("NativeGrassRepairs.cs")]
    public void TheKeysAreBoundThroughSettingsWithTheModelsNamesAndDefaults()
    {
        string code = Repairs();
        foreach (string key in new[] { "NativeGrassKeys.Grass, NativeGrassKeys.GrassDefault", "NativeGrassKeys.Drawing, NativeGrassKeys.DrawingDefault", "NativeGrassKeys.ArtSource, NativeGrassKeys.ArtSourceDefault", "NativeGrassKeys.UploadPath, NativeGrassKeys.UploadPathDefault" })
            Assert.Contains(key, code);
        Assert.Equal(4, System.Text.RegularExpressions.Regex.Matches(code, @"Plugin\.Instance\.Settings\.Bind\(").Count);
        Assert.Contains("NativeGrassKeys.SectionOf(map.Key)", code);
        Assert.Contains("if (!map.HasNativeGrass) continue;", code);          // a map without native grass binds nothing
        // the live keys re-read the flags; the Grass key is a bool and the wanted flag is key AND not refused
        Assert.Contains("keys.Grass.SettingChanged += (_, __) => Reflag();", code);
        Assert.Contains("_wanted = keys.Grass.Value && (owned == null || !_refused.ContainsKey(owned.Key));", code);
    }

    [GlueFact("NativeGrassRepairs.cs")]
    public void AMipLimitIsNeverMistakenForAFailedUploadPath()
    {
        string code = Repairs();
        Assert.Contains("class GrassMipLimitException", code);
        Assert.Contains("throw new GrassMipLimitException(", code);
        Assert.Contains("!(e is GrassMipLimitException)", Between(code, "static string UploadTextures(", "static string DescribeArray("));
    }

    [GlueFact("NativeGrassRepairs.cs")]
    public void ACameraReplacementIsSeenOnTheOpticCameraToo()
    {
        string cameras = Between(Repairs(), "static void ReadCameras()", "static bool IsGameStopping()");
        Assert.Contains("opticId", cameras);
        Assert.Contains("_lastOpticCameraId", cameras);
    }

    [GlueFact("NativeGrassRepairs.cs")]
    public void AFailingTickGoesQuietUntilTheNextRaid()
    {
        string code = Repairs();
        Assert.Contains("_tickBroken = true;", Between(code, "static void OnUnexpected(", "static void TickCore()"));
        Assert.Contains("_tickBroken = false;", Between(code, "static void OnSceneLoaded(", "static void OnSceneUnloaded("));
    }

    [GlueFact("NativeGrassRepairs.cs")]
    public void TheHiZBlitMaterialIsFreedAndTheLayerReachIsLoggedInsideTheBuildOnly()
    {
        string code = Repairs();
        Assert.Contains("HiZBlitMaterialField", Between(code, "static bool DestroyHiZ(", "static void DestroyObject("));
        string layers = Between(code, "static string LayerLine(", "static Shader ResolveShader(");
        Assert.Contains("cullingMask", layers);
        Assert.Contains("FindObjectsOfType<Light>", layers);
        Assert.Contains("LayerLine(b)", Between(code, "static void BuildCore(long start)", "static string LayerLine("));
    }
}
