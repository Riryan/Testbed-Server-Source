using Game.GameServer.Runtime;
using Game.Server.Application.Sessions;
using Game.Server.Application.Combat;
using Game.Shared.Content;
using Game.Server.Application.World;
using Game.Server.Application.Lifecycle;
using Game.Server.Application.Population;
using Game.Server.Domain.Characters;
using Game.Server.Domain.Players;
using Game.Shared.Abilities;
using Game.Shared.Combat;
using Game.Shared.Interactions;
using Game.Shared.Protocol;
using Game.Shared.Sessions;
using Game.Shared.World;
using LiteNetLib;
using LiteNetLib.Utils;
using Player.Networking;
using Player.Shared;

namespace Game.GameServer.Networking;

internal sealed partial class GameServerHost
{

    private void RegisterGameplayActionRequests(
        Dictionary<ushort, Action<ClientSession, uint, NetDataReader>> handlers)
    {
        RegisterRequest(handlers, PlayerGameplayActionRequestTypes.BasicAttack, HandleBasicAttack);
        RegisterRequest(handlers, PlayerGameplayActionRequestTypes.BeginAbility, HandleBeginAbility);
        RegisterRequest(handlers, PlayerGameplayActionRequestTypes.CancelAbility, HandleCancelAbility);
        RegisterRequest(handlers, PlayerGameplayActionRequestTypes.Interaction, HandleInteraction);
        RegisterRequest(handlers, PlayerGameplayActionRequestTypes.Reload, HandleReload);
        RegisterRequest(handlers, PlayerGameplayActionRequestTypes.CombatOwnerState, HandleCombatOwnerState);
        RegisterRequest(handlers, PlayerGameplayActionRequestTypes.Respawn,
            (session, requestId, _) => HandleRespawn(session, requestId));
    }
    // Mirrors the production interaction gateway's 8 requests/sec sustained rate with
    // a short burst capacity of 12. This is deliberately independent from movement Hz.
    private const double PlayerInteractionRefillPerSecond = 4d;
    private const double PlayerInteractionBurstCapacity = 6d;

    private void HandleBasicAttack(ClientSession session, uint requestId, NetDataReader reader)
    {
        var request = new PlayerBasicAttackRequestMessage();
        request.Deserialize(reader);

        if (!TryGetGameplayRuntime(session, out PlayerRuntime source))
        {
            SendResponse(session, requestId, PlayerBasicAttackResponseMessage.Failed(
                BasicAttackResultCode.RejectedInvalidSource));
            return;
        }

        if (!TryResolveCombatTarget(session, request.target, out PlayerRuntime target))
        {
            SendResponse(session, requestId, PlayerBasicAttackResponseMessage.Failed(
                BasicAttackResultCode.RejectedInvalidTarget));
            return;
        }

        if (!HasAuthoritativeCombatLineOfSight(session, target))
        {
            SendResponse(session, requestId, PlayerBasicAttackResponseMessage.Failed(
                BasicAttackResultCode.RejectedInvalidTarget));
            return;
        }

        // Legacy request compatibility path. Current clean clients use the compact
        // CombatActionIntent message. Keep this request authoritative and self-contained
        // without coupling it to the newer targetless firearm trigger signature.
        BasicAttackResult result = _runtime.BasicAttacks.TryAttack(
            source, target, request.InputKind, _scheduler.ServerTime);
        SendResponse(session, requestId, ToGameplayWire(result));
    }

    private void HandleBeginAbility(ClientSession session, uint requestId, NetDataReader reader)
    {
        var request = new PlayerBeginAbilityRequestMessage();
        request.Deserialize(reader);

        if (!TryGetGameplayRuntime(session, out PlayerRuntime source))
        {
            SendResponse(session, requestId, PlayerAbilityRequestAckMessage.Failed(
                AbilityCastFailure.InvalidState, request.abilityWireId));
            return;
        }

        if (!_runtime.Content.TryGetAbility(request.abilityWireId, out AbilityDefinition ability))
        {
            SendResponse(session, requestId, PlayerAbilityRequestAckMessage.Failed(
                AbilityCastFailure.UnknownAbility, request.abilityWireId));
            return;
        }

        PlayerRuntime target = null;
        bool targetSupplied = request.target.IsValid;
        if (targetSupplied && !TryResolveCombatTarget(session, request.target, out target))
        {
            SendResponse(session, requestId, PlayerAbilityRequestAckMessage.Failed(
                AbilityCastFailure.InvalidTarget, request.abilityWireId));
            return;
        }

        if (targetSupplied && target != null && !ReferenceEquals(source, target) &&
            !HasAuthoritativeCombatLineOfSight(session, target))
        {
            SendResponse(session, requestId, PlayerAbilityRequestAckMessage.Failed(
                AbilityCastFailure.InvalidTarget, request.abilityWireId));
            return;
        }

        AbilityCastResult result = _runtime.Abilities.TryBeginCast(
            source,
            target,
            ability.definitionId,
            Math.Max(1, (int)request.rank),
            new WorldPosition(0f, 0f, 0f),
            _scheduler.ServerTime);
        SendResponse(session, requestId, new PlayerAbilityRequestAckMessage
        {
            success = result.Success,
            failure = (byte)result.Failure,
            abilityWireId = request.abilityWireId,
        });
    }

