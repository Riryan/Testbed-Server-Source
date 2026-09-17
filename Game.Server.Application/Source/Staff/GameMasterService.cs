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
        // Gameplay-hidden bridge. This is intentionally not exposed as a player command.
        // The eventual Hide gameplay action/status may drive this existing visibility hook
        // without introducing another AOI or replication system.
        public bool PlayerHiddenFromPlayers { get; internal set; }
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

        /// <summary>
        /// Gameplay-owned ordinary-player visibility hook. It is deliberately not exposed
        /// through a player chat command. Hidden players are removed from ordinary AOI while
        /// all server-authorized staff retain visibility. Future Hide gameplay can drive this
        /// same hook without another observer system or network message.
        /// </summary>
        public bool TrySetPlayerHidden(long accountId, bool hidden, out string detail)
        {
            detail = string.Empty;
            StaffSessionState state = OpenSession(accountId);
            if (state == null)
            {
                detail = "player visibility session is unavailable";
                return false;
            }

            if (state.PlayerHiddenFromPlayers == hidden)
            {
                detail = hidden ? "already hidden from players" : "already visible to players";
                return true;
            }

            state.PlayerHiddenFromPlayers = hidden;
            Touch(state);
            detail = hidden ? "hidden from ordinary players" : "visible to players";
            return true;
        }

        public bool IsPlayerHidden(long accountId) =>
            accountId > 0 &&
            _sessions.TryGetValue(accountId, out StaffSessionState state) &&
            state.PlayerHiddenFromPlayers;

        /// <summary>
        /// Writes a player-originated moderation report through the already-existing durable
        /// audit stream. This intentionally reuses StaffAuditRecord/StaffAuditFileSink rather
        /// than introducing a second moderation log or a new client/server message.
        /// </summary>
        public bool TryWritePlayerReport(
            long reporterAccountId,
            long reporterCharacterId,
            long targetCharacterId,
            string reason,
            out string detail)
        {
            detail = string.Empty;
            string prepared = (reason ?? string.Empty).Trim();
            if (reporterAccountId <= 0 || reporterCharacterId <= 0 || targetCharacterId <= 0)
            {
                detail = "reporter or target is unavailable";
                return false;
            }
            if (reporterCharacterId == targetCharacterId)
            {
                detail = "you cannot report yourself";
                return false;
            }
            if (prepared.Length < 5 || prepared.Length > 500)
            {
                detail = "report reason must be 5-500 characters";
                return false;
            }

            long sequence = ++_auditSequence;
            if (sequence <= 0) sequence = _auditSequence = 1;
            AuditWritten?.Invoke(new StaffAuditRecord
            {
                sequence = sequence,
                utcTicks = DateTime.UtcNow.Ticks,
                accountId = reporterAccountId,
                characterId = reporterCharacterId,
                action = "PlayerReport",
                targetCharacterId = targetCharacterId,
                detail = prepared,
                success = true,
            });
            detail = "report submitted";
            return true;
        }

        public bool CanObserverSee(long observerAccountId, long targetAccountId)
        {
            if (observerAccountId <= 0 || targetAccountId <= 0 || observerAccountId == targetAccountId)
                return true;
            if (!_sessions.TryGetValue(targetAccountId, out StaffSessionState target))
                return true;

            bool observerIsStaff =
                _sessions.TryGetValue(observerAccountId, out StaffSessionState observer) &&
                observer.IsAuthorized;

            // Authorized staff always retain visibility of hidden actors, including other
            // hidden/spectating staff and gameplay-hidden players. Ordinary players do not.
            if (target.IsAuthorized &&
                (target.VisibilityMode == StaffVisibilityMode.HiddenObserver ||
                 target.VisibilityMode == StaffVisibilityMode.Spectating))
            {
                return observerIsStaff;
            }

            if (target.PlayerHiddenFromPlayers)
                return observerIsStaff;

            return true;
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
