using System;
using System.Collections.Generic;
using Game.Server.Domain.Players;

namespace Game.Server.Application.Social
{
    public sealed class PartyMemberSnapshot
    {
        public long CharacterId { get; }
        public string Name { get; }
        public bool IsLeader { get; }

        public PartyMemberSnapshot(long characterId, string name, bool isLeader)
        {
            CharacterId = characterId;
            Name = name ?? string.Empty;
            IsLeader = isLeader;
        }
    }

    public sealed class PartySnapshot
    {
        public ulong PartyId { get; }
        public long LeaderCharacterId { get; }
        public PartyMemberSnapshot[] Members { get; }

        public PartySnapshot(ulong partyId, long leaderCharacterId, PartyMemberSnapshot[] members)
        {
            PartyId = partyId;
            LeaderCharacterId = leaderCharacterId;
            Members = members ?? Array.Empty<PartyMemberSnapshot>();
        }
    }

    public readonly struct PartyInviteSnapshot
    {
        public long InviterCharacterId { get; }
        public string InviterName { get; }
        public DateTime ExpiresUtc { get; }

        public PartyInviteSnapshot(long inviterCharacterId, string inviterName, DateTime expiresUtc)
        {
            InviterCharacterId = inviterCharacterId;
            InviterName = inviterName ?? string.Empty;
            ExpiresUtc = expiresUtc;
        }
    }

    public readonly struct PartyOperationResult
    {
        public bool Success { get; }
        public string Message { get; }
        public long OtherCharacterId { get; }
        public PartySnapshot Party { get; }

        public PartyOperationResult(bool success, string message, long otherCharacterId = 0, PartySnapshot party = null)
        {
            Success = success;
            Message = message ?? string.Empty;
            OtherCharacterId = otherCharacterId;
            Party = party;
        }

        public static PartyOperationResult Ok(string message, long otherCharacterId = 0, PartySnapshot party = null) =>
            new PartyOperationResult(true, message, otherCharacterId, party);

        public static PartyOperationResult Fail(string message) =>
            new PartyOperationResult(false, message);
    }

    /// <summary>
    /// Canonical temporary Party authority. Membership/invitations remain in-memory and
    /// intentionally do not grant XP, skill credit, loot, combat, inventory, or world authority.
    /// StateChanged is a lightweight owner-state notification hook for the transport/UI layer.
    /// </summary>
    public sealed class PartyService
    {
        public const int MaxMembers = 8;
        public static readonly TimeSpan InviteLifetime = TimeSpan.FromSeconds(60);

        private sealed class Member
        {
            public long CharacterId;
            public string Name;
            public long JoinOrder;
        }

        private sealed class Party
        {
            public ulong PartyId;
            public long LeaderCharacterId;
            public readonly List<Member> Members = new List<Member>(MaxMembers);
        }

        private sealed class PendingInvite
        {
            public long InviterCharacterId;
            public string InviterName;
            public long TargetCharacterId;
            public ulong PartyId;
            public DateTime ExpiresUtc;
        }

        private readonly Dictionary<ulong, Party> _parties = new Dictionary<ulong, Party>();
        private readonly Dictionary<long, ulong> _partyByCharacter = new Dictionary<long, ulong>();
        private readonly Dictionary<long, PendingInvite> _inviteByTarget = new Dictionary<long, PendingInvite>();
        private ulong _nextPartyId = 1;
        private long _nextJoinOrder = 1;

        public event Action<long> StateChanged;

