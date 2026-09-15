using Game.GameServer.Runtime;
using Game.Server.Application.Combat;
using Game.Server.Domain.Players;
using Game.Shared.Abilities;
using Game.Shared.Content;
using LiteNetLib;
using LiteNetLib.Utils;
using Player.Networking;

namespace Game.GameServer.Networking;

/// <summary>
/// Standalone-server handler for the compact targetless combat intent.
/// Melee/unarmed actions contain no aim payload and resolve from authoritative player facing.
/// Precision-ranged actions append the existing packed yaw/pitch aim. No client-selected target
/// is trusted; range, LOS, cadence, resources/ammo and damage remain server authoritative.
/// </summary>
internal sealed partial class GameServerHost
{
    // Compact combat intents are one-way input hints, not authoritative attack cadence.
    // Keep admission independent from BasicAttackService so replay/flood traffic is rejected
    // before target/contact/LOS work while gameplay cadence remains server authoritative.
    private const double CombatRequestRefillPerSecond = 8d;
    private const double CombatRequestBurstCapacity = 8d;

    /// <summary>
    /// Handles PlayerGameplayActionMessageTypes.CombatActionIntent (63).
    /// Small melee actions are sequence + input only; firearms additionally carry packed aim.
    /// </summary>
    private void HandleCombatActionIntent(ClientSession session, NetDataReader reader)
    {
        var intent = new PlayerCombatActionIntentMessage();
        intent.Deserialize(reader);

        // Reject zero, stale/replayed and over-rate one-way combat input before any
        // authoritative range, AOI/contact, LOS, firearm or damage work. The client
        // sequence is freshness only; it never authorizes damage or attack cadence.
        if (!TryAdmitCombatActionIntent(session, intent.sequence))
            return;

        BasicAttackInputKind inputKind = intent.InputKind;
        if (inputKind != BasicAttackInputKind.Primary &&
            inputKind != BasicAttackInputKind.Light &&
            inputKind != BasicAttackInputKind.Heavy)
        {
            SendCombatPredictionCorrection(session);
            return;
        }

        if (!TryGetGameplayRuntime(session, out PlayerRuntime source) || session?.Entity == null)
        {
            SendCombatPredictionCorrection(session);
            return;
        }

        if (!TryResolveCompactCombatRange(source, out BasicAttackMode mode, out float authoritativeRange) ||
            authoritativeRange <= 0f)
        {
            SendCombatPredictionCorrection(session);
            return;
        }

        PlayerRuntime target;
        if (mode == BasicAttackMode.Firearm)
        {
            // Precision-ranged weapons require the compact aim extension. A forged melee-style
            // packet cannot turn a firearm into a server-facing shot.
            if (!intent.HasPrecisionAim)
            {
                SendCombatPredictionCorrection(session);
                return;
            }

            float yaw = PlayerCombatAimEncoding.DecodeYawDegrees(intent.packedAim);
            float pitch = PlayerCombatAimEncoding.DecodePitchDegrees(intent.packedAim);
            target = ResolveDirectCombatContact(
                session,
                source,
                authoritativeRange,
                mode,
                yaw,
                pitch);
        }
        else
        {
            // Ignore any client aim data for fists/melee. Their hit volume is the server-owned
            // player-facing swing arc + authored reach/body edge tolerance.
            target = ResolveDirectCombatContact(session, source, authoritativeRange, mode);
        }

        if (target == null)
        {
            SendCombatPredictionCorrection(session);
            return;
        }

        // FirearmActions owns cadence, magazine consumption, burst/full-auto and presentation.
        if (TryResolveFirearmTrigger(session, inputKind, source, target, out _))
            return;

        BasicAttackResult result = _runtime.BasicAttacks.TryAttack(
            source,
            target,
            inputKind,
            _scheduler.ServerTime);
        if (!result.Success)
            SendCombatPredictionCorrection(session);
    }


    private bool TryAdmitCombatActionIntent(ClientSession session, uint sequence)
    {
        if (session == null || sequence == 0)
            return false;

        uint previous = session.LastCombatActionSequence;
        if (previous != 0 && !IsNewerSequence(sequence, previous))
            return false;

        // Advance freshness before applying the token bucket. This mirrors interaction
        // admission and prevents a rate-limited packet from being replayed later.
        session.LastCombatActionSequence = sequence;

        long nowMs = Environment.TickCount64;
        long elapsedMs = Math.Max(0L, nowMs - session.CombatRequestTokenTimestampMs);
        session.CombatRequestTokenTimestampMs = nowMs;

        double tokens = Math.Min(
            CombatRequestBurstCapacity,
            session.CombatRequestTokens +
            elapsedMs * (CombatRequestRefillPerSecond / 1000d));

        if (tokens + 0.000001d < 1d)
        {
            session.CombatRequestTokens = tokens;
            return false;
        }

        session.CombatRequestTokens = tokens - 1d;
        return true;
    }

    /// <summary>
    /// Sends the existing canonical combat owner state after a prediction rejection/correction.
    /// No additional wire message is introduced here.
    /// </summary>
    private void SendCombatPredictionCorrection(ClientSession session, bool includeResources = false)
    {
        _ = includeResources;

        if (session == null || !IsCurrent(session) || !TryGetGameplayRuntime(session, out PlayerRuntime runtime))
            return;

        PlayerCombatOwnerStateMessage message = ToGameplayWire(
            _runtime.CombatLoadout.Capture(runtime, _scheduler.ServerTime));
        SendClientMessage(
            session,
            PlayerGameplayActionMessageTypes.CombatOwnerState,
            message,
            DeliveryMethod.ReliableOrdered);
    }

    /// <summary>
    /// Compatibility overload for current FirearmActions call sites that pass an existing
    /// positional correction token/sequence before the named includeResources argument.
    /// </summary>
    private void SendCombatPredictionCorrection<TCorrection>(
        ClientSession session,
        TCorrection correctionToken,
        bool includeResources = false)
    {
        _ = correctionToken;
        SendCombatPredictionCorrection(session, includeResources);
    }

    private bool TryResolveCompactCombatRange(
        PlayerRuntime source,
        out BasicAttackMode mode,
        out float range)
    {
        mode = BasicAttackMode.Unarmed;
        range = 0f;
        if (source == null)
            return false;

        CombatOwnerStateSnapshot owner = _runtime.CombatLoadout.Capture(source, _scheduler.ServerTime);
        if (!owner.Available)
            return false;

        mode = owner.Mode;
        if (mode == BasicAttackMode.Unarmed)
        {
            range = CombatRangePolicy.UnarmedRange;
            return range > 0f;
        }

        if (string.IsNullOrWhiteSpace(owner.WeaponDefinitionId) ||
            !_runtime.Content.TryGetItem(owner.WeaponDefinitionId, out ItemDefinition weapon))
        {
            return false;
        }

        range = CombatRangePolicy.ResolveServerRange(mode, weapon.basicAttackRange);
        return range > 0f;
    }
}
