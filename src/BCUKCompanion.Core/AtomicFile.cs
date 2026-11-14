using System.Diagnostics;
using System.Text;

namespace BCUKCompanion.Core;

/// <summary>
/// Writes files so that a crash or power cut mid-save leaves either the old file or the new
/// one, never an empty or half-written one: the contents go to <c>&lt;path&gt;.tmp</c>, are
/// flushed to disk, and only then replace the file. On Linux the containing directory isn't
/// synced after the swap, so there a power cut just after a save can still bring back the old file.
/// </summary>
public static class AtomicFile
{
    /// <summary>The suffix of the temp file a save writes before swapping it in.</summary>
    public const string TempSuffix = ".tmp";

    /// <summary>The suffix of the copy <see cref="TryBackUp"/> makes.</summary>
    public const string BackupSuffix = ".bak";

    private static readonly UTF8Encoding Utf8NoBom = new(encoderShouldEmitUTF8Identifier: false);

    /// <summary>Replaces <paramref name="path"/> with <paramref name="contents"/> as UTF-8 (no BOM), creating its folder if needed.</summary>
    /// <param name="path">The file to write.</param>
    /// <param name="contents">The new text of the file.</param>
    public static void WriteAllText(string path, string contents) => WriteAllBytes(path, Utf8NoBom.GetBytes(contents));

    /// <summary>
    /// Replaces <paramref name="path"/> with <paramref name="contents"/>, creating its folder if
    /// needed. If the write fails, the existing file is untouched and the temp file is deleted.
    /// </summary>
    /// <param name="path">The file to write.</param>
    /// <param name="contents">The new contents of the file.</param>
    public static void WriteAllBytes(string path, byte[] contents)
    {
        string fullPath = Path.GetFullPath(path);
        // A full path always has a parent folder unless it's a root, which can't be a file.
        Directory.CreateDirectory(Path.GetDirectoryName(fullPath)!);

        string tempPath = fullPath + TempSuffix;
        try
        {
            using (var stream = new FileStream(tempPath, FileMode.Create, FileAccess.Write, FileShare.None))
            {
                stream.Write(contents, 0, contents.Length);
                stream.Flush(flushToDisk: true);
            }

            File.Move(tempPath, fullPath, overwrite: true);
        }
        catch
        {
            TryDelete(tempPath);
            throw;
        }
    }

    /// <summary>Copies <paramref name="path"/> to <c>&lt;path&gt;.bak</c>, e.g. before a corrupt file is replaced, so it can be recovered by hand.</summary>
    /// <param name="path">The file to back up.</param>
    /// <returns>True if the copy was made; false (after tracing why) if it couldn't be.</returns>
    public static bool TryBackUp(string path)
    {
        try
        {
            File.Copy(path, path + BackupSuffix, overwrite: true);
            return true;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or NotSupportedException or ArgumentException)
        {
            Trace.TraceWarning("Couldn't back up '{0}': {1}", path, ex);
            return false;
        }
    }

    /// <summary>Deletes <paramref name="path"/> if it can, ignoring a file that's missing, locked or not ours to delete.</summary>
    /// <param name="path">The file to delete.</param>
    private static void TryDelete(string path)
    {
        try
        {
            File.Delete(path);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            Trace.TraceWarning("Couldn't delete '{0}': {1}", path, ex);
        }
    }
}