        public PartyOperationResult Invite(PlayerRuntime inviter, PlayerRuntime target)
        {
            if (!TryIdentity(inviter, out long inviterId, out string inviterName) ||
                !TryIdentity(target, out long targetId, out string targetName))
                return PartyOperationResult.Fail("Party invite target is unavailable.");

            if (inviterId == targetId)
                return PartyOperationResult.Fail("You cannot invite yourself to a party.");
            if (_partyByCharacter.ContainsKey(targetId))
                return PartyOperationResult.Fail($"{targetName} is already in a party.");

            Party party = null;
            ulong partyId = 0;
            if (_partyByCharacter.TryGetValue(inviterId, out partyId))
            {
                if (!_parties.TryGetValue(partyId, out party))
                {
                    _partyByCharacter.Remove(inviterId);
                    partyId = 0;
                }
                else
                {
                    if (party.LeaderCharacterId != inviterId)
                        return PartyOperationResult.Fail("Only the party leader can invite members.");
                    if (party.Members.Count >= MaxMembers)
                        return PartyOperationResult.Fail("The party is full.");
                }
            }

            _inviteByTarget[targetId] = new PendingInvite
            {
                InviterCharacterId = inviterId,
                InviterName = inviterName,
                TargetCharacterId = targetId,
                PartyId = partyId,
                ExpiresUtc = DateTime.UtcNow + InviteLifetime,
            };

            NotifyStateChanged(targetId);
            return PartyOperationResult.Ok($"Party invite sent to {targetName}.", targetId);
        }

        public PartyOperationResult Accept(PlayerRuntime target)
        {
            if (!TryIdentity(target, out long targetId, out string targetName))
                return PartyOperationResult.Fail("Party acceptance is unavailable.");
            if (_partyByCharacter.ContainsKey(targetId))
                return PartyOperationResult.Fail("You are already in a party.");
            if (!TryTakeValidInvite(targetId, out PendingInvite invite))
            {
                NotifyStateChanged(targetId);
                return PartyOperationResult.Fail("You do not have a pending party invite.");
            }

            Party party;
            if (invite.PartyId == 0)
            {
                if (_partyByCharacter.ContainsKey(invite.InviterCharacterId))
                {
                    ulong currentId = _partyByCharacter[invite.InviterCharacterId];
                    if (!_parties.TryGetValue(currentId, out party) || party.LeaderCharacterId != invite.InviterCharacterId)
                    {
                        NotifyStateChanged(targetId);
                        return PartyOperationResult.Fail("That party invite is no longer valid.");
                    }
                }
                else
                {
                    party = new Party
                    {
                        PartyId = AllocatePartyId(),
                        LeaderCharacterId = invite.InviterCharacterId,
                    };
                    party.Members.Add(new Member
                    {
                        CharacterId = invite.InviterCharacterId,
                        Name = invite.InviterName,
                        JoinOrder = _nextJoinOrder++,
                    });
                    _parties.Add(party.PartyId, party);
                    _partyByCharacter[invite.InviterCharacterId] = party.PartyId;
                }
            }
            else if (!_parties.TryGetValue(invite.PartyId, out party) ||
                     party.LeaderCharacterId != invite.InviterCharacterId ||
                     !_partyByCharacter.TryGetValue(invite.InviterCharacterId, out ulong inviterPartyId) ||
                     inviterPartyId != party.PartyId)
            {
                NotifyStateChanged(targetId);
                return PartyOperationResult.Fail("That party invite is no longer valid.");
            }

            if (party.Members.Count >= MaxMembers)
            {
                NotifyStateChanged(targetId);
                return PartyOperationResult.Fail("The party is full.");
            }

            party.Members.Add(new Member
            {
                CharacterId = targetId,
                Name = targetName,
                JoinOrder = _nextJoinOrder++,
            });
            _partyByCharacter[targetId] = party.PartyId;

            PartySnapshot snapshot = Snapshot(party);
            NotifySnapshotMembers(snapshot);
            return PartyOperationResult.Ok($"Joined {invite.InviterName}'s party.", invite.InviterCharacterId, snapshot);
        }

        public PartyOperationResult Decline(PlayerRuntime target)
        {
            if (!TryIdentity(target, out long targetId, out _))
                return PartyOperationResult.Fail("Party decline is unavailable.");
            if (!TryTakeValidInvite(targetId, out PendingInvite invite))
            {
                NotifyStateChanged(targetId);
                return PartyOperationResult.Fail("You do not have a pending party invite.");
            }
            NotifyStateChanged(targetId);
            return PartyOperationResult.Ok("Party invite declined.", invite.InviterCharacterId);
        }

