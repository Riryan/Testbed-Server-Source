using Game.Shared.Protocol;
using LiteNetLibManager;

namespace Game.GameServer.Networking;

internal sealed partial class GameServerHost
{
    private ICoreScheduledSystemHandle _feedingSimulationHandle;

    private sealed class FeedingSimulationSystem : ICoreBudgetedWorkSystem
    {
        private readonly GameServerHost _owner;
        public FeedingSimulationSystem(GameServerHost owner) => _owner = owner;
        public string Name => "FeedingSessions";
        public bool HasPendingWork => _owner._runtime.Feeding.HasDueWork(_owner._scheduler.ServerTime);
        public void ExecuteOneWorkUnit(in CoreTickContext context) => _owner._runtime.Feeding.ProcessOneDue(context.Now);
    }

    private void InitializeFeedingScheduling()
    {
        _feedingSimulationHandle = _scheduler.RegisterWorkSystem(
            new FeedingSimulationSystem(this),
            "Gameplay",
            maxWorkUnitsPerTick: System.Math.Min(128, System.Math.Max(16, _options.MaxConnections)),
            quarantineable: true,
            invocationLimitMilliseconds: 0.5);
    }

    private void OnFeedingCombatDamage(CombatDamageResult result)
    {
        if (result.dealtAmount <= 0) return;
        if (result.sourceCharacterId > 0) _runtime.Feeding.CancelForCharacter(result.sourceCharacterId);
        if (result.targetCharacterId > 0) _runtime.Feeding.CancelForCharacter(result.targetCharacterId);
    }
}
