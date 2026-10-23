using System.Diagnostics;
using BCUKCompanion.TrayApp.Updates;
using Xunit;

namespace BCUKCompanion.TrayApp.Tests.Updates;

/// <summary>
/// Runs <see cref="UpdateInstaller.ApplyUpdateScript"/> for real against temp folders: Windows
/// PowerShell on Windows (what the app uses), or PowerShell 7 (<c>pwsh</c>) elsewhere when it's
/// installed. A file held open without write sharing stands in for antivirus or a stuck process.
/// </summary>
public sealed class ApplyUpdateScriptTests : IDisposable
{
    private static readonly TimeSpan ScriptTimeout = TimeSpan.FromMinutes(2);

    private readonly string root = Path.Combine(Path.GetTempPath(), $"bcuk-apply-update-{Guid.NewGuid():N}");
    private readonly string source;
    private readonly string target;
    private readonly string work;
    private readonly string relaunchMarker;

    public ApplyUpdateScriptTests()
    {
        source = Path.Combine(root, "source");
        target = Path.Combine(root, "target");
        work = Path.Combine(root, "work");
        relaunchMarker = Path.Combine(root, "relaunched.txt");
        foreach (string dir in new[] { source, target, work })
        {
            Directory.CreateDirectory(dir);
        }

        File.WriteAllText(Path.Combine(source, "MyApp.exe"), "new");
        File.WriteAllText(Path.Combine(source, "Shared.dll"), "new");
        File.WriteAllText(Path.Combine(source, "Added.dll"), "new");
        File.WriteAllText(Path.Combine(target, "MyApp.exe"), "old");
        File.WriteAllText(Path.Combine(target, "Shared.dll"), "old");
        File.WriteAllText(Path.Combine(target, "user-notes.txt"), "mine");
    }

    public void Dispose()
    {
        try
        {
            Directory.Delete(root, recursive: true);
        }
        catch (IOException)
        {
            // A relaunched marker process may still be finishing; the temp folder is harmless.
        }
    }

    [SkippableFact]
    public async Task ExitedApp_SwapsInTheNewBuildAndRelaunches()
    {
        Process script = StartScript(ExitedProcessId());

        Assert.Equal(0, await WaitForExitAsync(script));

        Assert.Equal("new", File.ReadAllText(Path.Combine(target, "MyApp.exe")));
        Assert.Equal("new", File.ReadAllText(Path.Combine(target, "Shared.dll")));
        Assert.Equal("new", File.ReadAllText(Path.Combine(target, "Added.dll")));
        Assert.Equal("mine", File.ReadAllText(Path.Combine(target, "user-notes.txt")));
        Assert.False(Directory.Exists(work));
        await AssertRelaunchedAsync();
    }

    [SkippableFact]
    public async Task CopyFails_RestoresTheBackupAndRelaunchesTheOldBuild()
    {
        Task<int> exit;
        using (FileStream locked = LockAgainstWrites(Path.Combine(target, "Shared.dll")))
        {
            exit = WaitForExitAsync(StartScript(ExitedProcessId()));
            await WaitForFileAsync(Path.Combine(work, "update-failed.log"));
        }

        Assert.Equal(0, await exit);

        Assert.Equal("old", File.ReadAllText(Path.Combine(target, "MyApp.exe")));
        Assert.Equal("old", File.ReadAllText(Path.Combine(target, "Shared.dll")));
        Assert.Equal("mine", File.ReadAllText(Path.Combine(target, "user-notes.txt")));
        Assert.True(File.Exists(Path.Combine(work, "update-failed.log")));
        Assert.Equal("old", File.ReadAllText(Path.Combine(work, "backup", "MyApp.exe")));
        Assert.False(File.Exists(Path.Combine(work, "restore-failed.log")));
        await AssertRelaunchedAsync();
    }

    [SkippableFact]
    public async Task RestoreFailsToo_KeepsBothLogsAndDoesNotRelaunch()
    {
        int exitCode;
        using (FileStream locked = LockAgainstWrites(Path.Combine(target, "Shared.dll")))
        {
            exitCode = await WaitForExitAsync(StartScript(ExitedProcessId()));
        }

        Assert.Equal(1, exitCode);
        Assert.True(File.Exists(Path.Combine(work, "update-failed.log")));
        Assert.True(File.Exists(Path.Combine(work, "restore-failed.log")));
        Assert.True(Directory.Exists(Path.Combine(work, "backup")));
        Assert.False(File.Exists(relaunchMarker));
    }

