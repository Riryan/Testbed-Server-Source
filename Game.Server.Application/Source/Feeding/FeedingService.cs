using System;
using System.Collections.Generic;
using Game.Server.Application.Content;
using Game.Server.Application.Interactions;
using Game.Server.Application.Progression;
using Game.Server.Application.Resources;
using Game.Server.Domain.Players;
using Game.Server.Domain.Resources;
using Game.Shared.Content;
using Game.Shared.Interactions;
using Game.Shared.Protocol;
using Game.Shared.Resources;
using Game.Shared.World;

namespace Game.Server.Application.Feeding
{
    /// <summary>
    /// Vampire feeding foundation implemented as a canonical Interaction action over the
    /// existing Resource and Progression services. Only active feeding sessions are scheduled;
    /// there is no per-player scan or parallel Hunter/Vampire state store.
    /// </summary>
    public sealed class FeedingService : IInteractionActionHandler, IInteractionActionDescriptorProvider
    {
        private sealed class Session
        {
            public long Id;
            public PlayerRuntime Source;
            public PlayerRuntime Target;
            public FactionGameplayProfileDefinition Profile;
            public double StartedAt;
            public double EndsAt;
            public double NextPulseAt;
        }

        private readonly GameplayContentCatalog _content;
        private readonly CharacterResourceService _resources;
        private readonly ProgressionService _progression;
        private readonly IIncidentObservationProvider _observations;
        private readonly Dictionary<long, Session> _bySource = new Dictionary<long, Session>();
        private readonly Dictionary<long, Session> _byParticipant = new Dictionary<long, Session>();
        private long _nextSessionId;
        private double _nextDueAt = double.PositiveInfinity;

        public FeedingService(
            GameplayContentCatalog content,
            CharacterResourceService resources,
            ProgressionService progression,
            IIncidentObservationProvider observations)
        {
            _content = content ?? throw new ArgumentNullException(nameof(content));
            _resources = resources ?? throw new ArgumentNullException(nameof(resources));
            _progression = progression ?? throw new ArgumentNullException(nameof(progression));
            _observations = observations;
        }

        public InteractionActionId ActionId => InteractionActionId.Feed;
        public int ActiveCount => _bySource.Count;
        public bool HasDueWork(double now) => _bySource.Count > 0 && now + 0.000001d >= _nextDueAt;

        public InteractionActionEntry DescribeAction() => new InteractionActionEntry(
            InteractionCategoryId.Interact,
            InteractionActionId.Feed,
            "Feed",
            InteractionAvailability.Available,
            string.Empty,
            InteractionConsentMode.None,
            InteractionContentLevel.General,
            InteractionFeature.Vampire,
            100);

        public InteractionResult Execute(in InteractionExecutionContext context) =>
            Start(context.Source, context.TargetPlayer, context.Target, context.Sequence, context.ActionId, context.ServerTime);

        /// <summary>
        /// Uses the same authoritative feeding session for Resource-backed non-session runtimes,
        /// such as the baked combat test dummy. No alternate feeding rules are introduced.
        /// </summary>
        public InteractionResult ExecuteAgainstRuntime(
            PlayerRuntime source,
            PlayerRuntime target,
            InteractionTargetHandle targetHandle,
            uint sequence,
            double serverTime) =>
            Start(source, target, targetHandle, sequence, InteractionActionId.Feed, serverTime);

        public bool CanStart(PlayerRuntime source, PlayerRuntime target, out string reason)
        {
            reason = string.Empty;
            if (!TryValidateStart(source, target, out _, out reason))
                return false;
            return true;
        }

        private InteractionResult Start(
            PlayerRuntime source,
            PlayerRuntime target,
            InteractionTargetHandle targetHandle,
            uint sequence,
            InteractionActionId actionId,
            double serverTime)
        {
            if (!TryValidateStart(source, target, out FactionGameplayProfileDefinition profile, out string reason))
                return new InteractionResult(sequence, actionId, InteractionResultCode.InvalidState, targetHandle, reason);

            double pulse = Math.Max(0.10d, profile.feedingPulseSeconds);
            double duration = Math.Max(pulse, profile.feedingMaximumDurationSeconds);
            long id = ++_nextSessionId;
            if (id <= 0) id = _nextSessionId = 1;
            var session = new Session
            {
                Id = id,
                Source = source,
                Target = target,
                Profile = profile,
                StartedAt = serverTime,
                EndsAt = serverTime + duration,
                NextPulseAt = serverTime,
            };
            _bySource.Add(source.CharacterId.Value, session);
            _byParticipant.Add(source.CharacterId.Value, session);
            _byParticipant.Add(target.CharacterId.Value, session);
            if (session.NextPulseAt < _nextDueAt) _nextDueAt = session.NextPulseAt;

            if (_observations != null)
            {
                IncidentObservation observation = _observations.Observe(
                    source.Location.MapId, source.Location.InstanceId, source.Location.Position);
                if (observation.Observed)
                    RecordExposure(source, observation);
            }

            return new InteractionResult(sequence, actionId, InteractionResultCode.Success, targetHandle, "feeding started");
        }

