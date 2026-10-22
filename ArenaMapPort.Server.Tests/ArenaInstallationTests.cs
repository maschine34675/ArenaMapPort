using System.Diagnostics;
using System.Globalization;
using System.Runtime.InteropServices;
using System.Text;
using System.Text.Json;
using ArenaMapPort.Shared;
using Microsoft.Win32.SafeHandles;
using Xunit;

namespace ArenaMapPort.Server.Tests;

/// <summary>Runs only on Windows (the registry reader and the sparse-file helper are Windows features).</summary>
internal sealed class WindowsFactAttribute : FactAttribute
{
    public WindowsFactAttribute()
    {
        if (!OperatingSystem.IsWindows())
            Skip = "Windows only";
    }
}

internal static class RealArena
{
    public const string Folder = @"D:\Battlestate Games\Escape from Tarkov Arena";
    public const string UninstallKey =
        @"HKEY_LOCAL_MACHINE\SOFTWARE\WOW6432Node\Microsoft\Windows\CurrentVersion\Uninstall\EscapeFromTarkovArena_live";
}

/// <summary>Runs only when the developer's real Arena installation is present.</summary>
internal sealed class RealInstallFactAttribute : FactAttribute
{
    public RealInstallFactAttribute()
    {
        if (!OperatingSystem.IsWindows() || !File.Exists(Path.Combine(RealArena.Folder, "ConsistencyInfo")))
            Skip = "no Arena installation at " + RealArena.Folder;
    }
}

/// <summary>Runs only when the BSG launcher's registry entry points at an existing folder.</summary>
internal sealed class RealRegistryFactAttribute : FactAttribute
{
    public RealRegistryFactAttribute()
    {
        if (!OperatingSystem.IsWindows())
        {
            Skip = "Windows only";
            return;
        }
        string? location = new ReflectionRegistryProbe().GetValue(RealArena.UninstallKey, "InstallLocation");
        if (string.IsNullOrWhiteSpace(location) || !File.Exists(Path.Combine(location, "ConsistencyInfo")))
            Skip = "Arena is not registered in the registry on this machine";
    }
}

internal static class SparseFiles
{
    const uint FsctlSetSparse = 0x900C4;

    [DllImport("kernel32.dll", SetLastError = true)]
    static extern bool DeviceIoControl(SafeFileHandle device, uint code, IntPtr inBuffer, uint inSize, IntPtr outBuffer,
        uint outSize, out uint returned, IntPtr overlapped);

    public static bool MarkSparse(FileStream stream) =>
        DeviceIoControl(stream.SafeFileHandle, FsctlSetSparse, IntPtr.Zero, 0, IntPtr.Zero, 0, out _, IntPtr.Zero);
}

/// <summary>
/// Runs only where a file can be flagged sparse: FileStream.SetLength alone ALLOCATES on NTFS (measured on the VM:
/// 8 GB of SetLength took 8.6 GB of free space), so a 40 GB synthetic install needs the sparse flag first.
/// </summary>
internal sealed class SparseFileFactAttribute : FactAttribute
{
    public SparseFileFactAttribute()
    {
        if (!OperatingSystem.IsWindows())
        {
            Skip = "Windows only";
            return;
        }
        string probe = Path.Combine(Path.GetTempPath(), "arenamapport-sparse-probe-" + Guid.NewGuid().ToString("N"));
        try
        {
            using var stream = new FileStream(probe, FileMode.CreateNew, FileAccess.ReadWrite);
            if (!SparseFiles.MarkSparse(stream))
                Skip = "the temp volume does not support sparse files";
        }
        catch (Exception ex)
        {
            Skip = "sparse probe failed: " + ex.Message;
        }
        finally
        {
            try { File.Delete(probe); }
            catch (Exception) { /* best effort */ }
        }
    }
}

/// <summary>
/// ArenaInstallation (Shared): the manifest reader, the verification rules, the registry lookup and the precedence of
/// an explicit override over the registry. The detector is a good-faith installation gate, so the tests pin that every
/// rule has its own reason and that a wrong override never falls back to the registry.
/// </summary>
public sealed class ArenaInstallationTests : IDisposable
{
    const string Version = "0.6.1.0.48206";
    const string Uninstall = @"HKEY_LOCAL_MACHINE\SOFTWARE\WOW6432Node\Microsoft\Windows\CurrentVersion\Uninstall\";
    const string UninstallPlain = @"HKEY_LOCAL_MACHINE\SOFTWARE\Microsoft\Windows\CurrentVersion\Uninstall\";
    const string LevelDir = @"EscapeFromTarkovArena_Data\";

    // The default floors (5,000 entries, 40 GB, 100 level files, 50 MB GameAssembly.dll) scaled to the synthetic install.
    static readonly ArenaInstallationFloors Small = new(minEntries: 8, minTotalBytes: 3000, minLevelEntries: 2, minGameAssemblyBytes: 500);

    readonly List<string> _tempDirs = new();

    public void Dispose()
    {
        foreach (string dir in _tempDirs)
        {
            try { Directory.Delete(dir, true); }
            catch (Exception) { /* best effort */ }
        }
    }

    // ---- the manifest reader ----------------------------------------------------------------------------------

    const string Sample =
        """{"Version":"0.6.1.0.48206","Entries":[{"Path":"EscapeFromTarkovArena_Data\\StreamingAssets\\a.bundle","Size":123456,"Checksum":987654},{"Path":"GameAssembly.dll","Size":654321,"Checksum":-1357911,"IsCritical":true}]}""";

    [Fact]
    public void Manifest_ParsesTheShippedShapeWithEscapedBackslashes()
    {
        ArenaManifest manifest = ArenaManifest.Parse(Sample);
        Assert.Equal("0.6.1.0.48206", manifest.Version);
        Assert.Equal(2, manifest.Entries.Count);
        Assert.Equal(@"EscapeFromTarkovArena_Data\StreamingAssets\a.bundle", manifest.Entries[0].Path);
        Assert.Equal(123456L, manifest.Entries[0].Size);
        Assert.Equal("GameAssembly.dll", manifest.Entries[1].Path);
        Assert.Equal(654321L, manifest.Entries[1].Size);
    }

    [Fact]
    public void Manifest_DecodesEscapedQuotesSlashesAndUnicode()
    {
        ArenaManifest manifest = ArenaManifest.Parse("""{"Version":"v","Entries":[{"Path":"a\\b\"c\u0041\/d\t","Size":1}]}""");
        Assert.Equal("a\\b\"cA/d\t", manifest.Entries[0].Path);
    }

    [Fact]
    public void Manifest_AcceptsAnyKeyOrderWhitespaceUnknownKeysAndABom()
    {
        const string text = "\uFEFF {\n\t\"Entries\" : [ { \"IsCritical\" : false , \"Checksum\" : -7 , \"Size\" : 5 , \"Path\" : \"x\\\\y\" ,"
            + " \"Meta\" : { \"k\" : [ 1 , 2.5e-3 , true , null , \"s\" , { } , [ ] ] } } ] ,\n\t\"Version\" : \"9.9\"\n}\r\n";
        ArenaManifest manifest = ArenaManifest.Parse(text);
        Assert.Equal("9.9", manifest.Version);
        Assert.Single(manifest.Entries);
        Assert.Equal(@"x\y", manifest.Entries[0].Path);
        Assert.Equal(5L, manifest.Entries[0].Size);
    }

    [Fact]
    public void Manifest_AcceptsAnEmptyEntryList()
    {
        ArenaManifest manifest = ArenaManifest.Parse("""{"Version":"v","Entries":[]}""");
        Assert.Empty(manifest.Entries);
    }

    [Theory]
    [InlineData("""{"Entries":[]}""", "Version")]
    [InlineData("""{"Version":"v"}""", "Entries")]
    [InlineData("{}", "Version")]
    [InlineData("""{"Version":"v","Entries":[{"Size":1}]}""", "Path")]
    [InlineData("""{"Version":"v","Entries":[{"Path":"p"}]}""", "Size")]
    [InlineData("""{"Version":"v","Entries":[{"Path":"","Size":1}]}""", "empty Path")]
    public void Manifest_RejectsMissingKeys(string text, string expectedInMessage)
    {
        var ex = Assert.Throws<FormatException>(() => ArenaManifest.Parse(text));
        Assert.Contains(expectedInMessage, ex.Message);
    }

    [Fact]
    public void Manifest_RejectsEveryTruncationOfAValidManifest()
    {
        for (int length = 0; length < Sample.Length; length++)
        {
            string truncated = Sample.Substring(0, length);
            Assert.Throws<FormatException>(() => ArenaManifest.Parse(truncated));
        }
    }

    [Theory]
    [InlineData("\"12\"")]
    [InlineData("abc")]
    [InlineData("1.5")]
    [InlineData("1e3")]
    [InlineData("-1")]
    [InlineData("99999999999999999999")]
    [InlineData("null")]
    [InlineData("")]
    public void Manifest_RejectsANonNumericOrOutOfRangeSize(string size)
    {
        Assert.Throws<FormatException>(() => ArenaManifest.Parse("{\"Version\":\"v\",\"Entries\":[{\"Path\":\"p\",\"Size\":" + size + "}]}"));
    }

