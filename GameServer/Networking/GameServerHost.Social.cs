using System;
using System.Collections.Generic;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using Game.GameServer.Backend;
using Game.GameServer.Runtime;
using Game.Server.Application.Social;
using Game.Server.Domain.Players;
using Game.Shared.Chat;
using LiteNetLib;
using Player.Networking;

namespace Game.GameServer.Networking;

internal sealed partial class GameServerHost
{
    private PartyService _partyService;
    private GuildService _guildService;

    private PartyService PartySocial => _partyService ??= new PartyService();
    private GuildService GuildSocial => _guildService ??= new GuildService(
        new BackendGuildRepository(_runtime.Backend),
        _runtime.Leases);

    private bool TryHandleSocialCommand(ClientSession session, PlayerRuntime actor, string text)
    {
        if (string.IsNullOrWhiteSpace(text) || text[0] != '/')
            return false;

        if (StartsWithCommand(text, "/party", out string partyArgs))
        {
            HandlePartyCommand(session, actor, partyArgs);
            return true;
        }

        if (StartsWithCommand(text, "/guild", out string guildArgs))
        {
            RunGuildCommandAsync(session, actor, guildArgs).Forget();
            return true;
        }

        return false;
    }

    private void HandlePartyCommand(ClientSession session, PlayerRuntime actor, string args)
    {
        string command;
        string remainder;
        SplitCommand(args, out command, out remainder);

        if (command.Length == 0 || string.Equals(command, "help", StringComparison.OrdinalIgnoreCase))
        {
            SendSystemChat(session, "Party: /party invite NAME, /party accept, /party decline, /party list, /party leave, /party kick NAME, /party disband. Chat: /p MESSAGE.");
            return;
        }

        if (string.Equals(command, "invite", StringComparison.OrdinalIgnoreCase))
        {
            if (!TryFindOnlinePlayerByName(remainder, out ClientSession targetSession, out PlayerRuntime targetRuntime))
            {
                SendSystemChat(session, $"Player '{remainder}' is not online on this GameServer.");
                return;
            }

            PartyOperationResult result = PartySocial.Invite(actor, targetRuntime);
            SendSystemChat(session, result.Message);
            if (result.Success)
            {
                string actorName = actor.Character?.Name ?? "Player";
                SendSystemChat(targetSession, $"{actorName} invited you to a party. Type /party accept or /party decline.");
            }
            return;
        }

        if (string.Equals(command, "accept", StringComparison.OrdinalIgnoreCase))
        {
            PartyOperationResult result = PartySocial.Accept(actor);
            SendSystemChat(session, result.Message);
            if (!result.Success)
                return;

            string actorName = actor.Character?.Name ?? "Player";
            if (result.Party != null)
                BroadcastSystemToParty(result.Party, $"{actorName} joined the party.", exceptCharacterId: actor.CharacterId.Value);
            return;
        }

        if (string.Equals(command, "decline", StringComparison.OrdinalIgnoreCase))
        {
            PartyOperationResult result = PartySocial.Decline(actor);
            SendSystemChat(session, result.Message);
            if (result.Success && result.OtherCharacterId > 0)
            {
                ClientSession inviter = FindIndexedReadySessionByCharacterId(result.OtherCharacterId);
                if (inviter != null)
                    SendSystemChat(inviter, $"{actor.Character?.Name ?? "Player"} declined your party invite.");
            }
            return;
        }

        if (string.Equals(command, "list", StringComparison.OrdinalIgnoreCase) ||
            string.Equals(command, "info", StringComparison.OrdinalIgnoreCase))
        {
            if (!PartySocial.TryGetSnapshot(actor.CharacterId.Value, out PartySnapshot snapshot))
            {
                SendSystemChat(session, "You are not in a party.");
                return;
            }
            SendSystemChat(session, FormatParty(snapshot));
            return;
        }

        if (string.Equals(command, "leave", StringComparison.OrdinalIgnoreCase))
        {
            string actorName = actor.Character?.Name ?? "Player";
            PartyOperationResult result = PartySocial.Leave(actor);
            SendSystemChat(session, result.Message);
            if (result.Success && result.Party != null)
                BroadcastSystemToParty(result.Party, $"{actorName} left the party.");
            else if (result.Success && result.OtherCharacterId > 0)
            {
                ClientSession remaining = FindIndexedReadySessionByCharacterId(result.OtherCharacterId);
                if (remaining != null)
                    SendSystemChat(remaining, "The party was disbanded because only one member remained.");
            }
            return;
        }

        if (string.Equals(command, "kick", StringComparison.OrdinalIgnoreCase))
        {
            if (!PartySocial.TryFindMember(actor.CharacterId.Value, remainder, out long targetCharacterId))
            {
                SendSystemChat(session, $"'{remainder}' is not in your party.");
                return;
            }

            string targetName = remainder.Trim();
            PartyOperationResult result = PartySocial.Kick(actor, targetCharacterId);
            SendSystemChat(session, result.Message);
            if (!result.Success)
                return;

            ClientSession target = FindIndexedReadySessionByCharacterId(targetCharacterId);
            if (target != null)
                SendSystemChat(target, "You were removed from the party.");
            if (result.Party != null)
                BroadcastSystemToParty(result.Party, $"{targetName} was removed from the party.");
            return;
        }

        if (string.Equals(command, "disband", StringComparison.OrdinalIgnoreCase))
        {
            PartyOperationResult result = PartySocial.Disband(actor);
            if (!result.Success)
            {
                SendSystemChat(session, result.Message);
                return;
            }
            if (result.Party != null)
                BroadcastSystemToParty(result.Party, "The party was disbanded.");
            return;
        }

        SendSystemChat(session, "Unknown party command. Type /party help.");
    }

