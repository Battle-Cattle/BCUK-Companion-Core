namespace BCUKCompanion.Core.Actions;

/// <summary>What happened when <see cref="LegacyConfigMigration.Read"/> tried one legacy config file.</summary>
public enum LegacyConfigReadStatus
{
    /// <summary>The file doesn't exist, so there was nothing to migrate from it.</summary>
    Missing,

    /// <summary>The file was read, parsed and validated, and its contents were handed on.</summary>
    Read,

    /// <summary>The file exists but couldn't be read, parsed or validated; a later launch can retry it.</summary>
    Failed,
}
