using BCUKCompanion.Core.Tokens;
using Xunit;

namespace BCUKCompanion.Core.Tests;

#pragma warning disable CA1416 // Runtime-guarded below (Skip.IfNot) to only run on Windows.
public class DpapiFileTokenStoreTests
{
    [SkippableFact]
    public void RoundTripsSaveLoadClear()
    {
        Skip.IfNot(OperatingSystem.IsWindows(), "DPAPI is only supported on Windows.");

        string path = Path.Combine(Path.GetTempPath(), $"bcuk-companion-test-{Guid.NewGuid()}.bin");
        var store = new DpapiFileTokenStore(path);
        try
        {
            Assert.Null(store.Load());

            store.Save("super-secret-token");
            Assert.Equal("super-secret-token", store.Load());

            store.Clear();
            Assert.Null(store.Load());
        }
        finally
        {
            if (File.Exists(path))
            {
                File.Delete(path);
            }
        }
    }

    [SkippableFact]
    public void ClearRemovesATempFileLeftByAnInterruptedSave()
    {
        Skip.IfNot(OperatingSystem.IsWindows(), "DPAPI is only supported on Windows.");

        string path = Path.Combine(Path.GetTempPath(), $"bcuk-companion-test-{Guid.NewGuid()}.bin");
        string tempPath = path + ".tmp";
        var store = new DpapiFileTokenStore(path);
        try
        {
            store.Save("super-secret-token");
            File.WriteAllBytes(tempPath, [1, 2, 3]);

            store.Clear();

            Assert.False(File.Exists(path));
            Assert.False(File.Exists(tempPath));
        }
        finally
        {
            File.Delete(path);
            File.Delete(tempPath);
        }
    }
}
#pragma warning restore CA1416
