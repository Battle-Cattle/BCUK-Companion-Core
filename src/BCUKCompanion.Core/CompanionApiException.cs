namespace BCUKCompanion.Core;

/// <summary>
/// Thrown when a companion API call (other than the OAuth/login endpoints — see
/// <see cref="CompanionAuthException"/>) fails, e.g. GET /api/companion/rewards
/// returning a non-success status or an unparseable body.
/// </summary>
public sealed class CompanionApiException : Exception
{
    public int? StatusCode { get; }

    public CompanionApiException(string message, int? statusCode = null)
        : base(message)
    {
        StatusCode = statusCode;
    }
}