        public PartyOperationResult Leave(PlayerRuntime member)
        {
            if (!TryIdentity(member, out long characterId, out string name))
                return PartyOperationResult.Fail("Party leave is unavailable.");
            if (!TryGetParty(characterId, out Party party))
                return PartyOperationResult.Fail("You are not in a party.");

            long[] affected = MemberIds(party);
            RemoveMember(party, characterId);
            if (party.Members.Count <= 1)
            {
                long remainingId = party.Members.Count == 1 ? party.Members[0].CharacterId : 0;
                DestroyParty(party);
                NotifyCharacters(affected);
                return PartyOperationResult.Ok("Left the party. The party was disbanded.", remainingId);
            }

            if (party.LeaderCharacterId == characterId)
                party.LeaderCharacterId = OldestMemberId(party);

            PartySnapshot snapshot = Snapshot(party);
            NotifyCharacters(affected);
            return PartyOperationResult.Ok($"{name} left the party.", 0, snapshot);
        }

        public PartyOperationResult Kick(PlayerRuntime actor, long targetCharacterId)
        {
            if (!TryIdentity(actor, out long actorId, out _))
                return PartyOperationResult.Fail("Party kick is unavailable.");
            if (!TryGetParty(actorId, out Party party))
                return PartyOperationResult.Fail("You are not in a party.");
            if (party.LeaderCharacterId != actorId)
                return PartyOperationResult.Fail("Only the party leader can kick members.");
            if (targetCharacterId <= 0 || targetCharacterId == actorId)
                return PartyOperationResult.Fail("Invalid party member.");
            if (!_partyByCharacter.TryGetValue(targetCharacterId, out ulong targetPartyId) || targetPartyId != party.PartyId)
                return PartyOperationResult.Fail("That player is not in your party.");

            long[] affected = MemberIds(party);
            string targetName = FindMemberName(party, targetCharacterId);
            RemoveMember(party, targetCharacterId);
            if (party.Members.Count <= 1)
            {
                DestroyParty(party);
                NotifyCharacters(affected);
                return PartyOperationResult.Ok($"{targetName} was removed. The party was disbanded.", targetCharacterId);
            }

            PartySnapshot snapshot = Snapshot(party);
            NotifyCharacters(affected);
            return PartyOperationResult.Ok($"{targetName} was removed from the party.", targetCharacterId, snapshot);
        }

        public PartyOperationResult Disband(PlayerRuntime actor)
        {
            if (!TryIdentity(actor, out long actorId, out _))
                return PartyOperationResult.Fail("Party disband is unavailable.");
            if (!TryGetParty(actorId, out Party party))
                return PartyOperationResult.Fail("You are not in a party.");
            if (party.LeaderCharacterId != actorId)
                return PartyOperationResult.Fail("Only the party leader can disband the party.");

            PartySnapshot snapshot = Snapshot(party);
            long[] affected = MemberIds(party);
            DestroyParty(party);
            NotifyCharacters(affected);
            return PartyOperationResult.Ok("Party disbanded.", 0, snapshot);
        }

        public bool TryGetSnapshot(long characterId, out PartySnapshot snapshot)
        {
            if (TryGetParty(characterId, out Party party))
            {
                snapshot = Snapshot(party);
                return true;
            }
            snapshot = null;
            return false;
        }

        public bool TryGetPendingInvite(long targetCharacterId, out PartyInviteSnapshot invite)
        {
            invite = default;
            if (targetCharacterId <= 0 || !_inviteByTarget.TryGetValue(targetCharacterId, out PendingInvite pending))
                return false;
            if (pending.ExpiresUtc <= DateTime.UtcNow)
            {
                _inviteByTarget.Remove(targetCharacterId);
                return false;
            }

            invite = new PartyInviteSnapshot(
                pending.InviterCharacterId,
                pending.InviterName,
                pending.ExpiresUtc);
            return true;
        }

