#nullable disable
using System.Diagnostics;
using ArenaMapPort.Client;
using ArenaMapPort.Shared;
using Xunit;
using ClientGate = ArenaMapPort.Client.ArenaInstallationGate;

namespace ArenaMapPort.Server.Tests;

/// <summary>
/// The read-only state of the client's Arena gate that the map notices use (<see cref="GateState"/>, <c>State</c>, <c>FailureDetail</c>) and the snapshot
/// <see cref="GateSnapshot.Read"/> takes of it. Nothing here changes what the gate does; ClientArenaGateTests keeps pinning that.
/// </summary>
[Collection("MapPort client lifecycle")]
public sealed class MapNoticeGateStateTests : IDisposable
{
    static readonly TimeSpan Patience = TimeSpan.FromSeconds(10);
    readonly TimeSpan _bundleWait = ClientGate.BundleWait;

    public MapNoticeGateStateTests() => ClientGate.ResetForTests();

    public void Dispose()
    {
        ClientGate.BundleWait = _bundleWait;
        ClientGate.ResetForTests();
    }

    static ArenaInstallationResult Found() =>
        new(true, @"D:\Synthetic Arena", "registry", "0.6.1.0.48206", "0.6.0.0.47752", 15351, 76_100_000_000L, null, 0, null);

    static ArenaInstallationResult Missing(ArenaInstallCause cause, string source = "none", int mismatches = 0) =>
        new(false, @"D:\Synthetic Arena", source, "0.6.1.0.48206", null, 0, 0, "the reason sentence", mismatches, null, 0, cause);

    [Fact]
    public void BeforeAnyCheckTheGateIsNotStarted()
    {
        Assert.Equal(GateState.NotStarted, ClientGate.State);
        Assert.Null(ClientGate.FailureDetail);
        GateSnapshot snapshot = GateSnapshot.Read();
        Assert.Equal(GateState.NotStarted, snapshot.State);
        Assert.False(snapshot.Verified);
    }

    [Fact]
    public void ARunningCheckIsRunningAndTheSnapshotDoesNotBlock()
    {
        using var release = new ManualResetEventSlim(false);
        ClientGate.Start("", _ => { release.Wait(Patience); return Found(); });
        var clock = Stopwatch.StartNew();
        Assert.Equal(GateState.Running, ClientGate.State);
        Assert.Equal(GateState.Running, GateSnapshot.Read().State);
        Assert.True(clock.Elapsed < TimeSpan.FromSeconds(2), "reading the state never waits for the check");

        release.Set();
        Assert.True(ClientGate.WaitForResult(Patience));
        Assert.Equal(GateState.Verified, ClientGate.State);
    }

    [Fact]
    public void AVerifiedCheckCarriesItsFolderAndVersion()
    {
        ClientGate.Start("", _ => Found());
        Assert.True(ClientGate.WaitForResult(Patience));
        Assert.Equal(GateState.Verified, ClientGate.State);
        GateSnapshot snapshot = GateSnapshot.Read();
        Assert.True(snapshot.Verified);
        Assert.Equal(@"D:\Synthetic Arena", snapshot.InstallPath);
        Assert.Equal("0.6.1.0.48206", snapshot.ManifestVersion);
        Assert.Equal(ArenaInstallCause.None, snapshot.Cause);
        Assert.Equal("registry", snapshot.Source);
    }

    [Theory]
    [InlineData(ArenaInstallCause.NotRegistered, "none")]
    [InlineData(ArenaInstallCause.FolderMissing, "override")]
    [InlineData(ArenaInstallCause.FilesMismatch, "registry")]
    public void ANotVerifiedCheckCarriesItsCauseAndSource(ArenaInstallCause cause, string source)
    {
        ClientGate.Start("", _ => Missing(cause, source, 9));
        Assert.True(ClientGate.WaitForResult(Patience));
        Assert.Equal(GateState.NotVerified, ClientGate.State);
        GateSnapshot snapshot = GateSnapshot.Read();
        Assert.Equal(GateState.NotVerified, snapshot.State);
        Assert.Equal(cause, snapshot.Cause);
        Assert.Equal(source, snapshot.Source);
        Assert.Equal(9, snapshot.MismatchCount);
        Assert.Equal("the reason sentence", snapshot.Reason);
        Assert.True(MapNoticeInputs.ClassifyClient(snapshot, out _, out _));
    }

    [Fact]
    public void ACheckThatThrewIsCheckFailedWithItsDetail()
    {
        ClientGate.Start("", _ => throw new InvalidOperationException("boom"));
        Assert.True(ClientGate.WaitForResult(Patience));
        Assert.Equal(GateState.CheckFailed, ClientGate.State);
        Assert.Equal("InvalidOperationException: boom", ClientGate.FailureDetail);
        GateSnapshot snapshot = GateSnapshot.Read();
        Assert.Equal(GateState.CheckFailed, snapshot.State);
        Assert.Equal("InvalidOperationException: boom", snapshot.FailureDetail);
        Assert.True(MapNoticeInputs.ClassifyClient(snapshot, out NoticeId id, out _));
        Assert.Equal(NoticeId.ClientCheckFailed, id);
    }

    [Fact]
    public void ACheckThatReturnedNothingIsCheckFailed()
    {
        ClientGate.Start("", _ => null);
        Assert.True(ClientGate.WaitForResult(Patience));
        Assert.Equal(GateState.CheckFailed, ClientGate.State);
        Assert.Equal("the check returned no result", ClientGate.FailureDetail);
    }

    [Fact]
    public void ABundleWaitThatRanOutLeavesTheGateTimedOutAndALaterResultIsHonoured()
    {
        ClientGate.BundleWait = TimeSpan.FromMilliseconds(200);
        using var release = new ManualResetEventSlim(false);
        ClientGate.Start("", _ => { release.Wait(Patience); return Found(); });
        Assert.False(ClientGate.EnsureVerified()); // the bundle request waits, fails closed and latches
        Assert.Equal(GateState.TimedOut, ClientGate.State);
        Assert.Equal(GateState.TimedOut, GateSnapshot.Read().State);

        release.Set();
        Assert.True(ClientGate.WaitForResult(Patience));
        Assert.Equal(GateState.Verified, ClientGate.State);
    }

    [Fact]
    public void ReadingTheStateLogsAndLatchesNothing()
    {
        ClientGate.Start("", _ => Missing(ArenaInstallCause.NotRegistered));
        Assert.True(ClientGate.WaitForResult(Patience));
        int errors = Plugin.Log.Errors.Count;
        int warnings = Plugin.Log.Warnings.Count;
        for (int i = 0; i < 20; i++)
        {
            _ = ClientGate.State;
            _ = ClientGate.FailureDetail;
            GateSnapshot.Read();
        }
        Assert.Equal(errors, Plugin.Log.Errors.Count);
        Assert.Equal(warnings, Plugin.Log.Warnings.Count);
        Assert.False(ClientGate.OutcomePending);
        // the outcome line is still unlogged: only LogOutcome, a bundle request or the poll writes it
        Assert.StartsWith("[Arena]", ClientGate.OutcomeLine(ClientGate.Result));
    }

    [Fact]
    public void AStateThatContradictsItsResultIsNormalised()
    {
        // the three gate members are read one after the other; a check that finishes between the reads must not produce a contradiction
        GateSnapshot snapshot = GateSnapshot.From(GateState.Running, Missing(ArenaInstallCause.FilesMismatch), null);
        Assert.Equal(GateState.NotVerified, snapshot.State);
        Assert.Equal(ArenaInstallCause.FilesMismatch, snapshot.Cause);
    }
}