        private bool TryValidateStart(
            PlayerRuntime source,
            PlayerRuntime target,
            out FactionGameplayProfileDefinition profile,
            out string reason)
        {
            profile = null;
            reason = string.Empty;
            if (source == null || target == null || source.CharacterId.Value == target.CharacterId.Value)
            {
                reason = "feeding target is unavailable";
                return false;
            }
            if (_byParticipant.ContainsKey(source.CharacterId.Value) || _byParticipant.ContainsKey(target.CharacterId.Value))
            {
                reason = "a feeding session is already active";
                return false;
            }
            if (!TryGetFeedingProfile(source, out profile, out reason))
                return false;
            if (!_progression.MeetsPredicates(source, profile.feedingUnlockPredicates))
            {
                reason = "feeding progression requirements are not met";
                return false;
            }
            if (!source.TryGetCharacterResource(profile.feedingGainResourceId, out _, out CharacterResourceState gain) || !gain.Enabled)
            {
                reason = "feeding resource is unavailable";
                return false;
            }
            if (gain.Current >= gain.Maximum)
            {
                reason = "feeding resource is full";
                return false;
            }
            if (!target.TryGetCharacterResource(CharacterResourceId.Health, out _, out CharacterResourceState targetHealth) || !targetHealth.Enabled)
            {
                reason = "feeding target has no authoritative Health resource";
                return false;
            }
            if (!CanContinue(source, target, profile, out reason))
                return false;
            return true;
        }

        public bool ProcessOneDue(double now)
        {
            if (!HasDueWork(now)) return false;
            Session due = null;
            foreach (Session session in _bySource.Values)
            {
                if (session.NextPulseAt <= now + 0.000001d && (due == null || session.NextPulseAt < due.NextPulseAt))
                    due = session;
            }
            if (due == null)
            {
                RecalculateNextDue();
                return false;
            }

            if (now >= due.EndsAt || !CanContinue(due.Source, due.Target, due.Profile, out _))
            {
                End(due);
                return true;
            }

            if (!ApplyPulse(due))
            {
                End(due);
                return true;
            }

            due.NextPulseAt = now + Math.Max(0.10d, due.Profile.feedingPulseSeconds);
            if (due.NextPulseAt >= due.EndsAt)
                End(due);
            else
                RecalculateNextDue();
            return true;
        }

        public void CancelForCharacter(long characterId)
        {
            if (characterId > 0 && _byParticipant.TryGetValue(characterId, out Session session)) End(session);
        }

        public void NotifyMovementIntent(long characterId) => CancelForCharacter(characterId);

        /// <summary>
        /// Witness/camera systems call this only when feeding has actually been observed.
        /// Unobserved feeding creates no Heat/evidence mutation or owner packet.
        /// </summary>
        public bool RecordExposure(PlayerRuntime source, IncidentObservation observation)
        {
            if (source == null || !observation.Observed || !TryGetFeedingProfile(source, out FactionGameplayProfileDefinition profile, out _))
                return false;
            if (profile.feedingIncidentDataId == 0 || profile.feedingJurisdictionDataId == 0)
                return false;
            return _progression.RecordIncident(source, profile.feedingIncidentDataId, profile.feedingJurisdictionDataId, observation);
        }

