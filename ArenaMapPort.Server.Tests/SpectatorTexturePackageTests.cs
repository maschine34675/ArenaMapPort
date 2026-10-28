using System.Security.Cryptography;
using ArenaMapPort.Client;
using Newtonsoft.Json;
using Xunit;

namespace ArenaMapPort.Server.Tests;

public sealed class SpectatorTexturePackageTests
{
    const string Name = "0123456789abcdef.png";
    static string Hash(byte[] bytes) => Convert.ToHexString(SHA256.HashData(bytes)).ToLowerInvariant();
    static byte[] Manifest(byte[] bytes, string name = Name) => System.Text.Encoding.UTF8.GetBytes(
        JsonConvert.SerializeObject(new {schema=1, files=new[] {new {file=name, bytes=bytes.Length, sha256=Hash(bytes)}}}));

    [Fact]
    public void ExactSharedBytesLoadButCorruptionAndUnknownNamesFail()
    {
        var folder = Path.Combine(Path.GetTempPath(), "spectator-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(folder);
        try
        {
            byte[] bytes = {1, 2, 3};
            var manifest = Manifest(bytes);
            var package = new SpectatorTexturePackage(manifest, Hash(manifest));
            File.WriteAllBytes(Path.Combine(folder, Name), bytes);
            Assert.Equal(bytes, package.Read(folder, Name));
            Assert.Throws<InvalidDataException>(() => package.Read(folder, "../outside.png"));
            File.WriteAllBytes(Path.Combine(folder, Name), new byte[] {3, 2, 1});
            Assert.Throws<InvalidDataException>(() => package.Read(folder, Name));
            File.Delete(Path.Combine(folder, Name));
            Assert.Throws<FileNotFoundException>(() => package.Read(folder, Name));
        }
        finally { Directory.Delete(folder, true); }
    }

    [Fact]
    public void MixedMapAndSharedManifestFailsBeforeReadingTextures()
    {
        var first = Manifest(new byte[] {1, 2, 3});
        var second = Manifest(new byte[] {3, 2, 1});
        Assert.Throws<InvalidDataException>(() => new SpectatorTexturePackage(second, Hash(first)));
        Assert.Throws<InvalidDataException>(() => new SpectatorTexturePackage(first, null));
    }

    [Theory]
    [InlineData("../outside.png")]
    [InlineData("0123456789abcdef-6.png")]
    [InlineData("0123456789abcdef.png\n")]
    public void UnsafeManifestNamesFailEvenWithMatchingHash(string name)
    {
        var manifest = Manifest(new byte[] {1}, name);
        Assert.Throws<InvalidDataException>(() => new SpectatorTexturePackage(manifest, Hash(manifest)));
    }
}
