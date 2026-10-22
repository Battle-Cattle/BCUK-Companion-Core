using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.IO.Compression;
using BCUKCompanion.Core.Updates;

namespace BCUKCompanion.TrayApp.Updates;

/// <summary>
/// Installs a downloaded release in place: the zip is extracted to a temp folder, then a small
/// PowerShell script waits for this process to exit, copies the new files over the install folder
/// and relaunches the app. No WPF dependency: <see cref="CompanionUpdater"/> owns all prompts, through <see cref="IUpdateUi"/>.
/// </summary>
internal sealed class UpdateInstaller(GitHubReleaseUpdateChecker checker, string dataFolderName)
{
    /// <summary>
    /// Downloads and extracts <paramref name="update"/>, then starts the script that swaps it in
    /// once this process exits. The caller must shut the app down straight after this returns.
    /// Throws <see cref="UnauthorizedAccessException"/> if the install folder isn't writable (e.g.
    /// under Program Files) so the caller can point the user at a manual download instead.
    /// </summary>
    /// <param name="update">The release to download and install.</param>
    /// <param name="cancellationToken">Cancels the download and extraction.</param>
    /// <returns>A task that completes once the swap script has been started.</returns>
    public async Task PrepareAndLaunchAsync(AvailableUpdate update, CancellationToken cancellationToken = default)
    {
        string installDir = AppContext.BaseDirectory.TrimEnd(Path.DirectorySeparatorChar);
        string exePath = Environment.ProcessPath ?? throw new InvalidOperationException("Can't determine the running executable's path.");
        EnsureWritable(installDir);

        // A fresh folder per attempt: a failed install keeps its work folder (with the backup of
        // the previous build) for manual recovery, and a later attempt must not delete it. Named
        // from the parsed version, not the raw tag, so release data never shapes the path.
        string workDir = Path.Combine(
            Path.GetTempPath(),
            $"{dataFolderName}.Update",
            $"v{update.Version.ToString(3)}-{DateTime.UtcNow.ToString("yyyyMMddHHmmss", CultureInfo.InvariantCulture)}");
        string stagingDir = Path.Combine(workDir, "files");
        Directory.CreateDirectory(stagingDir);

        string zipPath = Path.Combine(workDir, "update.zip");
        await checker.DownloadAsync(update, zipPath, cancellationToken).ConfigureAwait(false);
        ExtractAndVerify(zipPath, stagingDir, Path.GetFileName(exePath));

        string scriptPath = Path.Combine(workDir, "apply-update.ps1");
        await File.WriteAllTextAsync(scriptPath, ApplyUpdateScript, cancellationToken).ConfigureAwait(false);

        // Paths go in as -File arguments, never spliced into the script text.
        var startInfo = new ProcessStartInfo("powershell.exe")
        {
            UseShellExecute = false,
            CreateNoWindow = true,
        };
        foreach (string arg in new[] { "-NoProfile", "-ExecutionPolicy", "Bypass", "-WindowStyle", "Hidden", "-File", scriptPath }
            .Concat(new ApplyUpdateRequest(Environment.ProcessId, stagingDir, installDir, exePath, workDir).ToScriptArguments()))
        {
            startInfo.ArgumentList.Add(arg);
        }

        Process.Start(startInfo)?.Dispose();
    }

    /// <summary>Extracts the downloaded zip and checks it holds the app's executable, so a wrong or broken release is never swapped in.</summary>
    /// <param name="zipPath">The downloaded, checksum-verified release zip.</param>
    /// <param name="stagingDir">The empty folder to extract it into.</param>
    /// <param name="exeName">The running executable's file name, e.g. <c>MyApp.exe</c>.</param>
    /// <exception cref="InvalidDataException">The zip doesn't contain <paramref name="exeName"/> at its root.</exception>
    internal static void ExtractAndVerify(string zipPath, string stagingDir, string exeName)
    {
        ZipFile.ExtractToDirectory(zipPath, stagingDir);
        if (!File.Exists(Path.Combine(stagingDir, exeName)))
        {
            throw new InvalidDataException($"The downloaded update doesn't contain {exeName}.");
        }
    }

    /// <summary>
    /// Waits for the app to exit (releasing its file locks and single-instance mutex), backs up the
    /// install folder, then copies the new build over it, retrying in case antivirus briefly holds
    /// a file. If anything fails part-way, the backup is copied back so the previous version still
    /// starts, and the work folder (with the backup and an update-failed.log) is kept for manual
    /// recovery. If the restore fails too, the folder may hold a mix of both builds, so the app is
    /// not relaunched; restore-failed.log records why. If the app hasn't exited within WaitSeconds,
    /// nothing is touched. Exits 1 when it doesn't relaunch the app, 0 when it does.
    /// Files the new build no longer ships are left in place rather than mirroring, so nothing a
    /// user dropped into the folder gets deleted; the leftovers of an older multi-file build are
    /// removed by the new build itself on launch (LegacyInstallCleanup).
    /// </summary>
    internal const string ApplyUpdateScript = """
        param(
            [int]$ProcessId,
            [string]$Source,
            [string]$Target,
            [string]$Exe,
            [string]$WorkDir,
            [int]$WaitSeconds = 60
        )
        $ErrorActionPreference = 'Stop'

        function Copy-Tree([string]$From, [string]$To) {
            for ($attempt = 1; $attempt -le 10; $attempt++) {
                try {
                    Copy-Item -Path (Join-Path $From '*') -Destination $To -Recurse -Force
                    return
                } catch {
                    if ($attempt -eq 10) { throw }
                    Start-Sleep -Seconds 1
                }
            }
        }

        # Best-effort: a log that can't be written must not stop the restore that follows it.
        function Write-Log([string]$Name, $Record) {
            try { $Record | Out-File -FilePath (Join-Path $WorkDir $Name) -Force } catch { }
        }

        try { Wait-Process -Id $ProcessId -Timeout $WaitSeconds } catch { }
        if (Get-Process -Id $ProcessId -ErrorAction SilentlyContinue) {
            exit 1
        }

        $backup = Join-Path $WorkDir 'backup'
        $updated = $false
        $restored = $false
        try {
            New-Item -ItemType Directory -Path $backup -Force | Out-Null
            Copy-Tree $Target $backup
            Copy-Tree $Source $Target
            $updated = $true
        } catch {
            Write-Log 'update-failed.log' $_
            try {
                Copy-Tree $backup $Target
                $restored = $true
            } catch {
                Write-Log 'restore-failed.log' $_
            }
        }

        if (-not $updated -and -not $restored) {
            exit 1
        }

        Start-Process -FilePath $Exe -WorkingDirectory $Target
        if ($updated) {
            Remove-Item -Path $WorkDir -Recurse -Force -ErrorAction SilentlyContinue
        }
        exit 0
        """;

    /// <summary>Throws <see cref="UnauthorizedAccessException"/> unless a file can be created in <paramref name="directory"/>, so an install under e.g. Program Files fails before anything is downloaded.</summary>
    /// <param name="directory">The install folder to probe with a temporary file.</param>
    internal static void EnsureWritable(string directory)
    {
        string probe = Path.Combine(directory, $".update-probe-{Guid.NewGuid():N}");
        try
        {
            File.WriteAllText(probe, string.Empty);
            File.Delete(probe);
        }
        catch (Exception ex) when (ex is UnauthorizedAccessException or IOException)
        {
            throw new UnauthorizedAccessException($"Can't write to the install folder ({directory}).", ex);
        }
    }
}