        private bool ApplyPulse(Session session)
        {
            FactionGameplayProfileDefinition profile = session.Profile;
            if (!session.Target.TryGetCharacterResource(CharacterResourceId.Health, out _, out CharacterResourceState targetHealth) || !targetHealth.Enabled)
                return false;
            if (profile.feedingGainResourceId == CharacterResourceId.None ||
                !session.Source.TryGetCharacterResource(profile.feedingGainResourceId, out _, out CharacterResourceState gainResource) ||
                !gainResource.Enabled)
                return false;

            int minimumHealth = Math.Max(targetHealth.Minimum + 1,
                (int)Math.Ceiling(targetHealth.Maximum * Math.Max(0.01f, Math.Min(0.95f, profile.feedingMinimumTargetHealthPercent))));
            int availableHealth = targetHealth.Current - minimumHealth;
            if (availableHealth <= 0 || gainResource.Current >= gainResource.Maximum) return false;

            int requestedDrain = Math.Max(1, profile.feedingHealthPerPulse);
            int drain = Math.Min(requestedDrain, availableHealth);
            float ratio = Math.Max(0f, profile.feedingResourcePerHealth);
            int requestedGain = ratio <= 0f ? 0 : Math.Max(1, (int)Math.Round(drain * ratio, MidpointRounding.AwayFromZero));
            if (requestedGain > 0)
            {
                int capacity = Math.Max(0, gainResource.Maximum - gainResource.Current);
                if (capacity <= 0) return false;
                requestedGain = Math.Min(requestedGain, capacity);
            }

            CharacterResourceOperationResult drainResult = _resources.Remove(
                session.Target,
                CharacterResourceId.Health,
                drain,
                CharacterResourceChangeReason.Feeding);
            if (!drainResult.Success) return false;

            if (requestedGain > 0)
            {
                CharacterResourceOperationResult gainResult = _resources.Add(
                    session.Source,
                    profile.feedingGainResourceId,
                    requestedGain,
                    CharacterResourceChangeReason.Feeding);
                if (!gainResult.Success) return false;
            }

            if (profile.feedingExposureResourceId != CharacterResourceId.None && profile.feedingExposurePerPulse > 0)
                _resources.Add(session.Source, profile.feedingExposureResourceId, profile.feedingExposurePerPulse, CharacterResourceChangeReason.Feeding);
            if (profile.feedingTrackDataId != 0 && profile.feedingTrackGainPerPulse > 0)
                _progression.GainTrack(session.Source, profile.feedingTrackDataId, profile.feedingTrackGainPerPulse);
            return true;
        }

        private bool TryGetFeedingProfile(PlayerRuntime source, out FactionGameplayProfileDefinition profile, out string reason)
        {
            profile = null;
            reason = string.Empty;
            ushort factionDataId = source?.CaptureProgressionState()?.factionDataId ?? 0;
            if (factionDataId == 0 || !_content.TryGetFaction(factionDataId, out FactionDefinition faction))
            {
                reason = "feeding requires an assigned faction";
                return false;
            }
            profile = faction.gameplayProfile;
            if (profile == null || !profile.feedingEnabled)
            {
                reason = "current faction cannot feed";
                return false;
            }
            return true;
        }

        private static bool CanContinue(PlayerRuntime source, PlayerRuntime target, FactionGameplayProfileDefinition profile, out string reason)
        {
            reason = string.Empty;
            if (source == null || target == null) { reason = "participant is unavailable"; return false; }
            if (!string.Equals(source.Location.MapId, target.Location.MapId, StringComparison.Ordinal) ||
                !string.Equals(source.Location.InstanceId, target.Location.InstanceId, StringComparison.Ordinal))
            { reason = "participants are no longer in the same world"; return false; }
            if (!IsAlive(source) || !IsAlive(target)) { reason = "participant is not alive"; return false; }

            float maximumRange = Math.Max(0.5f, profile?.feedingMaximumRange ?? 2.5f);
            WorldPosition a = source.Location.Position;
            WorldPosition b = target.Location.Position;
            double dx = a.X - b.X, dy = a.Y - b.Y, dz = a.Z - b.Z;
            if (dx * dx + dy * dy + dz * dz > maximumRange * maximumRange)
            { reason = "feeding target moved out of range"; return false; }
            return true;
        }

        private static bool IsAlive(PlayerRuntime runtime) =>
            runtime != null && runtime.TryGetCharacterResource(CharacterResourceId.Health, out _, out CharacterResourceState health) &&
            health.Enabled && health.Current > health.Minimum;

        private void End(Session session)
        {
            if (session == null) return;
            _bySource.Remove(session.Source?.CharacterId.Value ?? 0);
            _byParticipant.Remove(session.Source?.CharacterId.Value ?? 0);
            _byParticipant.Remove(session.Target?.CharacterId.Value ?? 0);
            RecalculateNextDue();
        }

        private void RecalculateNextDue()
        {
            double next = double.PositiveInfinity;
            foreach (Session session in _bySource.Values)
                if (session.NextPulseAt < next) next = session.NextPulseAt;
            _nextDueAt = next;
        }

        private static InteractionResult Fail(in InteractionExecutionContext context, InteractionResultCode code, string detail) =>
            new InteractionResult(context.Sequence, context.ActionId, code, context.Target, detail);
    }
}
