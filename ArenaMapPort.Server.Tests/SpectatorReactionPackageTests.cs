using ArenaMapPort.Client;
using ArenaMapPort.Crypto;
using Newtonsoft.Json;
using Xunit;

namespace ArenaMapPort.Server.Tests;

/// <summary>
/// The checks of the crowd worker's reaction loader (ArenaSpectators.LoadReactionPackage: validate the reaction set, read every clip, compare its length, hash it through the CNG provider
/// and decode it) run here, outside Unity, on the extractor's real output, because that function is Unity glue the tests cannot call. Runs only where the ignored output exists
/// (work/extract/spectator-audio, BSG-derived), like the other real-data reaction tests; the checkout's staged dist copy, when there is one, must be the same folder byte for byte.
/// </summary>
public sealed class SpectatorReactionPackageTests
{
    private static string? Folder()
    {
        string relative = Path.Combine("work", "extract", "spectator-audio", "spectator-audio.json");
        string? env = Environment.GetEnvironmentVariable("ARENAMAPPORT_WORK_DIR");
        if (!string.IsNullOrWhiteSpace(env))
        {
            string path = Path.Combine(env, "extract", "spectator-audio", "spectator-audio.json");
            if (File.Exists(path)) return Path.GetDirectoryName(path);
        }
        for (var dir = new DirectoryInfo(AppContext.BaseDirectory); dir != null; dir = dir.Parent)
        {
            string path = Path.Combine(dir.FullName, relative);
            if (File.Exists(path)) return Path.GetDirectoryName(path);
        }
        return null;
    }

    private static string? Staged()
    {
        for (var dir = new DirectoryInfo(AppContext.BaseDirectory); dir != null; dir = dir.Parent)
        {
            string path = Path.Combine(dir.FullName, "dist", "BepInEx", "plugins", "maschine-ArenaMapPort", "spectator-audio");
            if (Directory.Exists(path)) return path;
        }
        return null;
    }

    [RealReactionSetFact]
    public void EveryReactionClipPassesTheWorkersLengthHashAndDecodeChecksAndTheCallsValidateAlongside()
    {
        string folder = Folder()!;
        var manifest = JsonConvert.DeserializeObject<SpectatorAudioManifest>(File.ReadAllText(Path.Combine(folder, "spectator-audio.json")))!;
        manifest.ValidateReactions();
        var crypto = CryptoProviders.Select(null);
        long samples = 0;
        foreach (var c in manifest.reactions.clips)
        {
            byte[] bytes = File.ReadAllBytes(Path.Combine(folder, c.file));
            Assert.Equal(c.bytes, bytes.Length);
            Assert.Equal(c.sha256, Convert.ToHexString(crypto.Sha256(bytes, 0, bytes.Length)).ToLowerInvariant());
            var wav = PcmWav.Read(bytes);
            Assert.Equal((c.channels, c.sample_rate, c.frames), (wav.Channels, wav.SampleRate, wav.Frames));
            Assert.Equal(c.frames, wav.Samples.Length);
            samples += wav.Samples.Length;
            var floats = PcmWav.ToFloats(wav.Samples);
            Assert.Equal(c.frames, floats.Length);
            Assert.All(floats, f => Assert.InRange(f, -1f, 1f));
        }
        Assert.Equal(70, manifest.reactions.clips.Length);
        Assert.InRange(samples / 44100.0, 170.0, 180.0);                       // the phase-A set: 174.99 s
        // the calls and the bed validate on the same manifest, each on its own
        manifest.Validate();
        manifest.ValidateBed();
        // the planner builds from it and offers the five phase-A kinds
        Assert.Equal(CrowdReactionKind.ImplementedMask, new CrowdReactionPlanner(manifest.reactions, 1).KindsMask);
    }

    [RealReactionSetFact]
    public void TheFolderHoldsExactlyTheCallsTheReactionsTheBedAndTheManifest()
    {
        string folder = Folder()!;
        var manifest = JsonConvert.DeserializeObject<SpectatorAudioManifest>(File.ReadAllText(Path.Combine(folder, "spectator-audio.json")))!;
        var expected = manifest.clips.Select(c => c.file).Concat(manifest.reactions.clips.Select(c => c.file)).Append(manifest.bed.file).Append("spectator-audio.json")
            .OrderBy(n => n, StringComparer.Ordinal).ToArray();
        Assert.Equal(85, expected.Length);
        Assert.Equal(expected, Directory.GetFiles(folder).Select(Path.GetFileName).OrderBy(n => n, StringComparer.Ordinal).ToArray());
        string? staged = Staged();
        if (staged == null || !File.Exists(Path.Combine(staged, "spectator-audio.json"))) return;       // a checkout that never staged
        Assert.Equal(expected, Directory.GetFiles(staged).Select(Path.GetFileName).OrderBy(n => n, StringComparer.Ordinal).ToArray());
        Assert.Equal(File.ReadAllBytes(Path.Combine(folder, "spectator-audio.json")), File.ReadAllBytes(Path.Combine(staged, "spectator-audio.json")));
    }
}
