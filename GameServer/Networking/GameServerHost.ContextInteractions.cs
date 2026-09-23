using System;
using System.Threading;
using System.Threading.Tasks;
using Game.GameServer.Runtime;
using Game.Server.Application.Actors;
using Game.Server.Application.Combat;
using Game.Server.Application.Harvesting;
using Game.Server.Domain.Characters;
using Game.Server.Domain.Players;
using Game.Server.Domain.WorldItems;
using Game.Shared.Actors;
using Game.Shared.Content;
using Game.Shared.Interactions;
using Game.Shared.World;
using LiteNetLib;
using LiteNetLib.Utils;
using Player.Networking;
using Player.Shared;

namespace Game.GameServer.Networking;

internal sealed partial class GameServerHost
{

    private void RegisterContextInteractionRequests(
        Dictionary<ushort, Action<ClientSession, uint, NetDataReader>> handlers)
    {
        RegisterRequest(handlers, PlayerGameplayActionRequestTypes.InteractionMenu, HandleInteractionMenu);
        RegisterRequest(handlers, PlayerGameplayActionRequestTypes.ContextInteraction, HandleContextInteraction);
        RegisterRequest(handlers, PlayerGameplayActionRequestTypes.WorldLootOpen, HandleWorldLootOpen);
        RegisterRequest(handlers, PlayerGameplayActionRequestTypes.WorldLootTake, HandleWorldLootTake);
        RegisterRequest(handlers, PlayerGameplayActionRequestTypes.WorldLootTakeAll, HandleWorldLootTakeAll);
    }
    private void HandleInteractionMenu(ClientSession session, uint requestId, NetDataReader reader)
    {
        var request = new InteractionMenuRequestMessage();
        request.Deserialize(reader);

        if (!TryGetGameplayRuntime(session, out PlayerRuntime source))
        {
            SendResponse(session, requestId, InteractionMenuResponseMessage.Failed(request.target, "source character is not in world"));
            return;
        }

        switch (request.target.Kind)
        {
            case InteractionTargetKind.PlayerEntity:
            {
                var playerReference = new PlayerTargetReferenceWire
                {
                    objectId = request.target.objectId,
                    generation = request.target.generation,
                };
                if (!TryResolvePlayerTarget(session, playerReference, out PlayerRuntime target))
                {
                    SendResponse(session, requestId, InteractionMenuResponseMessage.Failed(request.target, "player target is unavailable or outside authoritative interest"));
                    return;
                }

                InteractionActionSet set = _runtime.Interactions.DiscoverPlayerActions(source, target, _scheduler.ServerTime);
                SendResponse(session, requestId, ToInteractionMenuWire(request.target, set));
                return;
            }

            case InteractionTargetKind.PopulationEntity:
            {
                if (!TryResolvePopulationInteractionTarget(source, request.target, out AuthoritativeActorRuntime actor))
                {
                    SendResponse(session, requestId, InteractionMenuResponseMessage.Failed(
                        request.target,
                        "population target is unavailable or outside authoritative interaction range"));
                    return;
                }

                InteractionActionSet set = _runtime.Interactions.DiscoverActorActions(
                    source,
                    actor,
                    _scheduler.ServerTime);
                set = AppendPopulationLootAction(set, actor);
                SendResponse(session, requestId, ToInteractionMenuWire(request.target, set));
                return;
            }

            case InteractionTargetKind.SceneObject:
            {
                if (request.target.primaryId <= 0)
                {
                    SendResponse(session, requestId, InteractionMenuResponseMessage.Failed(
                        request.target,
                        "scene object target is invalid"));
                    return;
                }

                InteractionActionSet set = FilterStandaloneSceneObjectActions(
                    _runtime.WorldInteractables.Discover(
                        source,
                        request.target.primaryId,
                        _scheduler.ServerTime));
                SendResponse(session, requestId, ToInteractionMenuWire(request.target, set));
                return;
            }

            case InteractionTargetKind.CombatTestTarget:
            {
                if (!TryResolveCombatTestDummyInteraction(source, request.target, out CombatTestDummyView dummy, out string failure))
                {
                    SendResponse(session, requestId, InteractionMenuResponseMessage.Failed(request.target, failure));
                    return;
                }

                InteractionActionSet set = BuildCombatTestDummyActions(source, dummy);
                SendResponse(session, requestId, ToInteractionMenuWire(request.target, set));
                return;
            }

            case InteractionTargetKind.NetworkWorldObject:
            {
                if (request.target.primaryId <= 0)
                {
                    SendResponse(session, requestId, InteractionMenuResponseMessage.Failed(request.target, "world target is invalid"));
                    return;
                }

                var location = source.Location;
                if (!_runtime.WorldItems.TryGet(location.MapId, location.InstanceId, request.target.primaryId, out WorldItemState item))
                {
                    SendResponse(session, requestId, InteractionMenuResponseMessage.Failed(request.target, "world item is unavailable"));
                    return;
                }

                string targetLabel = item.DefinitionId;
                if (_runtime.Content.TryGetItem(item.DefinitionId, out ItemDefinition definition) &&
                    !string.IsNullOrWhiteSpace(definition.displayName))
                {
                    targetLabel = definition.displayName;
                }
                if (item.Quantity > 1)
                    targetLabel += $" x{item.Quantity}";

                InteractionActionSet set = _runtime.Interactions.DiscoverWorldActions(
                    source,
                    new InteractionTargetHandle(InteractionTargetKind.NetworkWorldObject, item.ItemInstanceId.Value),
                    targetLabel,
                    item.MapId,
                    item.InstanceId,
                    item.Position,
                    _scheduler.ServerTime);
                SendResponse(session, requestId, ToInteractionMenuWire(request.target, set));
                return;
            }

            default:
                SendResponse(session, requestId, InteractionMenuResponseMessage.Failed(
                    request.target,
                    $"target kind {request.target.Kind} has no standalone authoritative resolver yet"));
                return;
        }
    }

