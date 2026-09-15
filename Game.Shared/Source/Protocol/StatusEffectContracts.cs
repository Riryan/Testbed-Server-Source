using System;
using Game.Shared.StatusEffects;

namespace Game.Shared.Protocol
{
    public enum StatusEffectOperationStatus : byte
    {
        None = 0,
        Success = 1,
        SessionUnavailable = 2,
        CharacterUnavailable = 3,
        UnknownDefinition = 4,
        InvalidStacks = 5,
        ActorNotAllowed = 6,
        TargetDead = 7,
        IgnoredWhileActive = 8,
        NotActive = 9,
        StaleState = 10,
    }

    [Serializable]
    public sealed class StatusEffectView
    {
        public ushort wireId;
        public string definitionId;
        public string displayName;
        public byte classification;
        public byte stacks;
        public double endTime;
        public ushort presentationId;
    }

    [Serializable]
    public sealed class StatusEffectsSnapshot
    {
        public long contentRevision;
        public long statusRevision;
        public StatusEffectView[] effects;
    }

    [Serializable]
    public readonly struct StatusEffectChangeView
    {
        public long statusRevision { get; }
        public StatusEffectChangeKind kind { get; }
        public string definitionId { get; }
        public string displayName { get; }
        public byte classification { get; }
        public byte stacks { get; }
        public double endTime { get; }
        public StatusEffectChangeReason reason { get; }
        public ushort presentationId { get; }

        public StatusEffectChangeView(
            long statusRevision,
            StatusEffectChangeKind kind,
            string definitionId,
            string displayName,
            byte classification,
            byte stacks,
            double endTime,
            StatusEffectChangeReason reason,
            ushort presentationId)
        {
            this.statusRevision = statusRevision;
            this.kind = kind;
            this.definitionId = definitionId ?? string.Empty;
            this.displayName = displayName ?? string.Empty;
            this.classification = classification;
            this.stacks = stacks;
            this.endTime = endTime;
            this.reason = reason;
            this.presentationId = presentationId;
        }
    }

    public readonly struct StatusEffectOperationResult
    {
        public bool Success { get; }
        public StatusEffectOperationStatus Status { get; }
        public string Error { get; }

        public StatusEffectOperationResult(bool success, StatusEffectOperationStatus status, string error)
        {
            Success = success;
            Status = status;
            Error = error ?? string.Empty;
        }

        public static StatusEffectOperationResult Succeeded() =>
            new StatusEffectOperationResult(true, StatusEffectOperationStatus.Success, string.Empty);

        public static StatusEffectOperationResult Failed(StatusEffectOperationStatus status, string error) =>
            new StatusEffectOperationResult(false, status, error);
    }
}
