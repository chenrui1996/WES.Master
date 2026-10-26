using Wes.Platform.Domain;
using Xunit;

namespace Wes.Modules.Tests.Platform;

public sealed class QuarantinedFileTests
{
    private static readonly DateTimeOffset Now = new(2026, 10, 26, 0, 0, 0, TimeSpan.Zero);
    private const string Digest = "0123456789ABCDEF0123456789ABCDEF0123456789ABCDEF0123456789ABCDEF";

    [Fact]
    public void StorageConfirmationAndCurrentCleanEvidenceAreRequired()
    {
        var file = Create();
        Assert.Throws<InvalidOperationException>(() => file.BeginScan(Now));
        file.ConfirmStored(Digest, 5, Now);
        var attempt = file.BeginScan(Now);
        Assert.False(file.CompleteScan(Guid.NewGuid(), FileScanVerdict.Clean, Digest, 5, Now.AddSeconds(1)));
        Assert.Equal(QuarantinedFileState.Quarantined, file.State);
        Assert.True(file.CompleteScan(attempt, FileScanVerdict.Clean, Digest, 5, Now.AddSeconds(1)));
        Assert.Equal(QuarantinedFileState.Available, file.State);
    }

    [Fact]
    public void CleanEvidenceMustMatchActualStoredObject()
    {
        var file = Stored();
        var attempt = file.BeginScan(Now);
        Assert.Throws<ArgumentException>(() =>
            file.CompleteScan(attempt, FileScanVerdict.Clean, new string('A', 64), 5, Now));
        Assert.Throws<ArgumentException>(() =>
            file.CompleteScan(attempt, FileScanVerdict.Clean, Digest, 4, Now));
        Assert.Equal(QuarantinedFileState.Quarantined, file.State);
    }

    [Theory]
    [InlineData(FileScanVerdict.Unavailable, QuarantinedFileState.Quarantined)]
    [InlineData(FileScanVerdict.TimedOut, QuarantinedFileState.Quarantined)]
    [InlineData(FileScanVerdict.Infected, QuarantinedFileState.Rejected)]
    public void ScanFailureNeverMakesFileAvailable(FileScanVerdict verdict, QuarantinedFileState expected)
    {
        var file = Stored();
        var attempt = file.BeginScan(Now);
        Assert.True(file.CompleteScan(attempt, verdict, Digest, 5, Now.AddSeconds(1)));
        Assert.Equal(expected, file.State);
        Assert.Throws<InvalidOperationException>(() => file.AddReference(Now.AddSeconds(2)));
    }

    [Theory]
    [InlineData(599999999, true)]
    [InlineData(600000000, false)]
    [InlineData(600000001, false)]
    public void ScanDeadlineRejectsCleanAtExactlySixtySeconds(long ticks, bool available)
    {
        var file = Stored();
        var attempt = file.BeginScan(Now);
        file.CompleteScan(attempt, FileScanVerdict.Clean, Digest, 5, Now.AddTicks(ticks));
        Assert.Equal(available ? QuarantinedFileState.Available : QuarantinedFileState.Quarantined, file.State);
    }

    [Fact]
    public void RetryFencesOldSuccessAndFailure()
    {
        var file = Stored();
        var first = file.BeginScan(Now);
        Assert.Throws<InvalidOperationException>(() => file.BeginScan(Now.AddSeconds(59)));
        var current = file.BeginScan(Now.AddSeconds(60));
        Assert.False(file.CompleteScan(first, FileScanVerdict.Clean, Digest, 5, Now.AddSeconds(61)));
        Assert.False(file.CompleteScan(first, FileScanVerdict.Infected, Digest, 5, Now.AddSeconds(61)));
        Assert.True(file.CompleteScan(current, FileScanVerdict.Clean, Digest, 5, Now.AddSeconds(61)));
        Assert.Equal(QuarantinedFileState.Available, file.State);
        Assert.False(file.CompleteScan(current, FileScanVerdict.Infected, Digest, 5, Now.AddSeconds(62)));
        Assert.Equal(QuarantinedFileState.Available, file.State);
    }

    [Fact]
    public void ProcessingProblemBeginsAtExactlyTenMinutesAndDoesNotChangeState()
    {
        var file = Stored();
        Assert.False(file.HasProcessingProblem(Now.AddMinutes(10).AddTicks(-1)));
        Assert.True(file.HasProcessingProblem(Now.AddMinutes(10)));
        Assert.Equal(QuarantinedFileState.Quarantined, file.State);
    }

