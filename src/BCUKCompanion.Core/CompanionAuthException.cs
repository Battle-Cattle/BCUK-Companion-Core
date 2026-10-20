namespace BCUKCompanion.Core;

/// <summary>
/// Thrown when the bot rejects a companion auth request (bad/expired code,
/// invalid redirect_uri/state, etc.) — see the error reference table in
/// companionappsetupguide.md.
/// </summary>
public sealed class CompanionAuthException : Exception
{
    /// <summary>The HTTP status the bot answered with, or null if the failure wasn't an HTTP status.</summary>
    public int? StatusCode { get; }

    /// <summary>Creates the exception.</summary>
    /// <param name="message">What failed, for the user or the log.</param>
    /// <param name="statusCode">The HTTP status the bot answered with, if any.</param>
    public CompanionAuthException(string message, int? statusCode = null)
        : base(message)
    {
        StatusCode = statusCode;
    }
}
