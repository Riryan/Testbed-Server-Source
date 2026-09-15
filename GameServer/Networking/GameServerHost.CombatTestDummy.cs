using System;
using System.Collections.Generic;
using Game.GameServer.Runtime;
using Game.Server.Application.Items;
using Game.Server.Domain.Characters;
using Game.Server.Domain.Equipment;
using Game.Server.Domain.Inventory;
using Game.Server.Domain.Players;
using Game.Server.Domain.Stats;
using Game.Shared.Identity;
using Game.Shared.World;
using Player.Networking;

namespace Game.GameServer.Networking;

/// <summary>
/// Opt-in development combat fixture. It deliberately reuses the canonical PlayerRuntime,
/// PlayerEntity spawn contract, combat services, statuses and resources instead of adding
/// a second test-only damage model. Enable with --combat-test-dummy.
/// </summary>
internal sealed partial class GameServerHost
{
    private const long CombatTestDummyCharacterId = 9_000_000_001L;
    private const long CombatTestDummyAccountId = 9_000_000_001L;
    private const long CombatTestDummyItemInstanceId = 9_000_000_001L;
    private const string CombatTestDummyArmorDefinitionId = "item.armor.test_conductive_plate";
    private const float CombatTestDummyOffsetX = 1.25f;
    private const float CombatTestDummyHealthMaximum = 10000f;

    private PlayerRuntime _combatTestDummyRuntime;
    private ServerPlayerEntity _combatTestDummyEntity;
    private bool _combatTestDummySchedulerActive;

    private void EnsureCombatTestDummyFor(ClientSession observer)
    {
        if (!_options.CombatTestDummy || observer?.Entity == null || !observer.Ready)
            return;

        if (_combatTestDummyRuntime == null || _combatTestDummyEntity == null)
            CreateCombatTestDummy(observer);

        if (IsCombatTestDummyVisibleTo(observer))
            SendSpawn(observer, _combatTestDummyEntity);
    }

    private void CreateCombatTestDummy(ClientSession anchor)
    {
        CharacterLocationState anchorLocation = anchor.Entity.CaptureLocation();
        var position = new WorldPosition(
            anchorLocation.Position.X + CombatTestDummyOffsetX,
            anchorLocation.Position.Y,
            anchorLocation.Position.Z);
        var location = new CharacterLocationState(
            anchorLocation.MapId,
            anchorLocation.InstanceId,
            position,
            180f);

        var runtime = new PlayerRuntime(
            new AccountId(CombatTestDummyAccountId),
            new CharacterId(CombatTestDummyCharacterId),
            PlayerSessionId.New(),
            new CharacterState("Combat Dummy"),
            location,
            0L);

        EquipmentState equipment = BuildCombatTestDummyEquipment();
        StatsState stats = EquipmentStatCalculator.Calculate(_runtime.Content, equipment);
        stats = WithStat(stats, "Health.Max", CombatTestDummyHealthMaximum);
        runtime.InitializePlayerItemSystems(
            new InventoryState(1, 0L),
            equipment,
            stats);
        runtime.InitializeCharacterResources(
            _runtime.Resources.CreateInitialState(stats, 0L, null));

        _combatTestDummyRuntime = runtime;
        _combatTestDummyEntity = new ServerPlayerEntity(
            NextObjectId(),
            NextGeneration(),
            0L,
            runtime,
            _runtime.Maps,
            _runtime.Content.GetMovementRules());

        _statusEffectScheduler.Activate(runtime);
        _combatTestDummySchedulerActive = true;

        Console.WriteLine(
            $"Combat test dummy: enabled object={_combatTestDummyEntity.ObjectId}/{_combatTestDummyEntity.Generation}, " +
            $"character={CombatTestDummyCharacterId}, map={location.MapId}, " +
            $"position=({position.X:0.##},{position.Y:0.##},{position.Z:0.##}), health={CombatTestDummyHealthMaximum:0}.");
    }

    private EquipmentState BuildCombatTestDummyEquipment()
    {
        if (!_runtime.Content.TryGetItem(CombatTestDummyArmorDefinitionId, out var definition))
        {
            Console.Error.WriteLine(
                $"Combat test dummy: optional '{CombatTestDummyArmorDefinitionId}' is unavailable; " +
                "weakness-by-equipment-tag checks will not be active.");
            return new EquipmentState(0L);
        }

        int durability = Math.Max(0, definition.maxDurability);
        var item = new ItemInstanceState(
            new ItemInstanceId(CombatTestDummyItemInstanceId),
            CombatTestDummyArmorDefinitionId,
            1,
            durability,
            0L);
        return new EquipmentState(0L, new[] { new EquippedItemState("Chest", item) });
    }

    private static StatsState WithStat(StatsState source, string statId, float value)
    {
        var values = new List<KeyValuePair<string, float>>(source?.Snapshot() ?? Array.Empty<KeyValuePair<string, float>>());
        bool replaced = false;
        for (int i = 0; i < values.Count; ++i)
        {
            if (!string.Equals(values[i].Key, statId, StringComparison.Ordinal))
                continue;
            values[i] = new KeyValuePair<string, float>(statId, value);
            replaced = true;
            break;
        }
        if (!replaced)
            values.Add(new KeyValuePair<string, float>(statId, value));
        return new StatsState(values);
    }

    private bool TryResolveCombatTestDummy(
        ClientSession observer,
        PlayerTargetReferenceWire reference,
        out PlayerRuntime runtime)
    {
        runtime = null;
        if (!_options.CombatTestDummy || _combatTestDummyEntity == null || _combatTestDummyRuntime == null ||
            observer?.Entity == null || !reference.IsValid ||
            reference.objectId != _combatTestDummyEntity.ObjectId ||
            reference.generation != _combatTestDummyEntity.Generation ||
            !IsCombatTestDummyVisibleTo(observer))
        {
            return false;
        }

        runtime = _combatTestDummyRuntime;
        return true;
    }

    private bool IsCombatTestDummy(PlayerRuntime runtime) =>
        runtime != null && ReferenceEquals(runtime, _combatTestDummyRuntime);

    private bool IsCombatTestDummyCharacter(long characterId) =>
        _combatTestDummyRuntime != null && characterId == CombatTestDummyCharacterId;

    private bool IsCombatTestDummyVisibleTo(ClientSession observer)
    {
        if (observer?.Entity == null || _combatTestDummyEntity == null)
            return false;

        CharacterLocationState observerLocation = observer.Entity.CaptureLocation();
        CharacterLocationState dummyLocation = _combatTestDummyRuntime.Location;
        if (!string.Equals(observerLocation.MapId, dummyLocation.MapId, StringComparison.Ordinal) ||
            !string.Equals(observerLocation.InstanceId, dummyLocation.InstanceId, StringComparison.Ordinal))
        {
            return false;
        }

        float dx = observer.Entity.X - _combatTestDummyEntity.X;
        float dy = observer.Entity.Y - _combatTestDummyEntity.Y;
        float dz = observer.Entity.Z - _combatTestDummyEntity.Z;
        float range = Math.Max(1f, _options.AoiRange);
        return (dx * dx) + (dy * dy) + (dz * dz) <= range * range;
    }

    private void DeactivateCombatTestDummy()
    {
        if (_combatTestDummySchedulerActive && _combatTestDummyRuntime != null)
            _statusEffectScheduler?.Deactivate(_combatTestDummyRuntime);
        _combatTestDummySchedulerActive = false;
        _combatTestDummyEntity = null;
        _combatTestDummyRuntime = null;
    }
}
