using System;
using System.Collections.Generic;
using System.Buffers;
using System.Diagnostics;
using Game.GameServer.Runtime;
using LiteNetLib;
using LiteNetLib.Utils;
using Player.Networking;

namespace Game.GameServer.Networking;

internal sealed partial class GameServerHost
{
    private enum OutboundPriority : byte
    {
        Critical = 0,
        High = 1,
        Normal = 2,
        Background = 3,
    }

    private enum OutboundFamily : byte
    {
        Control = 0,
        Movement = 1,
        Combat = 2,
        Inventory = 3,
        Resources = 4,
        Status = 5,
        WorldItems = 6,
        Interactions = 7,
        Appearance = 8,
        Chat = 9,
        Settings = 10,
        Other = 11,
        Count = 12,
    }

    private sealed class OutboundPacket
    {
        public byte[] Buffer;
        public int Length;
        public DeliveryMethod Delivery;
        public OutboundPriority Priority;
        public OutboundFamily Family;
        public long EnqueuedTimestamp;
    }

    private sealed class OutboundConnectionState
    {
        public readonly ClientSession Session;
        public readonly Queue<OutboundPacket>[] Queues =
        {
            new Queue<OutboundPacket>(8),
            new Queue<OutboundPacket>(16),
            new Queue<OutboundPacket>(32),
            new Queue<OutboundPacket>(32),
        };

        public double Tokens;
        public long LastRefillTimestamp;
        public int PendingBytes;
        public int PendingPackets;
        public long SentBytesWindow;
        public long SentPacketsWindow;
        public long DeferredFramesWindow;
        public long DroppedUnreliableWindow;
        public long MaxQueueAgeMillisecondsWindow;

        public OutboundConnectionState(ClientSession session, double burstBytes)
        {
            Session = session;
            Tokens = Math.Max(0d, burstBytes);
            LastRefillTimestamp = Stopwatch.GetTimestamp();
        }

        public bool HasPending => PendingPackets > 0;
    }

    private readonly Dictionary<ClientSession, OutboundConnectionState> _outboundStates =
        new Dictionary<ClientSession, OutboundConnectionState>();
    private readonly List<OutboundConnectionState> _outboundActive = new List<OutboundConnectionState>(128);
    private readonly long[] _outboundFamilyBytes = new long[(int)OutboundFamily.Count];
    private readonly long[] _outboundFamilyPackets = new long[(int)OutboundFamily.Count];
    private int _outboundFlushCursor;
    private long _outboundBudgetDeferrals;
    private long _outboundDroppedUnreliable;
    private long _outboundQueueLimitDisconnects;
    private int _outboundMaxPendingBytes;
    private int _outboundMaxPendingPackets;
    private long _outboundMaxQueueAgeMilliseconds;

    private void QueueOutbound(
        ClientSession session,
        NetDataWriter writer,
        DeliveryMethod deliveryMethod,
        OutboundPriority priority,
        OutboundFamily family)
    {
        if (!IsCurrent(session) ||
            session.Peer.ConnectionState != ConnectionState.Connected ||
            writer == null || writer.Length <= 0)
            return;

        if (!_outboundStates.TryGetValue(session, out OutboundConnectionState state))
        {
            state = new OutboundConnectionState(session, _options.OutboundBurstBytesPerConnection);
            _outboundStates.Add(session, state);
        }

        int length = writer.Length;
        long projectedBytes = (long)state.PendingBytes + length;
        if (projectedBytes > _options.OutboundMaxQueuedBytesPerConnection)
        {
            // Disposable normal/background unreliable traffic is shed first. Reliable and
            // high-priority traffic must not be silently discarded because doing so can
            // corrupt protocol/gameplay state. If essential traffic cannot remain inside
            // the configured per-connection memory bound, fail the slow consumer closed.
            if (deliveryMethod == DeliveryMethod.Unreliable && priority >= OutboundPriority.Normal)
            {
                state.DroppedUnreliableWindow++;
                _outboundDroppedUnreliable++;
                return;
            }

            _outboundQueueLimitDisconnects++;
            Console.Error.WriteLine(
                $"Outbound queue limit disconnect: peer={session.Peer.Id}, " +
                $"pending={state.PendingBytes}B, packet={length}B, " +
                $"limit={_options.OutboundMaxQueuedBytesPerConnection}B, " +
                $"delivery={deliveryMethod}, priority={priority}, family={family}.");

            // Return already-pooled queued buffers immediately rather than waiting for the
            // transport disconnect callback. The normal disconnect path remains idempotent.
            RemoveOutboundState(session);
            session.Peer.Disconnect();
            return;
        }

        byte[] buffer = ArrayPool<byte>.Shared.Rent(length);
        Buffer.BlockCopy(writer.Data, 0, buffer, 0, length);
        state.Queues[(int)priority].Enqueue(new OutboundPacket
        {
            Buffer = buffer,
            Length = length,
            Delivery = deliveryMethod,
            Priority = priority,
            Family = family,
            EnqueuedTimestamp = Stopwatch.GetTimestamp(),
        });
        state.PendingBytes += length;
        state.PendingPackets++;

        if (state.PendingBytes > _outboundMaxPendingBytes)
            _outboundMaxPendingBytes = state.PendingBytes;
        if (state.PendingPackets > _outboundMaxPendingPackets)
            _outboundMaxPendingPackets = state.PendingPackets;

        if (state.PendingPackets == 1)
            _outboundActive.Add(state);
    }