    [SkippableFact]
    public async Task AppNeverExits_LeavesTheInstallUntouched()
    {
        Process script = StartScript(Environment.ProcessId, "-WaitSeconds", "1");

        Assert.Equal(1, await WaitForExitAsync(script));

        Assert.Equal("old", File.ReadAllText(Path.Combine(target, "MyApp.exe")));
        Assert.False(File.Exists(Path.Combine(target, "Added.dll")));
        Assert.False(Directory.Exists(Path.Combine(work, "backup")));
        Assert.False(File.Exists(relaunchMarker));
    }

    /// <summary>Windows PowerShell on Windows, else pwsh from PATH or the dotnet global tools folder; skips the test if there's none.</summary>
    private static string FindPowerShell()
    {
        if (OperatingSystem.IsWindows())
        {
            string windowsPowerShell = Path.Combine(Environment.SystemDirectory, "WindowsPowerShell", "v1.0", "powershell.exe");
            Skip.IfNot(File.Exists(windowsPowerShell), "Windows PowerShell isn't installed.");
            return windowsPowerShell;
        }

        string toolsDir = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".dotnet", "tools");
        string? pwsh = (Environment.GetEnvironmentVariable("PATH") ?? string.Empty)
            .Split(Path.PathSeparator, StringSplitOptions.RemoveEmptyEntries)
            .Append(toolsDir)
            .Select(dir => Path.Combine(dir, "pwsh"))
            .FirstOrDefault(File.Exists);
        Skip.If(pwsh is null, "PowerShell (pwsh) isn't installed, so the update script can't run here.");
        return pwsh!; // Skip.If above throws when it's null.
    }

    /// <summary>The id of a process that has already exited, as the app's will have when the script runs.</summary>
    private static int ExitedProcessId()
    {
        var startInfo = new ProcessStartInfo(FindPowerShell()) { UseShellExecute = false, CreateNoWindow = true };
        startInfo.ArgumentList.Add("-NoProfile");
        startInfo.ArgumentList.Add("-Command");
        startInfo.ArgumentList.Add("exit");
        using Process process = Process.Start(startInfo)!;
        process.WaitForExit();
        return process.Id;
    }

    private static FileStream LockAgainstWrites(string path) =>
        new(path, FileMode.Open, FileAccess.Read, FileShare.Read);

    private static async Task<int> WaitForExitAsync(Process process)
    {
        using (process)
        {
            using var timeout = new CancellationTokenSource(ScriptTimeout);
            await process.WaitForExitAsync(timeout.Token);
            return process.ExitCode;
        }
    }

    private static async Task WaitForFileAsync(string path)
    {
        var appeared = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        // Every path passed here is a file under root, so it always has a directory.
        using var watcher = new FileSystemWatcher(Path.GetDirectoryName(path)!, Path.GetFileName(path));
        watcher.Created += (_, _) => appeared.TrySetResult();
        watcher.Renamed += (_, _) => appeared.TrySetResult();
        watcher.EnableRaisingEvents = true;

        // Checked after the watcher starts, so a file created before then isn't missed.
        if (File.Exists(path))
        {
            return;
        }

        try
        {
            await appeared.Task.WaitAsync(ScriptTimeout);
        }
        catch (TimeoutException)
        {
            Assert.Fail($"{Path.GetFileName(path)} never appeared.");
        }
    }

    private Process StartScript(int processId, params string[] extraArguments)
    {
        string scriptPath = Path.Combine(root, "apply-update.ps1");
        File.WriteAllText(scriptPath, UpdateInstaller.ApplyUpdateScript);

        var startInfo = new ProcessStartInfo(FindPowerShell()) { UseShellExecute = false, CreateNoWindow = true };
        foreach (string arg in new[] { "-NoProfile", "-ExecutionPolicy", "Bypass", "-File", scriptPath }
            .Concat(new ApplyUpdateRequest(processId, source, target, CreateRelaunchStub(), work).ToScriptArguments())
            .Concat(extraArguments))
        {
            startInfo.ArgumentList.Add(arg);
        }

        return Process.Start(startInfo)!;
    }

    /// <summary>A harmless stand-in for the app's executable that only records that it was started.</summary>
    private string CreateRelaunchStub()
    {
        if (OperatingSystem.IsWindows())
        {
            string cmd = Path.Combine(root, "relaunch.cmd");
            File.WriteAllText(cmd, $"@echo relaunched> \"{relaunchMarker}\"\r\n");
            return cmd;
        }

        string sh = Path.Combine(root, "relaunch.sh");
        File.WriteAllText(sh, $"#!/bin/sh\necho relaunched > '{relaunchMarker}'\n");
        File.SetUnixFileMode(sh, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
        return sh;
    }

    private async Task AssertRelaunchedAsync()
    {
        // Start-Process returns as soon as the app starts, so the stub may still be writing.
        await WaitForFileAsync(relaunchMarker);
    }
}
