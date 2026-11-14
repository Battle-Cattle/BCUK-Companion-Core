using System.Diagnostics;

namespace BCUKCompanion.Core.Events;

/// <summary>
/// Raises an event one subscriber at a time, so a subscriber that throws neither stops the
/// subscribers after it from running nor escapes into the code that raised the event.
/// </summary>
internal static class SubscriberInvoker
{
    /// <summary>Calls each of <paramref name="handler"/>'s subscribers in turn, reporting any that throws to <paramref name="onFailure"/>.</summary>
    /// <typeparam name="T">The event's argument type.</typeparam>
    /// <param name="handler">The event's delegate, or null when nothing is subscribed.</param>
    /// <param name="sender">The object raising the event.</param>
    /// <param name="args">The event's argument.</param>
    /// <param name="onFailure">Told about each exception a subscriber throws. An exception it throws itself is traced and dropped.</param>
    public static void Raise<T>(EventHandler<T>? handler, object sender, T args, Action<Exception> onFailure)
    {
        if (handler is null)
        {
            return;
        }

        foreach (EventHandler<T> subscriber in handler.GetInvocationList().Cast<EventHandler<T>>())
        {
            try
            {
                subscriber(sender, args);
            }
            catch (Exception ex)
            {
                ReportFailure(onFailure, ex);
            }
        }
    }

    /// <summary>Hands <paramref name="failure"/> to <paramref name="onFailure"/>, tracing rather than throwing if that fails too.</summary>
    /// <param name="onFailure">The failure callback.</param>
    /// <param name="failure">The exception a subscriber threw.</param>
    private static void ReportFailure(Action<Exception> onFailure, Exception failure)
    {
        try
        {
            onFailure(failure);
        }
        catch (Exception ex)
        {
            Trace.TraceError("An event subscriber threw ({0}), and so did reporting it: {1}", failure, ex);
        }
    }
}
