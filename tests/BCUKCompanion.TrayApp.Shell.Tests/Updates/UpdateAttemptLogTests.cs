using BCUKCompanion.TrayApp.Updates;
using Xunit;

namespace BCUKCompanion.TrayApp.Tests.Updates;

public sealed class UpdateAttemptLogTests : IDisposable
{
    private readonly string folder = Path.Combine(Path.GetTempPath(), $"bcuk-update-attempts-{Guid.NewGuid():N}");

    public UpdateAttemptLogTests() => Directory.CreateDirectory(folder);

    public void Dispose() => Directory.Delete(folder, recursive: true);

    [Fact]
    public void MarkReported_RewritesTheRecordAsReported()
    {
        var log = new UpdateAttemptLog(Path.Combine(folder, "update-result.json"));
        var attempt = new UpdateAttempt("1.2.0", UpdateAttempt.Restored, "C:\\work");

        log.MarkReported(attempt);

        Assert.Equal(attempt with { Reported = true }, log.Read());
    }

    [Fact]
    public void MarkReported_FileThatCantBeWritten_IsIgnored()
    {
        // A folder where the temp file goes makes the save fail.
        string path = Path.Combine(folder, "update-result.json");
        Directory.CreateDirectory(path + ".tmp");
        var log = new UpdateAttemptLog(path);

        log.MarkReported(new UpdateAttempt("1.2.0", UpdateAttempt.Installed));

        Assert.Null(log.Read());
    }
}