    private void HandleCancelAbility(ClientSession session, uint requestId, NetDataReader reader)
    {
        var request = new PlayerCancelAbilityRequestMessage();
        request.Deserialize(reader);

        if (!TryGetGameplayRuntime(session, out PlayerRuntime source))
        {
            SendResponse(session, requestId, PlayerAbilityRequestAckMessage.Failed(
                AbilityCastFailure.InvalidState));
            return;
        }

        AbilityCastResult result = _runtime.Abilities.CancelCast(source);
        PlayerAbilityCastStateMessage state = ToGameplayWire(result);
        SendResponse(session, requestId, new PlayerAbilityRequestAckMessage
        {
            success = result.Success,
            failure = (byte)result.Failure,
            abilityWireId = state.abilityWireId,
        });
    }

    private void HandleReload(ClientSession session, uint requestId, NetDataReader reader)
    {
        var request = new PlayerReloadRequestMessage();
        request.Deserialize(reader);

        if (!TryGetGameplayRuntime(session, out PlayerRuntime runtime))
        {
            SendResponse(session, requestId, PlayerReloadResponseMessage.Failed(
                (byte)CombatReloadResultCode.InvalidState));
            return;
        }
        if (!IsBackendPersistenceMutationAvailable)
        {
            SendResponse(session, requestId, PlayerReloadResponseMessage.Failed(
                (byte)CombatReloadResultCode.PersistenceRejected));
            return;
        }

        string preferredDefinitionId = string.Empty;
        if (request.preferredAmmoDataId != 0)
        {
            if (!_runtime.Content.TryGetItem(request.preferredAmmoDataId, out ItemDefinition preferredAmmo))
            {
                SendResponse(session, requestId, PlayerReloadResponseMessage.Failed(
                    (byte)CombatReloadResultCode.AmmoMismatch));
                return;
            }
            preferredDefinitionId = preferredAmmo.definitionId;
        }

        RunReloadAsync(session, requestId, runtime, preferredDefinitionId).Forget();
    }

    private async Task RunReloadAsync(
        ClientSession session,
        uint requestId,
        PlayerRuntime runtime,
        string preferredDefinitionId)
    {
        CombatReloadResult result;
        try
        {
            result = await _runtime.Reloads.ReloadAsync(
                runtime, preferredDefinitionId, _scheduler.ServerTime, CancellationToken.None)
                .ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            return;
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine($"Reload failed for peer {session?.Peer?.Id}: {ex.Message}");
            result = new CombatReloadResult(
                false,
                CombatReloadResultCode.PersistenceRejected,
                "reload transaction failed",
                default);
        }

        _mainThreadCompletions.Enqueue(() =>
        {
            if (!IsCurrent(session))
                return;

            SendResponse(session, requestId, ToGameplayWire(result));
            if (result.Success)
                PublishPlayerPresentationAction(session, PlayerEntityActionState.Reloading);
        });
    }

    private void PublishPlayerPresentationAction(
        ClientSession session,
        PlayerEntityActionState actionState)
    {
        if (session == null ||
            actionState == PlayerEntityActionState.None ||
            !IsCurrent(session) ||
            !session.Ready ||
            session.Entity == null)
        {
            return;
        }

        byte nextActionId = unchecked((byte)(session.PresentationActionId + 1));
        if (nextActionId == 0)
            nextActionId = 1;
        session.PresentationActionId = nextActionId;

        BroadcastSnapshot(
            session,
            session.Entity.SnapshotSpeed,
            session.Entity.SnapshotFlags,
            session.Entity.SnapshotMoveState,
            ServerPlayerSnapshotReason.PresentationState,
            (byte)actionState,
            nextActionId);
    }