        public bool TryFindMember(long actorCharacterId, string name, out long characterId)
        {
            characterId = 0;
            if (!TryGetParty(actorCharacterId, out Party party) || string.IsNullOrWhiteSpace(name))
                return false;
            string key = name.Trim();
            for (int i = 0; i < party.Members.Count; ++i)
            {
                if (!string.Equals(party.Members[i].Name, key, StringComparison.OrdinalIgnoreCase))
                    continue;
                characterId = party.Members[i].CharacterId;
                return true;
            }
            return false;
        }

        private bool TryGetParty(long characterId, out Party party)
        {
            if (_partyByCharacter.TryGetValue(characterId, out ulong partyId) && _parties.TryGetValue(partyId, out party))
                return true;
            _partyByCharacter.Remove(characterId);
            party = null;
            return false;
        }

        private bool TryTakeValidInvite(long targetCharacterId, out PendingInvite invite)
        {
            if (_inviteByTarget.TryGetValue(targetCharacterId, out invite))
            {
                _inviteByTarget.Remove(targetCharacterId);
                if (invite.ExpiresUtc > DateTime.UtcNow)
                    return true;
            }
            invite = null;
            return false;
        }

        private ulong AllocatePartyId()
        {
            ulong id = _nextPartyId++;
            if (id == 0)
                id = _nextPartyId++;
            return id;
        }

        private PartySnapshot Snapshot(Party party)
        {
            var members = new PartyMemberSnapshot[party.Members.Count];
            for (int i = 0; i < party.Members.Count; ++i)
            {
                Member member = party.Members[i];
                members[i] = new PartyMemberSnapshot(member.CharacterId, member.Name, member.CharacterId == party.LeaderCharacterId);
            }
            return new PartySnapshot(party.PartyId, party.LeaderCharacterId, members);
        }

        private void DestroyParty(Party party)
        {
            for (int i = 0; i < party.Members.Count; ++i)
                _partyByCharacter.Remove(party.Members[i].CharacterId);
            _parties.Remove(party.PartyId);
        }

        private void RemoveMember(Party party, long characterId)
        {
            for (int i = 0; i < party.Members.Count; ++i)
            {
                if (party.Members[i].CharacterId != characterId)
                    continue;
                party.Members.RemoveAt(i);
                break;
            }
            _partyByCharacter.Remove(characterId);
            _inviteByTarget.Remove(characterId);
        }

        private static long OldestMemberId(Party party)
        {
            long id = 0;
            long best = long.MaxValue;
            for (int i = 0; i < party.Members.Count; ++i)
            {
                if (party.Members[i].JoinOrder >= best)
                    continue;
                best = party.Members[i].JoinOrder;
                id = party.Members[i].CharacterId;
            }
            return id;
        }

        private static string FindMemberName(Party party, long characterId)
        {
            for (int i = 0; i < party.Members.Count; ++i)
                if (party.Members[i].CharacterId == characterId)
                    return party.Members[i].Name;
            return "Player";
        }

        private static long[] MemberIds(Party party)
        {
            if (party == null || party.Members.Count == 0)
                return Array.Empty<long>();
            var ids = new long[party.Members.Count];
            for (int i = 0; i < party.Members.Count; ++i)
                ids[i] = party.Members[i].CharacterId;
            return ids;
        }

        private void NotifySnapshotMembers(PartySnapshot snapshot)
        {
            if (snapshot?.Members == null)
                return;
            for (int i = 0; i < snapshot.Members.Length; ++i)
                NotifyStateChanged(snapshot.Members[i].CharacterId);
        }

        private void NotifyCharacters(long[] characterIds)
        {
            if (characterIds == null)
                return;
            for (int i = 0; i < characterIds.Length; ++i)
                NotifyStateChanged(characterIds[i]);
        }

        private void NotifyStateChanged(long characterId)
        {
            if (characterId > 0)
                StateChanged?.Invoke(characterId);
        }

        private static bool TryIdentity(PlayerRuntime runtime, out long characterId, out string name)
        {
            characterId = runtime?.CharacterId.Value ?? 0;
            name = runtime?.Character?.Name ?? string.Empty;
            return characterId > 0 && !string.IsNullOrWhiteSpace(name);
        }
    }
}
