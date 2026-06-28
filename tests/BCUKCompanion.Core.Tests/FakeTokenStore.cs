using BCUKCompanion.Core.Tokens;

namespace BCUKCompanion.Core.Tests;

internal sealed class FakeTokenStore : ITokenStore
{
    private string? _token;

    public string? Load() => _token;

    public void Save(string token) => _token = token;

    public void Clear() => _token = null;
}
