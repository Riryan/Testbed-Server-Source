using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using Game.Server.Application.Actors;
using Game.Server.Domain.Players;
using Game.Shared.Actors;
using Game.Shared.Interactions;
using Game.Shared.Resources;
using Game.Shared.World;

namespace Game.Server.Application.Interactions
{
    public readonly struct InteractionExecutionContext
    {
        public PlayerRuntime Source { get; }
        public PlayerRuntime TargetPlayer { get; }
        public InteractionTargetHandle Target { get; }
        public InteractionActionId ActionId { get; }
        public uint Sequence { get; }
        public double ServerTime { get; }

        public InteractionExecutionContext(PlayerRuntime source, PlayerRuntime targetPlayer, InteractionTargetHandle target, InteractionActionId actionId, uint sequence, double serverTime)
        {
            Source = source; TargetPlayer = targetPlayer; Target = target; ActionId = actionId; Sequence = sequence; ServerTime = serverTime;
        }
    }

    public readonly struct ActorInteractionExecutionContext
    {
        public PlayerRuntime Source { get; }
        public AuthoritativeActorRuntime TargetActor { get; }
        public InteractionTargetHandle Target { get; }
        public InteractionActionId ActionId { get; }
        public uint Sequence { get; }
        public double ServerTime { get; }

        public ActorInteractionExecutionContext(
            PlayerRuntime source,
            AuthoritativeActorRuntime targetActor,
            InteractionTargetHandle target,
            InteractionActionId actionId,
            uint sequence,
            double serverTime)
        {
            Source = source;
            TargetActor = targetActor;
            Target = target;
            ActionId = actionId;
            Sequence = sequence;
            ServerTime = serverTime;
        }
    }

    public readonly struct WorldInteractionExecutionContext
    {
        public PlayerRuntime Source { get; }
        public InteractionTargetHandle Target { get; }
        public InteractionActionId ActionId { get; }
        public uint Sequence { get; }
        public double ServerTime { get; }
        public string MapId { get; }
        public string InstanceId { get; }
        public WorldPosition Position { get; }

        public WorldInteractionExecutionContext(PlayerRuntime source, InteractionTargetHandle target, InteractionActionId actionId, uint sequence, double serverTime, string mapId, string instanceId, WorldPosition position)
        {
            Source = source; Target = target; ActionId = actionId; Sequence = sequence; ServerTime = serverTime;
            MapId = mapId ?? string.Empty; InstanceId = instanceId ?? string.Empty; Position = position;
        }
    }

    public interface IInteractionActionHandler
    {
        InteractionActionId ActionId { get; }
        InteractionResult Execute(in InteractionExecutionContext context);
    }

    /// <summary>
    /// Optional presentation metadata supplied by an authoritative action owner. The
    /// server still decides whether the action is currently available; clients receive
    /// this metadata only as menu/presentation data.
    /// </summary>
    public interface IInteractionActionDescriptorProvider
    {
        InteractionActionEntry DescribeAction();
    }

    public interface IActorInteractionActionHandler
    {
        InteractionActionId ActionId { get; }
        InteractionResult Execute(in ActorInteractionExecutionContext context);
    }

    public interface IAsyncWorldInteractionActionHandler
    {
        InteractionTargetKind TargetKind { get; }
        InteractionActionId ActionId { get; }
        Task<InteractionResult> ExecuteAsync(WorldInteractionExecutionContext context, CancellationToken cancellationToken);
    }

    /// <summary>
    /// Generic authoritative interaction dispatcher. Gameplay systems register only the
    /// actions they own. Player and world targets share the same state/world/range gates;
    /// asynchronous world handlers are used for durable ownership transfers such as loot.
    /// </summary>
    public sealed class InteractionService
    {
        private readonly struct WorldActionKey : IEquatable<WorldActionKey>
        {
            public InteractionTargetKind TargetKind { get; }
            public InteractionActionId ActionId { get; }

            public WorldActionKey(InteractionTargetKind targetKind, InteractionActionId actionId)
            {
                TargetKind = targetKind;
                ActionId = actionId;
            }

            public bool Equals(WorldActionKey other) => TargetKind == other.TargetKind && ActionId == other.ActionId;
            public override bool Equals(object obj) => obj is WorldActionKey other && Equals(other);
            public override int GetHashCode() => ((int)TargetKind * 397) ^ (int)ActionId;
        }