    private void HandleContextInteraction(ClientSession session, uint requestId, NetDataReader reader)
    {
        var request = new ContextInteractionRequestMessage();
        request.Deserialize(reader);

        if (!TryGetGameplayRuntime(session, out PlayerRuntime source))
        {
            SendResponse(session, requestId, ContextInteractionResponseMessage.Failed(
                request.sequence,
                request.target,
                request.CategoryId,
                request.ActionId,
                InteractionResultCode.InvalidState,
                "source character is not in world"));
            return;
        }

        if (!TryAdmitContextInteractionRequest(session, request.sequence, out InteractionResultCode admissionCode, out string admissionDetail))
        {
            SendResponse(session, requestId, ContextInteractionResponseMessage.Failed(
                request.sequence,
                request.target,
                request.CategoryId,
                request.ActionId,
                admissionCode,
                admissionDetail));
            return;
        }

        if (request.CategoryId == InteractionCategoryId.None || request.ActionId == InteractionActionId.None)
        {
            SendResponse(session, requestId, ContextInteractionResponseMessage.Failed(
                request.sequence,
                request.target,
                request.CategoryId,
                request.ActionId,
                InteractionResultCode.Unsupported,
                "interaction category/action pair is invalid"));
            return;
        }

        switch (request.target.Kind)
        {
            case InteractionTargetKind.PlayerEntity:
            {
                if (!InteractionCategoryCatalog.IsCompatible(InteractionTargetKind.PlayerEntity, request.CategoryId, request.ActionId))
                {
                    SendResponse(session, requestId, ContextInteractionResponseMessage.Failed(
                        request.sequence, request.target, request.CategoryId, request.ActionId, InteractionResultCode.Unsupported,
                        "interaction category/action pair is not supported for player targets"));
                    return;
                }

                var playerReference = new PlayerTargetReferenceWire
                {
                    objectId = request.target.objectId,
                    generation = request.target.generation,
                };
                if (!TryResolvePlayerTarget(session, playerReference, out PlayerRuntime target))
                {
                    SendResponse(session, requestId, ContextInteractionResponseMessage.Failed(
                        request.sequence,
                        request.target,
                        request.CategoryId,
                        request.ActionId,
                        InteractionResultCode.InvalidTarget,
                        "player target is unavailable or outside authoritative interest"));
                    return;
                }

                InteractionResult result = _runtime.Interactions.ExecutePlayerAction(
                    source,
                    target,
                    request.ActionId,
                    request.sequence,
                    _scheduler.ServerTime);
                SendResponse(session, requestId, ToContextInteractionWire(request.target, result));
                return;
            }

            case InteractionTargetKind.PopulationEntity:
            {
                if (!InteractionCategoryCatalog.IsCompatible(InteractionTargetKind.PopulationEntity, request.CategoryId, request.ActionId))
                {
                    SendResponse(session, requestId, ContextInteractionResponseMessage.Failed(
                        request.sequence, request.target, request.CategoryId, request.ActionId, InteractionResultCode.Unsupported,
                        "interaction category/action pair is not supported for population targets"));
                    return;
                }

                if (!TryResolvePopulationInteractionTarget(source, request.target, out AuthoritativeActorRuntime actor))
                {
                    SendResponse(session, requestId, ContextInteractionResponseMessage.Failed(
                        request.sequence,
                        request.target,
                        request.CategoryId,
                        request.ActionId,
                        InteractionResultCode.InvalidTarget,
                        "population target is unavailable or outside authoritative interaction range"));
                    return;
                }

                if (request.ActionId == InteractionActionId.Loot)
                {
                    if (!_runtime.PopulationLoot.TryOpenDeadLoot(source, actor, out var lootTarget, out string lootReason))
                    {
                        SendResponse(session, requestId, ContextInteractionResponseMessage.Failed(
                            request.sequence,
                            request.target,
                            request.CategoryId,
                            request.ActionId,
                            InteractionResultCode.Rejected,
                            lootReason));
                        return;
                    }

                    if (!TryValidateWorldLootContent(lootTarget, out lootReason))
                    {
                        SendResponse(session, requestId, ContextInteractionResponseMessage.Failed(
                            request.sequence,
                            request.target,
                            request.CategoryId,
                            request.ActionId,
                            InteractionResultCode.InvalidState,
                            lootReason));
                        return;
                    }

                    var opened = new InteractionResult(
                        request.sequence,
                        request.ActionId,
                        InteractionResultCode.Success,
                        new InteractionTargetHandle(InteractionTargetKind.PopulationEntity, actor.Handle.actorId),
                        "loot opened");
                    SendResponse(session, requestId, ToContextInteractionWire(request.target, opened));
                    SendClientMessage(
                        session,
                        WorldLootMessageTypes.Snapshot,
                        BuildWorldLootResponse(lootTarget),
                        DeliveryMethod.ReliableOrdered);
                    return;
                }

                InteractionResult result = _runtime.Interactions.ExecuteActorAction(
                    source,
                    actor,
                    request.ActionId,
                    request.sequence,
                    _scheduler.ServerTime);
                SendResponse(session, requestId, ToContextInteractionWire(request.target, result));
                return;
            }

            case InteractionTargetKind.SceneObject:
            {
                ExecuteSceneObjectInteraction(
                    session,
                    requestId,
                    request,
                    source);
                return;
            }

            case InteractionTargetKind.CombatTestTarget:
            {
                if (!InteractionCategoryCatalog.IsCompatible(InteractionTargetKind.CombatTestTarget, request.CategoryId, request.ActionId))
                {
                    SendResponse(session, requestId, ContextInteractionResponseMessage.Failed(
                        request.sequence, request.target, request.CategoryId, request.ActionId, InteractionResultCode.Unsupported,
                        "interaction category/action pair is not supported for test targets"));
                    return;
                }

                if (!TryResolveCombatTestDummyInteraction(source, request.target, out CombatTestDummyView dummy, out string failure))
                {
                    SendResponse(session, requestId, ContextInteractionResponseMessage.Failed(
                        request.sequence, request.target, request.CategoryId, request.ActionId, InteractionResultCode.InvalidTarget, failure));
                    return;
                }

                if (request.ActionId == InteractionActionId.Feed)
                {
                    InteractionResult feeding = _runtime.Feeding.ExecuteAgainstRuntime(
                        source,
                        dummy.Runtime,
                        new InteractionTargetHandle(InteractionTargetKind.CombatTestTarget, dummy.StableId),
                        request.sequence,
                        _scheduler.ServerTime);
                    SendResponse(session, requestId, ToContextInteractionWire(request.target, feeding));
                    return;
                }

                string detail = _runtime.PopulationCombat.ExecuteDeveloperAction(
                    dummy, request.ActionId, _scheduler.ServerTime, out bool success);
                SendResponse(session, requestId, new ContextInteractionResponseMessage
                {
                    success = success,
                    sequence = request.sequence,
                    categoryId = (ushort)request.CategoryId,
                    actionId = (ushort)request.ActionId,
                    resultCode = (byte)(success ? InteractionResultCode.Success : InteractionResultCode.Unsupported),
                    target = request.target,
                    detail = detail,
                });
                return;
            }

            case InteractionTargetKind.NetworkWorldObject:
            {
                if (!InteractionCategoryCatalog.IsCompatible(InteractionTargetKind.NetworkWorldObject, request.CategoryId, request.ActionId))
                {
                    SendResponse(session, requestId, ContextInteractionResponseMessage.Failed(
                        request.sequence, request.target, request.CategoryId, request.ActionId, InteractionResultCode.Unsupported,
                        "interaction category/action pair is not supported for world-item targets"));
                    return;
                }

                var location = source.Location;
                if (request.target.primaryId <= 0 ||
                    !_runtime.WorldItems.TryGet(location.MapId, location.InstanceId, request.target.primaryId, out WorldItemState item))
                {
                    SendResponse(session, requestId, ContextInteractionResponseMessage.Failed(
                        request.sequence,
                        request.target,
                        request.CategoryId,
                        request.ActionId,
                        InteractionResultCode.TargetUnavailable,
                        "world item is unavailable"));
                    return;
                }

                RunContextWorldInteractionAsync(session, requestId, request, source, item).Forget();
                return;
            }

            default:
                SendResponse(session, requestId, ContextInteractionResponseMessage.Failed(
                    request.sequence,
                    request.target,
                    request.CategoryId,
                    request.ActionId,
                    InteractionResultCode.Unsupported,
                    $"target kind {request.target.Kind} has no standalone authoritative resolver yet"));
                return;
        }
    }