    private void FlushOutboundQueues()
    {
        int count = _outboundActive.Count;
        if (count == 0)
            return;

        int globalBytesRemaining = Math.Max(1024, _options.OutboundBytesPerFrame);
        int globalPacketsRemaining = Math.Max(1, _options.OutboundPacketsPerFrame);
        int start = _outboundFlushCursor;
        if (start < 0 || start >= count)
            start = 0;

        int visited = 0;
        while (visited < count && globalBytesRemaining > 0 && globalPacketsRemaining > 0)
        {
            int index = (start + visited) % count;
            OutboundConnectionState state = _outboundActive[index];
            visited++;

            if (!IsCurrent(state.Session) || state.Session.Peer.ConnectionState != ConnectionState.Connected)
            {
                _outboundStates.Remove(state.Session);
                ReleaseOutboundState(state);
                continue;
            }

            RefillOutboundTokens(state);
            int connectionPackets = 0;
            bool deferred = false;
            while (connectionPackets < _options.OutboundPacketsPerConnectionFrame &&
                   globalBytesRemaining > 0 && globalPacketsRemaining > 0 &&
                   TryPeekNextOutbound(state, out OutboundPacket packet))
            {
                bool critical = packet.Priority == OutboundPriority.Critical;
                double permitted = state.Tokens + (critical ? _options.OutboundCriticalReserveBytes : 0d);
                if (packet.Length > permitted || packet.Length > globalBytesRemaining)
                {
                    deferred = true;
                    break;
                }

                DequeuePacket(state, packet.Priority);
                state.Tokens -= packet.Length;
                state.PendingBytes -= packet.Length;
                state.PendingPackets--;

                long ageMs = ElapsedMilliseconds(packet.EnqueuedTimestamp);
                if (ageMs > state.MaxQueueAgeMillisecondsWindow)
                    state.MaxQueueAgeMillisecondsWindow = ageMs;
                if (ageMs > _outboundMaxQueueAgeMilliseconds)
                    _outboundMaxQueueAgeMilliseconds = ageMs;

                try
                {
                    state.Session.Peer.Send(packet.Buffer, 0, packet.Length, 0, packet.Delivery);
                    state.SentBytesWindow += packet.Length;
                    state.SentPacketsWindow++;
                    _outboundFamilyBytes[(int)packet.Family] += packet.Length;
                    _outboundFamilyPackets[(int)packet.Family]++;
                }
                finally
                {
                    ArrayPool<byte>.Shared.Return(packet.Buffer);
                }

                connectionPackets++;
                globalPacketsRemaining--;
                globalBytesRemaining -= packet.Length;
            }

            if (deferred && state.HasPending)
            {
                state.DeferredFramesWindow++;
                _outboundBudgetDeferrals++;
            }
        }

        int retained = 0;
        for (int i = 0; i < _outboundActive.Count; ++i)
        {
            OutboundConnectionState state = _outboundActive[i];
            if (state.HasPending && IsCurrent(state.Session) &&
                state.Session.Peer.ConnectionState == ConnectionState.Connected)
            {
                _outboundActive[retained++] = state;
            }
        }
        if (retained < _outboundActive.Count)
            _outboundActive.RemoveRange(retained, _outboundActive.Count - retained);

        _outboundFlushCursor = retained > 0
            ? (_outboundFlushCursor + Math.Max(1, visited)) % retained
            : 0;
    }

