using Xunit;

namespace BCUKCompanion.Core.Tests;

public sealed class AtomicFileTests : IDisposable
{
    private readonly string folder = Path.Combine(Path.GetTempPath(), $"AtomicFileTests.{Guid.NewGuid():N}");

    public void Dispose()
    {
        if (Directory.Exists(folder))
        {
            Directory.Delete(folder, recursive: true);
        }
    }

    [Fact]
    public void WriteAllText_CreatesTheFolderAndReplacesTheFile()
    {
        string path = Path.Combine(folder, "nested", "file.json");

        AtomicFile.WriteAllText(path, "first");
        AtomicFile.WriteAllText(path, "second ✓");

        Assert.Equal("second ✓", File.ReadAllText(path));
        Assert.False(File.Exists(path + AtomicFile.TempSuffix));
        Assert.NotEqual(0xEF, File.ReadAllBytes(path)[0]); // No UTF-8 BOM.
    }

    // Runs on Windows too: there the move onto a folder fails with UnauthorizedAccessException, not IOException.
    [Fact]
    [Trait("Platform", "Windows")]
    public void WriteAllBytes_FailedSwap_LeavesNoTempFileBehind()
    {
        // A folder where the file should go makes the final move fail after the temp file is written.
        string path = Path.Combine(folder, "file.json");
        Directory.CreateDirectory(path);

        Exception ex = Assert.ThrowsAny<Exception>(() => AtomicFile.WriteAllBytes(path, [1, 2, 3]));

        Assert.True(ex is IOException or UnauthorizedAccessException, ex.ToString());

        Assert.False(File.Exists(path + AtomicFile.TempSuffix));
        Assert.True(Directory.Exists(path));
    }

    [Fact]
    public void WriteAllBytes_TempPathThatCantBeWrittenOrDeleted_ThrowsAndLeavesItAlone()
    {
        // A folder where the temp file should go: the write fails, and so does the cleanup delete.
        string path = Path.Combine(folder, "file.json");
        Directory.CreateDirectory(path + AtomicFile.TempSuffix);

        Exception ex = Assert.ThrowsAny<Exception>(() => AtomicFile.WriteAllBytes(path, [1]));

        Assert.True(ex is IOException or UnauthorizedAccessException, ex.ToString());
        Assert.True(Directory.Exists(path + AtomicFile.TempSuffix));
        Assert.False(File.Exists(path));
    }

    [Fact]
    public void TryBackUp_CopiesTheFileAlongside()
    {
        string path = Path.Combine(folder, "file.json");
        AtomicFile.WriteAllText(path, "keep me");

        Assert.True(AtomicFile.TryBackUp(path));

        Assert.Equal("keep me", File.ReadAllText(path + AtomicFile.BackupSuffix));
        Assert.Equal("keep me", File.ReadAllText(path));
    }

    [Fact]
    public void TryBackUp_MissingFile_ReturnsFalse()
    {
        Assert.False(AtomicFile.TryBackUp(Path.Combine(folder, "missing.json")));
    }
}
