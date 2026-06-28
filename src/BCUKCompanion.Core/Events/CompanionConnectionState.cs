namespace BCUKCompanion.Core.Events;

public enum CompanionConnectionState
{
    Connecting,
    Connected,
    Disconnected,
    RateLimited,
    AuthenticationFailed,
}
