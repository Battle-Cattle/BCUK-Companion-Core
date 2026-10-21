namespace BCUKCompanion.Core.Tokens;

/// <summary>
/// Persists the single companion-app bearer token between app runs.
/// </summary>
public interface ITokenStore
{
    /// <summary>Returns the stored token, or null if none has been saved.</summary>
    /// <returns>The saved bearer token, or null if none has been saved. An implementation may also return null for a token it can't decode; see each implementation for which errors it throws instead.</returns>
    string? Load();

    /// <summary>Persists (overwriting any existing) token.</summary>
    /// <param name="token">The companion bearer token to save.</param>
    void Save(string token);

    /// <summary>Deletes any stored token, if present.</summary>
    void Clear();
}
