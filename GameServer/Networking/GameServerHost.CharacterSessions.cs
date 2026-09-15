using System.Diagnostics;
using System.Net;
using System.Net.Sockets;
using Game.GameServer.Runtime;
using Game.GameServer.Backend;
using Game.GameServer.Replication;
using Game.Server.Application.Abilities;
using Game.Server.Application.Connections;
using Game.Server.Application.Population;
using Game.Server.Application.Sessions;
using Game.Server.Application.World;
using Game.Server.Domain.Characters;
using Game.Server.Domain.Players;
using Game.Shared.Abilities;
using Game.Shared.Characters;
using Game.Shared.Combat;
using Game.Shared.Content;
using Game.Shared.Interactions;
using Game.Shared.Identity;
using Game.Shared.Protocol;
using Game.Shared.Sessions;
using Game.Shared.World;
using Game.Shared.WorldItems;
using Game.UnityIntegration;
using LiteNetLib;
using LiteNetLib.Utils;
using LiteNetLibManager;
using Player.Networking;
using Player.Shared;

namespace Game.GameServer.Networking;

internal sealed partial class GameServerHost
{
    // Character-session transport integration. Authoritative session/character transitions remain
    // owned by PlayerSessionService/CharacterService; this partial only adapts wire requests/results.
    private void RegisterCoreAndCharacterRequests(
        Dictionary<ushort, Action<ClientSession, uint, NetDataReader>> handlers)
    {
        RegisterRequest(handlers, CorePlayerRequestTypes.EnterGame, HandleEnterGame);
        RegisterRequest(handlers, CorePlayerRequestTypes.ClientReady,
            (session, requestId, _) => HandleClientReady(session, requestId));
        RegisterRequest(handlers, CharacterSessionRequestTypes.AuthenticateAdmission, HandleAuthenticateAdmission);
        RegisterRequest(handlers, CharacterSessionRequestTypes.CharacterList,
            (session, requestId, _) => HandleCharacterList(session, requestId));
        RegisterRequest(handlers, CharacterSessionRequestTypes.CreateCharacter, HandleCreateCharacter);
        RegisterRequest(handlers, CharacterSessionRequestTypes.DeleteCharacter, HandleDeleteCharacter);
        RegisterRequest(handlers, CharacterSessionRequestTypes.EnterCharacter, HandleEnterCharacter);
    }

    private void HandleEnterGame(ClientSession session, uint requestId, NetDataReader reader)
    {
        uint packetVersion = reader.GetPackedUInt();
        ushort playerProtocol = reader.GetUShort();
        bool compatible = packetVersion == PacketVersion &&
                          playerProtocol == PlayerEntityProtocol.Version;

        _writer.Reset();
        _writer.PutPackedUShort(ResponseMessageType);
        _writer.PutPackedUInt(requestId);
        _writer.Put(compatible ? AckSuccess : AckError);

        // LiteNetLibManager EnterGameResponseMessage
        _writer.PutPackedLong(session.Peer.Id);
        _writer.Put(false); // ServerSceneInfo.HasValue
        _writer.Put(string.Empty);
        _writer.Put(string.Empty);
        // PlayerEntityGameManager extra response payload.
        _writer.Put(PlayerEntityProtocol.Version);
        _writer.Put(_options.TickRate);
        Send(session, _writer, DeliveryMethod.ReliableUnordered);

        if (!compatible)
        {
            Console.Error.WriteLine(
                $"Protocol mismatch peer={session.Peer.Id}: packet={packetVersion}/{PacketVersion}, " +
                $"player={playerProtocol}/{PlayerEntityProtocol.Version}");
        }
    }

