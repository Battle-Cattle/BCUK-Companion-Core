using System.Globalization;

namespace BCUKCompanion.TrayApp.Updates;

/// <summary>What <see cref="UpdateInstaller.ApplyUpdateScript"/> needs to swap a staged build in.</summary>
/// <param name="ProcessId">The running app's process id; the script waits for it to exit.</param>
/// <param name="Source">The folder the new build was extracted to.</param>
/// <param name="Target">The install folder to copy it over.</param>
/// <param name="Exe">The executable to relaunch afterwards.</param>
/// <param name="WorkDir">The attempt's work folder, which holds the backup and any failure logs.</param>
/// <param name="Version">The version being installed, recorded in <paramref name="ResultFile"/>; null to record none.</param>
/// <param name="ResultFile">Where the script records how it went (see <see cref="UpdateAttemptLog"/>); null to record nothing.</param>
internal sealed record ApplyUpdateRequest(
    int ProcessId, string Source, string Target, string Exe, string WorkDir, string? Version = null, string? ResultFile = null)
{
    /// <summary>The script's named arguments, each value its own argument so no path is ever parsed as script text.</summary>
    /// <returns>The <c>-Name value</c> pairs, in the order the script declares them.</returns>
    public IReadOnlyList<string> ToScriptArguments() =>
    [
        "-ProcessId", ProcessId.ToString(CultureInfo.InvariantCulture),
        "-Source", Source,
        "-Target", Target,
        "-Exe", Exe,
        "-WorkDir", WorkDir,
        .. Version is null ? Array.Empty<string>() : ["-Version", Version],
        .. ResultFile is null ? Array.Empty<string>() : ["-ResultFile", ResultFile],
    ];
}