    private async Task RunGuildCommandAsync(ClientSession session, PlayerRuntime actor, string args)
    {
        string command;
        string remainder;
        SplitCommand(args, out command, out remainder);

        if (command.Length == 0 || string.Equals(command, "help", StringComparison.OrdinalIgnoreCase))
        {
            QueueMainThreadCompletion(() =>
            {
                if (IsCurrent(session))
                    SendSystemChat(session, "Guild: /guild create NAME, /guild invite NAME, /guild accept, /guild decline, /guild list, /guild leave, /guild kick NAME, /guild disband. Chat: /g MESSAGE.");
            });
            return;
        }

        try
        {
            if (string.Equals(command, "create", StringComparison.OrdinalIgnoreCase))
            {
                GuildOperationResult result = await GuildSocial.CreateAsync(actor, remainder, CancellationToken.None).ConfigureAwait(false);
                QueueGuildResult(session, result, null);
                return;
            }

            if (string.Equals(command, "invite", StringComparison.OrdinalIgnoreCase))
            {
                ClientSession targetSession = null;
                PlayerRuntime targetRuntime = null;
                await RunOnMainThreadAsync(() =>
                {
                    TryFindOnlinePlayerByName(remainder, out targetSession, out targetRuntime);
                }).ConfigureAwait(false);

                if (targetSession == null || targetRuntime == null)
                {
                    QueueMainThreadCompletion(() =>
                    {
                        if (IsCurrent(session))
                            SendSystemChat(session, $"Player '{remainder}' is not online on this GameServer.");
                    });
                    return;
                }

                GuildOperationResult result = await GuildSocial.InviteAsync(actor, targetRuntime, CancellationToken.None).ConfigureAwait(false);
                QueueMainThreadCompletion(() =>
                {
                    if (!IsCurrent(session))
                        return;
                    SendSystemChat(session, result.Message);
                    if (result.Success && IsCurrent(targetSession))
                    {
                        string actorName = actor.Character?.Name ?? "Player";
                        string guildName = result.Guild?.Name ?? "the guild";
                        SendSystemChat(targetSession, $"{actorName} invited you to guild '{guildName}'. Type /guild accept or /guild decline.");
                    }
                });
                return;
            }

            if (string.Equals(command, "accept", StringComparison.OrdinalIgnoreCase))
            {
                string actorName = actor.Character?.Name ?? "Player";
                GuildOperationResult result = await GuildSocial.AcceptAsync(actor, CancellationToken.None).ConfigureAwait(false);
                QueueMainThreadCompletion(() =>
                {
                    if (!IsCurrent(session))
                        return;
                    SendSystemChat(session, result.Message);
                    if (result.Success && result.Guild != null)
                        BroadcastSystemToGuild(result.Guild, $"{actorName} joined the guild.", exceptCharacterId: actor.CharacterId.Value);
                });
                return;
            }

            if (string.Equals(command, "decline", StringComparison.OrdinalIgnoreCase))
            {
                GuildOperationResult result = GuildSocial.Decline(actor);
                QueueMainThreadCompletion(() =>
                {
                    if (!IsCurrent(session))
                        return;
                    SendSystemChat(session, result.Message);
                    if (result.Success && result.OtherCharacterId > 0)
                    {
                        ClientSession inviter = FindIndexedReadySessionByCharacterId(result.OtherCharacterId);
                        if (inviter != null)
                            SendSystemChat(inviter, $"{actor.Character?.Name ?? "Player"} declined your guild invite.");
                    }
                });
                return;
            }

            if (string.Equals(command, "list", StringComparison.OrdinalIgnoreCase) ||
                string.Equals(command, "info", StringComparison.OrdinalIgnoreCase))
            {
                GuildSnapshot guild = await GuildSocial.GetGuildForChatAsync(actor, CancellationToken.None).ConfigureAwait(false);
                QueueMainThreadCompletion(() =>
                {
                    if (!IsCurrent(session))
                        return;
                    SendSystemChat(session, guild == null ? "You are not in a guild." : FormatGuild(guild));
                });
                return;
            }

            if (string.Equals(command, "leave", StringComparison.OrdinalIgnoreCase))
            {
                string actorName = actor.Character?.Name ?? "Player";
                GuildOperationResult result = await GuildSocial.LeaveAsync(actor, CancellationToken.None).ConfigureAwait(false);
                QueueMainThreadCompletion(() =>
                {
                    if (!IsCurrent(session))
                        return;
                    SendSystemChat(session, result.Message);
                    if (result.Success && result.Guild != null)
                        BroadcastSystemToGuild(result.Guild, $"{actorName} left the guild.");
                });
                return;
            }

            if (string.Equals(command, "kick", StringComparison.OrdinalIgnoreCase))
            {
                GuildSnapshot before = await GuildSocial.GetGuildForChatAsync(actor, CancellationToken.None).ConfigureAwait(false);
                if (!TryFindGuildMember(before, remainder, out long targetCharacterId, out string targetName))
                {
                    QueueMainThreadCompletion(() =>
                    {
                        if (IsCurrent(session))
                            SendSystemChat(session, $"'{remainder}' is not in your guild.");
                    });
                    return;
                }

                GuildOperationResult result = await GuildSocial.KickAsync(actor, targetCharacterId, CancellationToken.None).ConfigureAwait(false);
                QueueMainThreadCompletion(() =>
                {
                    if (!IsCurrent(session))
                        return;
                    SendSystemChat(session, result.Message);
                    if (!result.Success)
                        return;
                    ClientSession target = FindIndexedReadySessionByCharacterId(targetCharacterId);
                    if (target != null)
                        SendSystemChat(target, $"You were removed from guild '{before?.Name ?? "guild"}'.");
                    if (result.Guild != null)
                        BroadcastSystemToGuild(result.Guild, $"{targetName} was removed from the guild.");
                });
                return;
            }

            if (string.Equals(command, "disband", StringComparison.OrdinalIgnoreCase))
            {
                GuildOperationResult result = await GuildSocial.DisbandAsync(actor, CancellationToken.None).ConfigureAwait(false);
                QueueMainThreadCompletion(() =>
                {
                    if (!IsCurrent(session))
                        return;
                    if (!result.Success)
                    {
                        SendSystemChat(session, result.Message);
                        return;
                    }
                    if (result.Guild != null)
                        BroadcastSystemToGuild(result.Guild, "The guild was disbanded.");
                    else
                        SendSystemChat(session, "Guild disbanded.");
                });
                return;
            }

            QueueMainThreadCompletion(() =>
            {
                if (IsCurrent(session))
                    SendSystemChat(session, "Unknown guild command. Type /guild help.");
            });
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine($"Guild command failed for character {actor?.CharacterId.Value ?? 0}: {ex.Message}");
            QueueMainThreadCompletion(() =>
            {
                if (IsCurrent(session))
                    SendSystemChat(session, "Guild service is temporarily unavailable.");
            });
        }
    }

