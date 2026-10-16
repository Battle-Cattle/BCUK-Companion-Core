using BCUKCompanion.Core.Models;

namespace BCUKCompanion.Core.Actions;

/// <summary>
/// Exposes the <see cref="BotEventArgs"/> being dispatched to the actions it runs, ahead of the
/// app's own context — so an action can read event details, such as an advanced command's
/// <c>param.&lt;key&gt;</c> metadata, with <c>context.GetService&lt;BotEventArgs&gt;()</c>.
/// </summary>
public sealed class BotEventActionContext(BotEventArgs botEvent, IEventActionContext inner) : IEventActionContext
{
    /// <summary>Returns the dispatched <see cref="BotEventArgs"/> when asked for it; otherwise defers to the app's context.</summary>
    public object? GetService(Type serviceType) =>
        serviceType == typeof(BotEventArgs) ? botEvent : inner.GetService(serviceType);
}
