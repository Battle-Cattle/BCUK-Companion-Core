namespace BCUKCompanion.Core;

/// <summary>What <see cref="CompanionClient.LogoutAsync"/> did with the bot after logging out locally.</summary>
public enum LogoutResult
{
    /// <summary>No token was saved, so there was nothing to revoke.</summary>
    NoToken,

    /// <summary>The bot revoked the token, or reported it already unknown or revoked: no copy of it works.</summary>
    Revoked,

    /// <summary>The bot couldn't be reached or didn't confirm the revoke, so a copy of the token may still work.</summary>
    NotConfirmed,
}