    private void SendPartyChat(ClientSession sender, PlayerRuntime runtime, string senderName, string text)
    {
        if (!PartySocial.TryGetSnapshot(runtime.CharacterId.Value, out PartySnapshot party))
        {
            SendSystemChat(sender, "You are not in a party.");
            return;
        }

        var delivery = new PlayerChatDeliveryMessage
        {
            channel = (byte)ChatChannel.Party,
            sender = senderName,
            target = string.Empty,
            message = text,
            serverUtcTicks = DateTime.UtcNow.Ticks,
        };

        for (int i = 0; i < party.Members.Length; ++i)
        {
            ClientSession recipient = FindIndexedReadySessionByCharacterId(party.Members[i].CharacterId);
            if (recipient != null)
                SendClientMessage(recipient, PlayerChatMessageTypes.Deliver, delivery, DeliveryMethod.ReliableOrdered);
        }
    }

    private async Task SendGuildChatAsync(ClientSession sender, PlayerRuntime runtime, string senderName, string text)
    {
        GuildSnapshot guild;
        try
        {
            guild = await GuildSocial.GetGuildForChatAsync(runtime, CancellationToken.None).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine($"Guild chat lookup failed for character {runtime.CharacterId.Value}: {ex.Message}");
            QueueMainThreadCompletion(() =>
            {
                if (IsCurrent(sender))
                    SendSystemChat(sender, "Guild service is temporarily unavailable.");
            });
            return;
        }

        QueueMainThreadCompletion(() =>
        {
            if (!IsCurrent(sender))
                return;
            if (guild == null)
            {
                SendSystemChat(sender, "You are not in a guild.");
                return;
            }

            var delivery = new PlayerChatDeliveryMessage
            {
                channel = (byte)ChatChannel.Guild,
                sender = senderName,
                target = string.Empty,
                message = text,
                serverUtcTicks = DateTime.UtcNow.Ticks,
            };

            for (int i = 0; i < guild.Members.Length; ++i)
            {
                ClientSession recipient = FindIndexedReadySessionByCharacterId(guild.Members[i].CharacterId);
                if (recipient != null)
                    SendClientMessage(recipient, PlayerChatMessageTypes.Deliver, delivery, DeliveryMethod.ReliableOrdered);
            }
        });
    }