        private readonly Dictionary<InteractionActionId, IInteractionActionHandler> _handlers = new Dictionary<InteractionActionId, IInteractionActionHandler>();
        private readonly Dictionary<InteractionActionId, IActorInteractionActionHandler> _actorHandlers = new Dictionary<InteractionActionId, IActorInteractionActionHandler>();
        private readonly Dictionary<WorldActionKey, IAsyncWorldInteractionActionHandler> _worldHandlers = new Dictionary<WorldActionKey, IAsyncWorldInteractionActionHandler>();

        public float PlayerInteractionRange { get; }
        public event Action<InteractionResult> Resolved;

        public InteractionService(float playerInteractionRange = InteractionRangePolicy.PlayerInteractionRange)
        {
            if (float.IsNaN(playerInteractionRange) || float.IsInfinity(playerInteractionRange) || playerInteractionRange <= 0f)
                throw new ArgumentOutOfRangeException(nameof(playerInteractionRange));
            PlayerInteractionRange = playerInteractionRange;
        }

        public bool Register(IInteractionActionHandler handler)
        {
            if (handler == null || handler.ActionId == InteractionActionId.None || _handlers.ContainsKey(handler.ActionId)) return false;
            _handlers.Add(handler.ActionId, handler); return true;
        }

        public bool RegisterActor(IActorInteractionActionHandler handler)
        {
            if (handler == null || handler.ActionId == InteractionActionId.None || _actorHandlers.ContainsKey(handler.ActionId))
                return false;
            _actorHandlers.Add(handler.ActionId, handler);
            return true;
        }

        public bool RegisterWorld(IAsyncWorldInteractionActionHandler handler)
        {
            if (handler == null || handler.TargetKind == InteractionTargetKind.None || handler.ActionId == InteractionActionId.None) return false;
            var key = new WorldActionKey(handler.TargetKind, handler.ActionId);
            if (_worldHandlers.ContainsKey(key)) return false;
            _worldHandlers.Add(key, handler); return true;
        }

        public bool Unregister(IInteractionActionHandler handler)
        {
            if (handler == null || !_handlers.TryGetValue(handler.ActionId, out IInteractionActionHandler current) || !ReferenceEquals(current, handler)) return false;
            return _handlers.Remove(handler.ActionId);
        }

        public bool UnregisterActor(IActorInteractionActionHandler handler)
        {
            if (handler == null ||
                !_actorHandlers.TryGetValue(handler.ActionId, out IActorInteractionActionHandler current) ||
                !ReferenceEquals(current, handler))
            {
                return false;
            }

            return _actorHandlers.Remove(handler.ActionId);
        }

        public bool UnregisterWorld(IAsyncWorldInteractionActionHandler handler)
        {
            if (handler == null) return false;
            var key = new WorldActionKey(handler.TargetKind, handler.ActionId);
            if (!_worldHandlers.TryGetValue(key, out IAsyncWorldInteractionActionHandler current) || !ReferenceEquals(current, handler)) return false;
            return _worldHandlers.Remove(key);
        }

        public InteractionActionSet DiscoverPlayerActions(PlayerRuntime source, PlayerRuntime target, double now)
        {
            InteractionTargetHandle handle = target == null ? default : InteractionTargetHandle.Player(target.CharacterId.Value);
            string targetLabel = target?.Character?.Name ?? string.Empty;
            var entries = new List<InteractionActionEntry>(_handlers.Count);
            foreach (KeyValuePair<InteractionActionId, IInteractionActionHandler> pair in _handlers)
            {
                InteractionActionEntry entry = Describe(pair.Key, pair.Value);
                InteractionResult validation = ValidatePlayerAction(source, target, handle, pair.Key, 0, now);
                if (validation.ResultCode == InteractionResultCode.Success)
                    entry = entry.WithAvailability(InteractionAvailability.Available);
                else
                    entry = entry.WithAvailability(InteractionAvailability.Disabled, validation.Detail);
                entries.Add(entry);
            }
            entries.Sort(CompareEntries);
            return new InteractionActionSet(handle, targetLabel, entries.ToArray());
        }

