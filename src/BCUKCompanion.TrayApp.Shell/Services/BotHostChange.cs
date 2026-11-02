namespace BCUKCompanion.TrayApp.Services;

/// <summary>
/// What a change of the configured bot URL means for the tray app: the new host, and whether it's
/// still the same server (scheme, host and port), in which case the saved token, which that server
/// issued, can be kept. Kept free of WPF so it can be tested.
/// </summary>
/// <param name="NewHost">The new bot URL, parsed.</param>
/// <param name="SameServer">
/// True when only the URL's path (or case) changed, so the login stays valid; false when it's a
/// different server, which must never be sent the old server's token.
/// </param>
internal sealed record BotHostChange(Uri NewHost, bool SameServer)
{
    /// <summary>Compares the new bot URL with the current one.</summary>
    /// <param name="currentHost">The bot URL the client is using now.</param>
    /// <param name="newBotHost">The bot URL the user entered.</param>
    /// <returns>The change, or null when <paramref name="newBotHost"/> equals the current URL, ignoring case.</returns>
    /// <exception cref="UriFormatException"><paramref name="newBotHost"/> isn't a valid URL.</exception>
    public static BotHostChange? Compare(Uri currentHost, string newBotHost)
    {
        if (string.Equals(currentHost.OriginalString, newBotHost, StringComparison.OrdinalIgnoreCase))
        {
            return null;
        }

        var newHost = new Uri(newBotHost);
        return new BotHostChange(newHost, AppSettings.ActivityKey(newHost) == AppSettings.ActivityKey(currentHost));
    }
}
