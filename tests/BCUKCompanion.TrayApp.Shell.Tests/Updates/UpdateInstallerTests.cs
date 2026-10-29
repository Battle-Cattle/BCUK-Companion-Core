using System.IO.Compression;
using System.Net;
using System.Security.Cryptography;
using System.Text.RegularExpressions;
using BCUKCompanion.Core.Updates;
using BCUKCompanion.TrayApp.Updates;
using Xunit;

namespace BCUKCompanion.TrayApp.Tests.Updates;

public sealed class UpdateInstallerTests : IDisposable
{
    private readonly string root = Path.Combine(Path.GetTempPath(), $"bcuk-update-installer-{Guid.NewGuid():N}");

    public UpdateInstallerTests()
    {
        Directory.CreateDirectory(root);
    }

    public void Dispose() => Directory.Delete(root, recursive: true);

    [Fact]
    public void EnsureWritable_WritableFolder_LeavesNothingBehind()
    {
        UpdateInstaller.EnsureWritable(root);

        Assert.Empty(Directory.EnumerateFileSystemEntries(root));
    }

    [Fact]
    public void EnsureWritable_FolderThatCantBeWritten_ThrowsUnauthorizedAccess()
    {
        string missing = Path.Combine(root, "missing");

        var ex = Assert.Throws<UnauthorizedAccessException>(() => UpdateInstaller.EnsureWritable(missing));

        Assert.Contains(missing, ex.Message);
    }

    [Fact]
    public void ExtractAndVerify_ZipWithTheExe_ExtractsIt()
    {
        string zip = CreateZip(("MyApp.exe", "new"), ("appsettings.json", "{}"));
        string staging = Path.Combine(root, "files");

        UpdateInstaller.ExtractAndVerify(zip, staging, "MyApp.exe");

        Assert.Equal("new", File.ReadAllText(Path.Combine(staging, "MyApp.exe")));
        Assert.True(File.Exists(Path.Combine(staging, "appsettings.json")));
    }

    [Theory]
    [InlineData("OtherApp.exe")]
    [InlineData("nested/MyApp.exe")]
    public void ExtractAndVerify_ZipWithoutTheExeAtItsRoot_Throws(string entry)
    {
        string zip = CreateZip((entry, "new"));

        var ex = Assert.Throws<InvalidDataException>(
            () => UpdateInstaller.ExtractAndVerify(zip, Path.Combine(root, "files"), "MyApp.exe"));

        Assert.Equal("The downloaded update doesn't contain MyApp.exe.", ex.Message);
    }

    [Fact]
    public async Task PrepareAndLaunchAsync_ExtractsTheDownloadedZipAndChecksItBeforeLaunching()
    {
        // The zip lacks the running executable, so the install stops before PowerShell starts.
        string dataFolderName = $"bcuk-update-installer-{Guid.NewGuid():N}";
        string workRoot = Path.Combine(Path.GetTempPath(), $"{dataFolderName}.Update");
        byte[] archive = File.ReadAllBytes(CreateZip(("OtherApp.exe", "new")));
        var handler = new DownloadHandler(archive, Convert.ToHexString(SHA256.HashData(archive)));
        var checker = new GitHubReleaseUpdateChecker(
            "Battle-Cattle", "App", new AppVersion(new Version(1, 0, 0), false), new HttpClient(handler));
        var update = new AvailableUpdate(
            new Version(1, 1, 0),
            "v1.1.0",
            new Uri("https://example.test/update.zip"),
            new Uri("https://example.test/release"),
            new Uri("https://example.test/update.zip.sha256"));

        try
        {
            var ex = await Assert.ThrowsAsync<InvalidDataException>(
                () => new UpdateInstaller(checker, dataFolderName).PrepareAndLaunchAsync(update));

            Assert.Equal($"The downloaded update doesn't contain {Path.GetFileName(Environment.ProcessPath)}.", ex.Message);
            string staged = Assert.Single(Directory.GetFiles(workRoot, "OtherApp.exe", SearchOption.AllDirectories));
            Assert.Equal("new", File.ReadAllText(staged));
            Assert.Equal(["/update.zip", "/update.zip.sha256"], handler.Paths);
        }
        finally
        {
            if (Directory.Exists(workRoot))
            {
                Directory.Delete(workRoot, recursive: true);
            }
        }
    }