    private void RefillOutboundTokens(OutboundConnectionState state)
    {
        long now = Stopwatch.GetTimestamp();
        long elapsedTicks = now - state.LastRefillTimestamp;
        if (elapsedTicks <= 0)
            return;

        double seconds = elapsedTicks / (double)Stopwatch.Frequency;
        state.LastRefillTimestamp = now;
        state.Tokens = Math.Min(
            _options.OutboundBurstBytesPerConnection,
            state.Tokens + seconds * _options.OutboundBytesPerConnectionSecond);
    }

    private static bool TryPeekNextOutbound(OutboundConnectionState state, out OutboundPacket packet)
    {
        for (int i = 0; i < state.Queues.Length; ++i)
        {
            Queue<OutboundPacket> queue = state.Queues[i];
            if (queue.Count > 0)
            {
                packet = queue.Peek();
                return true;
            }
        }
        packet = null;
        return false;
    }

    private static void DequeuePacket(OutboundConnectionState state, OutboundPriority priority) =>
        state.Queues[(int)priority].Dequeue();

    private void RemoveOutboundState(ClientSession session)
    {
        if (session == null || !_outboundStates.Remove(session, out OutboundConnectionState state))
            return;
        ReleaseOutboundState(state);
    }

    private void ReleaseAllOutboundStates()
    {
        foreach (OutboundConnectionState state in _outboundStates.Values)
            ReleaseOutboundState(state);
        _outboundStates.Clear();
        _outboundActive.Clear();
        _outboundFlushCursor = 0;
    }

    private static void ReleaseOutboundState(OutboundConnectionState state)
    {
        if (state == null)
            return;
        for (int i = 0; i < state.Queues.Length; ++i)
        {
            Queue<OutboundPacket> queue = state.Queues[i];
            while (queue.Count > 0)
            {
                OutboundPacket packet = queue.Dequeue();
                if (packet?.Buffer != null)
                    ArrayPool<byte>.Shared.Return(packet.Buffer);
            }
        }
        state.PendingBytes = 0;
        state.PendingPackets = 0;
    }

    private static long ElapsedMilliseconds(long timestamp)
    {
        long elapsed = Stopwatch.GetTimestamp() - timestamp;
        if (elapsed <= 0)
            return 0;
        return (long)(elapsed * 1000d / Stopwatch.Frequency);
    }

    private static OutboundPriority DefaultOutboundPriority(DeliveryMethod deliveryMethod)
    {
        return deliveryMethod switch
        {
            DeliveryMethod.ReliableUnordered => OutboundPriority.Critical,
            DeliveryMethod.ReliableOrdered => OutboundPriority.High,
            _ => OutboundPriority.Normal,
        };
    }

    private static OutboundPriority PriorityForClientMessage(ushort messageType, DeliveryMethod deliveryMethod)
    {
        // Keep all ReliableOrdered traffic in one FIFO priority lane. Reordering a later
        // combat/item message ahead of an earlier AOI spawn would break causal delivery even
        // though LiteNetLib itself preserves the order in which packets are actually sent.
        if (deliveryMethod == DeliveryMethod.ReliableOrdered)
            return OutboundPriority.High;

        if (messageType == PlayerGameplayActionMessageTypes.CombatPresentationBatch ||
            messageType == PlayerGameplayActionMessageTypes.CombatFireCycleBatch)
            return OutboundPriority.High;

        return DefaultOutboundPriority(deliveryMethod);
    }

