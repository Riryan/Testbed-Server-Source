using System;
using System.Collections.Generic;
using Game.Server.Domain.Players;
using Game.Shared.Interactions;
using Game.Shared.World;

namespace Game.Server.Application.Interactions
{
    public sealed class ServerInteractionSession
    {
        public long SessionId { get; internal set; }
        public InteractionActionId ActionId { get; internal set; }
        public InteractionTargetHandle Target { get; internal set; }
        public InteractionConsentMode ConsentMode { get; internal set; }
        public InteractionSessionState State { get; internal set; }
        public long InitiatorCharacterId { get; internal set; }
        public long TargetCharacterId { get; internal set; }
        public double CreatedAt { get; internal set; }
        public double ExpiresAt { get; internal set; }
        public double StartedAt { get; internal set; }
        public double CompletesAt { get; internal set; }
        public bool LockMovement { get; internal set; }
        public bool LockRotation { get; internal set; }
        public bool CancelOnDamage { get; internal set; }
        public bool CancelOnMovement { get; internal set; }
        public bool CancelOnTargetUnavailable { get; internal set; }
        public string MapId { get; internal set; } = string.Empty;
        public string InstanceId { get; internal set; } = string.Empty;
        public long WorldObjectId { get; internal set; }
        public string SlotId { get; internal set; } = string.Empty;
        public ServerPose AnchorPose { get; internal set; }
        public ServerPose ExitPose { get; internal set; }
        public bool HasExitPose { get; internal set; }
    }

    /// <summary>
    /// Shared authoritative lifetime for consent, occupancy and duration-based prop/social
    /// interactions. Instant operations continue to be owned by their feature handlers.
    /// </summary>
    public sealed class InteractionSessionService
    {
        private readonly Dictionary<long, ServerInteractionSession> _sessions = new Dictionary<long, ServerInteractionSession>();
        private readonly Dictionary<long, long> _sessionByCharacter = new Dictionary<long, long>();
        private readonly WorldInteractableService _worldObjects;
        private long _nextSessionId;

        public event Action<ServerInteractionSession> Changed;
        public int Count => _sessions.Count;

        public InteractionSessionService(WorldInteractableService worldObjects) =>
            _worldObjects = worldObjects ?? throw new ArgumentNullException(nameof(worldObjects));

        public bool TryStartWorldSession(
            PlayerRuntime actor,
            long worldObjectId,
            InteractionActionId actionId,
            double now,
            out ServerInteractionSession session,
            out string detail,
            float fixedDurationOverrideSeconds = -1f)
        {
            session = null;
            detail = string.Empty;
            if (actor == null)
            {
                detail = "actor is unavailable";
                return false;
            }
            if (_sessionByCharacter.ContainsKey(actor.CharacterId.Value))
            {
                detail = "actor already has an active interaction";
                return false;
            }

            string mapId = actor.Location.MapId;
            string instanceId = actor.Location.InstanceId;
            if (!_worldObjects.TryGet(mapId, instanceId, worldObjectId, out WorldInteractableRuntime target))
            {
                detail = "world object unavailable";
                return false;
            }

            ServerContextualInteractionDefinition def = WorldInteractableService.FindDefinition(target.Definition, actionId);
            if (def == null || !_worldObjects.Evaluate(actor, target, def, out detail)) return false;

            if (def.exclusiveOccupancy && target.ActiveSessionId != 0)
            {
                detail = "interaction is already in use";
                return false;
            }

            ServerInteractionSlotDefinition slot = null;
            if ((target.Definition.slots?.Length ?? 0) > 0 &&
                !target.TryReserve(actor.CharacterId.Value, string.Empty, out slot))
            {
                detail = "interaction slot unavailable";
                return false;
            }

            long id = ++_nextSessionId;
            if (id <= 0) id = _nextSessionId = 1;
            bool needsRemoteConsent =
                def.consentMode == InteractionConsentMode.TargetAcceptance ||
                def.consentMode == InteractionConsentMode.MutualOptIn;

            session = new ServerInteractionSession
            {
                SessionId = id,
                ActionId = actionId,
                Target = new InteractionTargetHandle(InteractionTargetKind.SceneObject, worldObjectId),
                ConsentMode = def.consentMode,
                State = needsRemoteConsent ? InteractionSessionState.Pending : InteractionSessionState.Active,
                InitiatorCharacterId = actor.CharacterId.Value,
                CreatedAt = now,
                ExpiresAt = now + 20d,
                StartedAt = needsRemoteConsent ? 0d : now,
                CompletesAt = !needsRemoteConsent &&
                    (fixedDurationOverrideSeconds > 0f || def.fixedDurationSeconds > 0f)
                    ? now + (fixedDurationOverrideSeconds > 0f ? fixedDurationOverrideSeconds : def.fixedDurationSeconds)
                    : 0d,
                LockMovement = def.lockMovement,
                LockRotation = def.lockRotation,
                CancelOnDamage = def.cancelOnDamage,
                CancelOnMovement = def.cancelOnMovement,
                CancelOnTargetUnavailable = def.cancelOnTargetUnavailable,
                MapId = mapId,
                InstanceId = instanceId,
                WorldObjectId = worldObjectId,
                SlotId = slot?.slotId ?? string.Empty,
                AnchorPose = slot?.anchor ?? target.Definition.pose,
                ExitPose = slot?.exit ?? default,
                HasExitPose = slot != null && slot.hasExit,
            };

            _sessions.Add(id, session);
            _sessionByCharacter.Add(actor.CharacterId.Value, id);
            target.ActiveSessionId = id;
            target.Phase = WorldInteractablePhase.Enter;
            _worldObjects.PublishChanged(target);
            Changed?.Invoke(session);
            return true;
        }