    private void HandleAuthenticateAdmission(ClientSession session, uint requestId, NetDataReader reader)
    {
        var request = new AdmissionAuthenticationRequestMessage();
        request.Deserialize(reader);

        if (!TryGetAuthoritativeSession(session, out PlayerSession authoritative))
        {
            SendResponse(session, requestId, AdmissionAuthenticationResponseMessage.Failed(
                (byte)PlayerSessionState.Closed,
                "session is unavailable"));
            return;
        }

        if (authoritative.HasAccount &&
            authoritative.State != PlayerSessionState.Disconnecting &&
            authoritative.State != PlayerSessionState.Closed)
        {
            SendResponse(session, requestId, new AdmissionAuthenticationResponseMessage
            {
                success = true,
                accountId = authoritative.AccountId.Value,
                sessionState = (byte)authoritative.State,
                error = string.Empty,
            });
            return;
        }

        if (authoritative.State != PlayerSessionState.Connected ||
            session.AuthenticationInFlight ||
            !_runtime.SessionService.BeginAuthentication(session.SessionHandle))
        {
            SendResponse(session, requestId, AdmissionAuthenticationResponseMessage.Failed(
                (byte)authoritative.State,
                "session is not ready for authentication"));
            return;
        }

        if (session.AdmissionAttempts >= _options.MaxAdmissionAttemptsPerSession)
        {
            _runtime.SessionService.CancelAuthentication(session.SessionHandle);
            SendResponse(session, requestId, AdmissionAuthenticationResponseMessage.Failed(
                (byte)PlayerSessionState.Connected,
                "authentication unavailable"));
            session.Peer.Disconnect();
            return;
        }

        session.AdmissionAttempts++;
        session.AuthenticationInFlight = true;
        AuthenticateAdmissionAsync(session, requestId, request.admissionToken).Forget();
    }

    private async Task AuthenticateAdmissionAsync(ClientSession session, uint requestId, string token)
    {
        AccountId accountId = default;
        Exception failure = null;
        try
        {
            accountId = await _runtime.Backend.RedeemAdmissionAsync(token, CancellationToken.None)
                .ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            failure = ex;
        }

        QueueMainThreadCompletion(() =>
        {
            if (!IsCurrent(session))
                return;

            session.AuthenticationInFlight = false;
            if (failure != null || !accountId.IsValid)
            {
                _runtime.SessionService.CancelAuthentication(session.SessionHandle);
                TryGetAuthoritativeSession(session, out PlayerSession failedSession);
                SendResponse(session, requestId, AdmissionAuthenticationResponseMessage.Failed(
                    (byte)(failedSession?.State ?? PlayerSessionState.Closed),
                    "authentication unavailable"));
                if (failure != null)
                    Console.Error.WriteLine($"Admission backend failure for peer {session.Peer.Id}: {failure.Message}");
                return;
            }

            if (!_runtime.SessionService.CompleteAuthentication(session.SessionHandle, accountId) ||
                !TryGetAuthoritativeSession(session, out PlayerSession authoritative))
            {
                _runtime.SessionService.CancelAuthentication(session.SessionHandle);
                SendResponse(session, requestId, AdmissionAuthenticationResponseMessage.Failed(
                    (byte)PlayerSessionState.Connected,
                    "account session already active"));
                return;
            }

            session.AuthenticatedAccountId = accountId.Value;
            _runtime.GameMasters.OpenSession(accountId.Value);

            SendResponse(session, requestId, new AdmissionAuthenticationResponseMessage
            {
                success = true,
                accountId = authoritative.AccountId.Value,
                sessionState = (byte)authoritative.State,
                error = string.Empty,
            });
        });
    }

    private void HandleCharacterList(ClientSession session, uint requestId)
    {
        if (!TryGetAuthoritativeSession(session, out PlayerSession authoritative) ||
            authoritative.State != PlayerSessionState.CharacterLobby)
        {
            SendResponse(session, requestId, CharacterListResponseMessage.Failed(
                (byte)(authoritative?.State ?? PlayerSessionState.Closed),
                "session is not in character lobby"));
            return;
        }

        LoadCharacterListAsync(session, requestId).Forget();
    }

