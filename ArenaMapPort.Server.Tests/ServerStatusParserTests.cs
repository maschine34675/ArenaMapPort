#nullable disable
using System.IO.Compression;
using System.Text;
using ArenaMapPort.Client;
using ArenaMapPort.Server;
using ArenaMapPort.Shared;
using Xunit;

namespace ArenaMapPort.Server.Tests;

/// <summary>
/// ServerStatusParser: the client's reader of <c>GET /arenamapport/status</c> (schema 1). Schema rules, unknown fields, garbage, the zlib-aware body decode with
/// real <c>ZLibStream</c> output, and the server-to-client round trip: <c>ArenaServerStatus.ToJson()</c> feeds the parser for every state, so a camelCase or field
/// drift on either side fails here.
/// </summary>
[Collection(StatusHolderCollection.Name)]
public sealed class ServerStatusParserTests : IDisposable
{
    public ServerStatusParserTests() => ArenaServerStatus.ResetForTests();

    public void Dispose() => ArenaServerStatus.ResetForTests();

    const string Good =
        "{\"schema\":1,\"serverVersion\":\"0.3.0\",\"gate\":{\"state\":\"not_verified\",\"cause\":\"not_registered\",\"source\":\"none\"," +
        "\"manifestVersion\":null,\"mismatchCount\":0,\"overrideFile\":\"absent\"}," +
        "\"maps\":[{\"key\":\"arena_equator\",\"state\":\"not_registered\",\"cause\":\"gate\"},{\"key\":\"arena_bowl\",\"state\":\"failed\",\"cause\":\"registration\"}]}";

    static byte[] ZLib(string text, CompressionLevel level = CompressionLevel.SmallestSize)
    {
        using var output = new MemoryStream();
        using (var zlib = new ZLibStream(output, level, true))
        {
            byte[] bytes = Encoding.UTF8.GetBytes(text);
            zlib.Write(bytes, 0, bytes.Length);
        }
        return output.ToArray();
    }

    static byte[] GZip(string text)
    {
        using var output = new MemoryStream();
        using (var zip = new GZipStream(output, CompressionLevel.Optimal, true))
        {
            byte[] bytes = Encoding.UTF8.GetBytes(text);
            zip.Write(bytes, 0, bytes.Length);
        }
        return output.ToArray();
    }

    // ---- schema 1 -------------------------------------------------------------------------------------------------

    [Fact]
    public void ASchemaOneDocumentIsReadInFull()
    {
        Assert.True(ServerStatusParser.TryParse(Good, out ServerStatusView view, out string error), error);
        Assert.Null(error);
        Assert.Equal(1, view.Schema);
        Assert.Equal("0.3.0", view.ServerVersion);
        Assert.Equal("not_verified", view.GateState);
        Assert.Equal("not_registered", view.GateCauseCode);
        Assert.True(view.GateCauseKnown);
        Assert.Equal(ArenaInstallCause.NotRegistered, view.GateCause);
        Assert.Equal("none", view.GateSource);
        Assert.Null(view.ManifestVersion);
        Assert.Equal(0, view.MismatchCount);
        Assert.Equal("absent", view.OverrideFile);
        Assert.False(view.GateVerified);
        Assert.False(view.GateNotRun);
        Assert.Equal(2, view.Maps.Count);
        Assert.Equal("gate", view.FindMap("arena_equator").Cause);
        Assert.True(view.FindMap("arena_bowl").Failed);
        Assert.False(view.FindMap("arena_equator").Registered);
        Assert.Null(view.FindMap("arena_bay5"));
        Assert.Same(view.Maps[0], view.FindMap("arena_equator"));
    }

    [Fact]
    public void ParseReturnsTheViewOrNull()
    {
        Assert.NotNull(ServerStatusParser.Parse(Good));
        Assert.Null(ServerStatusParser.Parse("{}"));
    }

    [Fact]
    public void UnknownFieldsAreIgnored()
    {
        string json = Good.Replace("\"schema\":1,", "\"schema\":1,\"newField\":{\"deep\":[1,2,3]},")
            .Replace("\"overrideFile\":\"absent\"}", "\"overrideFile\":\"absent\",\"future\":true}")
            .Replace("\"cause\":\"registration\"}", "\"cause\":\"registration\",\"extra\":\"x\"}");
        Assert.True(ServerStatusParser.TryParse(json, out ServerStatusView view, out string error), error);
        Assert.Equal(2, view.Maps.Count);
    }

