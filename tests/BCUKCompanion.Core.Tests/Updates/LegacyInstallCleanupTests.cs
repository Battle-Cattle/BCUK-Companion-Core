using BCUKCompanion.Core.Updates;
using Xunit;

namespace BCUKCompanion.Core.Tests.Updates;

public sealed class LegacyInstallCleanupTests : IDisposable
{
    // Trimmed from a real self-contained win-x64 publish of a WPF companion app.
    private const string DepsJson = """
        {
          "runtimeTarget": { "name": ".NETCoreApp,Version=v10.0/win-x64" },
          "targets": {
            ".NETCoreApp,Version=v10.0": {
              "Inactive/1.0.0": { "runtime": { "user-notes.txt": {} } }
            },
            ".NETCoreApp,Version=v10.0/win-x64": {
              "MyApp/1.0.0": {
                "dependencies": { "BCUKCompanion.Core": "0.13.0" },
                "runtime": { "MyApp.dll": {} }
              },
              "runtimepack.Microsoft.NETCore.App.Runtime.win-x64/10.0.0": {
                "runtime": { "System.Private.CoreLib.dll": { "assemblyVersion": "10.0.0.0" } },
                "native": { "coreclr.dll": { "fileVersion": "10.0.0.0" } }
              },
              "runtimepack.Microsoft.WindowsDesktop.App.Runtime.win-x64/10.0.0": {
                "runtime": { "PresentationCore.dll": {} },
                "native": { "wpfgfx_cor3.dll": {} },
                "resources": {
                  "cs/PresentationCore.resources.dll": { "locale": "cs" },
                  "zh-Hans/PresentationCore.resources.dll": { "locale": "zh-Hans" }
                }
              },
              "BCUKCompanion.Core/0.13.0": {
                "runtime": { "lib/net10.0/BCUKCompanion.Core.dll": {} }
              },
              "Evil/1.0.0": {
                "runtime": { "../outside.dll": {}, "./dot.dll": {}, "/rooted.dll": {}, "MyApp.exe": {}, "appsettings.json": {} },
                "resources": { "lib/x.resources.dll": { "locale": ".." } }
              }
            }
          }
        }
        """;

    private readonly string root = Path.Combine(Path.GetTempPath(), $"bcuk-legacy-cleanup-{Guid.NewGuid():N}");
    private readonly string installDir;

    public LegacyInstallCleanupTests()
    {
        installDir = Path.Combine(root, "install");
        Directory.CreateDirectory(installDir);
    }

    public void Dispose() => Directory.Delete(root, recursive: true);

    [Fact]
    public void Run_RemovesOnlyFilesTheOldBuildShipped()
    {
        WriteOldBuild();
        Touch("user-notes.txt");
        Touch("cs", "user-file.txt");
        Touch(Path.Combine("..", "outside.dll"));
        Touch("outside.dll");
        Touch("dot.dll");
        Touch("rooted.dll");

        Assert.True(LegacyInstallCleanup.Run(installDir, "MyApp"));

        Assert.Equal(
            new[]
            {
                "MyApp.exe", "appsettings.json", Path.Combine("cs", "user-file.txt"), "user-notes.txt",
                "outside.dll", "dot.dll", "rooted.dll",
            }.Order(),
            Directory.EnumerateFiles(installDir, "*", SearchOption.AllDirectories)
                .Select(f => Path.GetRelativePath(installDir, f)).Order());
        Assert.False(Directory.Exists(Path.Combine(installDir, "zh-Hans")));
        Assert.True(File.Exists(Path.Combine(root, "outside.dll")));
    }

    [Fact]
    public void Run_WithoutDepsJson_DoesNothing()
    {
        Touch("MyApp.exe");
        Touch("coreclr.dll");

        Assert.True(LegacyInstallCleanup.Run(installDir, "MyApp"));

        Assert.True(File.Exists(Path.Combine(installDir, "coreclr.dll")));
    }

