using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.IO.Compression;
using BCUKCompanion.Core.Updates;

namespace BCUKCompanion.TrayApp.Updates;

/// <summary>
/// Installs a downloaded release in place: the zip is extracted to a temp folder, then a small
/// PowerShell script waits for this process to exit, copies the new files over the install folder
/// and relaunches the app. No WPF dependency, so <see cref="CompanionUpdater"/> owns all prompts.
/// </summary>
internal sealed class UpdateInstaller(GitHubReleaseUpdateChecker checker, string dataFolderName)
{
    /// <summary>
    /// Downloads and extracts <paramref name="update"/>, then starts the script that swaps it in
    /// once this process exits. The caller must shut the app down straight after this returns.
    /// Throws <see cref="UnauthorizedAccessException"/> if the install folder isn't writable (e.g.
    /// under Program Files) so the caller can point the user at a manual download instead.
    /// </summary>
    public async Task PrepareAndLaunchAsync(AvailableUpdate update, CancellationToken cancellationToken = default)
    {
        string installDir = AppContext.BaseDirectory.TrimEnd(Path.DirectorySeparatorChar);
        string exePath = Environment.ProcessPath ?? throw new InvalidOperationException("Can't determine the running executable's path.");
        EnsureWritable(installDir);

        // A fresh folder per attempt: a failed install keeps its work folder (with the backup of
        // the previous build) for manual recovery, and a later attempt must not delete it.
        string workDir = Path.Combine(
            Path.GetTempPath(),
            $"{dataFolderName}.Update",
            $"{update.TagName}-{DateTime.UtcNow.ToString("yyyyMMddHHmmss", CultureInfo.InvariantCulture)}");
        string stagingDir = Path.Combine(workDir, "files");
        Directory.CreateDirectory(stagingDir);

        string zipPath = Path.Combine(workDir, "update.zip");
        await checker.DownloadAsync(update, zipPath, cancellationToken).ConfigureAwait(false);

        ZipFile.ExtractToDirectory(zipPath, stagingDir);
        if (!File.Exists(Path.Combine(stagingDir, Path.GetFileName(exePath))))
        {
            throw new InvalidDataException($"The downloaded update doesn't contain {Path.GetFileName(exePath)}.");
        }

        string scriptPath = Path.Combine(workDir, "apply-update.ps1");
        await File.WriteAllTextAsync(scriptPath, ApplyUpdateScript, cancellationToken).ConfigureAwait(false);

        // Paths go in as -File arguments, never spliced into the script text.
        var startInfo = new ProcessStartInfo("powershell.exe")
        {
            UseShellExecute = false,
            CreateNoWindow = true,
        };
        foreach (string arg in new[]
        {
            "-NoProfile", "-ExecutionPolicy", "Bypass", "-WindowStyle", "Hidden", "-File", scriptPath,
            "-ProcessId", Environment.ProcessId.ToString(CultureInfo.InvariantCulture),
            "-Source", stagingDir, "-Target", installDir, "-Exe", exePath, "-WorkDir", workDir,
        })
        {
            startInfo.ArgumentList.Add(arg);
        }

        Process.Start(startInfo)?.Dispose();
    }

    // Waits for the app to exit (releasing its file locks and single-instance mutex), backs up the
    // install folder, then copies the new build over it, retrying in case antivirus briefly holds
    // a file. If anything fails part-way, the backup is copied back so the previous version still
    // starts, and the work folder (with the backup) is kept for manual recovery. If the app never
    // exits, nothing is touched. Files the new build no longer ships are left in place rather than
    // mirroring, so nothing a user dropped into the folder gets deleted.
    private const string ApplyUpdateScript = """
        param(
            [int]$ProcessId,
            [string]$Source,
            [string]$Target,
            [string]$Exe,
            [string]$WorkDir
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

        try { Wait-Process -Id $ProcessId -Timeout 60 } catch { }
        if (Get-Process -Id $ProcessId -ErrorAction SilentlyContinue) {
            exit 1
        }

        $backup = Join-Path $WorkDir 'backup'
        $updated = $false
        try {
            New-Item -ItemType Directory -Path $backup -Force | Out-Null
            Copy-Tree $Target $backup
            Copy-Tree $Source $Target
            $updated = $true
        } catch {
            try { Copy-Tree $backup $Target } catch { }
        }

        Start-Process -FilePath $Exe -WorkingDirectory $Target
        if ($updated) {
            Remove-Item -Path $WorkDir -Recurse -Force -ErrorAction SilentlyContinue
        }
        """;

    private static void EnsureWritable(string directory)
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