    private bool TryFindOnlinePlayerByName(string rawName, out ClientSession session, out PlayerRuntime runtime)
    {
        session = null;
        runtime = null;
        string name = (rawName ?? string.Empty).Trim().Trim('"');
        if (name.Length == 0)
            return false;

        foreach (ClientSession candidate in _readySessionsByCharacterId.Values)
        {
            if (!IsCurrent(candidate) || candidate.Entity?.Runtime == null)
                continue;
            PlayerRuntime candidateRuntime = candidate.Entity.Runtime;
            string candidateName = candidateRuntime.Character?.Name ?? string.Empty;
            if (!string.Equals(candidateName, name, StringComparison.OrdinalIgnoreCase))
                continue;
            session = candidate;
            runtime = candidateRuntime;
            return true;
        }
        return false;
    }

    private void BroadcastSystemToParty(PartySnapshot party, string text, long exceptCharacterId = 0)
    {
        if (party == null)
            return;
        for (int i = 0; i < party.Members.Length; ++i)
        {
            long characterId = party.Members[i].CharacterId;
            if (characterId == exceptCharacterId)
                continue;
            ClientSession recipient = FindIndexedReadySessionByCharacterId(characterId);
            if (recipient != null)
                SendSystemChat(recipient, text);
        }
    }

