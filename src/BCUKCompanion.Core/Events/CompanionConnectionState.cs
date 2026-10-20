namespace BCUKCompanion.Core.Events;

/// <summary>Where <see cref="CompanionEventStream"/> is in its connect/reconnect cycle.</summary>
public enum CompanionConnectionState
{
    /// <summary>Opening a connection to the event stream (the first one, or a reconnect).</summary>
    Connecting,

    /// <summary>The stream is open and events are arriving.</summary>
    Connected,

    /// <summary>The connection failed or dropped; the stream reconnects after a backoff.</summary>
    Disconnected,

    /// <summary>
    /// The bot answered 429: this token already has the maximum number of event streams open (e.g.
    /// the app is running on another machine too). The stream backs off and retries.
    /// </summary>
    RateLimited,

    /// <summary>The bot rejected the token (401). The stream stops: the user has to log in again.</summary>
    AuthenticationFailed,
}