    [Fact]
    public void AMissingOrHigherSchemaReadsAsUnavailable()
    {
        foreach (string json in new[]
                 {
                     Good.Replace("\"schema\":1,", ""),
                     Good.Replace("\"schema\":1", "\"schema\":2"),
                     Good.Replace("\"schema\":1", "\"schema\":0"),
                     Good.Replace("\"schema\":1", "\"schema\":\"1\""),
                     Good.Replace("\"schema\":1", "\"schema\":1.5"),
                     Good.Replace("\"schema\":1", "\"schema\":null"),
                 })
        {
            Assert.False(ServerStatusParser.TryParse(json, out ServerStatusView view, out string error), json);
            Assert.Null(view);
            Assert.False(string.IsNullOrEmpty(error));
        }
    }

    [Fact]
    public void AnIncompleteDocumentReadsAsUnavailable()
    {
        foreach (string json in new[]
                 {
                     "{\"schema\":1}",
                     "{\"schema\":1,\"maps\":[]}",
                     "{\"schema\":1,\"gate\":{}}",
                     "{\"schema\":1,\"gate\":{\"state\":5}}",
                     "{\"schema\":1,\"gate\":\"verified\"}",
                 })
            Assert.False(ServerStatusParser.TryParse(json, out _, out _), json);
    }

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("not json")]
    [InlineData("<html>404</html>")]
    [InlineData("[]")]
    [InlineData("[1,2,3]")]
    [InlineData("42")]
    [InlineData("\"schema\"")]
    [InlineData("null")]
    [InlineData("{\"schema\":1,")]
    [InlineData("{\"schema\":1,\"gate\":{\"state\":\"verified\"")]
    public void GarbageReadsAsUnavailableWithoutThrowing(string json)
    {
        Assert.False(ServerStatusParser.TryParse(json, out ServerStatusView view, out string error));
        Assert.Null(view);
        Assert.False(string.IsNullOrEmpty(error));
        Assert.Null(ServerStatusParser.Parse(json));
    }

    [Fact]
    public void ANullTextAndAnOversizedTextAreUnavailable()
    {
        Assert.False(ServerStatusParser.TryParse(null, out _, out _));
        Assert.False(ServerStatusParser.TryParse(new string(' ', ServerStatusParser.MaxBytes + 1) + Good, out _, out _));
    }

    [Fact]
    public void ADeeplyNestedDocumentIsRefusedNotCrashed()
    {
        string deep = new string('[', 5000) + new string(']', 5000);
        Assert.False(ServerStatusParser.TryParse("{\"schema\":1,\"x\":" + deep + "}", out _, out _));
    }

    [Fact]
    public void ValuesThatAreNotTheExpectedTypeAreTolerated()
    {
        string json = "{\"schema\":1,\"serverVersion\":7,\"gate\":{\"state\":\"verified\",\"cause\":null,\"source\":5,\"manifestVersion\":12,\"mismatchCount\":\"x\",\"overrideFile\":false}," +
                      "\"maps\":[1,null,{\"key\":3},{\"state\":\"failed\"},{\"key\":\"arena_bowl\"},\"x\"]}";
        Assert.True(ServerStatusParser.TryParse(json, out ServerStatusView view, out string error), error);
        Assert.Null(view.ServerVersion);
        Assert.Equal("", view.GateCauseCode);
        Assert.False(view.GateCauseKnown);
        Assert.Equal("none", view.GateSource);
        Assert.Null(view.ManifestVersion);
        Assert.Equal(0, view.MismatchCount);
        Assert.Equal("absent", view.OverrideFile);
        var bowl = Assert.Single(view.Maps); // only the entry with a string key survives; its missing fields read as empty
        Assert.Equal("arena_bowl", bowl.Key);
        Assert.Equal("", bowl.State);
        Assert.Equal("", bowl.Cause);
        Assert.False(bowl.Failed);
    }

    [Fact]
    public void ACauseCodeFromAFutureServerIsKeptButNotKnown()
    {
        string json = Good.Replace("not_registered\",\"source", "brand_new_cause\",\"source");
        Assert.True(ServerStatusParser.TryParse(json, out ServerStatusView view, out _));
        Assert.Equal("brand_new_cause", view.GateCauseCode);
        Assert.False(view.GateCauseKnown);
        Assert.Equal(ArenaInstallCause.None, view.GateCause);
    }

    [Fact]
    public void CodesAreCutToSixtyFourCharactersAndTheMapListIsBounded()
    {
        string json = Good.Replace("\"serverVersion\":\"0.3.0\"", "\"serverVersion\":\"" + new string('v', 500) + "\"");
        Assert.True(ServerStatusParser.TryParse(json, out ServerStatusView view, out _));
        Assert.Equal(64, view.ServerVersion.Length);

        var maps = string.Join(",", Enumerable.Range(0, 500).Select(i => $"{{\"key\":\"m{i}\",\"state\":\"registered\",\"cause\":\"none\"}}"));
        string many = "{\"schema\":1,\"gate\":{\"state\":\"verified\"},\"maps\":[" + maps + "]}";
        Assert.True(ServerStatusParser.TryParse(many, out ServerStatusView manyView, out _));
        Assert.Equal(64, manyView.Maps.Count);
    }

    [Fact]
    public void ANegativeMismatchCountReadsAsZero()
    {
        string json = Good.Replace("\"mismatchCount\":0", "\"mismatchCount\":-5");
        Assert.True(ServerStatusParser.TryParse(json, out ServerStatusView view, out _));
        Assert.Equal(0, view.MismatchCount);
        string big = Good.Replace("\"mismatchCount\":0", "\"mismatchCount\":99999999999");
        Assert.True(ServerStatusParser.TryParse(big, out ServerStatusView bigView, out _));
        Assert.Equal(0, bigView.MismatchCount);
    }

    [Fact]
    public void TheVerifiedAndNotRunGatesAreRecognised()
    {
        Assert.True(ServerStatusParser.Parse(Good.Replace("\"not_verified\"", "\"verified\"")).GateVerified);
        Assert.True(ServerStatusParser.Parse(Good.Replace("\"not_verified\"", "\"not_run\"")).GateNotRun);
    }

    // ---- the body ----------------------------------------------------------------------------------------------------

    [Fact]
    public void ARealZlibBodyIsInflatedAndRead()
    {
        byte[] body = ZLib(Good);
        Assert.Equal(0x78, body[0]); // what SPT's server sends
        Assert.Equal(0, ((body[0] << 8) | body[1]) % 31);
        Assert.Equal(Good, ServerStatusParser.DecodeBody(body));
        Assert.True(ServerStatusParser.TryParseBody(body, out ServerStatusView view, out string error), error);
        Assert.Equal("not_verified", view.GateState);
    }

    [Fact]
    public void ZlibBodiesOfEveryLevelAreRead()
    {
        foreach (CompressionLevel level in new[] { CompressionLevel.Optimal, CompressionLevel.Fastest, CompressionLevel.SmallestSize, CompressionLevel.NoCompression })
        {
            byte[] body = ZLib(Good, level);
            Assert.Equal(0x78, body[0]);
            Assert.Equal(Good, ServerStatusParser.DecodeBody(body));
        }
    }

    [Fact]
    public void ASmallestSizeZlibBodyStartsWithSevenEightDA()
    {
        byte[] body = ZLib(Good, CompressionLevel.SmallestSize);
        Assert.Equal(new byte[] { 0x78, 0xDA }, body.Take(2).ToArray());
    }

    [Fact]
    public void APlainBodyAndAGzipBodyAreRead()
    {
        Assert.Equal(Good, ServerStatusParser.DecodeBody(Encoding.UTF8.GetBytes(Good)));
        Assert.Equal(Good, ServerStatusParser.DecodeBody(GZip(Good)));
        Assert.True(ServerStatusParser.TryParseBody(Encoding.UTF8.GetBytes(Good), out _, out _));
        Assert.True(ServerStatusParser.TryParseBody(GZip(Good), out _, out _));
    }

    [Fact]
    public void AByteOrderMarkIsDropped()
    {
        byte[] plain = new byte[] { 0xEF, 0xBB, 0xBF }.Concat(Encoding.UTF8.GetBytes(Good)).ToArray();
        Assert.Equal(Good, ServerStatusParser.DecodeBody(plain));
        Assert.True(ServerStatusParser.TryParseBody(plain, out _, out _));
    }

    [Fact]
    public void AJsonBodyThatBeginsWithAnXIsNotMistakenForZlib()
    {
        // "x" is 0x78: a body that starts with it but is not a zlib stream (0x78 0x7B ... is not a multiple of 31) is text
        byte[] text = Encoding.UTF8.GetBytes("x{\"schema\":1}");
        Assert.NotEqual(0, ((text[0] << 8) | text[1]) % 31);
        Assert.Equal("x{\"schema\":1}", ServerStatusParser.DecodeBody(text));
    }

    [Fact]
    public void DamagedOrHostileBodiesDecodeToNullNeverThrow()
    {
        Assert.Null(ServerStatusParser.DecodeBody(null));
        Assert.Null(ServerStatusParser.DecodeBody(new byte[0]));
        Assert.Null(ServerStatusParser.DecodeBody(new byte[ServerStatusParser.MaxBytes + 1]));
        byte[] truncated = ZLib(Good).Take(12).ToArray();
        string partial = ServerStatusParser.DecodeBody(truncated);
        Assert.True(partial == null || !ServerStatusParser.TryParse(partial, out _, out _));
        Assert.False(ServerStatusParser.TryParseBody(new byte[] { 0x78, 0x9C, 0xFF, 0xFF, 0xFF }, out ServerStatusView view, out string error));
        Assert.Null(view);
        Assert.False(string.IsNullOrEmpty(error));
        Assert.False(ServerStatusParser.TryParseBody(null, out _, out _));
        Assert.Null(ServerStatusParser.DecodeBody(new byte[] { 0x1F, 0x8B, 0x08, 0x00, 0x00 }));
    }

    [Fact]
    public void AnInflatedBombIsRefused()
    {
        string huge = new string('a', ServerStatusParser.MaxBytes * 4);
        byte[] body = ZLib(huge); // compresses to a few hundred bytes, inflates beyond the limit
        Assert.True(body.Length < ServerStatusParser.MaxBytes);
        Assert.Null(ServerStatusParser.DecodeBody(body));
        Assert.False(ServerStatusParser.TryParseBody(body, out _, out _));
        Assert.Null(ServerStatusParser.DecodeBody(GZip(huge)));
    }

    // ---- the round trip with the server's holder -----------------------------------------------------------------------------

    static ArenaInstallationResult Verified() =>
        new(true, @"D:\Secret Folder\Escape from Tarkov Arena", "registry", "0.6.1.0.48206", "0.6.0.0.47752", 15351, 76_118_000_000L, null, 0, null);

    static ArenaInstallationResult NotVerified(ArenaInstallCause cause, string source = "none", string manifestVersion = null, int mismatchCount = 0) =>
        new(false, @"D:\Secret Folder\Escape from Tarkov Arena", source, manifestVersion, null, 0, 0, "reason", mismatchCount, null, 0, cause);

    static ServerStatusView RoundTrip()
    {
        string json = ArenaServerStatus.ToJson();
        Assert.True(ServerStatusParser.TryParse(json, out ServerStatusView view, out string error), error + ": " + json);
        return view;
    }

    [Fact]
    public void AnEmptyHolderRoundTripsAsAGateThatHasNotRun()
    {
        ServerStatusView view = RoundTrip();
        Assert.True(view.GateNotRun);
        Assert.Equal(ArenaStatusContract.GateNotRun, view.GateState);
        Assert.Equal(4, view.Maps.Count);
        Assert.All(view.Maps, map => Assert.Equal(ArenaStatusContract.MapNotRegistered, map.State));
        Assert.Equal(ArenaMapRegistry.All.Select(map => map.Key), view.Maps.Select(map => map.Key));
    }

    [Fact]
    public void AVerifiedServerRoundTripsWithItsMapsAndVersion()
    {
        ArenaServerStatus.RecordGate(Verified(), ArenaStatusContract.OverrideUsed);
        foreach (MapInfo map in MapInfo.FromRegistry())
            ArenaServerStatus.RecordMap(map.Key, ArenaStatusContract.MapRegistered, ArenaStatusContract.MapCauseNone);

        ServerStatusView view = RoundTrip();
        Assert.True(view.GateVerified);
        Assert.Equal("none", view.GateCauseCode);
        Assert.Equal("registry", view.GateSource);
        Assert.Equal("0.6.1.0.48206", view.ManifestVersion);
        Assert.Equal("used", view.OverrideFile);
        Assert.Equal(new ModMetadata().Version.ToString(), view.ServerVersion);
        Assert.All(view.Maps, map => Assert.True(map.Registered));
    }

    [Theory]
    [InlineData(ArenaInstallCause.RegistryUnavailable, "registry_unreadable")]
    [InlineData(ArenaInstallCause.NotRegistered, "not_registered")]
    [InlineData(ArenaInstallCause.FolderMissing, "folder_missing")]
    [InlineData(ArenaInstallCause.NotArenaFolder, "not_arena_folder")]
    [InlineData(ArenaInstallCause.ManifestMissing, "manifest_missing")]
    [InlineData(ArenaInstallCause.ManifestInvalid, "manifest_invalid")]
    [InlineData(ArenaInstallCause.FilesMismatch, "files_mismatch")]
    [InlineData(ArenaInstallCause.CheckFailed, "check_failed")]
    public void EveryCauseRoundTripsAsTheSameEnumValue(ArenaInstallCause cause, string code)
    {
        ArenaServerStatus.RecordGate(NotVerified(cause, "override", "0.6.1.0.48206", 7), ArenaStatusContract.OverrideIgnored);
        ServerStatusView view = RoundTrip();
        Assert.Equal("not_verified", view.GateState);
        Assert.Equal(code, view.GateCauseCode);
        Assert.True(view.GateCauseKnown);
        Assert.Equal(cause, view.GateCause);
        Assert.Equal("override", view.GateSource);
        Assert.Equal(7, view.MismatchCount);
        Assert.Equal("ignored", view.OverrideFile);
        Assert.All(view.Maps, map =>
        {
            Assert.Equal("not_registered", map.State);
            Assert.Equal("gate", map.Cause);
        });
    }

    [Fact]
    public void AGateErrorRoundTrips()
    {
        ArenaServerStatus.RecordGateError(ArenaStatusContract.OverrideAbsent);
        ServerStatusView view = RoundTrip();
        Assert.Equal("error", view.GateState);
        Assert.Equal(ArenaInstallCause.CheckFailed, view.GateCause);
    }

    [Fact]
    public void FailedMapsRoundTripWithTheirCauses()
    {
        ArenaServerStatus.RecordGate(Verified(), ArenaStatusContract.OverrideAbsent);
        ArenaServerStatus.RecordMap("arena_equator", ArenaStatusContract.MapRegistered, ArenaStatusContract.MapCauseNone);
        ArenaServerStatus.RecordMap("arena_bay5", ArenaStatusContract.MapFailed, ArenaStatusContract.MapCauseBaseJsonParse);
        ArenaServerStatus.RecordMap("arena_airpit", ArenaStatusContract.MapFailed, ArenaStatusContract.MapCauseBaseJsonMissing);
        ArenaServerStatus.RecordMap("arena_bowl", ArenaStatusContract.MapFailed, ArenaStatusContract.MapCauseRegistration);

        ServerStatusView view = RoundTrip();
        Assert.True(view.FindMap("arena_equator").Registered);
        Assert.Equal("base_json_parse", view.FindMap("arena_bay5").Cause);
        Assert.Equal("base_json_missing", view.FindMap("arena_airpit").Cause);
        Assert.True(view.FindMap("arena_bowl").Failed);
        Assert.Equal("registration", view.FindMap("arena_bowl").Cause);
    }

    [Fact]
    public void EveryWireVocabularyValueTheServerCanSendIsKnownToTheClient()
    {
        // the server's contract lists; a value the client's parser or selector does not understand would read as "unknown server"
        foreach (string state in ArenaStatusContract.GateStates)
        {
            string json = Good.Replace("\"not_verified\"", "\"" + state + "\"");
            Assert.Equal(state, ServerStatusParser.Parse(json).GateState);
        }
        foreach (string overrideFile in ArenaStatusContract.OverrideFileStates)
            Assert.Equal(overrideFile, ServerStatusParser.Parse(Good.Replace("\"absent\"", "\"" + overrideFile + "\"")).OverrideFile);
        foreach (ArenaInstallCause cause in Enum.GetValues<ArenaInstallCause>())
        {
            string code = ArenaStatusContract.CauseCode(cause);
            ServerStatusView view = ServerStatusParser.Parse(Good.Replace("\"not_registered\",\"source", "\"" + code + "\",\"source"));
            Assert.True(view.GateCauseKnown);
            Assert.Equal(cause, view.GateCause);
        }
    }

    [Fact]
    public void TheRoundTrippedPrivacyRuleHoldsNoPathAndNoReasonTextReachesTheClient()
    {
        ArenaServerStatus.RecordGate(NotVerified(ArenaInstallCause.FolderMissing, "override"), ArenaStatusContract.OverrideUsed);
        string json = ArenaServerStatus.ToJson();
        Assert.DoesNotContain("Secret", json);
        Assert.DoesNotContain("reason", json.Replace("\"cause\"", ""), StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void TheRouteTheClientAsksForIsTheOneTheServerAnswers()
    {
        Assert.Equal("/arenamapport/status", ArenaStatusContract.StatusRoute);
        Assert.Equal(1, ArenaStatusContract.StatusSchema);
    }
}