    private static OutboundFamily FamilyForClientMessage(ushort messageType)
    {
        if (messageType == PlayerItemMessageTypes.Delta || messageType == PlayerItemMessageTypes.Snapshot)
            return OutboundFamily.Inventory;
        if (messageType == PlayerResourceMessageTypes.Delta || messageType == PlayerResourceMessageTypes.Snapshot)
            return OutboundFamily.Resources;
        if (messageType == PlayerStatusEffectMessageTypes.Delta || messageType == PlayerStatusEffectMessageTypes.Snapshot)
            return OutboundFamily.Status;
        if (messageType == WorldItemMessageTypes.Delta || messageType == WorldItemMessageTypes.Snapshot)
            return OutboundFamily.WorldItems;
        if (messageType == PlayerChatMessageTypes.Deliver)
            return OutboundFamily.Chat;
        if (messageType == GameplaySettingsMessageTypes.Delta || messageType == GameplaySettingsMessageTypes.Snapshot)
            return OutboundFamily.Settings;
        if (messageType == PlayerGameplayActionMessageTypes.WorldInteractableState)
            return OutboundFamily.Interactions;
        if (messageType >= PlayerGameplayActionMessageTypes.CombatDamage &&
            messageType <= PlayerGameplayActionMessageTypes.CombatFireCycleBatch)
            return OutboundFamily.Combat;
        return OutboundFamily.Other;
    }
    private string CaptureOutboundDiagnosticsAndReset(out bool activity, bool forceSummary = false)
    {
        long totalBytes = 0;
        long totalPackets = 0;
        long maxConnectionBytes = 0;
        long maxConnectionPackets = 0;
        long maxConnectionDeferrals = 0;
        long maxConnectionDrops = 0;
        for (int i = 0; i < _outboundFamilyBytes.Length; ++i)
        {
            totalBytes += _outboundFamilyBytes[i];
            totalPackets += _outboundFamilyPackets[i];
        }

        foreach (OutboundConnectionState state in _outboundStates.Values)
        {
            maxConnectionBytes = Math.Max(maxConnectionBytes, state.SentBytesWindow);
            maxConnectionPackets = Math.Max(maxConnectionPackets, state.SentPacketsWindow);
            maxConnectionDeferrals = Math.Max(maxConnectionDeferrals, state.DeferredFramesWindow);
            maxConnectionDrops = Math.Max(maxConnectionDrops, state.DroppedUnreliableWindow);
            state.SentBytesWindow = 0;
            state.SentPacketsWindow = 0;
            state.DeferredFramesWindow = 0;
            state.DroppedUnreliableWindow = 0;
            state.MaxQueueAgeMillisecondsWindow = 0;
        }

        activity = totalPackets > 0 || _outboundBudgetDeferrals > 0 ||
                   _outboundDroppedUnreliable > 0 || _outboundQueueLimitDisconnects > 0;
        string summary = string.Empty;
        if (activity || forceSummary)
        {
            summary =
                $"Outbound/10s: total={totalPackets}/{totalBytes}B, " +
                $"maxCCU={maxConnectionPackets}/{maxConnectionBytes}B, " +
                $"deferredFrames={_outboundBudgetDeferrals} (maxCCU={maxConnectionDeferrals}), " +
                $"unreliableDropped={_outboundDroppedUnreliable} (maxCCU={maxConnectionDrops}), " +
                $"queueLimitDisconnects={_outboundQueueLimitDisconnects}, " +
                $"queueMax={_outboundMaxPendingPackets}pkt/{_outboundMaxPendingBytes}B/{_outboundMaxQueueAgeMilliseconds}ms, " +
                $"families=" + FormatOutboundFamilies();
        }

        Array.Clear(_outboundFamilyBytes, 0, _outboundFamilyBytes.Length);
        Array.Clear(_outboundFamilyPackets, 0, _outboundFamilyPackets.Length);
        _outboundBudgetDeferrals = 0;
        _outboundDroppedUnreliable = 0;
        _outboundQueueLimitDisconnects = 0;
        _outboundMaxPendingBytes = 0;
        _outboundMaxPendingPackets = 0;
        _outboundMaxQueueAgeMilliseconds = 0;
        return summary;
    }

    private string FormatOutboundFamilies()
    {
        var parts = new List<string>(8);
        for (int i = 0; i < (int)OutboundFamily.Count; ++i)
        {
            long packets = _outboundFamilyPackets[i];
            long bytes = _outboundFamilyBytes[i];
            if (packets <= 0 && bytes <= 0)
                continue;
            parts.Add($"{(OutboundFamily)i}:{packets}/{bytes}B");
        }
        return parts.Count == 0 ? "none" : string.Join(",", parts);
    }

}
