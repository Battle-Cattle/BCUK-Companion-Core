namespace BCUKCompanion.Core.Models;

/// <summary>
/// A generic bot-host event a companion app's own logic may want to react to (e.g. a
/// channel-point redemption). Core has no built-in knowledge of what an event name means
/// or what a given app does with it — see <c>CompanionTrayAppOptions.OnBotEvent</c>.
/// </summary>
public sealed record BotEventArgs(string EventName, IReadOnlyDictionary<string, string?> Metadata);
