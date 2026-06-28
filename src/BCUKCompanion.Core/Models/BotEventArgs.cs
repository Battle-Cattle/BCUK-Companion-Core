namespace BCUKCompanion.Core.Models;

/// <summary>
/// A generic bot-host event a companion app's own logic may want to react to (e.g. a
/// channel-point redemption). Core has no built-in knowledge of what an event name means
/// or what a given app does with it — see <c>CompanionTrayAppOptions.OnBotEvent</c>.
/// </summary>
/// <param name="EventName">
/// A stable identifier for the kind of event (e.g. <c>"redemption.received"</c>), not a
/// display value — human-readable details (like the reward title) belong in
/// <paramref name="Metadata"/> so consumers can branch on <see cref="EventName"/> reliably
/// as Core grows more event kinds.
/// </param>
public sealed record BotEventArgs(string EventName, IReadOnlyDictionary<string, string?> Metadata);
