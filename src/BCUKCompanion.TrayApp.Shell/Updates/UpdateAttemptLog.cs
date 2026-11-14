using System.IO;
using System.Text.Json;
using System.Text.Json.Serialization;
using BCUKCompanion.Core;

namespace BCUKCompanion.TrayApp.Updates;

/// <summary>What became of the last update the apply script ran, as it recorded it.</summary>
/// <param name="Version">The version it tried to install, e.g. <c>1.2.0</c>.</param>
/// <param name="Outcome">One of the <see cref="UpdateAttempt"/> outcome constants.</param>
/// <param name="WorkDir">The attempt's work folder, which keeps the logs and backup when it failed.</param>
/// <param name="Reported">Whether the app has already told the user about this attempt.</param>
internal sealed record UpdateAttempt(
    [property: JsonPropertyName("version")] string Version,
    [property: JsonPropertyName("outcome")] string Outcome,
    [property: JsonPropertyName("workDir")] string? WorkDir = null,
    [property: JsonPropertyName("reported")] bool Reported = false)
{
    /// <summary>The new build was copied in and started.</summary>
    public const string Installed = "installed";

    /// <summary>Copying the new build failed, so the previous one was restored and started again.</summary>
    public const string Restored = "restored";

    /// <summary>Copying the new build failed and so did restoring the previous one; nothing was started.</summary>
    public const string Failed = "failed";

    /// <summary>The app didn't close in time, so the script left the install untouched.</summary>
    public const string NotExited = "notExited";

    /// <summary>The download, checksum check or extraction failed, so the swap script never ran. The app reports this itself, at once.</summary>
    public const string PrepareFailed = "prepareFailed";

    /// <summary>Whether the attempt installed its version.</summary>
    public bool Succeeded => Outcome == Installed;

    /// <summary>A sentence saying why the attempt failed, for the failure balloon.</summary>
    /// <returns>The reason, or a generic one for an outcome this build doesn't know.</returns>
    public string DescribeFailure() => Outcome switch
    {
        Restored => "its files couldn't be copied in, so the previous version was put back.",
        Failed => "its files couldn't be copied in, and the previous version couldn't be fully put back. Reinstall from the release page.",
        NotExited => "the app didn't close in time to be replaced.",
        PrepareFailed => "the download couldn't be prepared.",
        _ => "the update didn't finish.",
    };
}

/// <summary>
/// The file the apply script records its outcome in (<c>AppPaths.UpdateResultFile</c> for the tray app),
/// so the next launch can report it and not retry a version that just failed to install.
/// </summary>
internal sealed class UpdateAttemptLog
{
    /// <summary>Creates a log stored at <paramref name="path"/>.</summary>
    /// <param name="path">The result file the apply script writes.</param>
    public UpdateAttemptLog(string path)
    {
        Path = path;
    }

    /// <summary>The result file the apply script writes.</summary>
    public string Path { get; }

    /// <summary>Reads the last attempt the script recorded.</summary>
    /// <returns>The attempt, or null when there's none or the file can't be read or parsed.</returns>
    public UpdateAttempt? Read()
    {
        try
        {
            return File.Exists(Path) ? JsonSerializer.Deserialize<UpdateAttempt>(File.ReadAllText(Path)) : null;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException)
        {
            return null;
        }
    }

    /// <summary>Records that the user has been told about <paramref name="attempt"/>, so it isn't reported again.</summary>
    /// <param name="attempt">The attempt just reported.</param>
    public void MarkReported(UpdateAttempt attempt) => Write(attempt with { Reported = true });

    /// <summary>Saves <paramref name="attempt"/> as the last attempt, best-effort.</summary>
    /// <param name="attempt">The attempt to record.</param>
    public void Write(UpdateAttempt attempt)
    {
        try
        {
            AtomicFile.WriteAllText(Path, JsonSerializer.Serialize(attempt));
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // Best-effort: the worst case is the same balloon again next launch.
        }
    }
}
