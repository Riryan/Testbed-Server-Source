namespace Game.Server.Application.World
{
    public enum PlayerWorldLifecycleStatus : byte
    {
        None = 0,
        Entered = 1,
        EnterRejected = 2,
        EnterAdapterFailed = 3,
        EnterBindingFailed = 4,
        EnterCommitFailedRolledBack = 5,
        Left = 6,
        LeftWithoutBinding = 7,
        LeaveRejected = 8,
        LeaveAdapterFailed = 9,
        EnterStaleSessionRejected = 10,
        LeaveStaleSessionRejected = 11,
        EnterCommitFailedExternalRollbackRequired = 12,
        LeaveBindingFailed = 13,
    }
}
