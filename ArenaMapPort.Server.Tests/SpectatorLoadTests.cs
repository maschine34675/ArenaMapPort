using System.Security.Cryptography;
using System.Text;
using ArenaMapPort.Client;
using ArenaMapPort.Crypto;
using Newtonsoft.Json;
using Xunit;

namespace ArenaMapPort.Server.Tests;

/// <summary>
/// The crowd loader's Unity-free half (docs/progress/spectators-allmaps.md, "Load cost"): the shared-texture package hashes through the CNG
/// provider the bundle decryption uses (managed only as the fallback) and stays fail-closed on both, and the prefetch worker verifies the
/// files a bounded distance ahead of the consumer, in order, without ever holding the whole set, and hands every failure to the consumer so
/// that the main thread can fall back to reading the file itself.
/// </summary>
public sealed class SpectatorLoadTests : IDisposable
{
    readonly string _folder = Path.Combine(Path.GetTempPath(), "spectator-load-" + Guid.NewGuid().ToString("N"));
    readonly List<SpectatorTexturePrefetch> _prefetches = new();

    public SpectatorLoadTests() { Directory.CreateDirectory(_folder); }

    public void Dispose()
    {
        // a worker may still hold a file open on Windows: stop and join every worker before the folder goes
        foreach (var prefetch in _prefetches)
        {
            prefetch.Dispose();
            if (prefetch.Worker != null) SpinWait.SpinUntil(() => prefetch.Worker.IsCompleted, 5000);
        }
        try { Directory.Delete(_folder, true); } catch (IOException) { }
    }

    static string Hex(byte[] bytes) => Convert.ToHexString(SHA256.HashData(bytes)).ToLowerInvariant();

    (SpectatorTexturePackage Package, string[] Names, byte[][] Contents) Make(int count, int size, ICryptoProvider? crypto = null)
    {
        var names = new string[count];
        var contents = new byte[count][];
        var rows = new List<object>();
        for (int i = 0; i < count; i++)
        {
            names[i] = i.ToString("x16") + ".png";
            contents[i] = RandomNumberGenerator.GetBytes(size + i);
            File.WriteAllBytes(Path.Combine(_folder, names[i]), contents[i]);
            rows.Add(new { file = names[i], bytes = contents[i].Length, sha256 = Hex(contents[i]) });
        }
        byte[] manifest = Encoding.UTF8.GetBytes(JsonConvert.SerializeObject(new { schema = 1, files = rows }));
        return (new SpectatorTexturePackage(manifest, Hex(manifest), crypto), names, contents);
    }

    SpectatorTexturePrefetch Prefetch(SpectatorTexturePackage package, string[] order, long budget)
    {
        var prefetch = new SpectatorTexturePrefetch(package, _folder, order, budget);
        _prefetches.Add(prefetch);
        return prefetch;
    }

    static bool Joined(SpectatorTexturePrefetch prefetch) => SpinWait.SpinUntil(() => prefetch.Worker!.IsCompleted, 5000);

    static void WaitUntil(Func<bool> condition)
    {
        var until = DateTime.UtcNow.AddSeconds(10);
        while (!condition())
        {
            Assert.True(DateTime.UtcNow < until, "condition not reached within 10 s");
            Thread.Sleep(5);
        }
    }

    static IEnumerable<ICryptoProvider> Providers()
    {
        yield return CryptoProviders.Managed;
        if (CryptoProviders.Cng != null) yield return CryptoProviders.Cng;
    }

    [Fact]
    public void TheDefaultPackageUsesTheProcessWideProviderTheBundlesUse()
    {
        var (package, _, _) = Make(1, 100);
        Assert.Equal(CryptoProviders.Select(null).Name, package.HashProvider);
    }

    [Fact]
    public void EveryProviderAcceptsTheExactBytesAndFailsClosedOnAFlippedByteAGrownFileAndAShortFile()
    {
        foreach (var crypto in Providers())
        {
            var (package, names, contents) = Make(3, 300_000, crypto);
            Assert.Equal(crypto.Name, package.HashProvider);
            for (int i = 0; i < names.Length; i++)
                Assert.Equal(contents[i], package.Read(_folder, names[i]));
            Assert.True(package.ReadMilliseconds >= 0);
            Assert.True(package.HashMilliseconds > 0);

            string path = Path.Combine(_folder, names[1]);
            var flipped = (byte[])contents[1].Clone();
            flipped[flipped.Length / 2] ^= 0x01; // same size, one bit: only the digest can tell
            File.WriteAllBytes(path, flipped);
            Assert.Throws<InvalidDataException>(() => package.Read(_folder, names[1]));
            File.WriteAllBytes(path, contents[1].Concat(new byte[] { 0 }).ToArray());
            Assert.Throws<InvalidDataException>(() => package.Read(_folder, names[1]));
            File.WriteAllBytes(path, contents[1].Take(contents[1].Length - 1).ToArray());
            Assert.Throws<InvalidDataException>(() => package.Read(_folder, names[1]));
            File.WriteAllBytes(path, contents[1]);
            Assert.Equal(contents[1], package.Read(_folder, names[1]));
        }
    }

    [Fact]
    public void ReadsFromSeveralThreadsAgreeAndTheCountersAreSummed()
    {
        var (package, names, contents) = Make(8, 200_000);
        Parallel.For(0, 64, i => Assert.Equal(contents[i % names.Length], package.Read(_folder, names[i % names.Length])));
        Assert.True(package.HashMilliseconds > 0);
    }