    private void HandleCombatOwnerState(ClientSession session, uint requestId, NetDataReader reader)
    {
        var request = new PlayerCombatOwnerStateRequestMessage();
        request.Deserialize(reader);
        if (!TryGetGameplayRuntime(session, out PlayerRuntime runtime))
        {
            SendResponse(session, requestId, PlayerCombatOwnerStateMessage.Failed());
            return;
        }

        SendResponse(
            session,
            requestId,
            ToGameplayWire(_runtime.CombatLoadout.Capture(runtime, _scheduler.ServerTime)));
    }

    private void HandleRespawn(ClientSession session, uint requestId)
    {
        if (!TryGetGameplayRuntime(session, out PlayerRuntime runtime) || session.Entity == null)
        {
            SendResponse(session, requestId, PlayerRespawnResponseMessage.Failed(
                PlayerRespawnResultCode.CharacterUnavailable,
                "character is not in world"));
            return;
        }

        CharacterRespawnResult result = _runtime.Lifecycle.TryRespawn(runtime);
        if (!result.Success)
        {
            SendResponse(session, requestId, ToGameplayWire(result));
            return;
        }

        session.Entity.Warp(result.Location);
        BroadcastSnapshot(
            session,
            0f,
            (byte)(PlayerEntityFlags.Grounded | PlayerEntityFlags.Teleport),
            (byte)PlayerEntityMoveState.Idle,
            ServerPlayerSnapshotReason.Forced | ServerPlayerSnapshotReason.PresentationState);

        SendResponse(session, requestId, ToGameplayWire(result));
        Console.WriteLine(
            $"Character respawned: peer={session.Peer.Id}, character={runtime.CharacterId.Value}, " +
            $"map={result.Location.MapId}, position=({result.Location.Position.X:0.##}, {result.Location.Position.Y:0.##}, {result.Location.Position.Z:0.##})");
    }

    private void HandleInteraction(ClientSession session, uint requestId, NetDataReader reader)
    {
        var request = new PlayerInteractionRequestMessage();
        request.Deserialize(reader);

        if (!TryGetGameplayRuntime(session, out PlayerRuntime source))
        {
            SendResponse(session, requestId, PlayerInteractionResponseMessage.Failed(
                request.sequence,
                request.CategoryId,
                request.ActionId,
                InteractionResultCode.InvalidState,
                "source character is not in world"));
            return;
        }

        if (!TryAdmitPlayerInteractionRequest(session, request.sequence, out InteractionResultCode admissionCode, out string admissionDetail))
        {
            SendResponse(session, requestId, PlayerInteractionResponseMessage.Failed(
                request.sequence,
                request.CategoryId,
                request.ActionId,
                admissionCode,
                admissionDetail));
            return;
        }

        if (!InteractionCategoryCatalog.IsCompatible(
                InteractionTargetKind.PlayerEntity,
                request.CategoryId,
                request.ActionId))
        {
            SendResponse(session, requestId, PlayerInteractionResponseMessage.Failed(
                request.sequence,
                request.CategoryId,
                request.ActionId,
                InteractionResultCode.Unsupported,
                "interaction category/action pair is not supported for player targets"));
            return;
        }

        if (!TryResolvePlayerTarget(session, request.target, out PlayerRuntime target))
        {
            SendResponse(session, requestId, PlayerInteractionResponseMessage.Failed(
                request.sequence,
                request.CategoryId,
                request.ActionId,
                InteractionResultCode.InvalidTarget,
                "target is unavailable or outside authoritative interest"));
            return;
        }

        // Party/Guild already have canonical standalone owners. Reuse them from the
        // existing player-interaction request instead of inventing a social wire route.
        if (request.ActionId == InteractionActionId.PartyInvite)
        {
            InteractionResult partyInvite = ExecutePartyInviteInteraction(source, target, request.sequence);
            SendResponse(session, requestId, ToGameplayWire(partyInvite));
            return;
        }
        if (request.ActionId == InteractionActionId.GuildInvite)
        {
            RunGuildInviteInteractionAsync(session, requestId, source, target, request.sequence).Forget();
            return;
        }

        InteractionResult result = _runtime.Interactions.ExecutePlayerAction(
            source,
            target,
            request.ActionId,
            request.sequence,
            _scheduler.ServerTime);
        SendResponse(session, requestId, ToGameplayWire(result));
    }