    private async Task LoadCharacterListAsync(ClientSession session, uint requestId)
    {
        CharacterListResult result = null;
        Exception failure = null;
        try
        {
            result = await _runtime.SessionService
                .GetCharacterListAsync(session.SessionHandle, CancellationToken.None)
                .ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            failure = ex;
        }

        QueueMainThreadCompletion(() =>
        {
            if (!IsCurrent(session))
                return;

            TryGetAuthoritativeSession(session, out PlayerSession authoritative);
            if (failure != null || result == null || !result.Success)
            {
                SendResponse(session, requestId, CharacterListResponseMessage.Failed(
                    (byte)(authoritative?.State ?? PlayerSessionState.Closed),
                    result?.Error ?? "character list load failed"));
                if (failure != null)
                    Console.Error.WriteLine($"Character-list failure for peer {session.Peer.Id}: {failure.Message}");
                return;
            }

            CharacterSummary[] source = result.Characters ?? Array.Empty<CharacterSummary>();
            int count = Math.Min(source.Length, CharacterListResponseMessage.MaxCharacters);
            var characters = new CharacterSessionCharacterSummary[count];
            for (int i = 0; i < count; ++i)
            {
                CharacterSummary summary = source[i];
                characters[i] = new CharacterSessionCharacterSummary(
                    summary.CharacterId.Value,
                    summary.Name,
                    summary.MapId);
            }

            SendResponse(session, requestId, new CharacterListResponseMessage
            {
                success = true,
                sessionState = (byte)(authoritative?.State ?? PlayerSessionState.CharacterLobby),
                error = source.Length > count ? $"showing first {count} characters" : string.Empty,
                characters = characters,
            });
        });
    }

    private void HandleCreateCharacter(ClientSession session, uint requestId, NetDataReader reader)
    {
        var request = new CreateCharacterRequestMessage();
        request.Deserialize(reader);

        if (!TryGetAuthoritativeSession(session, out PlayerSession authoritative))
        {
            SendResponse(session, requestId, CreateCharacterResponseMessage.Failed(
                (byte)PlayerSessionState.Closed,
                (byte)CharacterCreateFailure.SessionNotFound,
                "character session not found"));
            return;
        }

        if (authoritative.State != PlayerSessionState.CharacterLobby)
        {
            SendResponse(session, requestId, CreateCharacterResponseMessage.Failed(
                (byte)authoritative.State,
                (byte)CharacterCreateFailure.InvalidSessionState,
                "session is not in character lobby"));
            return;
        }

        if (session.CharacterCreateInFlight)
        {
            SendResponse(session, requestId, CreateCharacterResponseMessage.Failed(
                (byte)authoritative.State,
                (byte)CharacterCreateFailure.InvalidSessionState,
                "character creation is already in progress"));
            return;
        }

        GameplayContentSnapshot snapshot = _runtime.Content.Snapshot;
        CharacterSpawnDefinition spawn = snapshot?.initialCharacterSpawn;
        if (spawn == null)
        {
            SendResponse(session, requestId, CreateCharacterResponseMessage.Failed(
                (byte)authoritative.State,
                (byte)CharacterCreateFailure.PersistenceFailed,
                "character creation spawn is not configured"));
            return;
        }

        CharacterLocationState initialLocation;
        try
        {
            initialLocation = new CharacterLocationState(
                spawn.mapId,
                spawn.instanceId ?? string.Empty,
                new WorldPosition(spawn.positionX, spawn.positionY, spawn.positionZ),
                spawn.yawDegrees);
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine($"Character-creation spawn configuration is invalid: {ex.Message}");
            SendResponse(session, requestId, CreateCharacterResponseMessage.Failed(
                (byte)authoritative.State,
                (byte)CharacterCreateFailure.PersistenceFailed,
                "character creation spawn is invalid"));
            return;
        }

        if (!_runtime.Spawns.TryResolveFirstSpawn(initialLocation, out CharacterLocationState resolvedInitialLocation, out string firstSpawnDetail))
        {
            SendResponse(session, requestId, CreateCharacterResponseMessage.Failed(
                (byte)authoritative.State,
                (byte)CharacterCreateFailure.PersistenceFailed,
                string.IsNullOrWhiteSpace(firstSpawnDetail) ? "baked first-spawn location is invalid" : firstSpawnDetail));
            return;
        }

        session.CharacterCreateInFlight = true;
        CreateCharacterAsync(session, requestId, request.name, request.initialAppearance, request.initialPresentation, resolvedInitialLocation).Forget();
    }

