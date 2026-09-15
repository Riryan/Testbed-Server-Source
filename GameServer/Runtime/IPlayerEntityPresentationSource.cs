namespace Game.GameServer.Runtime;

/// <summary>
/// Internal replication view for the existing PlayerEntity wire/presentation contract.
/// Players and humanoid Population both feed this contract; gameplay authority remains
/// in their respective canonical runtimes.
/// </summary>
internal interface IPlayerEntityPresentationSource
{
    uint ObjectId { get; }
    ushort Generation { get; }
    long ConnectionId { get; }
    float X { get; }
    float Y { get; }
    float Z { get; }
    float YawDegrees { get; }
    float SnapshotSpeed { get; }
    float SnapshotVerticalSpeed { get; }
    byte SnapshotFlags { get; }
    byte SnapshotMoveState { get; }
    bool IsDead { get; }
}