    /// <summary>
    /// Resolve an authoritative in-world runtime without depending on Inventory being
    /// initialized. Combat/ability/interaction ownership is the player session itself.
    /// </summary>
    private bool TryGetGameplayRuntime(ClientSession session, out PlayerRuntime runtime)
    {
        runtime = null;
        if (!IsCurrent(session) || !session.Ready || session.Entity == null)
            return false;
        if (!TryGetAuthoritativeSession(session, out PlayerSession authoritative) ||
            authoritative.State != PlayerSessionState.InWorld ||
            authoritative.Runtime == null ||
            !ReferenceEquals(authoritative.Runtime, session.Entity.Runtime))
        {
            return false;
        }

        runtime = authoritative.Runtime;
        return true;
    }

    /// <summary>
    /// Resolves a player combat/ability target through the authoritative observer graph.
    /// This deliberately allows the observer's own entity because self-targeted abilities
    /// are valid gameplay; WorldInterestService always owns the mandatory self edge.
    /// BasicAttackService remains responsible for its existing source/target gameplay rules.
    /// </summary>
    private bool TryResolveCombatTarget(
        ClientSession observer,
        CombatTargetReferenceWire reference,
        out PlayerRuntime runtime)
    {
        runtime = null;
        if (observer == null || !reference.IsValid || !IsCurrent(observer))
            return false;

        if (reference.IsPlayer)
        {
            return TryResolveCombatPlayerTarget(
                observer,
                new PlayerTargetReferenceWire
                {
                    objectId = reference.objectId,
                    generation = reference.generation,
                },
                out runtime);
        }

        if (!TryGetGameplayRuntime(observer, out PlayerRuntime source))
            return false;

        if (reference.IsPopulation)
        {
            if (!_runtime.PopulationCombat.TryGetPopulationActor(
                    reference.primaryId,
                    reference.generation,
                    out PopulationActorRuntime population) ||
                population?.Actor == null ||
                !string.Equals(source.Location.MapId, population.Actor.MapId, StringComparison.Ordinal) ||
                !string.Equals(source.Location.InstanceId, population.Actor.InstanceId, StringComparison.Ordinal))
            {
                return false;
            }

            // Do not let guessed actor IDs materialize heavyweight combat state across the
            // whole map. Explicit Population references must first be inside the same
            // authoritative AOI envelope; ability-specific range is revalidated afterwards.
            double dx = source.Location.Position.X - population.Actor.Position.X;
            double dz = source.Location.Position.Z - population.Actor.Position.Z;
            double admissionRange = Math.Max(1d, _options.AoiRange);
            if (dx * dx + dz * dz > admissionRange * admissionRange ||
                !_runtime.PopulationCombat.TryActivatePopulation(population, out runtime))
            {
                runtime = null;
                return false;
            }

            return true;
        }

        if (!reference.IsCombatTestTarget ||
            !_runtime.PopulationCombat.TryGet(
                source.Location.MapId,
                source.Location.InstanceId,
                reference.primaryId,
                out CombatTestDummyView dummy) ||
            dummy.Runtime == null)
        {
            return false;
        }

        runtime = dummy.Runtime;
        return true;
    }

    private bool TryResolveCombatPlayerTarget(
        ClientSession observer,
        PlayerTargetReferenceWire reference,
        out PlayerRuntime runtime)
    {
        runtime = null;
        if (observer == null || !reference.IsValid || !IsCurrent(observer))
            return false;

        ClientSession candidate = FindIndexedReadySessionByObjectId(reference.objectId, reference.generation);
        if (candidate == null || !TryGetGameplayRuntime(candidate, out PlayerRuntime current))
            return false;

        // Reuse the exact authoritative visibility graph that drives replication.
        // This rejects cross-partition, out-of-AOI, hidden-staff and future visibility
        // policy failures before combat/ability services perform their own range/state
        // validation. Do not fall back to global object-id lookup here.
        if (!_worldInterest.IsVisibleTo(observer, candidate))
            return false;

        runtime = current;
        return true;
    }

