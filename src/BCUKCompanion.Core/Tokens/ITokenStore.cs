namespace BCUKCompanion.Core.Tokens;

/// <summary>
/// Persists the single companion-app bearer token between app runs.
/// </summary>
public interface ITokenStore
{
    /// <summary>Returns the stored token, or null if none has been saved.</summary>
    string? Load();

    /// <summary>Persists (overwriting any existing) token.</summary>
    void Save(string token);

    /// <summary>Deletes any stored token, if present.</summary>
    void Clear();
}
