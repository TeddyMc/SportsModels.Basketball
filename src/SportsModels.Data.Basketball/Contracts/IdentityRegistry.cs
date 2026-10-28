namespace SportsModels.Data.Basketball.Contracts;

public sealed record IdentityResolution(long? EntityId, string Status, string Evidence);

/// <summary>Provider keys are typed strings. Mappings must never be silently reassigned.</summary>
public interface IIdentityRegistry
{
    string RegistryId { get; }
    long Revision { get; }
    T InTransaction<T>(Func<T> operation);

    IdentityResolution Resolve(string entityType, string source, string sourceId, string evidence,
        bool establishedNew = false, long? confirmedEntityId = null);

    IdentityResolution ResolveEvent(string source, string gameSourceId, string snapshotId,
        string rowKey, string? actionId, string fingerprint, bool uniqueActionId);
}