    private bool TryResolveCombatTestDummyInteraction(
        PlayerRuntime source,
        InteractionTargetReferenceWire target,
        out CombatTestDummyView dummy,
        out string failure)
    {
        dummy = default;
        failure = string.Empty;
        if (source == null || target.primaryId <= 0 ||
            !_runtime.PopulationCombat.TryGet(source.Location.MapId, source.Location.InstanceId, target.primaryId, out dummy))
        {
            failure = "combat test dummy is unavailable";
            return false;
        }

        WorldPosition a = source.Location.Position;
        WorldPosition b = dummy.Runtime.Location.Position;
        float dx = a.X - b.X;
        float dy = a.Y - b.Y;
        float dz = a.Z - b.Z;
        float range = InteractionRangePolicy.WorldObjectUseRange;
        if (dx * dx + dy * dy + dz * dz > range * range)
        {
            failure = "combat test dummy is outside interaction range";
            return false;
        }
        return true;
    }

    private InteractionActionSet BuildCombatTestDummyActions(PlayerRuntime source, CombatTestDummyView dummy)
    {
        bool canFeed = _runtime.Feeding.CanStart(source, dummy.Runtime, out string feedReason);
        InteractionActionEntry[] actions =
        {
            new InteractionActionEntry(
                InteractionCategoryId.Use,
                InteractionActionId.Feed,
                "Feed",
                canFeed ? InteractionAvailability.Available : InteractionAvailability.Disabled,
                canFeed ? string.Empty : feedReason,
                InteractionConsentMode.None,
                InteractionContentLevel.General,
                InteractionFeature.Vampire,
                -10),
            DummyAction(InteractionActionId.CombatDummyResetHealth, "Reset Health", 0),
            DummyAction(InteractionActionId.CombatDummyClearStatuses, "Clear Statuses", 10),
            DummyAction(InteractionActionId.CombatDummyNormalDefense, "Normal Defense", 20),
            DummyAction(InteractionActionId.CombatDummyConductivePlate, "Conductive Plate", 30),
            DummyAction(InteractionActionId.CombatDummyPoisonResistant, "Poison Resistant", 40),
            DummyAction(InteractionActionId.CombatDummyPoisonImmune, "Poison Immune", 50),
            DummyAction(InteractionActionId.CombatDummyFireWeak, "Fire Weak", 60),
            DummyAction(InteractionActionId.CombatDummyInvulnerable, "Invulnerable", 70),
            DummyAction(InteractionActionId.CombatDummyShowStats, "Show Current Stats", 80),
        };
        return new InteractionActionSet(
            new InteractionTargetHandle(InteractionTargetKind.CombatTestTarget, dummy.StableId),
            dummy.Label,
            actions);
    }

