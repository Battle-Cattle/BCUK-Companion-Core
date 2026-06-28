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
}