    private void BroadcastSystemToGuild(GuildSnapshot guild, string text, long exceptCharacterId = 0)
    {
        if (guild == null)
            return;
        for (int i = 0; i < guild.Members.Length; ++i)
        {
            long characterId = guild.Members[i].CharacterId;
            if (characterId == exceptCharacterId)
                continue;
            ClientSession recipient = FindIndexedReadySessionByCharacterId(characterId);
            if (recipient != null)
                SendSystemChat(recipient, text);
        }
    }

    private static string FormatParty(PartySnapshot party)
    {
        var builder = new StringBuilder(128);
        builder.Append("Party (").Append(party.Members.Length).Append('/').Append(PartyService.MaxMembers).Append("): ");
        for (int i = 0; i < party.Members.Length; ++i)
        {
            if (i > 0) builder.Append(", ");
            PartyMemberSnapshot member = party.Members[i];
            if (member.IsLeader) builder.Append('*');
            builder.Append(member.Name);
        }
        return builder.ToString();
    }

    private static string FormatGuild(GuildSnapshot guild)
    {
        var builder = new StringBuilder(256);
        builder.Append("Guild '").Append(guild.Name).Append("' (").Append(guild.Members.Length).Append("): ");
        for (int i = 0; i < guild.Members.Length; ++i)
        {
            if (i > 0) builder.Append(", ");
            GuildMemberSnapshot member = guild.Members[i];
            if (member.Role == GuildMemberRole.Owner) builder.Append('*');
            builder.Append(member.Name);
        }
        return builder.ToString();
    }

    private static bool TryFindGuildMember(GuildSnapshot guild, string rawName, out long characterId, out string name)
    {
        characterId = 0;
        name = string.Empty;
        if (guild == null)
            return false;
        string key = (rawName ?? string.Empty).Trim().Trim('"');
        for (int i = 0; i < guild.Members.Length; ++i)
        {
            GuildMemberSnapshot member = guild.Members[i];
            if (!string.Equals(member.Name, key, StringComparison.OrdinalIgnoreCase))
                continue;
            characterId = member.CharacterId;
            name = member.Name;
            return true;
        }
        return false;
    }

    private static bool StartsWithCommand(string text, string command, out string args)
    {
        if (string.Equals(text, command, StringComparison.OrdinalIgnoreCase))
        {
            args = string.Empty;
            return true;
        }
        if (text.Length > command.Length &&
            text.StartsWith(command, StringComparison.OrdinalIgnoreCase) &&
            char.IsWhiteSpace(text[command.Length]))
        {
            args = text.Substring(command.Length).Trim();
            return true;
        }
        args = string.Empty;
        return false;
    }

    private static void SplitCommand(string args, out string command, out string remainder)
    {
        string prepared = (args ?? string.Empty).Trim();
        int split = prepared.IndexOf(' ');
        if (split < 0)
        {
            command = prepared;
            remainder = string.Empty;
            return;
        }
        command = prepared.Substring(0, split).Trim();
        remainder = prepared.Substring(split + 1).Trim().Trim('"');
    }

    private Task RunOnMainThreadAsync(Action action)
    {
        var completion = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        if (!QueueMainThreadCompletion(() =>
            {
                try
                {
                    action();
                    completion.TrySetResult(true);
                }
                catch (Exception ex)
                {
                    completion.TrySetException(ex);
                }
            }))
        {
            completion.TrySetException(new InvalidOperationException("Server completion queue is stopping."));
        }
        return completion.Task;
    }

    private void QueueGuildResult(ClientSession session, GuildOperationResult result, Action<GuildOperationResult> onSuccess)
    {
        QueueMainThreadCompletion(() =>
        {
            if (!IsCurrent(session))
                return;
            SendSystemChat(session, result.Message);
            if (result.Success)
                onSuccess?.Invoke(result);
        });
    }
}