        public InteractionActionSet DiscoverActorActions(
            PlayerRuntime source,
            AuthoritativeActorRuntime target,
            double now)
        {
            InteractionTargetHandle handle = ToInteractionTarget(target);
            string targetLabel = target?.DisplayName ?? string.Empty;
            var entries = new List<InteractionActionEntry>(_actorHandlers.Count);

            foreach (KeyValuePair<InteractionActionId, IActorInteractionActionHandler> pair in _actorHandlers)
            {
                InteractionActionEntry entry = Describe(pair.Key, pair.Value);
                InteractionResult validation = ValidateActorAction(source, target, handle, pair.Key, 0, now);
                if (validation.ResultCode == InteractionResultCode.Success)
                    entry = entry.WithAvailability(InteractionAvailability.Available);
                else
                    entry = entry.WithAvailability(InteractionAvailability.Disabled, validation.Detail);
                entries.Add(entry);
            }

            entries.Sort(CompareEntries);
            return new InteractionActionSet(handle, targetLabel, entries.ToArray());
        }

        public InteractionActionSet DiscoverWorldActions(
            PlayerRuntime source,
            InteractionTargetHandle target,
            string targetLabel,
            string mapId,
            string instanceId,
            WorldPosition position,
            double now)
        {
            var entries = new List<InteractionActionEntry>(_worldHandlers.Count);
            foreach (KeyValuePair<WorldActionKey, IAsyncWorldInteractionActionHandler> pair in _worldHandlers)
            {
                if (pair.Key.TargetKind != target.Kind) continue;
                InteractionActionEntry entry = Describe(pair.Key.ActionId, pair.Value);
                InteractionResult validation = ValidateWorldAction(source, target, mapId, instanceId, position, pair.Key.ActionId, 0, now);
                if (validation.ResultCode == InteractionResultCode.Success)
                    entry = entry.WithAvailability(InteractionAvailability.Available);
                else
                    entry = entry.WithAvailability(InteractionAvailability.Disabled, validation.Detail);
                entries.Add(entry);
            }
            entries.Sort(CompareEntries);
            return new InteractionActionSet(target, targetLabel, entries.ToArray());
        }

        public InteractionResult ExecutePlayerAction(PlayerRuntime source, PlayerRuntime target, InteractionActionId actionId, uint sequence, double now)
        {
            InteractionTargetHandle handle = target == null ? default : InteractionTargetHandle.Player(target.CharacterId.Value);
            InteractionResult invalid = ValidatePlayerAction(source, target, handle, actionId, sequence, now);
            if (invalid.ResultCode != InteractionResultCode.Success) return Publish(invalid);
            if (!_handlers.TryGetValue(actionId, out IInteractionActionHandler handler)) return Publish(new InteractionResult(sequence, actionId, InteractionResultCode.Unsupported, handle, "interaction action has no authoritative owner yet"));
            return Publish(handler.Execute(new InteractionExecutionContext(source, target, handle, actionId, sequence, now)));
        }

        public InteractionResult ExecuteActorAction(
            PlayerRuntime source,
            AuthoritativeActorRuntime target,
            InteractionActionId actionId,
            uint sequence,
            double now)
        {
            InteractionTargetHandle handle = ToInteractionTarget(target);
            InteractionResult invalid = ValidateActorAction(source, target, handle, actionId, sequence, now);
            if (invalid.ResultCode != InteractionResultCode.Success)
                return Publish(invalid);

            if (!_actorHandlers.TryGetValue(actionId, out IActorInteractionActionHandler handler))
            {
                return Publish(new InteractionResult(
                    sequence,
                    actionId,
                    InteractionResultCode.Unsupported,
                    handle,
                    "actor interaction action has no authoritative owner yet"));
            }

            return Publish(handler.Execute(new ActorInteractionExecutionContext(
                source,
                target,
                handle,
                actionId,
                sequence,
                now)));
        }