    [Fact]
    public async Task PrepareAndLaunchAsync_StartsTheSwapScriptWithEachPathAsItsOwnArgument()
    {
        string dataFolderName = $"bcuk-update-installer-{Guid.NewGuid():N}";
        string workRoot = Path.Combine(Path.GetTempPath(), $"{dataFolderName}.Update");
        string exeName = Path.GetFileName(Environment.ProcessPath)!;
        byte[] archive = File.ReadAllBytes(CreateZip((exeName, "new")));
        var checker = new GitHubReleaseUpdateChecker(
            "Battle-Cattle", "App", new AppVersion(new Version(1, 0, 0), false),
            new HttpClient(new DownloadHandler(archive, Convert.ToHexString(SHA256.HashData(archive)))));
        var update = new AvailableUpdate(
            new Version(1, 1, 0),
            "v1.1.0",
            new Uri("https://example.test/update.zip"),
            new Uri("https://example.test/release"),
            new Uri("https://example.test/update.zip.sha256"));
        System.Diagnostics.ProcessStartInfo? started = null;

        try
        {
            await new UpdateInstaller(checker, dataFolderName) { StartProcess = info => started = info }.PrepareAndLaunchAsync(update);

            Assert.Equal("powershell.exe", started!.FileName);
            Assert.False(started.UseShellExecute);
            var args = started.ArgumentList.ToList();
            string script = args[args.IndexOf("-File") + 1];
            Assert.Equal(UpdateInstaller.ApplyUpdateScript, File.ReadAllText(script));
            Assert.Equal(Environment.ProcessId.ToString(), args[args.IndexOf("-ProcessId") + 1]);
            Assert.Equal("new", File.ReadAllText(Path.Combine(args[args.IndexOf("-Source") + 1], exeName)));
        }
        finally
        {
            if (Directory.Exists(workRoot))
            {
                Directory.Delete(workRoot, recursive: true);
            }
        }
    }

    [Fact]
    public void ToScriptArguments_PassesEachValueAsItsOwnArgument()
    {
        var request = new ApplyUpdateRequest(1234, @"C:\tmp\files", @"C:\Program Files\My App", @"C:\Program Files\My App\MyApp.exe", @"C:\tmp");

        Assert.Equal(
            new[]
            {
                "-ProcessId", "1234", "-Source", @"C:\tmp\files", "-Target", @"C:\Program Files\My App",
                "-Exe", @"C:\Program Files\My App\MyApp.exe", "-WorkDir", @"C:\tmp",
            },
            request.ToScriptArguments());
    }

    [Fact]
    public void ToScriptArguments_NamesOnlyParametersTheScriptDeclares()
    {
        string paramBlock = UpdateInstaller.ApplyUpdateScript[..UpdateInstaller.ApplyUpdateScript.IndexOf(')')];
        var declared = Regex.Matches(paramBlock, @"\$(\w+)").Select(m => "-" + m.Groups[1].Value).ToHashSet();
        var request = new ApplyUpdateRequest(1, "s", "t", "e", "w");

        var passed = request.ToScriptArguments().Where((_, i) => i % 2 == 0).ToList();

        Assert.All(passed, name => Assert.Contains(name, declared));
    }

    private string CreateZip(params (string Name, string Content)[] entries)
    {
        string path = Path.Combine(root, $"{Guid.NewGuid():N}.zip");
        using var archive = ZipFile.Open(path, ZipArchiveMode.Create);
        foreach (var (name, content) in entries)
        {
            using var writer = new StreamWriter(archive.CreateEntry(name).Open());
            writer.Write(content);
        }

        return path;
    }

    private sealed class DownloadHandler(byte[] archive, string checksum) : HttpMessageHandler
    {
        public List<string> Paths { get; } = [];

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            string path = request.RequestUri!.AbsolutePath;
            Paths.Add(path);
            HttpContent content = path.EndsWith(".sha256", StringComparison.Ordinal)
                ? new StringContent(checksum)
                : new ByteArrayContent(archive);
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK) { Content = content });
        }
    }
}