    [Fact]
    public void ThePrefetchDeliversEveryFileInOrderAndNeverHoldsMoreThanItsBudgetPlusOneFile()
    {
        const int size = 1 << 20;
        const long budget = 4L << 20;
        var (package, names, contents) = Make(24, size);
        var prefetch = Prefetch(package, names, budget);
        prefetch.Start();
        WaitUntil(() => prefetch.PendingBytes >= budget);
        Thread.Sleep(200); // the worker must now be waiting for the consumer, not reading on
        Assert.InRange(prefetch.PendingBytes, budget, budget + size + names.Length);
        for (int i = 0; i < names.Length; i++)
        {
            Assert.Equal(contents[i], prefetch.Take(names[i], 10_000));
            Assert.True(prefetch.PendingBytes <= budget + size + names.Length);
        }
        Assert.True(Joined(prefetch));
        Assert.Equal(0, prefetch.PendingBytes);
        Assert.True(prefetch.WaitMilliseconds >= 0);
    }

    [Fact]
    public void AFileLargerThanTheBudgetStillPasses()
    {
        var (package, names, contents) = Make(3, 50_000);
        var prefetch = Prefetch(package, names, 10); // smaller than any file: one file at a time
        prefetch.Start();
        for (int i = 0; i < names.Length; i++)
            Assert.Equal(contents[i], prefetch.Take(names[i], 10_000));
    }

    [Fact]
    public void ADamagedFileSurfacesFromTakeAfterTheFilesBeforeItWithTheOriginalCause()
    {
        var (package, names, contents) = Make(6, 10_000);
        File.WriteAllBytes(Path.Combine(_folder, names[3]), new byte[contents[3].Length]); // right size, wrong digest
        var prefetch = Prefetch(package, names, 1L << 30);
        prefetch.Start();
        for (int i = 0; i < 3; i++)
            Assert.Equal(contents[i], prefetch.Take(names[i], 10_000));
        var ex = Assert.Throws<InvalidOperationException>(() => prefetch.Take(names[3], 10_000));
        Assert.IsType<InvalidDataException>(ex.InnerException);
        Assert.Contains(names[3], ex.InnerException!.Message);
        Assert.True(Joined(prefetch)); // the worker ended; it never faulted the task
        Assert.False(prefetch.Worker.IsFaulted);
    }

    [Fact]
    public void AMissingFileSurfacesFromTake()
    {
        var (package, names, _) = Make(2, 1000);
        File.Delete(Path.Combine(_folder, names[1]));
        var prefetch = Prefetch(package, names, 1L << 30);
        prefetch.Start();
        prefetch.Take(names[0], 10_000);
        var ex = Assert.Throws<InvalidOperationException>(() => prefetch.Take(names[1], 10_000));
        Assert.IsType<FileNotFoundException>(ex.InnerException);
    }

    [Fact]
    public void AnUnexpectedOrderCancelsThePrefetchSoTheCallerReadsTheFilesItself()
    {
        var (package, names, contents) = Make(4, 1000);
        var prefetch = Prefetch(package, names, 1L << 30);
        prefetch.Start();
        Assert.Throws<InvalidOperationException>(() => prefetch.Take(names[1], 10_000));
        Assert.ThrowsAny<InvalidOperationException>(() => prefetch.Take(names[0], 100)); // disposed after the mismatch
        Assert.True(Joined(prefetch));
        Assert.Equal(contents[0], package.Read(_folder, names[0])); // the synchronous path still works
    }

    [Fact]
    public void DisposeReleasesAWorkerThatIsWaitingForRoom()
    {
        var (package, names, _) = Make(10, 100_000);
        var prefetch = Prefetch(package, names, 100_000);
        prefetch.Start();
        WaitUntil(() => prefetch.PendingBytes >= 100_000);
        prefetch.Dispose();
        Assert.True(Joined(prefetch));
        Assert.Equal(0, prefetch.PendingBytes);
        Assert.ThrowsAny<InvalidOperationException>(() => prefetch.Take(names[0], 100));
    }

    [Fact]
    public void ATakeThatNothingAnswersTimesOut()
    {
        var (package, names, _) = Make(1, 1000);
        var prefetch = Prefetch(package, names, 1L << 30); // never started
        var ex = Assert.Throws<InvalidOperationException>(() => prefetch.Take(names[0], 50));
        Assert.Contains("timed out", ex.Message);
    }

    [Fact]
    public void AskingForMoreThanWasScheduledFailsInsteadOfWaiting()
    {
        var (package, names, contents) = Make(2, 1000);
        var prefetch = Prefetch(package, names, 1L << 30);
        prefetch.Start();
        Assert.Equal(contents[0], prefetch.Take(names[0], 10_000));
        Assert.Equal(contents[1], prefetch.Take(names[1], 10_000));
        var ex = Assert.Throws<InvalidOperationException>(() => prefetch.Take("00000000000000ff.png", 10_000));
        Assert.Contains("no further texture", ex.Message);
    }

    [Fact]
    public void StartingTwiceOrAfterDisposeDoesNotStartASecondWorker()
    {
        var (package, names, _) = Make(2, 1000);
        var prefetch = Prefetch(package, names, 1L << 30);
        prefetch.Start();
        var first = prefetch.Worker;
        prefetch.Start();
        Assert.Same(first, prefetch.Worker);
        var late = Prefetch(package, names, 1L << 30);
        late.Dispose();
        late.Start();
        Assert.Null(late.Worker);
    }
}