        public async Task<InteractionResult> ExecuteWorldActionAsync(
            PlayerRuntime source,
            InteractionTargetHandle target,
            string mapId,
            string instanceId,
            WorldPosition position,
            InteractionActionId actionId,
            uint sequence,
            double now,
            CancellationToken cancellationToken)
        {
            InteractionResult invalid = ValidateWorldAction(source, target, mapId, instanceId, position, actionId, sequence, now);
            if (invalid.ResultCode != InteractionResultCode.Success) return Publish(invalid);
            if (!_worldHandlers.TryGetValue(new WorldActionKey(target.Kind, actionId), out IAsyncWorldInteractionActionHandler handler))
                return Publish(new InteractionResult(sequence, actionId, InteractionResultCode.Unsupported, target, "interaction action has no authoritative owner yet"));

            InteractionResult result = await handler.ExecuteAsync(
                new WorldInteractionExecutionContext(source, target, actionId, sequence, now, mapId, instanceId, position), cancellationToken).ConfigureAwait(false);
            return Publish(result);
        }

        private InteractionResult ValidateActorAction(
            PlayerRuntime source,
            AuthoritativeActorRuntime target,
            InteractionTargetHandle handle,
            InteractionActionId actionId,
            uint sequence,
            double now)
        {
            InteractionResult invalid = ValidateSource(source, handle, actionId, sequence, now);
            if (invalid.ResultCode != InteractionResultCode.Success)
                return invalid;

            if (target == null ||
                !target.Handle.IsValid ||
                target.Handle.kind == AuthoritativeActorKind.Player ||
                !target.Alive ||
                target.HealthCurrent <= 0)
            {
                return new InteractionResult(
                    sequence,
                    actionId,
                    InteractionResultCode.InvalidTarget,
                    handle,
                    "actor target is unavailable");
            }

            if (!string.Equals(source.Location.MapId, target.MapId, StringComparison.Ordinal) ||
                !string.Equals(source.Location.InstanceId, target.InstanceId ?? string.Empty, StringComparison.Ordinal))
            {
                return new InteractionResult(
                    sequence,
                    actionId,
                    InteractionResultCode.LocationRestricted,
                    handle,
                    "actor target is in another world");
            }

            if (!WithinPlanarRange(source.Location.Position, target.Position, PlayerInteractionRange))
            {
                return new InteractionResult(
                    sequence,
                    actionId,
                    InteractionResultCode.OutOfRange,
                    handle,
                    "actor target is out of range");
            }

            return new InteractionResult(
                sequence,
                actionId,
                InteractionResultCode.Success,
                handle);
        }

        private InteractionResult ValidatePlayerAction(PlayerRuntime source, PlayerRuntime target, InteractionTargetHandle handle, InteractionActionId actionId, uint sequence, double now)
        {
            InteractionResult invalid = ValidateSource(source, handle, actionId, sequence, now);
            if (invalid.ResultCode != InteractionResultCode.Success) return invalid;
            if (target == null || !IsAlive(target) || ReferenceEquals(source, target))
                return new InteractionResult(sequence, actionId, InteractionResultCode.InvalidTarget, handle, "target is unavailable");
            if (!SameWorld(source, target))
                return new InteractionResult(sequence, actionId, InteractionResultCode.LocationRestricted, handle, "target is in another world");
            if (!WithinPlanarRange(source.Location.Position, target.Location.Position, PlayerInteractionRange))
                return new InteractionResult(sequence, actionId, InteractionResultCode.OutOfRange, handle, "target is out of range");
            return new InteractionResult(sequence, actionId, InteractionResultCode.Success, handle);
        }