        public bool TryGet(long sessionId, out ServerInteractionSession session) => _sessions.TryGetValue(sessionId, out session);

        public bool TryGetForCharacter(long characterId, out ServerInteractionSession session)
        {
            if (_sessionByCharacter.TryGetValue(characterId, out long id) && _sessions.TryGetValue(id, out session)) return true;
            session = null;
            return false;
        }

        public bool CancelForCharacter(long characterId, string reason)
        {
            if (!TryGetForCharacter(characterId, out ServerInteractionSession session)) return false;
            Finish(session, InteractionSessionState.Cancelled, reason);
            return true;
        }

        public bool TryAccept(long sessionId, long acceptingCharacterId, double now, out string detail)
        {
            detail = string.Empty;
            if (!_sessions.TryGetValue(sessionId, out ServerInteractionSession session) || session.State != InteractionSessionState.Pending)
            {
                detail = "interaction request is unavailable";
                return false;
            }
            if (now >= session.ExpiresAt)
            {
                Finish(session, InteractionSessionState.Expired, "interaction request expired");
                detail = "interaction request expired";
                return false;
            }
            if (session.TargetCharacterId > 0 && session.TargetCharacterId != acceptingCharacterId)
            {
                detail = "actor is not a participant in this request";
                return false;
            }

            session.TargetCharacterId = acceptingCharacterId;
            session.State = InteractionSessionState.Active;
            session.StartedAt = now;
            Changed?.Invoke(session);
            return true;
        }

        public bool TryDecline(long sessionId, long decliningCharacterId, out string detail)
        {
            detail = string.Empty;
            if (!_sessions.TryGetValue(sessionId, out ServerInteractionSession session) || session.State != InteractionSessionState.Pending)
            {
                detail = "interaction request is unavailable";
                return false;
            }
            if (session.TargetCharacterId > 0 && session.TargetCharacterId != decliningCharacterId)
            {
                detail = "actor is not a participant in this request";
                return false;
            }
            Finish(session, InteractionSessionState.Declined, "declined");
            return true;
        }

        public void NotifyMovementIntent(long characterId)
        {
            if (TryGetForCharacter(characterId, out ServerInteractionSession session) && session.CancelOnMovement)
                Finish(session, InteractionSessionState.Cancelled, "cancelled by movement");
        }

        public void NotifyDamage(long characterId)
        {
            if (TryGetForCharacter(characterId, out ServerInteractionSession session) && session.CancelOnDamage)
                Finish(session, InteractionSessionState.Cancelled, "cancelled by damage");
        }

        public void NotifyDisconnect(long characterId) => CancelForCharacter(characterId, "participant disconnected");

        public void Tick(double now)
        {
            if (_sessions.Count == 0) return;
            List<ServerInteractionSession> finished = null;
            foreach (ServerInteractionSession session in _sessions.Values)
            {
                if (session.State == InteractionSessionState.Pending && now >= session.ExpiresAt)
                {
                    session.State = InteractionSessionState.Expired;
                    finished ??= new List<ServerInteractionSession>();
                    finished.Add(session);
                }
                else if (session.State == InteractionSessionState.Active && session.CompletesAt > 0d && now >= session.CompletesAt)
                {
                    session.State = InteractionSessionState.Completed;
                    finished ??= new List<ServerInteractionSession>();
                    finished.Add(session);
                }
            }
            if (finished == null) return;
            for (int i = 0; i < finished.Count; ++i)
                Finish(finished[i], finished[i].State, string.Empty);
        }

        private void Finish(ServerInteractionSession session, InteractionSessionState terminal, string reason)
        {
            if (session == null || !_sessions.Remove(session.SessionId)) return;
            _sessionByCharacter.Remove(session.InitiatorCharacterId);
            if (session.TargetCharacterId > 0)
                _sessionByCharacter.Remove(session.TargetCharacterId);
            session.State = terminal;

            if (_worldObjects.TryGet(session.MapId, session.InstanceId, session.WorldObjectId, out WorldInteractableRuntime target))
            {
                target.Release(session.InitiatorCharacterId);
                if (session.TargetCharacterId > 0) target.Release(session.TargetCharacterId);
                target.ActiveSessionId = 0;
                target.Phase = WorldInteractablePhase.Exit;
                _worldObjects.PublishChanged(target);
            }
            Changed?.Invoke(session);
        }
    }
}
