using System.IO.Compression;
using System.Text.RegularExpressions;
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
}