    private async Task CreateCharacterAsync(
        ClientSession session,
        uint requestId,
        string requestedName,
        CharacterAppearanceRecipe initialAppearance,
        CharacterPresentationPreferences initialPresentation,
        CharacterLocationState initialLocation)
    {
        CharacterCreateResult result = default;
        bool hasResult = false;
        Exception failure = null;
        try
        {
            result = await _runtime.SessionService
                .CreateCharacterAsync(
                    session.SessionHandle,
                    requestedName,
                    initialLocation,
                    initialAppearance,
                    initialPresentation,
                    CancellationToken.None)
                .ConfigureAwait(false);
            hasResult = true;
        }
        catch (Exception ex)
        {
            failure = ex;
        }

        QueueMainThreadCompletion(() =>
        {
            if (!IsCurrent(session))
                return;

            session.CharacterCreateInFlight = false;
            TryGetAuthoritativeSession(session, out PlayerSession authoritative);

            if (failure != null || !hasResult)
            {
                SendResponse(session, requestId, CreateCharacterResponseMessage.Failed(
                    (byte)(authoritative?.State ?? PlayerSessionState.Closed),
                    (byte)CharacterCreateFailure.PersistenceFailed,
                    "character creation failed"));
                if (failure != null)
                    Console.Error.WriteLine($"Character-creation failure for peer {session.Peer.Id}: {failure.Message}");
                return;
            }

            if (!result.Success)
            {
                SendResponse(session, requestId, CreateCharacterResponseMessage.Failed(
                    (byte)(authoritative?.State ?? PlayerSessionState.Closed),
                    (byte)result.Failure,
                    CharacterCreateFailureText(result.Failure)));
                return;
            }

            SendResponse(session, requestId, new CreateCharacterResponseMessage
            {
                success = true,
                characterId = result.CharacterId.Value,
                name = result.Name,
                sessionState = (byte)(authoritative?.State ?? PlayerSessionState.CharacterLobby),
                failure = (byte)CharacterCreateFailure.None,
                error = string.Empty,
            });

        });
    }

    private void HandleDeleteCharacter(ClientSession session, uint requestId, NetDataReader reader)
    {
        var request = new DeleteCharacterRequestMessage();
        request.Deserialize(reader);

        if (request.characterId <= 0)
        {
            SendResponse(session, requestId, DeleteCharacterResponseMessage.Failed(
                request.characterId,
                (byte)PlayerSessionState.Closed,
                (byte)CharacterDeleteFailure.InvalidCharacter,
                "character id is invalid"));
            return;
        }

        if (!TryGetAuthoritativeSession(session, out PlayerSession authoritative))
        {
            SendResponse(session, requestId, DeleteCharacterResponseMessage.Failed(
                request.characterId,
                (byte)PlayerSessionState.Closed,
                (byte)CharacterDeleteFailure.SessionNotFound,
                "character session not found"));
            return;
        }

        if (authoritative.State != PlayerSessionState.CharacterLobby)
        {
            SendResponse(session, requestId, DeleteCharacterResponseMessage.Failed(
                request.characterId,
                (byte)authoritative.State,
                (byte)CharacterDeleteFailure.InvalidSessionState,
                "session is not in character lobby"));
            return;
        }

        if (session.CharacterDeleteInFlight || session.CharacterCreateInFlight || session.CharacterLoadInFlight)
        {
            SendResponse(session, requestId, DeleteCharacterResponseMessage.Failed(
                request.characterId,
                (byte)authoritative.State,
                (byte)CharacterDeleteFailure.InvalidSessionState,
                "another character-lobby operation is already in progress"));
            return;
        }

        session.CharacterDeleteInFlight = true;
        DeleteCharacterAsync(session, requestId, new CharacterId(request.characterId)).Forget();
    }