    [Theory]
    [InlineData("")]
    [InlineData("null")]
    [InlineData("[]")]
    [InlineData("""{"Version":"v","Entries":[{"Path":"p","Size":1},]}""")]
    [InlineData("""{"Version":"v" "Entries":[]}""")]
    [InlineData("""{"Version":"v","Entries":[]} trailing""")]
    [InlineData("""{"Version":"v","Entries":[]}{}""")]
    [InlineData("""{"Version":"v","Entries":[{"Path":"unterminated,"Size":1}]}""")]
    [InlineData("""{"Version":"a\qb","Entries":[]}""")]
    [InlineData("""{"Version":"a\u12","Entries":[]}""")]
    [InlineData("""{"Version":"a\uZZZZb","Entries":[]}""")]
    [InlineData("{\"Version\":\"a\tb\",\"Entries\":[]}")]
    [InlineData("""{"Version":"v","Entries":{}}""")]
    [InlineData("""{"Version":5,"Entries":[]}""")]
    [InlineData("""{"Version":"v","Other":[1,2,,3],"Entries":[]}""")]
    [InlineData("""{"Version":"v","Other":{"a":tru},"Entries":[]}""")]
    [InlineData("""{"Version":"v","Other":{"a":1,},"Entries":[]}""")]
    [InlineData("""{"Version":"v","Entries":[{"Path":"p","Size":1,"Checksum":}]}""")]
    [InlineData("""{Version:"v","Entries":[]}""")]
    public void Manifest_RejectsMalformedInput(string text)
    {
        Assert.Throws<FormatException>(() => ArenaManifest.Parse(text));
    }

    [Fact]
    public void Manifest_RejectsRunawayNestingInAnUnknownKey()
    {
        string deep = new string('[', 100) + new string(']', 100);
        Assert.Throws<FormatException>(() => ArenaManifest.Parse("{\"Version\":\"v\",\"Other\":" + deep + ",\"Entries\":[]}"));
    }

    // ---- Verify: one rule, one reason ---------------------------------------------------------------------------

    [Fact]
    public void Verify_AcceptsAHealthyInstallAndSkipsTheManifestsOwnEntry()
    {
        Install install = MakeInstall(); // the manifest lists "ConsistencyInfo" with a size that cannot match
        ArenaInstallationResult result = ArenaInstallation.Verify(install.Dir, "override", null, Small);

        Assert.True(result.Verified, result.Describe());
        Assert.Null(result.Reason);
        Assert.Equal(install.Dir, result.InstallPath);
        Assert.Equal("override", result.Source);
        Assert.Equal(Version, result.ManifestVersion);
        Assert.Equal(install.Listed.Count, result.EntriesChecked);
        Assert.Equal(install.Listed.Sum(entry => entry.Size), result.TotalBytes);
        Assert.Equal(0, result.MismatchCount);
        Assert.Empty(result.Mismatches);
    }

    [Fact]
    public void Verify_ToleratesADifferentSizeForBattlEyeAndLoggingConfigButNotTheirAbsence()
    {
        Install install = MakeInstall();
        AddFile(install, @"BattlEye\BEClient_x64.dll", 300);
        AddFile(install, "Logging.config", 40);
        WriteManifest(install);
        File.WriteAllBytes(Path.Combine(install.Dir, "BattlEye", "BEClient_x64.dll"), new byte[777]); // BattlEye updated itself
        File.WriteAllBytes(Path.Combine(install.Dir, "Logging.config"), new byte[41]); // the user edited it

        ArenaInstallationResult result = ArenaInstallation.Verify(install.Dir, "override", null, Small);
        Assert.True(result.Verified, result.Describe());
        Assert.Equal(install.Listed.Count, result.EntriesChecked);
        Assert.Equal(0, result.MismatchCount);

        File.Delete(Path.Combine(install.Dir, "BattlEye", "BEClient_x64.dll"));
        result = ArenaInstallation.Verify(install.Dir, "override", null, Small);
        Assert.False(result.Verified);
        Assert.Contains(@"BattlEye\BEClient_x64.dll: missing", result.Mismatches);
    }

    [Theory]
    [InlineData(@"BattlEye\BEClient_x64.dll", true)]
    [InlineData(@"battleye\Text\en.ini", true)]
    [InlineData("Logging.config", true)]
    [InlineData("logging.CONFIG", true)]
    [InlineData("GameAssembly.dll", false)]
    [InlineData(@"EscapeFromTarkovArena_Data\level41", false)]
    [InlineData("BattlEyeX.dll", false)]
    [InlineData("", false)]
    public void IsSizeTolerant_CoversOnlyBattlEyeAndLoggingConfig(string logical, bool expected) =>
        Assert.Equal(expected, ArenaInstallation.IsSizeTolerant(logical));

    [Fact]
    public void Verify_SkipsEntriesAtTheClassicWindowsPathLimitAndCountsThem()
    {
        Install install = MakeInstall();
        string deep = LevelDir + @"StreamingAssets\" + string.Join(@"\", Enumerable.Repeat(new string('x', 50), 6)) + @"\long.bundle";
        Assert.True(Path.Combine(install.Dir, deep).Length >= ArenaInstallation.MaxPathLength);
        var listed = install.Listed.Concat(new[] { (deep, 10L) }).ToList(); // never created on disk
        WriteManifest(install, entries: listed);

        ArenaInstallationResult result = ArenaInstallation.Verify(install.Dir, "override", null, Small);
        Assert.True(result.Verified, result.Describe());
        Assert.Equal(1, result.LongPathsSkipped);
        Assert.Equal(install.Listed.Count, result.EntriesChecked);
        Assert.EndsWith("GB verified, 1 long path skipped)", result.Describe());
    }

    [Fact]
    public void Verify_ComparesTheManifestsOwnPathCaseInsensitively()
    {
        Install install = MakeInstall();
        WriteManifest(install, ownEntryPath: "consistencyinfo");
        Assert.True(ArenaInstallation.Verify(install.Dir, "override", null, Small).Verified);
    }

    [Fact]
    public void Verify_ReportsAMissingFolder()
    {
        string missing = Path.Combine(Path.GetTempPath(), "arenamapport-no-such-" + Guid.NewGuid().ToString("N"));
        ArenaInstallationResult result = ArenaInstallation.Verify(missing, "override", null, Small);
        Assert.False(result.Verified);
        Assert.Contains("installation folder does not exist", result.Reason);
        Assert.Contains(missing, result.Reason);
        Assert.Equal(missing, result.InstallPath);
    }