    private bool CanCompleteScheduledAbilityTarget(
        PlayerRuntime source,
        PlayerRuntime target)
    {
        if (source == null || target == null)
            return false;
        if (ReferenceEquals(source, target))
            return true;

        ClientSession sourceSession = FindReadySessionByCharacterId(source.CharacterId.Value);
        if (sourceSession == null || !IsCurrent(sourceSession) || sourceSession.Entity == null)
            return false;

        if (_runtime.PopulationCombat.TryGetNonPlayerWorld(target, out string targetMapId, out string targetInstanceId))
        {
            CharacterLocationState sourceLocation = sourceSession.Entity.CaptureLocation();
            if (!string.Equals(sourceLocation.MapId, targetMapId, StringComparison.Ordinal) ||
                !string.Equals(sourceLocation.InstanceId, targetInstanceId, StringComparison.Ordinal))
                return false;
            return HasAuthoritativeCombatLineOfSight(sourceSession, target);
        }

        ClientSession targetSession = FindReadySessionByCharacterId(target.CharacterId.Value);
        if (targetSession == null || !IsCurrent(targetSession) || targetSession.Entity == null)
            return false;

        // Revalidate the same authoritative observer graph used at cast admission. A target
        // that left AOI, crossed partition/world boundaries, or became hidden while the cast
        // was in flight is no longer a valid impact target.
        if (!_worldInterest.IsVisibleTo(sourceSession, targetSession))
            return false;

        return HasAuthoritativeCombatLineOfSight(sourceSession, target);
    }

    /// <summary>
    /// Authoritative combat occlusion over the baked server collision world.
    ///
    /// While P0.3 map enforcement remains optional, missing baked collision data must
    /// preserve the existing development fallback rather than disabling all combat.
    /// Once strict map-data enforcement is enabled, every production combat map will
    /// necessarily have this authority query available.
    ///
    /// Two torso/head-height samples are used instead of a feet-to-feet segment so the
    /// ground plane does not self-occlude combat and low cover can behave sensibly.
    /// Dynamic blockers in ServerCollisionWorld (doors, gates, etc.) participate
    /// automatically.
    /// </summary>
    private bool HasAuthoritativeCombatLineOfSight(
        ClientSession sourceSession,
        PlayerRuntime target)
    {
        if (sourceSession == null || sourceSession.Entity == null || target == null ||
            !TryGetGameplayRuntime(sourceSession, out PlayerRuntime source))
        {
            return false;
        }

        if (ReferenceEquals(source, target))
            return true;

        CharacterLocationState targetLocation = target.Location;

        // Player targets use the live authoritative motor pose. Population and baked combat
        // fixtures have no ClientSession, so their synchronized runtime location remains valid.
        ClientSession targetSession = FindReadySessionByCharacterId(target.CharacterId.Value);
        if (targetSession?.Entity != null)
            targetLocation = targetSession.Entity.CaptureLocation();

        return HasAuthoritativeCombatLineOfSight(sourceSession, targetLocation);
    }

    private bool HasAuthoritativeCombatLineOfSight(
        ClientSession sourceSession,
        CharacterLocationState targetLocation)
    {
        if (sourceSession == null || sourceSession.Entity == null ||
            !TryGetGameplayRuntime(sourceSession, out _))
        {
            return false;
        }

        CharacterLocationState sourceLocation = sourceSession.Entity.CaptureLocation();
        if (!string.Equals(sourceLocation.MapId, targetLocation.MapId, StringComparison.Ordinal) ||
            !string.Equals(sourceLocation.InstanceId, targetLocation.InstanceId, StringComparison.Ordinal))
        {
            return false;
        }

        if (!_runtime.Maps.TryGetCollisionWorld(
                sourceLocation.MapId,
                sourceLocation.InstanceId,
                out ServerCollisionWorld collision) ||
            collision == null)
        {
            // Development compatibility while GAME_SERVER_REQUIRE_MAP_DATA=0.
            return true;
        }

        WorldPosition sourceFeet = sourceLocation.Position;
        WorldPosition targetFeet = targetLocation.Position;

        // First sample: center mass / upper torso.
        var sourceTorso = new WorldPosition(sourceFeet.X, sourceFeet.Y + 1.15f, sourceFeet.Z);
        var targetTorso = new WorldPosition(targetFeet.X, targetFeet.Y + 1.15f, targetFeet.Z);
        if (!collision.IsLineObstructed(sourceTorso, targetTorso))
            return true;

        // Second sample: head/upper-body. This avoids treating low cover/rails as a full
        // occluder while still rejecting walls and enabled full-height dynamic blockers.
        var sourceHead = new WorldPosition(sourceFeet.X, sourceFeet.Y + 1.65f, sourceFeet.Z);
        var targetHead = new WorldPosition(targetFeet.X, targetFeet.Y + 1.65f, targetFeet.Z);
        return !collision.IsLineObstructed(sourceHead, targetHead);
    }

