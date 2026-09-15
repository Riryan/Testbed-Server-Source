using System;
using System.Collections.Generic;
using Game.Shared.Staff;

namespace Game.Server.Application.Staff
{
    public sealed class StaffSessionState
    {
        public long AccountId { get; internal set; }
        public string RoleName { get; internal set; } = string.Empty;
        public StaffCapability Capabilities { get; internal set; }
        public StaffVisibilityMode VisibilityMode { get; internal set; }
        public long SpectateCharacterId { get; internal set; }
        public long Revision { get; internal set; }
        public bool IsAuthorized => AccountId > 0 && Capabilities != StaffCapability.None;

        public bool Has(StaffCapability capability) =>
            capability != StaffCapability.None && (Capabilities & capability) == capability;
    }

    /// <summary>
    /// Server-authorized GameMaster/staff control plane. No client-originated flag can grant
    /// staff capability. Hidden staff are intended to be removed from ordinary AOI rather
    /// than merely hidden by presentation.
    /// </summary>
    public sealed class GameMasterService
    {
        private readonly Dictionary<long, StaffAuthorizationSnapshot> _authorizations =
            new Dictionary<long, StaffAuthorizationSnapshot>();
        private readonly Dictionary<long, StaffSessionState> _sessions =
            new Dictionary<long, StaffSessionState>();
        private long _auditSequence;

        public event Action<StaffSessionState> SessionChanged;
        public event Action<StaffAuditRecord> AuditWritten;

        public GameMasterService(IEnumerable<StaffAuthorizationSnapshot> authorizations)
        {
            if (authorizations == null) return;
            foreach (StaffAuthorizationSnapshot authorization in authorizations)
            {
                if (authorization == null || authorization.accountId <= 0 || authorization.CapabilityMask == StaffCapability.None)
                    continue;
                _authorizations[authorization.accountId] = authorization;
            }
        }

        public int AuthorizedAccountCount => _authorizations.Count;

        public StaffSessionState OpenSession(long accountId)
        {
            if (accountId <= 0) return null;
            if (_sessions.TryGetValue(accountId, out StaffSessionState existing)) return existing;

            _authorizations.TryGetValue(accountId, out StaffAuthorizationSnapshot authorization);
            var state = new StaffSessionState
            {
                AccountId = accountId,
                RoleName = authorization?.roleName ?? string.Empty,
                Capabilities = authorization?.CapabilityMask ?? StaffCapability.None,
                VisibilityMode = StaffVisibilityMode.VisibleStaff,
                Revision = 1,
            };
            _sessions[accountId] = state;
            if (state.IsAuthorized)
                WriteAudit(accountId, "StaffSessionOpen", 0, state.RoleName, true);
            return state;
        }

        public void CloseSession(long accountId)
        {
            if (!_sessions.Remove(accountId, out StaffSessionState state)) return;
            if (state.IsAuthorized)
                WriteAudit(accountId, "StaffSessionClose", state.SpectateCharacterId, state.VisibilityMode.ToString(), true);
        }

        public bool TryGetSession(long accountId, out StaffSessionState state) => _sessions.TryGetValue(accountId, out state);

        public bool TrySetVisibility(long accountId, StaffVisibilityMode mode, out string detail)
        {
            detail = string.Empty;
            StaffSessionState state = OpenSession(accountId);
            if (state == null || !state.IsAuthorized)
            {
                detail = "staff authorization is unavailable";
                return false;
            }
            StaffCapability required = mode == StaffVisibilityMode.HiddenObserver
                ? StaffCapability.HiddenObserve
                : StaffCapability.ObservePlayers;
            if (!state.Has(required))
            {
                detail = "staff capability is not authorized";
                WriteAudit(accountId, "SetVisibility", 0, mode.ToString(), false);
                return false;
            }
            state.VisibilityMode = mode;
            if (mode != StaffVisibilityMode.Spectating)
                state.SpectateCharacterId = 0;
            Touch(state);
            WriteAudit(accountId, "SetVisibility", 0, mode.ToString(), true);
            return true;
        }

        public bool TryStartSpectate(long accountId, long targetCharacterId, out string detail)
        {
            detail = string.Empty;
            if (targetCharacterId <= 0)
            {
                detail = "spectate target is invalid";
                return false;
            }
            StaffSessionState state = OpenSession(accountId);
            if (state == null || !state.Has(StaffCapability.SpectatePlayers))
            {
                detail = "spectate capability is not authorized";
                WriteAudit(accountId, "StartSpectate", targetCharacterId, detail, false);
                return false;
            }
            state.VisibilityMode = StaffVisibilityMode.Spectating;
            state.SpectateCharacterId = targetCharacterId;
            Touch(state);
            WriteAudit(accountId, "StartSpectate", targetCharacterId, string.Empty, true);
            return true;
        }

        public bool StopSpectate(long accountId, out string detail)
        {
            detail = string.Empty;
            if (!_sessions.TryGetValue(accountId, out StaffSessionState state) || !state.IsAuthorized)
            {
                detail = "staff session is unavailable";
                return false;
            }
            long target = state.SpectateCharacterId;
            state.SpectateCharacterId = 0;
            state.VisibilityMode = StaffVisibilityMode.HiddenObserver;
            Touch(state);
            WriteAudit(accountId, "StopSpectate", target, string.Empty, true);
            return true;
        }

        public bool CanObserverSee(long observerAccountId, long targetAccountId)
        {
            if (observerAccountId <= 0 || targetAccountId <= 0 || observerAccountId == targetAccountId)
                return true;
            if (!_sessions.TryGetValue(targetAccountId, out StaffSessionState target) || !target.IsAuthorized)
                return true;
            if (target.VisibilityMode != StaffVisibilityMode.HiddenObserver && target.VisibilityMode != StaffVisibilityMode.Spectating)
                return true;

            // Hidden staff remain visible only to other authorized observers. This prevents
            // ordinary players from learning hidden-GM presence through replication/nameplates.
            return _sessions.TryGetValue(observerAccountId, out StaffSessionState observer) &&
                   observer.IsAuthorized && observer.Has(StaffCapability.ObservePlayers);
        }

        public bool IsHiddenObserver(long accountId) =>
            _sessions.TryGetValue(accountId, out StaffSessionState state) &&
            state.IsAuthorized &&
            (state.VisibilityMode == StaffVisibilityMode.HiddenObserver || state.VisibilityMode == StaffVisibilityMode.Spectating);

        private void Touch(StaffSessionState state)
        {
            state.Revision = state.Revision == long.MaxValue ? 1 : state.Revision + 1;
            SessionChanged?.Invoke(state);
        }

        private void WriteAudit(long accountId, string action, long targetId, string detail, bool success)
        {
            long sequence = ++_auditSequence;
            if (sequence <= 0) sequence = _auditSequence = 1;
            AuditWritten?.Invoke(new StaffAuditRecord
            {
                sequence = sequence,
                utcTicks = DateTime.UtcNow.Ticks,
                accountId = accountId,
                action = action ?? string.Empty,
                targetCharacterId = targetId,
                detail = detail ?? string.Empty,
                success = success,
            });
        }
    }
}
