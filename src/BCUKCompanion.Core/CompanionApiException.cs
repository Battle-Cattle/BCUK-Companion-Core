namespace BCUKCompanion.Core;

/// <summary>
/// Thrown when a companion API call (other than the OAuth/login endpoints — see
/// <see cref="CompanionAuthException"/>) fails, e.g. GET /api/companion/rewards
/// returning a non-success status or an unparseable body.
/// </summary>
public sealed class CompanionApiException : Exception
{
    /// <summary>The HTTP status the bot answered with, or null if the failure wasn't an HTTP status.</summary>
    public int? StatusCode { get; }

    /// <summary>Creates the exception.</summary>
    /// <param name="message">What failed, for the user or the log.</param>
    /// <param name="statusCode">The HTTP status the bot answered with, if any.</param>
    public CompanionApiException(string message, int? statusCode = null)
        : base(message)
    {
        StatusCode = statusCode;
    }
}