    private bool TryResolvePlayerTarget(
        ClientSession observer,
        PlayerTargetReferenceWire reference,
        out PlayerRuntime runtime)
    {
        runtime = null;
        if (observer == null || !reference.IsValid)
            return false;

        ClientSession candidate = FindIndexedReadySessionByObjectId(reference.objectId, reference.generation);
        if (candidate == null ||
            ReferenceEquals(candidate, observer) ||
            !TryGetGameplayRuntime(candidate, out PlayerRuntime current))
        {
            return false;
        }

        // Match the production interaction resolver: a network target must already
        // be visible to the requesting connection. Range is still revalidated by the
        // authoritative InteractionService after target resolution.
        if (!_worldInterest.IsVisibleTo(observer, candidate))
            return false;

        runtime = current;
        return true;
    }

    private enum InteractionSequenceStream : byte
    {
        Player = 0,
        Context = 1,
        WorldItem = 2,
    }

    private bool TryAdmitPlayerInteractionRequest(
        ClientSession session,
        uint sequence,
        out InteractionResultCode code,
        out string detail) =>
        TryAdmitInteractionRequest(session, sequence, InteractionSequenceStream.Player, out code, out detail);

    private bool TryAdmitContextInteractionRequest(
        ClientSession session,
        uint sequence,
        out InteractionResultCode code,
        out string detail) =>
        TryAdmitInteractionRequest(session, sequence, InteractionSequenceStream.Context, out code, out detail);

    private bool TryAdmitWorldItemInteractionRequest(
        ClientSession session,
        uint sequence,
        out InteractionResultCode code,
        out string detail) =>
        TryAdmitInteractionRequest(session, sequence, InteractionSequenceStream.WorldItem, out code, out detail);

    private bool TryAdmitInteractionRequest(
        ClientSession session,
        uint sequence,
        InteractionSequenceStream stream,
        out InteractionResultCode code,
        out string detail)
    {
        code = InteractionResultCode.Success;
        detail = string.Empty;
        if (session == null)
        {
            code = InteractionResultCode.InvalidState;
            detail = "interaction session is unavailable";
            return false;
        }

        // Public network interactions require a non-zero monotonic sequence. Current
        // clients already skip zero. Removing the old zero compatibility bypass prevents
        // unlimited replay of an otherwise valid interaction request.
        if (sequence == 0)
        {
            code = InteractionResultCode.Expired;
            detail = "interaction request sequence is invalid";
            return false;
        }

        uint previous = stream switch
        {
            InteractionSequenceStream.Player => session.LastPlayerInteractionSequence,
            InteractionSequenceStream.Context => session.LastContextInteractionSequence,
            InteractionSequenceStream.WorldItem => session.LastWorldItemInteractionSequence,
            _ => 0u,
        };

        if (previous != 0 && !IsNewerSequence(sequence, previous))
        {
            code = InteractionResultCode.Expired;
            detail = "interaction request sequence is stale";
            return false;
        }

        // Keep independent freshness streams because the current client owns separate
        // monotonic counters for player and world-item interactions. A single shared
        // counter would incorrectly reject valid cross-family requests.
        switch (stream)
        {
            case InteractionSequenceStream.Player:
                session.LastPlayerInteractionSequence = sequence;
                break;
            case InteractionSequenceStream.Context:
                session.LastContextInteractionSequence = sequence;
                break;
            case InteractionSequenceStream.WorldItem:
                session.LastWorldItemInteractionSequence = sequence;
                break;
            default:
                code = InteractionResultCode.InvalidState;
                detail = "interaction request stream is invalid";
                return false;
        }

        long nowMs = Environment.TickCount64;
        long elapsedMs = Math.Max(0L, nowMs - session.PlayerInteractionTokenTimestampMs);
        session.PlayerInteractionTokenTimestampMs = nowMs;

        double tokens = Math.Min(
            PlayerInteractionBurstCapacity,
            session.PlayerInteractionTokens +
            elapsedMs * (PlayerInteractionRefillPerSecond / 1000d));

        if (tokens + 0.000001d < 1d)
        {
            session.PlayerInteractionTokens = tokens;
            code = InteractionResultCode.RateLimited;
            detail = "interaction request rate limit reached";
            return false;
        }

        session.PlayerInteractionTokens = tokens - 1d;
        return true;
    }