    private async Task DeleteCharacterAsync(
        ClientSession session,
        uint requestId,
        CharacterId characterId)
    {
        CharacterDeleteResult result = default;
        bool hasResult = false;
        Exception failure = null;
        try
        {
            result = await _runtime.SessionService
                .DeleteCharacterAsync(session.SessionHandle, characterId, CancellationToken.None)
                .ConfigureAwait(false);
            hasResult = true;
        }
        catch (Exception ex)
        {
            failure = ex;
        }

        QueueMainThreadCompletion(() =>
        {
            if (!IsCurrent(session))
                return;

            session.CharacterDeleteInFlight = false;
            TryGetAuthoritativeSession(session, out PlayerSession authoritative);

            if (failure != null || !hasResult)
            {
                SendResponse(session, requestId, DeleteCharacterResponseMessage.Failed(
                    characterId.Value,
                    (byte)(authoritative?.State ?? PlayerSessionState.Closed),
                    (byte)CharacterDeleteFailure.PersistenceFailed,
                    "character deletion failed"));
                if (failure != null)
                    Console.Error.WriteLine($"Character-delete failure for peer {session.Peer.Id}: {failure.Message}");
                return;
            }

            if (!result.Success)
            {
                SendResponse(session, requestId, DeleteCharacterResponseMessage.Failed(
                    characterId.Value,
                    (byte)(authoritative?.State ?? PlayerSessionState.Closed),
                    (byte)result.Failure,
                    CharacterDeleteFailureText(result.Failure)));
                return;
            }

            SendResponse(session, requestId, new DeleteCharacterResponseMessage
            {
                success = true,
                characterId = result.CharacterId.Value,
                name = result.Name,
                sessionState = (byte)(authoritative?.State ?? PlayerSessionState.CharacterLobby),
                failure = (byte)CharacterDeleteFailure.None,
                error = string.Empty,
            });
        });
    }

    private void HandleEnterCharacter(ClientSession session, uint requestId, NetDataReader reader)
    {
        var request = new EnterCharacterRequestMessage();
        request.Deserialize(reader);

        if (request.characterId <= 0)
        {
            SendResponse(session, requestId, EnterCharacterResponseMessage.Failed(
                request.characterId,
                (byte)PlayerSessionState.Closed,
                (byte)CharacterSelectFailure.InvalidSessionState,
                "character id is invalid"));
            return;
        }

        if (!TryGetAuthoritativeSession(session, out PlayerSession authoritative))
        {
            SendResponse(session, requestId, EnterCharacterResponseMessage.Failed(
                request.characterId,
                (byte)PlayerSessionState.Closed,
                (byte)CharacterSelectFailure.SessionNotFound,
                "character session not found"));
            return;
        }

        CharacterId characterId = new CharacterId(request.characterId);
        if (authoritative.State == PlayerSessionState.InWorld &&
            authoritative.HasSelectedCharacter &&
            authoritative.SelectedCharacterId == characterId)
        {
            SendResponse(session, requestId, SuccessfulEnterResponse(request.characterId, authoritative.State, true));
            return;
        }

        if (authoritative.State == PlayerSessionState.AwaitingWorldEntry &&
            authoritative.HasSelectedCharacter &&
            authoritative.SelectedCharacterId == characterId)
        {
            SendResponse(session, requestId, SuccessfulEnterResponse(request.characterId, authoritative.State, false));
            return;
        }

        if (authoritative.State != PlayerSessionState.CharacterLobby || session.CharacterLoadInFlight)
        {
            SendResponse(session, requestId, EnterCharacterResponseMessage.Failed(
                request.characterId,
                (byte)authoritative.State,
                (byte)CharacterSelectFailure.InvalidSessionState,
                "character session is not ready to load this character"));
            return;
        }

        session.CharacterLoadInFlight = true;
        LoadCharacterForEntryAsync(session, requestId, characterId).Forget();
    }

