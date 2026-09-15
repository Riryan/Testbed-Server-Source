using Game.Server.Domain.Players;

namespace Game.Server.Application.Sessions
{
    /// <summary>
    /// Portable application rule for when a Character Session is eligible to ask the
    /// networking integration layer for its canonical gameplay PlayerEntity.
    ///
    /// Transport connection alone is never sufficient. Authentication, character
    /// ownership/load, and the exact session generation must already have produced an
    /// AwaitingWorldEntry PlayerRuntime.
    /// </summary>
    public static class PlayerSessionWorldAdmissionPolicy
    {
        public static bool CanRequestCanonicalReady(PlayerSession session)
        {
            if (session == null ||
                !session.TryGetAwaitingWorldEntryRuntime(out PlayerRuntime runtime) ||
                runtime == null)
            {
                return false;
            }

            return runtime.SessionId == session.SessionId &&
                   runtime.AccountId == session.AccountId &&
                   runtime.CharacterId == session.SelectedCharacterId;
        }
    }
}