    private static InteractionActionEntry DummyAction(InteractionActionId actionId, string label, short sortOrder) =>
        new InteractionActionEntry(
            InteractionCategoryId.Use,
            actionId,
            label,
            InteractionAvailability.Available,
            sortOrder: sortOrder);

    private bool TryResolvePopulationInteractionTarget(
        PlayerRuntime source,
        InteractionTargetReferenceWire reference,
        out AuthoritativeActorRuntime actor)
    {
        actor = null;
        if (source == null ||
            reference.Kind != InteractionTargetKind.PopulationEntity ||
            reference.primaryId <= 0 ||
            reference.generation == 0)
        {
            return false;
        }

        var handle = new AuthoritativeActorHandle(
            reference.primaryId,
            reference.generation,
            AuthoritativeActorKind.Population);

        if (!_runtime.Actors.TryGet(handle, out actor) ||
            actor == null)
        {
            actor = null;
            return false;
        }

        if (!string.Equals(source.Location.MapId, actor.MapId, StringComparison.Ordinal) ||
            !string.Equals(source.Location.InstanceId, actor.InstanceId ?? string.Empty, StringComparison.Ordinal))
        {
            actor = null;
            return false;
        }

        double dx = source.Location.Position.X - actor.Position.X;
        double dz = source.Location.Position.Z - actor.Position.Z;
        double range = Math.Max(0.1d, _runtime.Interactions.PlayerInteractionRange);
        if (dx * dx + dz * dz > range * range)
        {
            actor = null;
            return false;
        }

        return true;
    }