    private async Task LoadCharacterForEntryAsync(ClientSession session, uint requestId, CharacterId characterId)
    {
        CharacterSelectResult result = default;
        Exception failure = null;
        try
        {
            result = await _runtime.SessionService
                .SelectCharacterAsync(session.SessionHandle, characterId, CancellationToken.None)
                .ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            failure = ex;
        }

        QueueMainThreadCompletion(() =>
        {
            if (!IsCurrent(session))
                return;

            session.CharacterLoadInFlight = false;
            if (!TryGetAuthoritativeSession(session, out PlayerSession authoritative))
            {
                SendResponse(session, requestId, EnterCharacterResponseMessage.Failed(
                    characterId.Value,
                    (byte)PlayerSessionState.Closed,
                    (byte)CharacterSelectFailure.SessionChangedDuringLoad,
                    "character session changed while loading"));
                return;
            }

            if (failure != null)
            {
                SendResponse(session, requestId, EnterCharacterResponseMessage.Failed(
                    characterId.Value,
                    (byte)authoritative.State,
                    (byte)CharacterSelectFailure.CharacterLoadFailed,
                    "character enter failed"));
                Console.Error.WriteLine($"Character-load failure for peer {session.Peer.Id}: {failure.Message}");
                return;
            }

            if (!result.Success)
            {
                SendResponse(session, requestId, EnterCharacterResponseMessage.Failed(
                    characterId.Value,
                    (byte)authoritative.State,
                    (byte)result.Failure,
                    CharacterSelectFailureText(result.Failure)));
                return;
            }

            if (!authoritative.HasSelectedCharacter ||
                authoritative.SelectedCharacterId != characterId ||
                authoritative.State != PlayerSessionState.AwaitingWorldEntry)
            {
                SendResponse(session, requestId, EnterCharacterResponseMessage.Failed(
                    characterId.Value,
                    (byte)authoritative.State,
                    (byte)CharacterSelectFailure.InvalidSessionState,
                    "character session is not awaiting world entry"));
                return;
            }

            SendResponse(session, requestId, SuccessfulEnterResponse(characterId.Value, authoritative.State, false));
        });
    }

