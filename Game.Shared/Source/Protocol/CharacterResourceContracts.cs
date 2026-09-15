using System;
using Game.Shared.Resources;

namespace Game.Shared.Protocol
{
    public enum CharacterResourceOperationStatus : byte
    {
        None = 0,
        Success = 1,
        SessionUnavailable = 2,
        CharacterUnavailable = 3,
        ResourceUnavailable = 4,
        InvalidAmount = 5,
        Insufficient = 6,
        NotSpendable = 7,
        StaleState = 8,
    }

    [Serializable]
    public sealed class CharacterResourceView
    {
        public CharacterResourceId id;
        public string displayName;
        public int current;
        public int minimum;
        public int maximum;
        public CharacterResourceReplicationMode replication;
    }

    [Serializable]
    public sealed class CharacterResourcesSnapshot
    {
        public long contentRevision;
        public long resourceRevision;
        public CharacterResourceView[] resources;
    }

    [Serializable]
    public readonly struct CharacterResourceChangeView
    {
        public long resourceRevision { get; }
        public CharacterResourceId id { get; }
        public int previous { get; }
        public int current { get; }
        public int minimum { get; }
        public int maximum { get; }
        public CharacterResourceChangeReason reason { get; }

        public CharacterResourceChangeView(
            long resourceRevision,
            CharacterResourceId id,
            int previous,
            int current,
            int minimum,
            int maximum,
            CharacterResourceChangeReason reason)
        {
            this.resourceRevision = resourceRevision;
            this.id = id;
            this.previous = previous;
            this.current = current;
            this.minimum = minimum;
            this.maximum = maximum;
            this.reason = reason;
        }
    }

    public readonly struct CharacterResourceOperationResult
    {
        public bool Success { get; }
        public CharacterResourceOperationStatus Status { get; }
        public string Error { get; }
        public CharacterResourcesSnapshot Snapshot { get; }

        public CharacterResourceOperationResult(
            bool success,
            CharacterResourceOperationStatus status,
            string error,
            CharacterResourcesSnapshot snapshot)
        {
            Success = success;
            Status = status;
            Error = error ?? string.Empty;
            Snapshot = snapshot;
        }

        public static CharacterResourceOperationResult Succeeded(CharacterResourcesSnapshot snapshot = null) =>
            new CharacterResourceOperationResult(true, CharacterResourceOperationStatus.Success, string.Empty, snapshot);

        public static CharacterResourceOperationResult Failed(
            CharacterResourceOperationStatus status,
            string error,
            CharacterResourcesSnapshot snapshot = null) =>
            new CharacterResourceOperationResult(false, status, error, snapshot);
    }
}