    [Fact]
    public void Verify_ReportsAFileInsteadOfAFolder()
    {
        string file = Path.Combine(NewTempDir(), "EscapeFromTarkovArena.exe");
        File.WriteAllText(file, "x");
        ArenaInstallationResult result = ArenaInstallation.Verify(file, "override", null, Small);
        Assert.False(result.Verified);
        Assert.Contains("is a file, not the installation folder", result.Reason);
    }

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("\"\"")]
    public void Verify_ReportsAnEmptyPath(string path)
    {
        ArenaInstallationResult result = ArenaInstallation.Verify(path, "override", null, Small);
        Assert.False(result.Verified);
        Assert.Contains("No installation folder was given", result.Reason);
        Assert.Null(result.InstallPath);
    }

    [Fact]
    public void Verify_ReportsAnInvalidPathWithoutThrowing()
    {
        ArenaInstallationResult result = ArenaInstallation.Verify("C:\\bad\0path", "override", null, Small);
        Assert.False(result.Verified);
        Assert.False(string.IsNullOrEmpty(result.Reason));
    }

    [Fact]
    public void Verify_ReportsAMissingExe()
    {
        string dir = NewTempDir();
        ArenaInstallationResult result = ArenaInstallation.Verify(dir, "override", null, Small);
        Assert.False(result.Verified);
        Assert.Contains("does not contain EscapeFromTarkovArena.exe", result.Reason);
    }

    [Fact]
    public void Verify_ReportsAMissingManifest()
    {
        Install install = MakeInstall();
        File.Delete(Path.Combine(install.Dir, "ConsistencyInfo"));
        ArenaInstallationResult result = ArenaInstallation.Verify(install.Dir, "override", null, Small);
        Assert.False(result.Verified);
        Assert.Contains("does not contain the launcher manifest ConsistencyInfo", result.Reason);
    }

    [Fact]
    public void Verify_ReportsAnUnparsableManifest()
    {
        Install install = MakeInstall();
        File.WriteAllText(Path.Combine(install.Dir, "ConsistencyInfo"), "not json at all");
        ArenaInstallationResult result = ArenaInstallation.Verify(install.Dir, "override", null, Small);
        Assert.False(result.Verified);
        Assert.Contains("ConsistencyInfo is not valid", result.Reason);
    }

    [Fact]
    public void Verify_ReportsATruncatedManifest()
    {
        Install install = MakeInstall();
        string path = Path.Combine(install.Dir, "ConsistencyInfo");
        string text = File.ReadAllText(path);
        File.WriteAllText(path, text.Substring(0, text.Length / 2));
        ArenaInstallationResult result = ArenaInstallation.Verify(install.Dir, "override", null, Small);
        Assert.False(result.Verified);
        Assert.Contains("is not valid", result.Reason);
    }

    [Fact]
    public void Verify_ReportsAManifestWithoutAVersion()
    {
        Install install = MakeInstall();
        WriteManifest(install, version: "  ");
        ArenaInstallationResult result = ArenaInstallation.Verify(install.Dir, "override", null, Small);
        Assert.False(result.Verified);
        Assert.Contains("has no version", result.Reason);
    }

    [Fact]
    public void Verify_ReportsAManifestWithoutEntries()
    {
        Install install = MakeInstall();
        WriteManifest(install, entries: Array.Empty<(string, long)>(), withOwnEntry: false);
        ArenaInstallationResult result = ArenaInstallation.Verify(install.Dir, "override", null, Small);
        Assert.False(result.Verified);
        Assert.Contains("lists no files", result.Reason);
        Assert.Equal(Version, result.ManifestVersion);
    }

    [Fact]
    public void Verify_FloorTooFewEntries()
    {
        Install install = MakeInstall();
        var floors = new ArenaInstallationFloors(100, 4000, 2, 500);
        ArenaInstallationResult result = ArenaInstallation.Verify(install.Dir, "override", null, floors);
        Assert.False(result.Verified);
        Assert.Contains("lists only " + (install.Listed.Count + 1) + " files", result.Reason);
        Assert.Contains("at least 100", result.Reason);
    }

    [Fact]
    public void Verify_FloorTooLittleListedSize()
    {
        Install install = MakeInstall();
        var floors = new ArenaInstallationFloors(8, 40_000_000_000L, 2, 500);
        ArenaInstallationResult result = ArenaInstallation.Verify(install.Dir, "override", null, floors);
        Assert.False(result.Verified);
        Assert.Contains("GB of files; an Arena installation has at least 40.0 GB", result.Reason);
    }

    [Fact]
    public void Verify_FloorTooFewLevelFiles()
    {
        Install install = MakeInstall();
        var floors = new ArenaInstallationFloors(8, 4000, 50, 500);
        ArenaInstallationResult result = ArenaInstallation.Verify(install.Dir, "override", null, floors);
        Assert.False(result.Verified);
        Assert.Contains("lists only 3 level files", result.Reason);
    }

    [Fact]
    public void Verify_FloorGameAssemblyTooSmall()
    {
        Install install = MakeInstall();
        var floors = new ArenaInstallationFloors(8, 4000, 2, 10_000);
        ArenaInstallationResult result = ArenaInstallation.Verify(install.Dir, "override", null, floors);
        Assert.False(result.Verified);
        Assert.Contains("does not list GameAssembly.dll with at least", result.Reason);
    }

    [Fact]
    public void Verify_FloorGameAssemblyNotListed()
    {
        Install install = MakeInstall();
        WriteManifest(install, entries: install.Listed.Where(entry => entry.Path != "GameAssembly.dll").ToList());
        ArenaInstallationResult result = ArenaInstallation.Verify(install.Dir, "override", null, Small);
        Assert.False(result.Verified);
        Assert.Contains("does not list GameAssembly.dll", result.Reason);
    }

    [Fact]
    public void Verify_FloorExeNotListed()
    {
        Install install = MakeInstall();
        WriteManifest(install, entries: install.Listed.Where(entry => entry.Path != "EscapeFromTarkovArena.exe").ToList());
        ArenaInstallationResult result = ArenaInstallation.Verify(install.Dir, "override", null, Small);
        Assert.False(result.Verified);
        Assert.Contains("does not list EscapeFromTarkovArena.exe", result.Reason);
    }

    [Fact]
    public void Verify_ProductionFloorsRejectAHandMadeManifest()
    {
        Install install = MakeInstall();
        ArenaInstallationResult result = ArenaInstallation.Verify(install.Dir); // public entry point, production floors
        Assert.False(result.Verified);
        Assert.Equal("override", result.Source);
        Assert.Contains("lists only " + (install.Listed.Count + 1) + " files; an Arena installation has at least 5,000", result.Reason);
    }

    [Fact]
    public void Verify_ProductionFloorsAreThePublishedOnes()
    {
        Assert.Equal(5000, ArenaInstallationFloors.Default.MinEntries);
        Assert.Equal(40_000_000_000L, ArenaInstallationFloors.Default.MinTotalBytes);
        Assert.Equal(100, ArenaInstallationFloors.Default.MinLevelEntries);
        Assert.Equal(50_000_000L, ArenaInstallationFloors.Default.MinGameAssemblyBytes);
    }

    [Fact]
    public void Verify_ReportsOneMissingFileAndOneWrongSizeWithTheirPaths()
    {
        Install install = MakeInstall();
        File.Delete(Path.Combine(install.Dir, "EscapeFromTarkovArena_Data", "level1"));
        string bundle = Path.Combine(install.Dir, "EscapeFromTarkovArena_Data", "StreamingAssets", "Windows", "a.bundle");
        using (var stream = new FileStream(bundle, FileMode.Open, FileAccess.Write))
            stream.SetLength(1999);

        ArenaInstallationResult result = ArenaInstallation.Verify(install.Dir, "override", null, Small);

        Assert.False(result.Verified);
        Assert.Equal(2, result.MismatchCount);
        Assert.Equal(2, result.Mismatches.Count);
        Assert.Contains(@"EscapeFromTarkovArena_Data\level1: missing", result.Mismatches);
        Assert.Contains(@"EscapeFromTarkovArena_Data\StreamingAssets\Windows\a.bundle: size 1999 != 2000", result.Mismatches);
        Assert.Equal(install.Listed.Count, result.EntriesChecked);
        Assert.Contains("2 of " + install.Listed.Count + " files", result.Reason);
        Assert.Equal(install.Listed.Sum(entry => entry.Size) - 150 - 2000, result.TotalBytes);
        Assert.Equal(Version, result.ManifestVersion);
    }

    [Fact]
    public void Verify_CountsEveryMismatchButReportsAtMostEight()
    {
        Install install = MakeInstall(extraFiles: 15);
        for (int i = 0; i < 12; i++)
            File.Delete(Path.Combine(install.Dir, "EscapeFromTarkovArena_Data", "StreamingAssets", "Windows", "extra" + i + ".bundle"));

        ArenaInstallationResult result = ArenaInstallation.Verify(install.Dir, "override", null, Small);

        Assert.False(result.Verified);
        Assert.Equal(12, result.MismatchCount);
        Assert.Equal(8, result.Mismatches.Count);
        Assert.All(result.Mismatches, mismatch => Assert.EndsWith(": missing", mismatch));
        Assert.Contains("12 of ", result.Reason);
    }

    [Fact]
    public void Verify_RejectsListedPathsThatLeaveTheInstallation()
    {
        Install install = MakeInstall();
        File.WriteAllText(Path.Combine(Path.GetDirectoryName(install.Dir)!, "outside.bin"), "x");
        var entries = install.Listed.ToList();
        entries.Add((@"..\outside.bin", 1));
        entries.Add((@"C:\Windows\notepad.exe", 1));
        WriteManifest(install, entries: entries);

        ArenaInstallationResult result = ArenaInstallation.Verify(install.Dir, "override", null, Small);

        Assert.False(result.Verified);
        Assert.Equal(2, result.MismatchCount);
        Assert.All(result.Mismatches, mismatch => Assert.EndsWith(": invalid path", mismatch));
    }

    /// <summary>
    /// A segment of dots and spaces is trimmed to ".." by legacy path normalisation (.NET Framework, the Mono inside EFT), so
    /// ".. \x" would resolve OUTSIDE the installation there. The rule is on the text, so these tests hold on every runtime:
    /// each spelling is refused as an invalid path whether or not the file outside exists.
    /// </summary>
    [Theory]
    [InlineData(@"..\outside.bin")]
    [InlineData("../outside.bin")]
    [InlineData(@".. \outside.bin")]
    [InlineData(".. /outside.bin")]
    [InlineData(@"..  \outside.bin")]
    [InlineData(@"... \outside.bin")]
    [InlineData(@"...\outside.bin")]
    [InlineData(@"sub\.. \..\outside.bin")]
    [InlineData(@"sub\.. \.. \outside.bin")]
    [InlineData(@"sub\..\..\outside.bin")]
    [InlineData(@"sub\..")]
    [InlineData(@".\EscapeFromTarkovArena.exe")]
    [InlineData(@"sub\.\x")]
    [InlineData(@"sub\ \x")]
    [InlineData(@"sub\\x")]
    [InlineData(@"EscapeFromTarkovArena_Data\")]
    [InlineData(@"\outside.bin")]
    [InlineData(@"\\server\share\outside.bin")]
    [InlineData(@"C:outside.bin")]
    [InlineData(@"C:\Windows\notepad.exe")]
    [InlineData("EscapeFromTarkovArena.exe:stream")]
    public void Verify_RefusesEveryEscapingOrAliasingSpelling(string listedPath)
    {
        Install install = MakeInstall();
        File.WriteAllText(Path.Combine(Path.GetDirectoryName(install.Dir)!, "outside.bin"), "12345");
        var entries = install.Listed.ToList();
        entries.Add((listedPath, 5));
        WriteManifest(install, entries: entries);

        ArenaInstallationResult result = ArenaInstallation.Verify(install.Dir, "override", null, Small);

        Assert.False(result.Verified);
        Assert.Equal(1, result.MismatchCount);
        Assert.Equal(listedPath.Replace('/', '\\') + ": invalid path", Assert.Single(result.Mismatches));
    }

    [Theory]
    [InlineData("EscapeFromTarkovArena.exe")]
    [InlineData(@"EscapeFromTarkovArena_Data\StreamingAssets\Windows\a.bundle")]
    [InlineData(@"BattlEye\Text\zh-TW.ini")]
    [InlineData(@"my folder\file name.dll")]
    [InlineData(@"a\..b\c")]
    [InlineData(@"a\b.c.d")]
    [InlineData(@"a\.hidden")]
    public void PlainRelativePath_AcceptsOrdinaryNames(string path)
    {
        Assert.True(ArenaInstallation.IsPlainRelativePath(path), path);
    }

    [Theory]
    [InlineData("")]
    [InlineData(".")]
    [InlineData("..")]
    [InlineData("...")]
    [InlineData(".. ")]
    [InlineData(" ")]
    [InlineData(@"..\x")]
    [InlineData(@".. \x")]
    [InlineData(@"a\.. \..\x")]
    [InlineData(@"a\.. ")]
    [InlineData(@"...\x")]
    [InlineData(@"a\.\x")]
    [InlineData(@"a\ \x")]
    [InlineData(@"a\\x")]
    [InlineData(@"a\")]
    [InlineData(@"\a")]
    [InlineData(@"\\a\b")]
    [InlineData(@"C:\a")]
    [InlineData("C:a")]
    [InlineData("a:b")]
    public void PlainRelativePath_RefusesRootedDottedAndEmptySegments(string path)
    {
        Assert.False(ArenaInstallation.IsPlainRelativePath(path), path);
    }

    [Fact]
    public void ResolveInside_RequiresTheResolvedPathToStayUnderTheInstallationFolder()
    {
        string root = Path.GetPathRoot(Path.GetTempPath())!;
        string dir = Path.Combine(root, "arenamapport-inst");
        string dirPrefix = dir + Path.DirectorySeparatorChar;

        Assert.Equal(Path.Combine(dir, "a", "b.bin"), ArenaInstallation.ResolveInside(dir, dirPrefix, @"a\b.bin"));
        Assert.Null(ArenaInstallation.ResolveInside(dir, dirPrefix, @"..\b.bin"));
        // the second guard: whatever the text says, a result outside the prefix is refused
        Assert.Null(ArenaInstallation.ResolveInside(dir, Path.Combine(root, "elsewhere") + Path.DirectorySeparatorChar, @"a\b.bin"));
        // an installation folder that is a drive root keeps its separator and still works
        Assert.Equal(Path.Combine(root, "a.bin"), ArenaInstallation.ResolveInside(root, root, "a.bin"));
    }

    /// <summary>
    /// The forged install of the review, scaled to the Small floors: a handful of real files, each listed over and over under
    /// spellings that resolve to one file. The floors count distinct files, so none of them is met.
    /// </summary>
    [Fact]
    public void Verify_FloorsCountDistinctFilesSoOneFileListedManyTimesMeetsNone()
    {
        var forged = new Install(NewTempDir(), new List<(string Path, long Size)>());
        AddFile(forged, "EscapeFromTarkovArena.exe", 120);
        AddFile(forged, "GameAssembly.dll", 600);
        AddFile(forged, LevelDir + "level0", 150);
        AddFile(forged, LevelDir + "filler.bin", 1000);
        var listed = forged.Listed.ToList();
        for (int i = 0; i < 25; i++)
        {
            listed.Add(("GameAssembly.dll", 600));
            listed.Add(("gameassembly.DLL", 600));
            listed.Add((LevelDir.Replace('\\', '/') + "level0", 150));
            listed.Add((LevelDir.ToUpperInvariant() + "LEVEL0", 150));
            listed.Add((LevelDir + "filler.bin", 1000));
        }
        WriteManifest(forged, entries: listed);

        ArenaInstallationResult result = ArenaInstallation.Verify(forged.Dir, "override", null, Small);

        Assert.False(result.Verified);
        // exe, GameAssembly.dll, level0, filler.bin and the manifest's own entry
        Assert.Contains("lists only 5 files; an Arena installation has at least 8", result.Reason);
    }

    [Fact]
    public void Verify_RepeatedListingsDoNotInflateTheListedSizeOrTheLevelCount()
    {
        var forged = new Install(NewTempDir(), new List<(string Path, long Size)>());
        AddFile(forged, "EscapeFromTarkovArena.exe", 120);
        AddFile(forged, "GameAssembly.dll", 600);
        AddFile(forged, LevelDir + "level0", 150);
        for (int i = 0; i < 12; i++)
            AddFile(forged, LevelDir + "extra" + i, 1);
        var listed = forged.Listed.ToList();
        for (int i = 0; i < 40; i++)
            listed.Add((LevelDir + "level0", 150));
        WriteManifest(forged, entries: listed);

        // 16 distinct entries (8 required) but only 883 distinct bytes (3,000 required) and 1 level file (2 required)
        ArenaInstallationResult bytes = ArenaInstallation.Verify(forged.Dir, "override", null, Small);
        Assert.False(bytes.Verified);
        Assert.Contains("lists only 0.0 GB of files", bytes.Reason);

        var levels = new ArenaInstallationFloors(8, 100, 2, 500);
        ArenaInstallationResult level = ArenaInstallation.Verify(forged.Dir, "override", null, levels);
        Assert.False(level.Verified);
        Assert.Contains("lists only 1 level files", level.Reason);
    }

    [Fact]
    public void Verify_AFileListedTwiceWithTheSameSizeCountsOnce()
    {
        Install install = MakeInstall();
        var entries = install.Listed.ToList();
        entries.Add((@"EscapeFromTarkovArena_Data\StreamingAssets\Windows\a.bundle", 2000));
        entries.Add(("ESCAPEFROMTARKOVARENA_DATA/streamingassets/windows/A.BUNDLE", 2000));
        WriteManifest(install, entries: entries);

        ArenaInstallationResult result = ArenaInstallation.Verify(install.Dir, "override", null, Small);

        Assert.True(result.Verified, result.Describe());
        Assert.Equal(install.Listed.Count, result.EntriesChecked);
        Assert.Equal(install.Listed.Sum(entry => entry.Size), result.TotalBytes);
    }

    [Fact]
    public void Verify_AFileListedTwiceWithDifferentSizesIsRefusedByName()
    {
        Install install = MakeInstall();
        var entries = install.Listed.ToList();
        entries.Add(("GameAssembly.dll", 601));
        WriteManifest(install, entries: entries);

        ArenaInstallationResult result = ArenaInstallation.Verify(install.Dir, "override", null, Small);

        Assert.False(result.Verified);
        Assert.Contains("lists GameAssembly.dll more than once with different sizes (600 and 601 bytes)", result.Reason);
        Assert.Equal(Version, result.ManifestVersion);
        Assert.Equal(0, result.EntriesChecked);
    }

    [Fact]
    public void Verify_AcceptsAQuotedPathWithATrailingSeparator()
    {
        Install install = MakeInstall();
        ArenaInstallationResult result = ArenaInstallation.Verify("\"" + install.Dir + Path.DirectorySeparatorChar + "\"", "override", null, Small);
        Assert.True(result.Verified, result.Describe());
        Assert.Equal(install.Dir, result.InstallPath);
    }

    [Fact]
    public void Describe_OneLineForBothOutcomes()
    {
        var verified = new ArenaInstallationResult(true, @"D:\Battlestate Games\Escape from Tarkov Arena", "registry", "0.6.1.0.48206",
            "0.6.0.0.47752", 15351, 76_118_000_000L, null, 0, null);
        Assert.Equal(@"Escape from Tarkov: Arena 0.6.1.0.48206 at D:\Battlestate Games\Escape from Tarkov Arena (registry; 15,351 files, 76.1 GB verified)",
            verified.Describe());

        var notVerified = new ArenaInstallationResult(false, null, "none", null, null, 0, 0, "Nothing here.", 0, null);
        Assert.Equal("Escape from Tarkov: Arena not found: Nothing here.", notVerified.Describe());
    }

    [Fact]
    public void Describe_IsIndependentOfTheCurrentCulture()
    {
        CultureInfo previous = CultureInfo.CurrentCulture;
        try
        {
            CultureInfo.CurrentCulture = new CultureInfo("de-DE");
            var verified = new ArenaInstallationResult(true, "X", "override", "1", null, 15351, 76_118_000_000L, null, 0, null);
            Assert.Contains("15,351 files, 76.1 GB verified", verified.Describe());
        }
        finally
        {
            CultureInfo.CurrentCulture = previous;
        }
    }

    // ---- Detect: precedence and the registry --------------------------------------------------------------------

    [Fact]
    public void Detect_AnExplicitOverrideWinsAndDoesNotFallBackToTheRegistry()
    {
        Install good = MakeInstall();
        var registry = new FakeRegistry().Add(Uninstall + "EscapeFromTarkovArena_live", good.Dir, "0.6.0.0.47752");
        string wrong = Path.Combine(Path.GetTempPath(), "arenamapport-wrong-" + Guid.NewGuid().ToString("N"));

        ArenaInstallationResult result = ArenaInstallation.Detect(wrong, registry, Small);

        Assert.False(result.Verified);
        Assert.Equal("override", result.Source);
        Assert.Contains("installation folder does not exist", result.Reason);
        Assert.Equal(0, registry.Calls);
    }

    [Fact]
    public void Detect_AValidOverrideIsUsedEvenWhenTheRegistryPointsElsewhere()
    {
        Install mine = MakeInstall();
        Install registered = MakeInstall();
        var registry = new FakeRegistry().Add(Uninstall + "EscapeFromTarkovArena_live", registered.Dir, "0.6.0.0.47752");

        ArenaInstallationResult result = ArenaInstallation.Detect(mine.Dir, registry, Small);

        Assert.True(result.Verified, result.Describe());
        Assert.Equal("override", result.Source);
        Assert.Equal(mine.Dir, result.InstallPath);
        Assert.Null(result.RegistryVersion);
        Assert.Equal(0, registry.Calls);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("\"\"")]
    public void Detect_WithoutAnOverrideTheRegistryPathIsUsed(string? overridePath)
    {
        Install install = MakeInstall();
        var registry = new FakeRegistry().Add(Uninstall + "EscapeFromTarkovArena_live", install.Dir, "0.6.0.0.47752");

        ArenaInstallationResult result = ArenaInstallation.Detect(overridePath!, registry, Small);

        Assert.True(result.Verified, result.Describe());
        Assert.Equal("registry", result.Source);
        Assert.Equal(install.Dir, result.InstallPath);
        Assert.Equal("0.6.0.0.47752", result.RegistryVersion);
        Assert.Equal(Version, result.ManifestVersion);
        Assert.Contains("(registry; ", result.Describe());
    }

    [Fact]
    public void Detect_NothingFoundNamesTheRegistryKeyAndTheOverride()
    {
        ArenaInstallationResult result = ArenaInstallation.Detect(null!, new FakeRegistry(), Small);

        Assert.False(result.Verified);
        Assert.Equal("none", result.Source);
        Assert.Null(result.InstallPath);
        Assert.Contains(@"SOFTWARE\WOW6432Node\Microsoft\Windows\CurrentVersion\Uninstall\EscapeFromTarkovArena_live", result.Reason);
        Assert.Contains("override", result.Reason);
        Assert.StartsWith("Escape from Tarkov: Arena not found: ", result.Describe());
    }

    [Fact]
    public void Detect_ARegistryThatCannotBeReadSaysSoAndNamesTheOverride()
    {
        ArenaInstallationResult result = ArenaInstallation.Detect(null!, new FakeRegistry { Available = false }, Small);

        Assert.False(result.Verified);
        Assert.Contains("registry cannot be read", result.Reason);
        Assert.Contains("override", result.Reason);
    }

    [Fact]
    public void Detect_FindsAnyKeyThatStartsWithEscapeFromTarkovArenaInEitherUninstallBranch()
    {
        Install install = MakeInstall();
        var registry = new FakeRegistry().Add(UninstallPlain + "EscapeFromTarkovArena_beta", install.Dir, "0.7.0.0.1");

        ArenaInstallationResult result = ArenaInstallation.Detect(null!, registry, Small);

        Assert.True(result.Verified, result.Describe());
        Assert.Equal("0.7.0.0.1", result.RegistryVersion);
    }

    [Fact]
    public void Detect_NeverTreatsTheMainGamesUninstallEntryAsArena()
    {
        Install install = MakeInstall();
        var registry = new FakeRegistry()
            .Add(Uninstall + "EscapeFromTarkov", install.Dir, "0.16.9.5")
            .Add(Uninstall + "SomethingElse", install.Dir, "1");

        ArenaInstallationResult result = ArenaInstallation.Detect(null!, registry, Small);

        Assert.False(result.Verified);
        Assert.Equal("none", result.Source);
    }

    [Fact]
    public void Detect_TriesTheNextRegisteredInstallationWhenTheFirstFails()
    {
        Install install = MakeInstall();
        string gone = Path.Combine(Path.GetTempPath(), "arenamapport-gone-" + Guid.NewGuid().ToString("N"));
        var registry = new FakeRegistry()
            .Add(Uninstall + "EscapeFromTarkovArena_live", gone, "0.6.0.0.47752")
            .Add(Uninstall + "EscapeFromTarkovArena_test", install.Dir, "0.6.1.0.48206");

        ArenaInstallationResult result = ArenaInstallation.Detect(null!, registry, Small);

        Assert.True(result.Verified, result.Describe());
        Assert.Equal(install.Dir, result.InstallPath);
        Assert.Equal("0.6.1.0.48206", result.RegistryVersion);
    }

    [Fact]
    public void Detect_WhenEveryRegisteredInstallationFailsTheFirstReasonIsReported()
    {
        string first = Path.Combine(Path.GetTempPath(), "arenamapport-first-" + Guid.NewGuid().ToString("N"));
        string second = Path.Combine(Path.GetTempPath(), "arenamapport-second-" + Guid.NewGuid().ToString("N"));
        var registry = new FakeRegistry()
            .Add(Uninstall + "EscapeFromTarkovArena_live", first, "1")
            .Add(Uninstall + "EscapeFromTarkovArena_test", second, "2");

        ArenaInstallationResult result = ArenaInstallation.Detect(null!, registry, Small);

        Assert.False(result.Verified);
        Assert.Equal("registry", result.Source);
        Assert.Equal(first, result.InstallPath);
        Assert.Equal("1", result.RegistryVersion);
    }

    [Fact]
    public void Detect_TrimsQuotesAndSkipsEntriesWithoutAnInstallLocation()
    {
        Install install = MakeInstall();
        var registry = new FakeRegistry()
            .Add(Uninstall + "EscapeFromTarkovArena_live", "", "0.6.0.0.1")
            .Add(Uninstall + "EscapeFromTarkovArena_x", "\"" + install.Dir + "\\\"", "0.6.0.0.2");

        ArenaInstallationResult result = ArenaInstallation.Detect(null!, registry, Small);

        Assert.True(result.Verified, result.Describe());
        Assert.Equal(install.Dir, result.InstallPath);
        Assert.Equal("0.6.0.0.2", result.RegistryVersion);
    }

    [Fact]
    public void Detect_NeverThrowsEvenWhenTheRegistryProbeDoes()
    {
        ArenaInstallationResult result = ArenaInstallation.Detect(null!, new FakeRegistry { Throws = true }, Small);

        Assert.False(result.Verified);
        Assert.Contains("unexpectedly", result.Reason);
    }

    // ---- the reflection registry reader -------------------------------------------------------------------------

    [WindowsFact]
    public void ReflectionRegistry_ReadsProgramFilesDirFromTheWindowsKey()
    {
        var probe = new ReflectionRegistryProbe();
        Assert.True(probe.IsAvailable);

        string? programFiles = probe.GetValue(@"HKEY_LOCAL_MACHINE\SOFTWARE\Microsoft\Windows\CurrentVersion", "ProgramFilesDir");

        Assert.False(string.IsNullOrWhiteSpace(programFiles));
        Assert.True(Directory.Exists(programFiles), programFiles);
    }

    [WindowsFact]
    public void ReflectionRegistry_ListsSubKeysAndReturnsEmptyOrNullForAbsentKeys()
    {
        var probe = new ReflectionRegistryProbe();

        string[] names = probe.GetSubKeyNames(@"HKEY_LOCAL_MACHINE\SOFTWARE\Microsoft\Windows\CurrentVersion");
        Assert.Contains("Uninstall", names, StringComparer.OrdinalIgnoreCase);

        Assert.Empty(probe.GetSubKeyNames(@"HKEY_LOCAL_MACHINE\SOFTWARE\ArenaMapPortNoSuchKey"));
        Assert.Empty(probe.GetSubKeyNames(@"HKEY_CURRENT_USER\SOFTWARE"));
        Assert.Empty(probe.GetSubKeyNames(null!));
        Assert.Null(probe.GetValue(@"HKEY_LOCAL_MACHINE\SOFTWARE\ArenaMapPortNoSuchKey", "InstallLocation"));
        Assert.Null(probe.GetValue(@"HKEY_LOCAL_MACHINE\SOFTWARE\Microsoft\Windows\CurrentVersion", "ArenaMapPortNoSuchValue"));
        Assert.Null(probe.GetValue("not a key", "x"));
    }

    [Fact]
    public void ReflectionRegistry_AndDetectNeverThrowOnAnyPlatform()
    {
        var probe = new ReflectionRegistryProbe();
        _ = probe.IsAvailable;
        _ = probe.GetValue(@"HKEY_LOCAL_MACHINE\SOFTWARE", "x");
        _ = probe.GetSubKeyNames(@"HKEY_LOCAL_MACHINE\SOFTWARE");

        ArenaInstallationResult result = ArenaInstallation.Detect(null!);

        Assert.NotNull(result);
        Assert.True(result.Verified || !string.IsNullOrEmpty(result.Reason));
    }

    // ---- production floors against a synthetic 40 GB install, and the real game ------------------------------------

    [SparseFileFact]
    public void ProductionFloors_AcceptASyntheticSparseInstall()
    {
        string dir = NewTempDir();
        long freeBefore = new DriveInfo(Path.GetPathRoot(dir)!).AvailableFreeSpace;
        var listed = new List<(string Path, long Size)>();
        void Sparse(string relative, long size)
        {
            string full = Path.Combine(dir, relative.Replace('\\', Path.DirectorySeparatorChar));
            Directory.CreateDirectory(Path.GetDirectoryName(full)!);
            using (var stream = new FileStream(full, FileMode.Create, FileAccess.ReadWrite))
            {
                Assert.True(SparseFiles.MarkSparse(stream), "could not flag " + full + " sparse");
                stream.SetLength(size);
            }
            listed.Add((relative, size));
        }

        Sparse("EscapeFromTarkovArena.exe", 3_000_000);
        Sparse("GameAssembly.dll", 60_000_000);
        for (int i = 0; i < 120; i++)
            Sparse(LevelDir + "level" + i, 400_000_000);
        long freeAfterLevels = new DriveInfo(Path.GetPathRoot(dir)!).AvailableFreeSpace;
        Assert.True(freeBefore - freeAfterLevels < 200_000_000, "sparse files must not allocate disk space");
        for (int i = 0; i < 4900; i++)
            Sparse(LevelDir + @"StreamingAssets\Windows\b" + (i % 40) + @"\f" + i + ".bundle", 1000 + i);
        WriteManifest(new Install(dir, listed));

        ArenaInstallationResult result = ArenaInstallation.Verify(dir); // public API, production floors

        Assert.True(result.Verified, result.Describe());
        Assert.Equal(listed.Count, result.EntriesChecked);
        Assert.Equal(listed.Sum(entry => entry.Size), result.TotalBytes);
        Assert.True(result.TotalBytes >= 40_000_000_000L);
    }

    [RealInstallFact]
    public void RealInstall_Verifies()
    {
        var timer = Stopwatch.StartNew();
        ArenaInstallationResult result = ArenaInstallation.Verify(RealArena.Folder);
        timer.Stop();

        Assert.True(result.Verified, result.Describe());
        Assert.True(result.EntriesChecked >= 10000, "entries checked: " + result.EntriesChecked);
        Assert.False(string.IsNullOrEmpty(result.ManifestVersion));
        Assert.Equal("override", result.Source);
        Assert.True(timer.Elapsed < TimeSpan.FromSeconds(10), "verification took " + timer.Elapsed);
    }

    [RealInstallFact]
    public void RealInstall_ManifestReaderAgreesWithSystemTextJson()
    {
        string text = File.ReadAllText(Path.Combine(RealArena.Folder, "ConsistencyInfo"), Encoding.UTF8);

        ArenaManifest manifest = ArenaManifest.Parse(text);

        using JsonDocument document = JsonDocument.Parse(text);
        JsonElement entries = document.RootElement.GetProperty("Entries");
        Assert.Equal(document.RootElement.GetProperty("Version").GetString(), manifest.Version);
        Assert.Equal(entries.GetArrayLength(), manifest.Entries.Count);
        int index = 0;
        foreach (JsonElement entry in entries.EnumerateArray())
        {
            Assert.Equal(entry.GetProperty("Path").GetString(), manifest.Entries[index].Path);
            Assert.Equal(entry.GetProperty("Size").GetInt64(), manifest.Entries[index].Size);
            index++;
        }
    }

    [RealRegistryFact]
    public void RealRegistry_DetectFindsTheLauncherInstallation()
    {
        ArenaInstallationResult result = ArenaInstallation.Detect(null!);

        Assert.True(result.Verified, result.Describe());
        Assert.Equal("registry", result.Source);
        Assert.False(string.IsNullOrEmpty(result.RegistryVersion));
        Assert.False(string.IsNullOrEmpty(result.ManifestVersion));
        Assert.True(result.EntriesChecked >= 10000);
    }

    // ---- the machine-readable cause (map notices, 2026-10-22) -----------------------------------------------------
    // Every failure site of ArenaInstallation names an ArenaInstallCause (the NotFound factory requires it, so a new site that
    // forgets does not compile). These tests pin which cause each site gives and that the Reason sentences stay as they were.

    [Fact]
    public void Cause_AVerifiedInstallationHasNone()
    {
        Install install = MakeInstall();
        ArenaInstallationResult result = ArenaInstallation.Verify(install.Dir, "override", null, Small);
        Assert.True(result.Verified, result.Describe());
        Assert.Equal(ArenaInstallCause.None, result.Cause);
        Assert.Empty(result.RegistryCandidates);
    }

    [Fact]
    public void Cause_ARegistryThatCannotBeReadIsRegistryUnavailable()
    {
        ArenaInstallationResult result = ArenaInstallation.Detect(null!, new FakeRegistry { Available = false }, Small);
        Assert.Equal(ArenaInstallCause.RegistryUnavailable, result.Cause);
        Assert.Contains("registry cannot be read", result.Reason);
    }

    [Fact]
    public void Cause_NoRegistryEntryIsNotRegistered()
    {
        ArenaInstallationResult result = ArenaInstallation.Detect(null!, new FakeRegistry(), Small);
        Assert.Equal(ArenaInstallCause.NotRegistered, result.Cause);
        Assert.Contains("No Escape from Tarkov: Arena installation is registered", result.Reason);
    }

    [Fact]
    public void Cause_ARegistryProbeThatThrowsIsCheckFailed()
    {
        ArenaInstallationResult result = ArenaInstallation.Detect(null!, new FakeRegistry { Throws = true }, Small);
        Assert.Equal(ArenaInstallCause.CheckFailed, result.Cause);
        Assert.Contains("failed unexpectedly", result.Reason);
    }

    [Fact]
    public void Cause_AFolderThatDoesNotExistIsFolderMissing()
    {
        string missing = Path.Combine(Path.GetTempPath(), "arenamapport-no-such-" + Guid.NewGuid().ToString("N"));
        Assert.Equal(ArenaInstallCause.FolderMissing, ArenaInstallation.Verify(missing, "override", null, Small).Cause);
    }

    [Fact]
    public void Cause_AFileInsteadOfAFolderIsFolderMissing()
    {
        string file = Path.Combine(NewTempDir(), "EscapeFromTarkovArena.exe");
        File.WriteAllText(file, "x");
        Assert.Equal(ArenaInstallCause.FolderMissing, ArenaInstallation.Verify(file, "override", null, Small).Cause);
    }

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("\"\"")]
    public void Cause_NoFolderGivenIsFolderMissing(string path)
    {
        Assert.Equal(ArenaInstallCause.FolderMissing, ArenaInstallation.Verify(path, "override", null, Small).Cause);
    }

    [Fact]
    public void Cause_AnInvalidPathIsFolderMissing()
    {
        // An embedded NUL makes Path.GetFullPath throw an ArgumentException: the "path is not valid" site.
        ArenaInstallationResult result = ArenaInstallation.Verify("C:\\bad\0path", "override", null, Small);
        Assert.Equal(ArenaInstallCause.FolderMissing, result.Cause);
        Assert.Contains("is not valid", result.Reason);
    }

    [Fact]
    public void Cause_AFolderWithoutTheExeIsNotArenaFolder()
    {
        ArenaInstallationResult result = ArenaInstallation.Verify(NewTempDir(), "override", null, Small);
        Assert.Equal(ArenaInstallCause.NotArenaFolder, result.Cause);
    }

    [Fact]
    public void Cause_AMissingManifestIsManifestMissing()
    {
        Install install = MakeInstall();
        File.Delete(Path.Combine(install.Dir, "ConsistencyInfo"));
        Assert.Equal(ArenaInstallCause.ManifestMissing, ArenaInstallation.Verify(install.Dir, "override", null, Small).Cause);
    }

    [Fact]
    public void Cause_AnImplausiblyLargeManifestIsManifestInvalid()
    {
        Install install = MakeInstall();
        File.WriteAllBytes(Path.Combine(install.Dir, "ConsistencyInfo"), new byte[64_000_001]);
        ArenaInstallationResult result = ArenaInstallation.Verify(install.Dir, "override", null, Small);
        Assert.Equal(ArenaInstallCause.ManifestInvalid, result.Cause);
        Assert.Contains("implausibly large", result.Reason);
    }

    [Fact]
    public void Cause_AnUnparsableOrTruncatedManifestIsManifestInvalid()
    {
        Install install = MakeInstall();
        string path = Path.Combine(install.Dir, "ConsistencyInfo");
        File.WriteAllText(path, "not json at all");
        Assert.Equal(ArenaInstallCause.ManifestInvalid, ArenaInstallation.Verify(install.Dir, "override", null, Small).Cause);

        WriteManifest(install);
        string text = File.ReadAllText(path);
        File.WriteAllText(path, text.Substring(0, text.Length / 2));
        Assert.Equal(ArenaInstallCause.ManifestInvalid, ArenaInstallation.Verify(install.Dir, "override", null, Small).Cause);
    }

    [WindowsFact]
    public void Cause_AManifestThatCannotBeReadIsManifestInvalid()
    {
        Install install = MakeInstall();
        using var locked = new FileStream(Path.Combine(install.Dir, "ConsistencyInfo"), FileMode.Open, FileAccess.ReadWrite, FileShare.None);
        ArenaInstallationResult result = ArenaInstallation.Verify(install.Dir, "override", null, Small);
        Assert.Equal(ArenaInstallCause.ManifestInvalid, result.Cause);
        Assert.Contains("cannot be read", result.Reason);
    }

    [Fact]
    public void Cause_AManifestWithoutAVersionOrWithoutEntriesIsManifestInvalid()
    {
        Install install = MakeInstall();
        WriteManifest(install, version: "  ");
        Assert.Equal(ArenaInstallCause.ManifestInvalid, ArenaInstallation.Verify(install.Dir, "override", null, Small).Cause);

        WriteManifest(install, entries: Array.Empty<(string, long)>(), withOwnEntry: false);
        Assert.Equal(ArenaInstallCause.ManifestInvalid, ArenaInstallation.Verify(install.Dir, "override", null, Small).Cause);
    }

    [Fact]
    public void Cause_ASelfContradictingManifestIsManifestInvalid()
    {
        Install install = MakeInstall();
        var entries = install.Listed.ToList();
        entries.Add(("GameAssembly.dll", 601));
        WriteManifest(install, entries: entries);
        ArenaInstallationResult result = ArenaInstallation.Verify(install.Dir, "override", null, Small);
        Assert.Equal(ArenaInstallCause.ManifestInvalid, result.Cause);
        Assert.Contains("more than once with different sizes", result.Reason);
    }

    [Fact]
    public void Cause_EveryPlausibilityFloorIsManifestInvalid()
    {
        Install install = MakeInstall();
        foreach (ArenaInstallationFloors floors in new[]
                 {
                     new ArenaInstallationFloors(100, 4000, 2, 500),                  // entries
                     new ArenaInstallationFloors(8, 40_000_000_000L, 2, 500),         // listed size
                     new ArenaInstallationFloors(8, 3000, 50, 500),                   // level files
                     new ArenaInstallationFloors(8, 3000, 2, 5_000_000),              // GameAssembly.dll size
                 })
        {
            ArenaInstallationResult result = ArenaInstallation.Verify(install.Dir, "override", null, floors);
            Assert.False(result.Verified);
            Assert.Equal(ArenaInstallCause.ManifestInvalid, result.Cause);
        }

        // A manifest that does not list the exe at all.
        var withoutExe = install.Listed.Where(entry => entry.Path != "EscapeFromTarkovArena.exe").ToList();
        WriteManifest(install, entries: withoutExe);
        ArenaInstallationResult noExe = ArenaInstallation.Verify(install.Dir, "override", null, Small);
        Assert.Equal(ArenaInstallCause.ManifestInvalid, noExe.Cause);
        Assert.Contains("does not list EscapeFromTarkovArena.exe", noExe.Reason);
    }

    [Fact]
    public void Cause_MissingOrResizedFilesAreFilesMismatch()
    {
        Install install = MakeInstall();
        File.Delete(Path.Combine(install.Dir, "EscapeFromTarkovArena_Data", "level1"));
        ArenaInstallationResult result = ArenaInstallation.Verify(install.Dir, "override", null, Small);
        Assert.False(result.Verified);
        Assert.Equal(ArenaInstallCause.FilesMismatch, result.Cause);
        Assert.Equal(1, result.MismatchCount);
        Assert.Equal(Version, result.ManifestVersion);
    }

    [Fact]
    public void Cause_NamedAtEveryFailureSeam_AndNoneOnlyWhenVerified()
    {
        string missing = Path.Combine(Path.GetTempPath(), "arenamapport-no-such-" + Guid.NewGuid().ToString("N"));
        Install mismatch = MakeInstall();
        File.Delete(Path.Combine(mismatch.Dir, "GameAssembly.dll"));
        Install noManifest = MakeInstall();
        File.Delete(Path.Combine(noManifest.Dir, "ConsistencyInfo"));
        Install badManifest = MakeInstall();
        File.WriteAllText(Path.Combine(badManifest.Dir, "ConsistencyInfo"), "{");

        var seams = new Dictionary<string, ArenaInstallationResult>
        {
            ["registry unavailable"] = ArenaInstallation.Detect(null!, new FakeRegistry { Available = false }, Small),
            ["not registered"] = ArenaInstallation.Detect(null!, new FakeRegistry(), Small),
            ["probe throws"] = ArenaInstallation.Detect(null!, new FakeRegistry { Throws = true }, Small),
            ["folder missing"] = ArenaInstallation.Verify(missing, "override", null, Small),
            ["no folder"] = ArenaInstallation.Verify("", "override", null, Small),
            ["not arena folder"] = ArenaInstallation.Verify(NewTempDir(), "registry", "1", Small),
            ["manifest missing"] = ArenaInstallation.Verify(noManifest.Dir, "override", null, Small),
            ["manifest invalid"] = ArenaInstallation.Verify(badManifest.Dir, "override", null, Small),
            ["files mismatch"] = ArenaInstallation.Verify(mismatch.Dir, "override", null, Small),
            ["production floors"] = ArenaInstallation.Verify(MakeInstall().Dir),
            ["explicit wrong override through Detect"] = ArenaInstallation.Detect(missing, new FakeRegistry(), Small),
        };

        foreach (var (name, result) in seams)
        {
            Assert.False(result.Verified, name);
            Assert.NotEqual(ArenaInstallCause.None, result.Cause);
            Assert.False(string.IsNullOrEmpty(result.Reason), name);
        }

        Install healthy = MakeInstall();
        Assert.Equal(ArenaInstallCause.None, ArenaInstallation.Verify(healthy.Dir, "override", null, Small).Cause);
    }

    // ---- the "no registry entry" diagnostic ----------------------------------------------------------------------

    [Fact]
    public void RegistryCandidates_AreEmptyWhenNothingMatchesArenaOrTarkov()
    {
        var registry = new FakeRegistry().Add(Uninstall + "SomethingElse", @"C:\Games\Other", "1");

        ArenaInstallationResult result = ArenaInstallation.Detect(null!, registry, Small);

        Assert.Equal(ArenaInstallCause.NotRegistered, result.Cause);
        Assert.Empty(result.RegistryCandidates);
        Assert.Equal("none", result.DescribeRegistryCandidates());
    }

    [Fact]
    public void RegistryCandidates_ListSubkeysWithArenaOrTarkovInTheirNameAndWhetherTheExeIsThere()
    {
        Install withExe = MakeInstall();
        string withoutExe = NewTempDir();
        var registry = new FakeRegistry()
            .Add(Uninstall + "EscapeFromTarkov", withoutExe, "0.16.9.5")           // the main game: matches "Tarkov", no exe
            .Add(UninstallPlain + "BSG Arena (Steam)", withExe.Dir, "0.6.1")         // matches "Arena", the exe is there
            .Add(Uninstall + "Unrelated", withExe.Dir, "1")                           // matches neither
            .Add(Uninstall + "arena-without-location", "", "2");                      // matches, but no InstallLocation

        ArenaInstallationResult result = ArenaInstallation.Detect(null!, registry, Small);

        Assert.False(result.Verified);
        Assert.Equal(ArenaInstallCause.NotRegistered, result.Cause);
        Assert.Equal("none", result.Source);
        Assert.Equal(
            new[] { "EscapeFromTarkov", "arena-without-location", "BSG Arena (Steam)" },
            result.RegistryCandidates.Select(candidate => candidate.Name).ToArray());
        Assert.Equal(new[] { false, false, true }, result.RegistryCandidates.Select(candidate => candidate.ExeFound).ToArray());
        Assert.Equal(withoutExe, result.RegistryCandidates[0].InstallLocation);
        Assert.Null(result.RegistryCandidates[1].InstallLocation);
        Assert.Equal(
            "EscapeFromTarkov=" + withoutExe + ", exe=no; arena-without-location=-, exe=no; BSG Arena (Steam)=" + withExe.Dir + ", exe=yes",
            result.DescribeRegistryCandidates());
        // The verdict and its sentence are the same as without the diagnostic.
        Assert.Contains("No Escape from Tarkov: Arena installation is registered", result.Reason);
        Assert.DoesNotContain(withExe.Dir, result.Reason);
    }

    [Fact]
    public void RegistryCandidates_NeverProbeAFileOnANetworkLocation()
    {
        // a stale entry that points at an unreachable share blocks a file probe for 20 s or more, and the server runs this diagnostic on its startup thread
        var registry = new FakeRegistry()
            .Add(Uninstall + "EscapeFromTarkov", @"\\unreachable-server\share\EFT", "1")
            .Add(UninstallPlain + "Arena (old)", "//unreachable-server/share/Arena", "1");

        ArenaInstallationResult result = ArenaInstallation.Detect(null!, registry, Small);

        Assert.Equal(ArenaInstallCause.NotRegistered, result.Cause);
        Assert.Equal(new[] { false, false }, result.RegistryCandidates.Select(candidate => candidate.ExeChecked).ToArray());
        Assert.Equal(new[] { false, false }, result.RegistryCandidates.Select(candidate => candidate.ExeFound).ToArray());
        Assert.Equal(
            @"EscapeFromTarkov=\\unreachable-server\share\EFT, exe=not checked; Arena (old)=//unreachable-server/share/Arena, exe=not checked",
            result.DescribeRegistryCandidates());
    }

    [Theory]
    [InlineData(@"\\server\share\Arena", true)]
    [InlineData("//server/share/Arena", true)]
    [InlineData(@"C:\Games\Arena", false)]
    [InlineData(@"D:\Nowhere", false)]
    [InlineData("", false)]
    [InlineData(null, false)]
    public void IsNetworkLocation_RecognisesUncAndLeavesLocalAndUnknownDrivesAlone(string? location, bool network)
    {
        Assert.Equal(network, ArenaInstallation.IsNetworkLocation(location!));
    }

    [Fact]
    public void RegistryCandidates_AreBoundedAndLineBreaksAreRemoved()
    {
        var registry = new FakeRegistry();
        for (int i = 0; i < 12; i++)
            registry.Add(Uninstall + "Arena" + i.ToString("00"), @"C:\Nowhere\" + i, "1");
        registry.Add(UninstallPlain + "Tarkov\nWith Break", @"C:\Nowhere\" + new string('x', 400), "1");

        ArenaInstallationResult result = ArenaInstallation.Detect(null!, registry, Small);

        Assert.Equal(ArenaInstallation.MaxRegistryCandidates, result.RegistryCandidates.Count);
        string line = result.DescribeRegistryCandidates();
        Assert.DoesNotContain("\n", line);
        Assert.DoesNotContain("\r", line);

        var long400 = new FakeRegistry().Add(Uninstall + "Tarkov\r\nWith Break", @"C:\Nowhere\" + new string('x', 400), "1");
        string cut = ArenaInstallation.Detect(null!, long400, Small).DescribeRegistryCandidates();
        Assert.DoesNotContain("\n", cut);
        Assert.DoesNotContain("\r", cut);
        Assert.Contains("...", cut);
        Assert.True(cut.Length < 400, cut.Length.ToString());
    }

    [Fact]
    public void RegistryCandidates_AreOnlyFilledForTheNoRegistryEntryBranch()
    {
        Install install = MakeInstall();
        string gone = Path.Combine(Path.GetTempPath(), "arenamapport-gone-" + Guid.NewGuid().ToString("N"));
        var registry = new FakeRegistry()
            .Add(Uninstall + "EscapeFromTarkovArena_live", gone, "0.6.1")
            .Add(Uninstall + "EscapeFromTarkov", install.Dir, "0.16.9.5");

        ArenaInstallationResult failed = ArenaInstallation.Detect(null!, registry, Small);
        Assert.Equal(ArenaInstallCause.FolderMissing, failed.Cause);
        Assert.Equal("registry", failed.Source);
        Assert.Empty(failed.RegistryCandidates);

        Assert.Empty(ArenaInstallation.Detect(null!, new FakeRegistry { Available = false }, Small).RegistryCandidates);
        Assert.Empty(ArenaInstallation.Verify(NewTempDir(), "override", null, Small).RegistryCandidates);
    }

    [Fact]
    public void RegistryCandidates_AProbeThatFailsDuringTheDiagnosticChangesNoVerdict()
    {
        // FindRegistryEntries makes four probe calls here (two sub-key listings, the preferred key in each branch); the diagnostic's own
        // first call is the fifth. A probe that starts throwing then must leave the NotRegistered verdict and an empty list.
        var registry = new ThrowAfterProbe(new FakeRegistry().Add(Uninstall + "EscapeFromTarkov", @"C:\Nowhere", "1"), allowedCalls: 4);

        ArenaInstallationResult result = ArenaInstallation.Detect(null!, registry, Small);

        Assert.False(result.Verified);
        Assert.Equal(ArenaInstallCause.NotRegistered, result.Cause);
        Assert.Empty(result.RegistryCandidates);
        Assert.True(registry.Threw, "the diagnostic never reached the failing probe call");
    }

    sealed class ThrowAfterProbe : IRegistryProbe
    {
        readonly IRegistryProbe _inner;
        readonly int _allowedCalls;
        int _calls;

        public ThrowAfterProbe(IRegistryProbe inner, int allowedCalls)
        {
            _inner = inner;
            _allowedCalls = allowedCalls;
        }

        public bool Threw { get; private set; }

        public bool IsAvailable => _inner.IsAvailable;

        public string? GetValue(string keyName, string valueName)
        {
            Count();
            return _inner.GetValue(keyName, valueName);
        }

        public string[] GetSubKeyNames(string keyName)
        {
            Count();
            return _inner.GetSubKeyNames(keyName);
        }

        void Count()
        {
            if (++_calls <= _allowedCalls)
                return;
            Threw = true;
            throw new InvalidOperationException("registry probe failure after " + _allowedCalls + " calls");
        }
    }

    // ---- helpers ---------------------------------------------------------------------------------------------

    sealed class Install
    {
        public Install(string dir, List<(string Path, long Size)> listed)
        {
            Dir = dir;
            Listed = listed;
        }

        public string Dir { get; }

        /// <summary>The files on disk and in the manifest, without the manifest's own entry.</summary>
        public List<(string Path, long Size)> Listed { get; }
    }

    string NewTempDir()
    {
        string dir = Path.Combine(Path.GetTempPath(), "arenamapport-tests-" + Guid.NewGuid().ToString("N"), "Arena");
        Directory.CreateDirectory(dir);
        _tempDirs.Add(Path.GetDirectoryName(dir)!);
        return dir;
    }

    /// <summary>A small install that meets <see cref="Small"/>: 8 files and 9 manifest entries, 4,370 bytes, 3 level files, GameAssembly.dll of 600 bytes.</summary>
    Install MakeInstall(int extraFiles = 0)
    {
        var install = new Install(NewTempDir(), new List<(string Path, long Size)>());
        AddFile(install, "EscapeFromTarkovArena.exe", 120);
        AddFile(install, "GameAssembly.dll", 600);
        AddFile(install, "UnityPlayer.dll", 200);
        AddFile(install, LevelDir + "level0", 150);
        AddFile(install, LevelDir + "level1", 150);
        AddFile(install, LevelDir + "level2", 150);
        AddFile(install, LevelDir + @"StreamingAssets\Windows\a.bundle", 2000);
        AddFile(install, LevelDir + @"StreamingAssets\Windows\b.bundle", 1000);
        for (int i = 0; i < extraFiles; i++)
            AddFile(install, LevelDir + @"StreamingAssets\Windows\extra" + i + ".bundle", 10);
        WriteManifest(install);
        return install;
    }

    static void AddFile(Install install, string relative, long size)
    {
        string full = Path.Combine(install.Dir, relative.Replace('\\', Path.DirectorySeparatorChar));
        Directory.CreateDirectory(Path.GetDirectoryName(full)!);
        File.WriteAllBytes(full, new byte[size]);
        install.Listed.Add((relative, size));
    }

    /// <summary>
    /// Writes ConsistencyInfo with System.Text.Json (an independent writer for the reader under test). The manifest's
    /// own entry carries a size that cannot match, like the real one.
    /// </summary>
    static void WriteManifest(Install install, string? version = null, IReadOnlyList<(string Path, long Size)>? entries = null,
        bool withOwnEntry = true, string ownEntryPath = "ConsistencyInfo")
    {
        var list = (entries ?? install.Listed)
            .Select(entry => new ManifestEntry(entry.Path, entry.Size, 123456789, false))
            .ToList();
        if (withOwnEntry)
            list.Add(new ManifestEntry(ownEntryPath, 1, 0, false));
        string json = JsonSerializer.Serialize(new ManifestFile(version ?? Version, list));
        File.WriteAllText(Path.Combine(install.Dir, "ConsistencyInfo"), json, new UTF8Encoding(false));
    }

    sealed record ManifestEntry(string Path, long Size, int Checksum, bool IsCritical);

    sealed record ManifestFile(string Version, List<ManifestEntry> Entries);

    sealed class FakeRegistry : IRegistryProbe
    {
        readonly Dictionary<string, Dictionary<string, string>> _keys = new(StringComparer.OrdinalIgnoreCase);

        public bool Available { get; set; } = true;
        public bool Throws { get; set; }
        public int Calls { get; private set; }

        public bool IsAvailable => Available;

        public FakeRegistry Add(string keyName, string installLocation, string displayVersion)
        {
            _keys[keyName] = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
            {
                ["InstallLocation"] = installLocation,
                ["DisplayVersion"] = displayVersion,
            };
            return this;
        }

        public string? GetValue(string keyName, string valueName)
        {
            Calls++;
            if (Throws)
                throw new InvalidOperationException("registry probe failure");
            return _keys.TryGetValue(keyName, out Dictionary<string, string>? values) && values.TryGetValue(valueName, out string? value) ? value : null;
        }

        public string[] GetSubKeyNames(string keyName)
        {
            Calls++;
            if (Throws)
                throw new InvalidOperationException("registry probe failure");
            string prefix = keyName + "\\";
            return _keys.Keys
                .Where(key => key.StartsWith(prefix, StringComparison.OrdinalIgnoreCase) && key.IndexOf('\\', prefix.Length) < 0)
                .Select(key => key.Substring(prefix.Length))
                .ToArray();
        }
    }
}
