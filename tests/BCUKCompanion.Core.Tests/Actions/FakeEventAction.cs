using BCUKCompanion.Core.Actions;

namespace BCUKCompanion.Core.Tests.Actions;

internal sealed class FakeEventAction(
    string kind = "fake",
    bool result = true,
    IReadOnlyList<string>? validationErrors = null,
    Action? onExecute = null,
    Exception? throwOnExecute = null) : IEventAction
{
    public string Kind { get; } = kind;

    public string Describe(IEventActionContext context) => $"Fake({Kind})";

    public IReadOnlyList<string> Validate(IEventActionContext context) => validationErrors ?? [];

    public Task<bool> ExecuteAsync(IEventActionContext context, CancellationToken cancellationToken)
    {
        if (throwOnExecute is not null)
        {
            throw throwOnExecute;
        }

        onExecute?.Invoke();
        return Task.FromResult(result);
    }
}

internal sealed class NoServiceEventActionContext : IEventActionContext
{
    public object? GetService(Type serviceType) => null;
}