        private InteractionResult ValidateWorldAction(PlayerRuntime source, InteractionTargetHandle target, string mapId, string instanceId, WorldPosition position, InteractionActionId actionId, uint sequence, double now)
        {
            InteractionResult invalid = ValidateSource(source, target, actionId, sequence, now);
            if (invalid.ResultCode != InteractionResultCode.Success) return invalid;
            if (!target.IsValid || target.Kind != InteractionTargetKind.NetworkWorldObject)
                return new InteractionResult(sequence, actionId, InteractionResultCode.InvalidTarget, target, "world target is invalid");
            if (!string.Equals(source.Location.MapId, mapId, StringComparison.Ordinal) || !string.Equals(source.Location.InstanceId, instanceId ?? string.Empty, StringComparison.Ordinal))
                return new InteractionResult(sequence, actionId, InteractionResultCode.LocationRestricted, target, "target is in another world");
            if (!WithinRange(source.Location.Position, position, InteractionRangePolicy.WorldObjectUseRange))
                return new InteractionResult(sequence, actionId, InteractionResultCode.OutOfRange, target, "target is out of range");
            return new InteractionResult(sequence, actionId, InteractionResultCode.Success, target);
        }

        private static InteractionTargetHandle ToInteractionTarget(AuthoritativeActorRuntime target)
        {
            if (target == null || !target.Handle.IsValid)
                return default;

            InteractionTargetKind kind = target.Handle.kind == AuthoritativeActorKind.Population
                ? InteractionTargetKind.PopulationEntity
                : InteractionTargetKind.NetworkEntity;

            return new InteractionTargetHandle(
                kind,
                target.Handle.actorId,
                target.Handle.generation);
        }

        private static InteractionActionEntry Describe(InteractionActionId actionId, object handler)
        {
            if (handler is IInteractionActionDescriptorProvider provider)
            {
                InteractionActionEntry described = provider.DescribeAction();
                if (described.ActionId == actionId)
                    return described;
            }
            return InteractionActionCatalog.Default(actionId);
        }

        private static int CompareEntries(InteractionActionEntry left, InteractionActionEntry right)
        {
            int order = left.SortOrder.CompareTo(right.SortOrder);
            if (order != 0) return order;
            return ((ushort)left.ActionId).CompareTo((ushort)right.ActionId);
        }

        private static InteractionResult ValidateSource(PlayerRuntime source, InteractionTargetHandle target, InteractionActionId actionId, uint sequence, double now)
        {
            if (source == null || !IsAlive(source)) return new InteractionResult(sequence, actionId, InteractionResultCode.InvalidState, target, "source is unavailable");
            if (actionId == InteractionActionId.None) return new InteractionResult(sequence, actionId, InteractionResultCode.Unsupported, target, "interaction action is not specified");
            if (!IsFiniteTime(now)) return new InteractionResult(sequence, actionId, InteractionResultCode.InvalidState, target, "server time is invalid");
            if (source.CaptureActionState().ActiveCast.IsActive) return new InteractionResult(sequence, actionId, InteractionResultCode.SessionBusy, target, "source is casting");
            return new InteractionResult(sequence, actionId, InteractionResultCode.Success, target);
        }

        private InteractionResult Publish(InteractionResult result) { Resolved?.Invoke(result); return result; }
        private static bool IsAlive(PlayerRuntime runtime) => runtime != null && runtime.TryGetCharacterResource(CharacterResourceId.Health, out _, out var health) && health.Enabled && health.Current > health.Minimum;
        private static bool SameWorld(PlayerRuntime a, PlayerRuntime b) => string.Equals(a.Location.MapId, b.Location.MapId, StringComparison.Ordinal) && string.Equals(a.Location.InstanceId, b.Location.InstanceId, StringComparison.Ordinal);
        private static bool WithinPlanarRange(WorldPosition p, WorldPosition q, float range)
        {
            double dx = p.X - q.X;
            double dz = p.Z - q.Z;
            double r = Math.Max(0.1d, range);
            return dx * dx + dz * dz <= r * r;
        }

        private static bool WithinRange(WorldPosition p, WorldPosition q, float range)
        {
            double dx = p.X - q.X;
            double dy = p.Y - q.Y;
            double dz = p.Z - q.Z;
            double r = Math.Max(0.1d, range);
            return dx * dx + dy * dy + dz * dz <= r * r;
        }

        private static bool IsFiniteTime(double value) => !double.IsNaN(value) && !double.IsInfinity(value) && value >= 0d;
    }
}