    private static bool IsNewerSequence(uint candidate, uint reference) =>
        candidate != reference && unchecked((int)(candidate - reference)) > 0;

    private PlayerRuntime ResolveReadyRuntime(long characterId)
    {
        ClientSession session = FindIndexedReadySessionByCharacterId(characterId);
        if (session != null && TryGetGameplayRuntime(session, out PlayerRuntime runtime))
            return runtime;
        return _runtime.PopulationCombat.ResolveRuntime(characterId);
    }

    private void OnCombatDamageResolved(CombatDamageResult result)
    {
        if (result.dealtAmount > 0 && result.targetCharacterId > 0)
            _runtime.InteractionSessions.NotifyDamage(result.targetCharacterId);

        PlayerCombatDamageEventMessage message = new PlayerCombatDamageEventMessage
        {
            damage = ToGameplayWire(result),
        };

        _mainThreadCompletions.Enqueue(() =>
        {
            ClientSession source = FindReadySessionByCharacterId(result.sourceCharacterId);
            ClientSession target = FindReadySessionByCharacterId(result.targetCharacterId);

            if (source != null)
                SendClientMessage(source, PlayerGameplayActionMessageTypes.CombatDamage, message, DeliveryMethod.ReliableOrdered);
            if (target != null && !ReferenceEquals(target, source))
                SendClientMessage(target, PlayerGameplayActionMessageTypes.CombatDamage, message, DeliveryMethod.ReliableOrdered);
        });
    }

    private void OnAbilityCastStateChanged(AbilityCastResult result)
    {
        PlayerAbilityCastStateMessage message = ToGameplayWire(result);
        _mainThreadCompletions.Enqueue(() =>
        {
            ClientSession source = FindReadySessionByCharacterId(result.SourceCharacterId);
            ClientSession target = FindReadySessionByCharacterId(result.TargetCharacterId);

            if (source != null)
                SendClientMessage(source, PlayerGameplayActionMessageTypes.AbilityCastState, message, DeliveryMethod.ReliableOrdered);
            if (target != null && !ReferenceEquals(target, source))
                SendClientMessage(target, PlayerGameplayActionMessageTypes.AbilityCastState, message, DeliveryMethod.ReliableOrdered);
        });
    }

    private ClientSession FindReadySessionByCharacterId(long characterId)
    {
        ClientSession session = FindIndexedReadySessionByCharacterId(characterId);
        return session != null && TryGetGameplayRuntime(session, out _)
            ? session
            : null;
    }

    private static PlayerRespawnResponseMessage ToGameplayWire(CharacterRespawnResult result) =>
        new PlayerRespawnResponseMessage
        {
            success = result.Success,
            resultCode = (byte)result.Status,
            detail = result.Detail,
        };

    private static PlayerBasicAttackResponseMessage ToGameplayWire(BasicAttackResult result) =>
        new PlayerBasicAttackResponseMessage
        {
            success = result.Success,
            resultCode = (byte)result.Code,
        };

    private PlayerAbilityCastStateMessage ToGameplayWire(AbilityCastResult result)
    {
        ushort wireId = 0;
        if (!string.IsNullOrWhiteSpace(result.AbilityDefinitionId) &&
            _runtime.Content.TryGetAbility(result.AbilityDefinitionId, out AbilityDefinition ability))
            wireId = ability.wireId;

        double remaining = result.CompletesAt > 0d
            ? Math.Max(0d, result.CompletesAt - _scheduler.ServerTime)
            : 0d;
        uint remainingMilliseconds = remaining >= uint.MaxValue / 1000d
            ? uint.MaxValue
            : (uint)Math.Round(remaining * 1000d, MidpointRounding.AwayFromZero);

        return new PlayerAbilityCastStateMessage
        {
            success = result.Success,
            failure = (byte)result.Failure,
            phase = (byte)result.Phase,
            castSequence = unchecked((uint)result.CastId),
            abilityWireId = wireId,
            source = ToTargetReference(result.SourceCharacterId, FindReadySessionByCharacterId(result.SourceCharacterId)),
            target = ToTargetReference(result.TargetCharacterId, FindReadySessionByCharacterId(result.TargetCharacterId)),
            remainingMilliseconds = remainingMilliseconds,
            totalAffected = (ushort)Math.Max(0, Math.Min(ushort.MaxValue, result.TotalAffected)),
        };
    }