    private void HandleClientReady(ClientSession session, uint requestId)
    {
        if (!TryGetAuthoritativeSession(session, out PlayerSession authoritative) ||
            authoritative.State != PlayerSessionState.AwaitingWorldEntry ||
            authoritative.Runtime == null ||
            session.Entity != null ||
            session.Ready)
        {
            SendBareResponse(session, requestId, AckError);
            return;
        }

        CharacterLocationState requestedEntry = authoritative.Runtime.Location;
        if (!_runtime.Spawns.TryValidateEntryLocation(requestedEntry, out CharacterLocationState resolvedEntry, out string spawnDetail))
        {
            Console.Error.WriteLine($"World entry spawn validation failed for peer {session.Peer.Id}: {spawnDetail}");
            SendBareResponse(session, requestId, AckError);
            return;
        }
        if (resolvedEntry != requestedEntry)
        {
            authoritative.Runtime.UpdateLocation(resolvedEntry);
        }

        uint objectId = NextObjectId();
        ushort generation = NextGeneration();
        var entity = new ServerPlayerEntity(
            objectId,
            generation,
            session.Peer.Id,
            authoritative.Runtime,
            _runtime.Maps);

        session.Entity = entity;

        PlayerWorldLifecycleResult adopted = _runtime.WorldLifecycle.AdoptExternalEnter(
            session.SessionHandle,
            new PlayerWorldHandle(objectId));
        if (!adopted.Success)
        {
            session.Entity = null;
            Console.Error.WriteLine(
                $"World adoption refused: peer={session.Peer.Id}, character={authoritative.SelectedCharacterId.Value}, status={adopted.Status}");
            SendBareResponse(session, requestId, AckError);
            return;
        }

        session.Ready = true;
        RegisterReadySessionIndexes(session);
        ActivatePlayerGameplayRuntime(session, authoritative.Runtime);
        SendCurrentBackendStatus(session);

        // Preserve the canonical Ready contract: the request is only successful after
        // authoritative session adoption is committed to InWorld.
        SendBareResponse(session, requestId, AckSuccess);

        // World-item presentation must never gate canonical player admission. If loot AOI
        // initialization fails, the player still enters normally and the fault is isolated
        // to the optional world-item replication slice.
        TryInitializeWorldItemInterestAfterReady(session);

        // Build the authoritative observer graph before sending PlayerEntity baselines.
        // Only same-map/instance entities inside AOI are exposed to this client, and only
        // observers inside AOI receive this new entity. Self visibility remains mandatory.
        ApplyInterestChanges(_worldInterest.Register(session));
        ReconcilePopulationObserver(session);

        // Push immutable/public gameplay references before compact owner state so item,
        // equipment, status and ability IDs can be resolved client-side without strings
        // on every gameplay packet. Changed world-interactable state follows on the same
        // reliable stream for doors/gates and other non-default runtime state.
        SendOwnerBaselinesAfterReady(session);
        SendChangedWorldInteractableStatesAfterReady(session);

    }

    private static string CharacterCreateFailureText(CharacterCreateFailure failure) => failure switch
    {
        CharacterCreateFailure.SessionNotFound => "character session not found",
        CharacterCreateFailure.InvalidSessionState => "session is not in character lobby",
        CharacterCreateFailure.InvalidName => "name must be 1-16 letters with optional single spaces",
        CharacterCreateFailure.InvalidAppearance => "character appearance data is invalid",
        CharacterCreateFailure.NameAlreadyExists => "character name already exists",
        CharacterCreateFailure.CharacterLimitReached => "character limit reached",
        CharacterCreateFailure.SessionChangedDuringCreate => "character session changed during creation",
        _ => "character creation failed",
    };

    private static string CharacterDeleteFailureText(CharacterDeleteFailure failure) => failure switch
    {
        CharacterDeleteFailure.SessionNotFound => "character session not found",
        CharacterDeleteFailure.InvalidSessionState => "session is not in character lobby",
        CharacterDeleteFailure.InvalidCharacter => "character id is invalid",
        CharacterDeleteFailure.CharacterNotFoundOrNotOwned => "character was not found or is not owned by this account",
        CharacterDeleteFailure.CharacterActive => "character is active or its authority lease is still recovering",
        CharacterDeleteFailure.GuildOwner => "guild owner must disband the guild before deleting this character",
        CharacterDeleteFailure.SessionChangedDuringDelete => "character session changed during deletion",
        _ => "character deletion failed",
    };

    private static string CharacterSelectFailureText(CharacterSelectFailure failure) => failure switch
    {
        CharacterSelectFailure.SessionNotFound => "character session not found",
        CharacterSelectFailure.InvalidSessionState => "character session is not ready",
        CharacterSelectFailure.CharacterAlreadyActive => "character is already active",
        CharacterSelectFailure.CharacterNotFoundOrNotOwned => "character was not found or is not owned by this account",
        CharacterSelectFailure.CharacterDataInvalid => "character data is invalid",
        CharacterSelectFailure.CharacterLoadFailed => "character load failed",
        CharacterSelectFailure.SessionChangedDuringLoad => "character session changed during load",
        _ => "character selection failed",
    };
}