    private static InteractionActionSet FilterStandaloneSceneObjectActions(InteractionActionSet source)
    {
        InteractionActionEntry[] actions = source.Actions ?? Array.Empty<InteractionActionEntry>();
        if (actions.Length == 0)
            return source;

        var filtered = new InteractionActionEntry[actions.Length];
        for (int i = 0; i < actions.Length; ++i)
        {
            InteractionActionEntry entry = actions[i];

            // SceneObject TargetAcceptance/MutualOptIn requires a second participant and a
            // participant-addressable consent exchange. That is not the same thing as a
            // visual client feature, and the current standalone protocol does not expose
            // such a participant/session endpoint yet. Fail closed in discovery instead
            // of advertising an action the server cannot complete safely.
            if (entry.IsAvailable &&
                (entry.ConsentMode == InteractionConsentMode.TargetAcceptance ||
                 entry.ConsentMode == InteractionConsentMode.MutualOptIn))
            {
                filtered[i] = entry.WithAvailability(
                    InteractionAvailability.Disabled,
                    "multi-participant consent is not active on the standalone server");
            }
            else
            {
                filtered[i] = entry;
            }
        }

        return new InteractionActionSet(
            source.Target,
            source.TargetLabel,
            filtered,
            source.Detail);
    }

    private void ExecuteSceneObjectInteraction(
        ClientSession client,
        uint requestId,
        ContextInteractionRequestMessage request,
        PlayerRuntime source)
    {
        long stableId = request.target.primaryId;
        if (stableId <= 0 ||
            !_runtime.WorldInteractables.TryGet(
                source.Location.MapId,
                source.Location.InstanceId,
                stableId,
                out var target))
        {
            SendResponse(client, requestId, ContextInteractionResponseMessage.Failed(
                request.sequence,
                request.target,
                request.CategoryId,
                request.ActionId,
                InteractionResultCode.TargetUnavailable,
                "scene object is unavailable"));
            return;
        }

        var definition = Game.Server.Application.Interactions.WorldInteractableService.FindDefinition(
            target.Definition,
            request.CategoryId,
            request.ActionId);
        if (definition == null)
        {
            SendResponse(client, requestId, ContextInteractionResponseMessage.Failed(
                request.sequence,
                request.target,
                request.CategoryId,
                request.ActionId,
                InteractionResultCode.Unsupported,
                "action is not supported by this scene object"));
            return;
        }

        if (definition.consentMode == InteractionConsentMode.TargetAcceptance ||
            definition.consentMode == InteractionConsentMode.MutualOptIn)
        {
            SendResponse(client, requestId, ContextInteractionResponseMessage.Failed(
                request.sequence,
                request.target,
                request.CategoryId,
                request.ActionId,
                InteractionResultCode.Unsupported,
                "multi-participant scene-object consent is not active on the standalone server"));
            return;
        }

        // Search is a normal category/sub-action request. On success the GameServer pushes
        // the owner-only loot snapshot immediately; the client does not send a second open
        // request. WorldLootOpen remains registered only for explicit reconciliation.
        if (request.ActionId == InteractionActionId.Search &&
            target.Definition.kind == ServerWorldInteractableKind.Searchable)
        {
            if (!_runtime.WorldInteractables.TryOpenLoot(source, stableId, out var lootTarget, out string lootReason))
            {
                SendResponse(client, requestId, ContextInteractionResponseMessage.Failed(
                    request.sequence, request.target, request.CategoryId, request.ActionId,
                    InteractionResultCode.Rejected, lootReason));
                return;
            }

            if (!TryValidateWorldLootContent(lootTarget, out lootReason))
            {
                Console.Error.WriteLine($"[WorldLoot] Search rejected for peer {client?.Peer?.Id}, object {stableId}: {lootReason}");
                SendResponse(client, requestId, ContextInteractionResponseMessage.Failed(
                    request.sequence, request.target, request.CategoryId, request.ActionId,
                    InteractionResultCode.InvalidState, lootReason));
                return;
            }

            var opened = new InteractionResult(
                request.sequence,
                request.ActionId,
                InteractionResultCode.Success,
                new InteractionTargetHandle(InteractionTargetKind.SceneObject, stableId),
                "loot opened");
            SendResponse(client, requestId, ToContextInteractionWire(request.target, opened));
            SendClientMessage(
                client,
                WorldLootMessageTypes.Snapshot,
                BuildWorldLootResponse(lootTarget),
                DeliveryMethod.ReliableOrdered);
            return;
        }

        bool isHarvest =
            request.ActionId == InteractionActionId.Harvest &&
            target.Definition.kind == ServerWorldInteractableKind.HarvestNode;
        HarvestAttemptPlan harvestPlan = default;
        if (isHarvest &&
            !_runtime.Harvesting.TryPrepareAttempt(source, stableId, out harvestPlan, out string harvestReason))
        {
            SendResponse(client, requestId, ContextInteractionResponseMessage.Failed(
                request.sequence,
                request.target,
                request.CategoryId,
                request.ActionId,
                InteractionResultCode.Rejected,
                harvestReason));
            return;
        }

        if (!isHarvest)
        {
            InteractionResult instant = _runtime.WorldInteractables.ExecuteInstant(
                source,
                stableId,
                request.ActionId,
                request.sequence,
                _scheduler.ServerTime);

            if (instant.ResultCode != InteractionResultCode.ConsentRequired)
            {
                SendResponse(client, requestId, ToContextInteractionWire(request.target, instant));
                if (instant.ResultCode == InteractionResultCode.Success &&
                    request.ActionId == InteractionActionId.OpenStorage)
                {
                    OpenStorageFor(client, source, stableId);
                }
                return;
            }
        }

        // Harvest duration is authoritative content, so Harvest always enters the existing
        // interaction-session path even if an old authored fixture had a zero fixed duration.
        if (!_runtime.InteractionSessions.TryStartWorldSession(
                source,
                stableId,
                request.ActionId,
                _scheduler.ServerTime,
                out var interactionSession,
                out string detail,
                fixedDurationOverrideSeconds: isHarvest ? harvestPlan.DurationSeconds : -1f))
        {
            SendResponse(client, requestId, ContextInteractionResponseMessage.Failed(
                request.sequence,
                request.target,
                request.CategoryId,
                request.ActionId,
                InteractionResultCode.SessionBusy,
                string.IsNullOrWhiteSpace(detail) ? "interaction session could not start" : detail));
            return;
        }

        if (isHarvest)
        {
            if (!_runtime.Harvesting.BindSession(interactionSession.SessionId, harvestPlan, out string bindReason))
            {
                _runtime.InteractionSessions.CancelForCharacter(
                    source.CharacterId.Value,
                    string.IsNullOrWhiteSpace(bindReason) ? "harvest session bind failed" : bindReason);
                SendResponse(client, requestId, ContextInteractionResponseMessage.Failed(
                    request.sequence,
                    request.target,
                    request.CategoryId,
                    request.ActionId,
                    InteractionResultCode.SessionBusy,
                    string.IsNullOrWhiteSpace(bindReason) ? "harvest session could not bind" : bindReason));
                return;
            }

            SendHarvestStarted(client, stableId, harvestPlan);
        }

        // Interactions that require authored alignment keep the existing anchor warp.
        // Harvest already passed authoritative range/facing checks and remains at the
        // player's validated world position instead of snapping to the node anchor.
        if (!isHarvest && client.Entity != null)
        {
            var aligned = new CharacterLocationState(
                interactionSession.MapId,
                interactionSession.InstanceId,
                interactionSession.AnchorPose.ToWorldPosition(),
                interactionSession.AnchorPose.yaw);

            client.Entity.Warp(aligned);
            MarkPlayerSimulationDirty(client);
            BroadcastSnapshot(
                client,
                0f,
                (byte)(PlayerEntityFlags.Grounded | PlayerEntityFlags.Teleport),
                (byte)PlayerEntityMoveState.Idle,
                ServerPlayerSnapshotReason.Forced | ServerPlayerSnapshotReason.PresentationState);
        }

        var started = new InteractionResult(
            request.sequence,
            request.ActionId,
            InteractionResultCode.Success,
            interactionSession.Target,
            "interaction session started");
        SendResponse(client, requestId, ToContextInteractionWire(request.target, started));
    }

