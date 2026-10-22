using ArenaMapPort.Shared;
using Xunit;

namespace ArenaMapPort.Server.Tests;

/// <summary>
/// The wire contract of the server's status route (Shared/ArenaStatusContract.cs, map notices 2026-10-22): the route, the schema
/// number and every code the client reads. A rename here breaks the client's parser, so each value is pinned literally.
/// </summary>
public sealed class ArenaStatusContractTests
{
    [Fact]
    public void TheRouteAndTheSchemaArePinned()
    {
        Assert.Equal("/arenamapport/status", ArenaStatusContract.StatusRoute);
        Assert.Equal(1, ArenaStatusContract.StatusSchema);
    }

    [Fact]
    public void TheCauseNumbersAndWireCodesArePinned()
    {
        var expected = new (ArenaInstallCause Cause, int Number, string Code)[]
        {
            (ArenaInstallCause.None, 0, "none"),
            (ArenaInstallCause.RegistryUnavailable, 1, "registry_unreadable"),
            (ArenaInstallCause.NotRegistered, 2, "not_registered"),
            (ArenaInstallCause.FolderMissing, 3, "folder_missing"),
            (ArenaInstallCause.NotArenaFolder, 4, "not_arena_folder"),
            (ArenaInstallCause.ManifestMissing, 5, "manifest_missing"),
            (ArenaInstallCause.ManifestInvalid, 6, "manifest_invalid"),
            (ArenaInstallCause.FilesMismatch, 7, "files_mismatch"),
            (ArenaInstallCause.CheckFailed, 8, "check_failed"),
        };

        foreach (var (cause, number, code) in expected)
        {
            Assert.Equal(number, (int)cause);
            Assert.Equal(code, ArenaStatusContract.CauseCode(cause));
        }

        // Nothing was forgotten: the table above covers every enum member, and the codes are unique.
        Assert.Equal(Enum.GetValues<ArenaInstallCause>().Length, expected.Length);
        Assert.Equal(expected.Length, expected.Select(row => row.Code).Distinct().Count());
    }

    [Fact]
    public void EveryCauseCodeParsesBackToItsCause()
    {
        foreach (ArenaInstallCause cause in Enum.GetValues<ArenaInstallCause>())
        {
            Assert.True(ArenaStatusContract.TryParseCause(ArenaStatusContract.CauseCode(cause), out ArenaInstallCause parsed));
            Assert.Equal(cause, parsed);
        }
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("NONE")]
    [InlineData("None")]
    [InlineData("not-registered")]
    [InlineData(" not_registered")]
    [InlineData("a_cause_from_a_newer_server")]
    public void AnUnknownCauseCodeDoesNotParse(string? code)
    {
        Assert.False(ArenaStatusContract.TryParseCause(code!, out ArenaInstallCause cause));
        Assert.Equal(ArenaInstallCause.None, cause);
    }

    [Fact]
    public void AnOutOfRangeCauseIsNeverReportedAsNone()
    {
        Assert.Equal("check_failed", ArenaStatusContract.CauseCode((ArenaInstallCause)99));
        Assert.Equal("check_failed", ArenaStatusContract.CauseCode((ArenaInstallCause)(-1)));
    }

    [Fact]
    public void TheGateAndMapVocabularyIsPinned()
    {
        Assert.Equal(new[] { "not_run", "verified", "not_verified", "error" }, ArenaStatusContract.GateStates);
        Assert.Equal(new[] { "absent", "used", "ignored" }, ArenaStatusContract.OverrideFileStates);
        Assert.Equal(new[] { "not_registered", "registered", "failed" }, ArenaStatusContract.MapStates);
        Assert.Equal(new[] { "none", "gate", "base_json_parse", "base_json_missing", "registration" }, ArenaStatusContract.MapCauses);

        Assert.Equal("not_run", ArenaStatusContract.GateNotRun);
        Assert.Equal("verified", ArenaStatusContract.GateVerified);
        Assert.Equal("not_verified", ArenaStatusContract.GateNotVerified);
        Assert.Equal("error", ArenaStatusContract.GateError);
        Assert.Equal("absent", ArenaStatusContract.OverrideAbsent);
        Assert.Equal("used", ArenaStatusContract.OverrideUsed);
        Assert.Equal("ignored", ArenaStatusContract.OverrideIgnored);
        Assert.Equal("not_registered", ArenaStatusContract.MapNotRegistered);
        Assert.Equal("registered", ArenaStatusContract.MapRegistered);
        Assert.Equal("failed", ArenaStatusContract.MapFailed);
        Assert.Equal("none", ArenaStatusContract.MapCauseNone);
        Assert.Equal("gate", ArenaStatusContract.MapCauseGate);
        Assert.Equal("base_json_parse", ArenaStatusContract.MapCauseBaseJsonParse);
        Assert.Equal("base_json_missing", ArenaStatusContract.MapCauseBaseJsonMissing);
        Assert.Equal("registration", ArenaStatusContract.MapCauseRegistration);
    }

    [Fact]
    public void IsOneOfMatchesOrdinallyAndNeverThrows()
    {
        Assert.True(ArenaStatusContract.IsOneOf("verified", ArenaStatusContract.GateStates));
        Assert.False(ArenaStatusContract.IsOneOf("Verified", ArenaStatusContract.GateStates));
        Assert.False(ArenaStatusContract.IsOneOf(null!, ArenaStatusContract.GateStates));
        Assert.False(ArenaStatusContract.IsOneOf("verified", null!));
    }

    [Fact]
    public void TheDocumentSourcesAreTheInstallationsOwnConstants()
    {
        // The wire's gate.source is the ArenaInstallation source string, unchanged.
        Assert.Equal("override", ArenaInstallation.SourceOverride);
        Assert.Equal("registry", ArenaInstallation.SourceRegistry);
        Assert.Equal("none", ArenaInstallation.SourceNone);
    }
}