    private static PlayerInteractionResponseMessage ToGameplayWire(InteractionResult result) =>
        new PlayerInteractionResponseMessage
        {
            success = result.Success,
            sequence = result.Sequence,
            actionId = (ushort)result.ActionId,
            resultCode = (byte)result.ResultCode,
            targetCharacterId = result.Target.PrimaryId,
            detail = result.Detail,
        };

    private CombatDamageWire ToGameplayWire(CombatDamageResult result) =>
        new CombatDamageWire
        {
            eventSequence = unchecked((uint)result.eventId),
            source = ToTargetReference(result.sourceCharacterId, FindReadySessionByCharacterId(result.sourceCharacterId)),
            target = ToTargetReference(result.targetCharacterId, FindReadySessionByCharacterId(result.targetCharacterId)),
            damageTypeId = result.damageTypeId,
            amount = result.dealtAmount,
            resultCode = (byte)result.resultCode,
            cause = (byte)result.cause,
            flags = (ushort)result.presentationFlags,
        };

    private PlayerCombatOwnerStateMessage ToGameplayWire(CombatOwnerStateSnapshot state)
    {
        if (!state.Available)
            return PlayerCombatOwnerStateMessage.Failed();

        ushort weaponDataId = 0;
        if (!string.IsNullOrWhiteSpace(state.WeaponDefinitionId) &&
            _runtime.Content.TryGetItem(state.WeaponDefinitionId, out ItemDefinition weapon))
            weaponDataId = weapon.dataId;

        ushort ammoDataId = 0;
        if (!string.IsNullOrWhiteSpace(state.LoadedAmmoDefinitionId) &&
            _runtime.Content.TryGetItem(state.LoadedAmmoDefinitionId, out ItemDefinition ammo))
            ammoDataId = ammo.dataId;

        return new PlayerCombatOwnerStateMessage
        {
            success = true,
            resultCode = 0,
            revision = state.Revision,
            mode = (byte)state.Mode,
            weaponDataId = weaponDataId,
            weaponDefinitionId = state.WeaponDefinitionId,
            loadedAmmoDataId = ammoDataId,
            loadedAmmoDefinitionId = state.LoadedAmmoDefinitionId,
            loadedRounds = state.LoadedRounds,
            magazineCapacity = state.MagazineCapacity,
            unarmedLightStaminaCost = state.UnarmedLightStaminaCost,
            unarmedHeavyStaminaCost = state.UnarmedHeavyStaminaCost,
            primaryInterval = state.PrimaryInterval,
            heavyInterval = state.HeavyInterval,
            comboStep = state.ComboStep,
            firearmFireMode = (byte)state.FireMode,
            firearmRoundsPerTrigger = (byte)Math.Max(1, Math.Min(byte.MaxValue, state.FirearmRoundsPerTrigger)),
            firearmRoundsPerSecond = state.FirearmRoundsPerSecond,
            firearmPresentationId = state.FirearmPresentationId,
        };
    }

    private PlayerReloadResponseMessage ToGameplayWire(CombatReloadResult result)
    {
        if (!result.Success)
            return PlayerReloadResponseMessage.Failed((byte)result.Code);

        ushort ammoDataId = 0;
        if (!string.IsNullOrWhiteSpace(result.State.LoadedAmmoDefinitionId) &&
            _runtime.Content.TryGetItem(result.State.LoadedAmmoDefinitionId, out ItemDefinition ammo))
            ammoDataId = ammo.dataId;

        return new PlayerReloadResponseMessage
        {
            resultCode = (byte)result.Code,
            revision = result.State.Revision,
            loadedAmmoDataId = ammoDataId,
            loadedAmmoDefinitionId = result.State.LoadedAmmoDefinitionId,
            loadedRounds = result.State.LoadedRounds,
        };
    }

    private static string BasicAttackFailureText(BasicAttackResultCode code) => code switch
    {
        BasicAttackResultCode.RejectedInvalidSource => "source character is unavailable",
        BasicAttackResultCode.RejectedInvalidTarget => "target is unavailable",
        BasicAttackResultCode.RejectedDead => "source or target is dead",
        BasicAttackResultCode.RejectedAlreadyCasting => "cannot basic attack while casting",
        BasicAttackResultCode.RejectedRecovery => "basic attack is recovering",
        BasicAttackResultCode.RejectedOutOfRange => "target is out of range",
        BasicAttackResultCode.RejectedDifferentWorld => "target is in another world",
        BasicAttackResultCode.RejectedNoDamage => "attack produced no authoritative damage",
        _ => "basic attack was rejected",
    };
}