    private async Task RunContextWorldInteractionAsync(
        ClientSession session,
        uint requestId,
        ContextInteractionRequestMessage request,
        PlayerRuntime source,
        WorldItemState item)
    {
        InteractionResult result;
        try
        {
            result = await _runtime.Interactions.ExecuteWorldActionAsync(
                source,
                new InteractionTargetHandle(InteractionTargetKind.NetworkWorldObject, item.ItemInstanceId.Value),
                item.MapId,
                item.InstanceId,
                item.Position,
                request.ActionId,
                request.sequence,
                _scheduler.ServerTime,
                CancellationToken.None).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            return;
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine($"Context world interaction failed for peer {session?.Peer?.Id}: {ex.Message}");
            result = new InteractionResult(
                request.sequence,
                request.ActionId,
                InteractionResultCode.Rejected,
                new InteractionTargetHandle(InteractionTargetKind.NetworkWorldObject, request.target.primaryId),
                "world interaction failed");
        }

        _mainThreadCompletions.Enqueue(() =>
        {
            if (!IsCurrent(session)) return;
            SendResponse(session, requestId, ToContextInteractionWire(request.target, result));
        });
    }

    private InteractionActionSet AppendPopulationLootAction(
        InteractionActionSet set,
        AuthoritativeActorRuntime actor)
    {
        if (actor == null || actor.Alive || actor.HealthCurrent > 0 || !_runtime.PopulationLoot.CanLoot(actor))
            return set;

        InteractionActionEntry[] existing = set.Actions ?? Array.Empty<InteractionActionEntry>();
        for (int i = 0; i < existing.Length; ++i)
            if (existing[i].ActionId == InteractionActionId.Loot)
                return set;

        var actions = new InteractionActionEntry[existing.Length + 1];
        Array.Copy(existing, actions, existing.Length);
        actions[existing.Length] = new InteractionActionEntry(
            InteractionCategoryId.Interact,
            InteractionActionId.Loot,
            "Loot",
            InteractionAvailability.Available,
            string.Empty,
            feature: InteractionFeature.Loot,
            sortOrder: 10);
        Array.Sort(actions, (a, b) => a.SortOrder.CompareTo(b.SortOrder));
        return new InteractionActionSet(set.Target, set.TargetLabel, actions, set.Detail);
    }

    private static InteractionMenuResponseMessage ToInteractionMenuWire(
        InteractionTargetReferenceWire target,
        InteractionActionSet set)
    {
        InteractionActionEntry[] source = set.Actions ?? Array.Empty<InteractionActionEntry>();
        int count = Math.Min(source.Length, InteractionMenuResponseMessage.MaxActions);
        var actions = new InteractionActionEntryWire[count];
        for (int i = 0; i < count; ++i)
            actions[i] = InteractionActionEntryWire.From(source[i]);

        return new InteractionMenuResponseMessage
        {
            success = true,
            target = target,
            targetLabel = set.TargetLabel,
            detail = set.Detail,
            actions = actions,
        };
    }

    private static ContextInteractionResponseMessage ToContextInteractionWire(
        InteractionTargetReferenceWire target,
        InteractionResult result) =>
        new ContextInteractionResponseMessage
        {
            success = result.Success,
            sequence = result.Sequence,
            actionId = (ushort)result.ActionId,
            resultCode = (byte)result.ResultCode,
            target = target,
            detail = result.Detail,
        };
}
