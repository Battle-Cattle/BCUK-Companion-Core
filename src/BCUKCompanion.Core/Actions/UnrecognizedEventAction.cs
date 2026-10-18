using System.Text.Json.Serialization;

namespace BCUKCompanion.Core.Actions;

/// <summary>
/// A saved action <see cref="EventActionJsonConverter"/> couldn't turn into a registered kind: its
/// kind isn't registered (e.g. a kind a newer or older build of the app had), it has no kind, or
/// one of its values couldn't be read. Kept, rather than failing the whole config file, so every
/// other action still loads and runs, and written back exactly as it was read, so saving the
/// config never loses it. It never runs: <see cref="Validate"/> always reports why.
/// </summary>
public sealed class UnrecognizedEventAction : IEventAction
{
    /// <param name="kind">The saved <c>kind</c>, or an empty string if it had none.</param>
    /// <param name="rawJson">The action's JSON exactly as saved.</param>
    /// <param name="reason">Why it couldn't be read, shown as its validation error.</param>
    public UnrecognizedEventAction(string kind, string rawJson, string reason)
    {
        Kind = kind;
        RawJson = rawJson;
        Reason = reason;
    }

    [JsonIgnore]
    public string Kind { get; }

    /// <summary>The action's JSON exactly as saved; written back unchanged.</summary>
    [JsonIgnore]
    public string RawJson { get; }

    /// <summary>Why the action couldn't be read.</summary>
    [JsonIgnore]
    public string Reason { get; }

    public string Describe(IEventActionContext context) =>
        string.IsNullOrEmpty(Kind) ? "Unrecognized action" : $"Unrecognized action ({Kind})";

    public IReadOnlyList<string> Validate(IEventActionContext context) => [Reason];

    public Task<bool> ExecuteAsync(IEventActionContext context, CancellationToken cancellationToken) => Task.FromResult(false);
}
