using BCUKCompanion.Core.Tokens;
using Xunit;

namespace BCUKCompanion.Core.Tests;

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
}