    [Fact]
    public void ReferencedFileIsNotAnOrphanEvenAfterOneDay()
    {
        var file = Available();
        file.AddReference(Now.AddSeconds(2));
        Assert.Throws<InvalidOperationException>(() => file.BeginDeletion(true, Now.AddDays(2)));
        file.RemoveReference(Now.AddDays(2));
        Assert.Throws<InvalidOperationException>(() => file.BeginDeletion(false, Now.AddDays(2)));
        file.BeginDeletion(true, Now.AddDays(2));
        Assert.Equal(QuarantinedFileState.Deleting, file.State);
        Assert.Throws<InvalidOperationException>(() => file.AddReference(Now.AddDays(2)));
        Assert.Throws<InvalidOperationException>(() => file.BeginScan(Now.AddDays(2)));
        file.ConfirmDeleted(Now.AddDays(2));
        Assert.Equal(QuarantinedFileState.Deleted, file.State);
        Assert.Throws<InvalidOperationException>(() => file.ConfirmStored(Digest, 5, Now.AddDays(2)));
    }

    [Fact]
    public void OrphanThresholdIsInclusiveAndActiveScanBlocksDeletion()
    {
        var file = Stored();
        Assert.Throws<InvalidOperationException>(() => file.BeginDeletion(true, Now.AddDays(1).AddTicks(-1)));
        file.BeginDeletion(true, Now.AddDays(1));
        var scanning = Stored();
        scanning.BeginScan(Now.AddDays(1));
        Assert.Throws<InvalidOperationException>(() => scanning.BeginDeletion(true, Now.AddDays(1).AddSeconds(59)));
        scanning.BeginDeletion(true, Now.AddDays(1).AddSeconds(60));
    }

    [Fact]
    public void InvalidClockVerdictAndReferenceUnderflowAreExplicit()
    {
        var file = Stored();
        var attempt = file.BeginScan(Now);
        Assert.Throws<ArgumentOutOfRangeException>(() =>
            file.CompleteScan(attempt, (FileScanVerdict)100, Digest, 5, Now));
        Assert.Throws<ArgumentOutOfRangeException>(() =>
            file.CompleteScan(attempt, FileScanVerdict.Clean, Digest, 5, Now.AddTicks(-1)));
        Assert.Throws<InvalidOperationException>(() => file.RemoveReference(Now));
        Assert.Throws<InvalidOperationException>(() => file.ConfirmDeleted(Now));
        Assert.Throws<ArgumentOutOfRangeException>(() => file.HasProcessingProblem(Now.AddTicks(-1)));
    }

    [Fact]
    public void StorageMismatchDoesNotSetConfirmation()
    {
        var file = Create();
        Assert.Throws<ArgumentException>(() => file.ConfirmStored(Digest, 6, Now));
        Assert.Throws<InvalidOperationException>(() => file.BeginScan(Now));
    }

    [Theory]
    [InlineData(0)]
    [InlineData(20971521)]
    public void InvalidLengthIsRejected(long length)
    {
        Assert.Throws<ArgumentOutOfRangeException>(() =>
            new QuarantinedFile(Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid(), "test.txt", "text/plain", length, Digest, Now));
    }

    [Fact]
    public void UnsafeIdentityMetadataAndDigestAreRejected()
    {
        Assert.Throws<ArgumentException>(() =>
            new QuarantinedFile(Guid.Empty, Guid.NewGuid(), Guid.NewGuid(), "test.txt", "text/plain", 5, Digest, Now));
        Assert.Throws<ArgumentException>(() =>
            new QuarantinedFile(Guid.NewGuid(), Guid.Empty, Guid.NewGuid(), "test.txt", "text/plain", 5, Digest, Now));
        Assert.Throws<ArgumentException>(() =>
            new QuarantinedFile(Guid.NewGuid(), Guid.NewGuid(), Guid.Empty, "test.txt", "text/plain", 5, Digest, Now));
        Assert.Throws<ArgumentException>(() =>
            new QuarantinedFile(Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid(), "..\\test.txt", "text/plain", 5, Digest, Now));
        Assert.Throws<ArgumentException>(() =>
            new QuarantinedFile(Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid(), "test.txt", "text/plain", 5, "bad", Now));
    }

    private static QuarantinedFile Create() =>
        new(Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid(), "test.txt", "text/plain", 5, Digest, Now);

    private static QuarantinedFile Stored()
    {
        var file = Create();
        file.ConfirmStored(Digest, 5, Now);
        return file;
    }

    private static QuarantinedFile Available()
    {
        var file = Stored();
        var attempt = file.BeginScan(Now);
        file.CompleteScan(attempt, FileScanVerdict.Clean, Digest, 5, Now.AddSeconds(1));
        return file;
    }
}