    [Fact]
    public void Run_WithoutTheRuntimeTarget_KeepsEverything()
    {
        Touch("coreclr.dll");
        File.WriteAllText(
            Path.Combine(installDir, "MyApp.deps.json"),
            DepsJson.Replace("\"name\": \".NETCoreApp,Version=v10.0/win-x64\"", "\"name\": \"missing\""));

        Assert.False(LegacyInstallCleanup.Run(installDir, "MyApp"));

        Assert.True(File.Exists(Path.Combine(installDir, "coreclr.dll")));
        Assert.True(File.Exists(Path.Combine(installDir, "MyApp.deps.json")));
    }

    [SkippableFact]
    public void Run_DoesNotFollowALocaleJunction()
    {
        string outsideLocale = Path.Combine(root, "elsewhere");
        Directory.CreateDirectory(outsideLocale);
        File.WriteAllText(Path.Combine(outsideLocale, "PresentationCore.resources.dll"), string.Empty);
        File.WriteAllText(Path.Combine(installDir, "MyApp.deps.json"), DepsJson);
        try
        {
            Directory.CreateSymbolicLink(Path.Combine(installDir, "cs"), outsideLocale);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            Skip.If(true, "Creating a directory link needs extra rights on this machine.");
        }

        Assert.True(LegacyInstallCleanup.Run(installDir, "MyApp"));

        Assert.True(File.Exists(Path.Combine(outsideLocale, "PresentationCore.resources.dll")));
    }

    [Fact]
    public void Run_WithUnreadableDepsJson_KeepsEverything()
    {
        Touch("coreclr.dll");
        File.WriteAllText(Path.Combine(installDir, "MyApp.deps.json"), "{ not json");

        Assert.False(LegacyInstallCleanup.Run(installDir, "MyApp"));

        Assert.True(File.Exists(Path.Combine(installDir, "coreclr.dll")));
        Assert.True(File.Exists(Path.Combine(installDir, "MyApp.deps.json")));
    }

    [SkippableFact]
    public void Run_WhenAFileIsLocked_KeepsDepsJsonSoTheNextLaunchRetries()
    {
        Skip.IfNot(OperatingSystem.IsWindows(), "Only Windows refuses to delete an open file.");
        WriteOldBuild();

        using (File.Open(Path.Combine(installDir, "coreclr.dll"), FileMode.Open, FileAccess.Read, FileShare.None))
        {
            Assert.False(LegacyInstallCleanup.Run(installDir, "MyApp"));
        }

        Assert.True(File.Exists(Path.Combine(installDir, "MyApp.deps.json")));
        Assert.True(LegacyInstallCleanup.Run(installDir, "MyApp"));
        Assert.False(File.Exists(Path.Combine(installDir, "MyApp.deps.json")));
    }

    [Fact]
    public void GetStaleFiles_ListsExistingShippedFilesAndPdbs()
    {
        WriteOldBuild();

        Assert.Equal(
            new[]
            {
                "BCUKCompanion.Core.dll", "BCUKCompanion.Core.pdb", Path.Combine("cs", "PresentationCore.resources.dll"),
                "coreclr.dll", "MyApp.dll", "MyApp.pdb", "PresentationCore.dll", "System.Private.CoreLib.dll",
                "wpfgfx_cor3.dll", Path.Combine("zh-Hans", "PresentationCore.resources.dll"),
            }.Select(f => Path.Combine(installDir, f)).Order(StringComparer.OrdinalIgnoreCase),
            LegacyInstallCleanup.GetStaleFiles(installDir, "MyApp"));
    }

    private void WriteOldBuild()
    {
        File.WriteAllText(Path.Combine(installDir, "MyApp.deps.json"), DepsJson);
        foreach (string file in new[]
        {
            "MyApp.exe", "MyApp.dll", "MyApp.pdb", "MyApp.runtimeconfig.json", "appsettings.json",
            "System.Private.CoreLib.dll", "coreclr.dll", "PresentationCore.dll", "wpfgfx_cor3.dll",
            "BCUKCompanion.Core.dll", "BCUKCompanion.Core.pdb",
        })
        {
            Touch(file);
        }

        Touch("cs", "PresentationCore.resources.dll");
        Touch("zh-Hans", "PresentationCore.resources.dll");
    }

    private void Touch(params string[] parts)
    {
        string path = Path.Combine([installDir, .. parts]);
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllText(path, string.Empty);
    }
}
