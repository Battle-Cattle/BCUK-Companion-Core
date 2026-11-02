using System.IO;
using BCUKCompanion.Core;

namespace BCUKCompanion.TrayApp.Services;

/// <summary>
/// The tray app's handling of a login that stops being usable: a token the bot rejected, and the
/// old server's token after a switch to another server. Kept free of WPF so it can be tested; the
/// tray app passes in how to show a balloon.
/// </summary>
internal static class LoginCleanup
{
    /// <summary>The balloon text shown when the old server didn't confirm it revoked the old login.</summary>
    internal const string OldLoginNotRevokedMessage =
        "Switched server, but the old server didn't confirm the old login was revoked, so a copy of it may still work there.";

    /// <summary>
    /// The bot rejected the token (revoked or replaced): discards it, so the next launch doesn't
    /// retry it and the Login window starts from a logged-out state. Only that token, though: one
    /// saved since (a new login while the loop was connecting) is kept, and then there's nothing to
    /// do. A token file that can't be deleted is reported through <paramref name="reportFailure"/>
    /// (with listening stopped) rather than thrown back into the listen loop.
    /// </summary>
    /// <param name="client">The client whose listen loop was rejected.</param>
    /// <param name="reportFailure">Shows why the rejected token couldn't be discarded, as balloon text.</param>
    /// <returns>True when the Login window should open (the token was discarded, or discarding it failed); false when a newer login is in place.</returns>
    public static bool DiscardRejectedLogin(CompanionClient client, Action<string> reportFailure)
    {
        try
        {
            return client.DiscardRejectedToken();
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or System.Security.SecurityException)
        {
            // Traced too, since the balloon is best-effort and may not show.
            System.Diagnostics.Trace.TraceError("Couldn't discard the rejected login: {0}", ex);
            client.StopListening();
            reportFailure($"Couldn't discard the rejected login: {ex.Message}");
            return true;
        }
    }

    /// <summary>
    /// Waits for the old bot's revoke after a server change (a few seconds at most), says so if it
    /// wasn't confirmed, then disposes the old client, which owns the connection the revoke uses.
    /// </summary>
    /// <param name="oldClient">The client for the old bot host, disposed once the revoke ends.</param>
    /// <param name="revoke">The old client's logout, whose result says whether the old bot confirmed the revoke.</param>
    /// <param name="showBalloon">Shows <see cref="OldLoginNotRevokedMessage"/> when the revoke wasn't confirmed.</param>
    /// <returns>A task that completes once the old client is disposed; it faults only if the revoke itself threw.</returns>
    public static async Task FinishOldServerLogoutAsync(CompanionClient oldClient, Task<LogoutResult> revoke, Action<string> showBalloon)
    {
        try
        {
            if (await revoke.ConfigureAwait(true) == LogoutResult.NotConfirmed)
            {
                showBalloon(OldLoginNotRevokedMessage);
            }
        }
        finally
        {
            oldClient.Dispose();
        }
    }
}
