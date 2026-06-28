namespace BCUKCompanion.Core;

/// <summary>
/// Thrown when the bot rejects a companion auth request (bad/expired code,
/// invalid redirect_uri/state, etc.) — see the error reference table in
/// companionappsetupguide.md.
/// </summary>
public sealed class CompanionAuthException : Exception
{
    public int? StatusCode { get; }

    public CompanionAuthException(string message, int? statusCode = null)
        : base(message)
    {
        StatusCode = statusCode;
    }
}
