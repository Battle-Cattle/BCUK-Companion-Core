using BCUKCompanion.Core.Models;

namespace BCUKCompanion.TrayApp;

/// <summary>
/// Per-host customization for <see cref="CompanionTrayApplication"/>. Hosts that install
/// alongside other companion apps on the same Windows account should set
/// <see cref="DataFolderName"/> to a unique value to avoid colliding on settings/token
/// storage, the single-instance lock, and the Windows startup registry entry.
/// </summary>
public sealed class CompanionTrayAppOptions
{
    public string DataFolderName { get; init; } = "BCUKCompanion";

    /// <summary>
    /// Invoked on the UI thread whenever the bot host reports an event (e.g. a channel-point
    /// redemption), so a companion app can react to it without owning any bot-connection
    /// logic itself.
    /// </summary>
    public Action<BotEventArgs>? OnBotEvent { get; init; }

    /// <summary>
    /// Extra entries shown in the tray icon's context menu, letting a companion app surface
    /// its own settings UI (or any other action) from the shared tray icon.
    /// </summary>
    public IReadOnlyList<TrayMenuItem>? AdditionalMenuItems { get; init; }
}
