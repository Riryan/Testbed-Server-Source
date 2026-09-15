using System.Text.Json;
using Game.Shared.Authentication;
using Game.Shared.Backend;
using Game.Shared.Characters;
using Game.Shared.Content;
using Game.Shared.Protocol;
using Game.Shared.Progression;
using SQLite;

namespace Game.BackendServer;

internal sealed partial class BackendDatabase
{
    // Character list/load/create/delete/checkpoint persistence domain and durable state codecs.
    public BackendCharacterListResponse ListCharacters(long accountId)
    {
        if (accountId <= 0)
            return FailedCharacterList("invalid account");

        return Execute(conn =>
        {
            List<CharacterSummaryRow> rows = conn.Query<CharacterSummaryRow>(
                "SELECT characterId, name, mapId FROM characters WHERE accountId=? ORDER BY characterId ASC",
                accountId);

            var summaries = new BackendCharacterSummaryDto[rows.Count];
            for (int i = 0; i < rows.Count; ++i)
            {
                CharacterSummaryRow row = rows[i];
                summaries[i] = new BackendCharacterSummaryDto
                {
                    characterId = row.characterId,
                    name = row.name,
                    mapId = row.mapId,
                };
            }

            return new BackendCharacterListResponse
            {
                success = true,
                error = string.Empty,
                characters = summaries,
            };
        });
    }

    public bool OwnsCharacter(long accountId, long characterId)
    {
        if (accountId <= 0 || characterId <= 0)
            return false;

        return Execute(conn =>
            conn.ExecuteScalar<int>(
                "SELECT COUNT(1) FROM characters WHERE characterId=? AND accountId=?",
                characterId,
                accountId) > 0);
    }

    public BackendCharacterLoadResponse LoadCharacter(long accountId, long characterId)
    {
        if (accountId <= 0 || characterId <= 0)
            return FailedCharacterLoad("invalid character request");

        return Execute(conn =>
        {
            CharacterRow row = conn.FindWithQuery<CharacterRow>(
                "SELECT * FROM characters WHERE characterId=? AND accountId=? LIMIT 1",
                characterId,
                accountId);

            return new BackendCharacterLoadResponse
            {
                success = true,
                found = row != null,
                error = string.Empty,
                character = row == null ? null : ToDto(row),
            };
        });
    }

    public BackendCharacterCreateResponse TryCreateCharacter(
        long accountId,
        string requestedName,
        BackendCharacterLocationDto initialLocation,
        CharacterAppearanceRecipe initialAppearance,
        CharacterPresentationPreferences initialPresentation,
        int maxCharactersForAccount,
        GameplayContentSnapshot content)
    {
        string name = CharacterNamePolicy.Normalize(requestedName);
        CharacterAppearanceRecipe appearance =
            initialAppearance?.Clone() ?? CharacterAppearanceRecipe.CreateDefault();
        CharacterPresentationPreferences presentation =
            initialPresentation?.Clone() ?? CharacterPresentationPreferences.CreateDefault();

        if (!appearance.IsValid(out _))
            return CharacterCreateFailed(CharacterCreateFailure.InvalidAppearance, "character appearance data is invalid");
        if (!presentation.IsValid(out _))
            return CharacterCreateFailed(CharacterCreateFailure.InvalidPresentation, "character movement presentation data is invalid");

        if (accountId <= 0 ||
            !CharacterNamePolicy.IsAllowed(name) ||
            maxCharactersForAccount <= 0 ||
            !IsValidLocation(initialLocation) ||
            !GameplayContentSnapshotCache.TryValidate(content, out _))
        {
            return CharacterCreateFailed(CharacterCreateFailure.InvalidName, "character creation unavailable");
        }

        string key = NormalizeCharacterNameKey(name);
        BackendCharacterCreateResponse result = CharacterCreateFailed(
            CharacterCreateFailure.PersistenceFailed,
            "character creation unavailable");

        try
        {
            Execute(conn =>
            {
                conn.RunInTransaction(() =>
                {
                    if (conn.ExecuteScalar<int>(
                            "SELECT COUNT(1) FROM characters WHERE nameKey=?", key) > 0)
                    {
                        result = CharacterCreateFailed(
                            CharacterCreateFailure.NameAlreadyExists,
                            "character name unavailable");
                        return;
                    }

                    int ownedCount = conn.ExecuteScalar<int>(
                        "SELECT COUNT(1) FROM characters WHERE accountId=?", accountId);
                    if (ownedCount >= maxCharactersForAccount)
                    {
                        result = CharacterCreateFailed(
                            CharacterCreateFailure.CharacterLimitReached,
                            "character limit reached");
                        return;
                    }

                    long now = DateTime.UtcNow.Ticks;
                    var row = new CharacterRow
                    {
                        accountId = accountId,
                        name = name,
                        nameKey = key,
                        mapId = initialLocation.mapId,
                        instanceId = initialLocation.instanceId ?? string.Empty,
                        positionX = initialLocation.positionX,
                        positionY = initialLocation.positionY,
                        positionZ = initialLocation.positionZ,
                        yawDegrees = initialLocation.yawDegrees,
                        revision = 0,
                        schemaVersion = CharacterPersistenceSchema.CurrentVersion,
                        resourceRevision = 0,
                        resourceStateJson = "[]",
                        appearanceStateJson = JsonSerializer.Serialize(appearance, StateJsonOptions),
                        presentationStateJson = JsonSerializer.Serialize(presentation, StateJsonOptions),
                        progressionStateJson = JsonSerializer.Serialize(CharacterProgressionState.CreateDefault(), StateJsonOptions),
                        createdUtcTicks = now,
                        updatedUtcTicks = now,
                    };

                    if (conn.Insert(row) == 1 && row.characterId > 0)
                    {
                        InitializePlayerSystems(conn, row.characterId, content, now);
                        result = new BackendCharacterCreateResponse
                        {
                            success = true,
                            failure = (byte)CharacterCreateFailure.None,
                            characterId = row.characterId,
                            error = string.Empty,
                        };
                    }
                });
            });
        }
        catch (SQLiteException)
        {
            // Treat the unique index as authoritative if two requests raced.
            result = Execute(conn =>
                conn.ExecuteScalar<int>(
                    "SELECT COUNT(1) FROM characters WHERE nameKey=?", key) > 0
                    ? CharacterCreateFailed(
                        CharacterCreateFailure.NameAlreadyExists,
                        "character name unavailable")
                    : CharacterCreateFailed(
                        CharacterCreateFailure.PersistenceFailed,
                        "character creation unavailable"));
        }

        return result;
    }

    public BackendCharacterDeleteResponse TryArchiveDeleteCharacter(long accountId, long characterId)
    {
        if (accountId <= 0 || characterId <= 0)
            return CharacterDeleteFailed(CharacterDeleteFailure.InvalidCharacter, "invalid character delete request");

        return Execute(conn =>
        {
            BackendCharacterDeleteResponse response = CharacterDeleteFailed(
                CharacterDeleteFailure.PersistenceFailed,
                "character deletion unavailable");

            conn.RunInTransaction(() =>
            {
                CharacterRow character = conn.FindWithQuery<CharacterRow>(
                    "SELECT * FROM characters WHERE characterId=? AND accountId=? LIMIT 1",
                    characterId,
                    accountId);
                if (character == null)
                {
                    response = CharacterDeleteFailed(
                        CharacterDeleteFailure.CharacterNotFoundOrNotOwned,
                        "character was not found or is not owned by this account");
                    return;
                }

                if (conn.Find<DeletedCharacterRow>(characterId) != null)
                {
                    response = CharacterDeleteFailed(
                        CharacterDeleteFailure.PersistenceFailed,
                        "character archive already exists");
                    return;
                }

                GuildMemberRow membership = conn.Find<GuildMemberRow>(characterId);
                GuildRow guild = membership == null ? null : conn.Find<GuildRow>(membership.guildId);
                if (membership != null &&
                    (membership.role == GuildRoleOwner || guild?.ownerCharacterId == characterId))
                {
                    response = CharacterDeleteFailed(
                        CharacterDeleteFailure.GuildOwner,
                        "guild owner must disband the guild before deleting this character");
                    return;
                }

                CharacterPlayerSystemsRow playerSystems = conn.Find<CharacterPlayerSystemsRow>(characterId);
                List<CharacterItemRow> items = conn.Query<CharacterItemRow>(
                    "SELECT * FROM character_items WHERE characterId=? ORDER BY itemInstanceId ASC",
                    characterId);

                var snapshot = new DeletedCharacterSnapshot
                {
                    character = ArchivedCharacterState.From(character),
                    playerSystems = ArchivedPlayerSystemsState.From(playerSystems),
                    items = ArchivedItemState.From(items),
                    guildMembership = ArchivedGuildMembershipState.From(membership),
                };

                long deletedUtcTicks = DateTime.UtcNow.Ticks;
                long purgeAfterUtcTicks = checked(
                    deletedUtcTicks + TimeSpan.FromDays(DeletedCharacterRetentionDays).Ticks);
                string snapshotJson = JsonSerializer.Serialize(snapshot, StateJsonOptions);

                if (conn.Insert(new DeletedCharacterRow
                {
                    characterId = character.characterId,
                    accountId = character.accountId,
                    originalName = character.name ?? string.Empty,
                    archiveSchemaVersion = DeletedCharacterArchiveSchemaVersion,
                    snapshotJson = snapshotJson,
                    deletedUtcTicks = deletedUtcTicks,
                    purgeAfterUtcTicks = purgeAfterUtcTicks,
                }) != 1)
                {
                    throw new InvalidOperationException("Failed to archive deleted character.");
                }

                if (membership != null)
                {
                    if (conn.Delete<GuildMemberRow>(characterId) != 1)
                        throw new InvalidOperationException("Failed to detach deleted character from guild.");
                    if (guild != null)
                    {
                        TouchGuild(guild, deletedUtcTicks);
                        if (conn.Update(guild) != 1)
                            throw new InvalidOperationException("Failed to advance guild revision after character deletion.");
                    }
                }

                conn.Execute("DELETE FROM character_items WHERE characterId=?", characterId);
                conn.Execute("DELETE FROM character_player_systems WHERE characterId=?", characterId);
                if (conn.Delete<CharacterRow>(characterId) != 1)
                    throw new InvalidOperationException("Failed to remove active character after archival.");

                response = new BackendCharacterDeleteResponse
                {
                    success = true,
                    failure = (byte)CharacterDeleteFailure.None,
                    characterId = character.characterId,
                    name = character.name ?? string.Empty,
                    deletedUtcTicks = deletedUtcTicks,
                    purgeAfterUtcTicks = purgeAfterUtcTicks,
                    error = string.Empty,
                };
            });

            return response;
        });
    }

    private sealed class CharacterSummaryRow
    {
        public long characterId { get; set; }
        public string name { get; set; }
        public string mapId { get; set; }
    }

    private void PurgeExpiredDeletedCharactersOnStartup()
    {
        long now = DateTime.UtcNow.Ticks;
        int totalPurged = 0;

        for (int batch = 0; batch < DeletedCharacterPurgeMaxBatches; ++batch)
        {
            int purged = Execute(conn => conn.Execute(
                "DELETE FROM deleted_characters WHERE characterId IN (" +
                "SELECT characterId FROM deleted_characters WHERE purgeAfterUtcTicks<=? " +
                "ORDER BY purgeAfterUtcTicks ASC LIMIT ?)",
                now,
                DeletedCharacterPurgeBatchSize));

            totalPurged += purged;
            if (purged < DeletedCharacterPurgeBatchSize)
                break;
        }

        if (totalPurged > 0)
            Console.WriteLine($"Deleted character startup purge: permanently removed {totalPurged} expired archive(s).");
    }

    private static BackendCharacterDeleteResponse CharacterDeleteFailed(
        CharacterDeleteFailure failure,
        string error) =>
        new()
        {
            success = false,
            failure = (byte)failure,
            characterId = 0,
            name = string.Empty,
            deletedUtcTicks = 0,
            purgeAfterUtcTicks = 0,
            error = error ?? string.Empty,
        };

    /// <summary>
    /// Applies location/checkpoint updates transactionally. Each row carries its own
    /// monotonically increasing revision so an older asynchronous save can never
    /// overwrite a newer durable state.
    /// </summary>
    public BackendCharacterSaveBatchResponse SaveCharacterBatch(BackendCharacterRecordDto[] records, GameplayContentSnapshot content)
    {
        records ??= Array.Empty<BackendCharacterRecordDto>();
        if (records.Length == 0)
        {
            return new BackendCharacterSaveBatchResponse
            {
                success = true,
                error = string.Empty,
                acknowledgements = Array.Empty<BackendCharacterSaveAckDto>(),
            };
        }

        if (records.Length > BackendServiceContracts.MaxCharacterBatchSize)
        {
            return new BackendCharacterSaveBatchResponse
            {
                success = false,
                error = "checkpoint batch too large",
                acknowledgements = Array.Empty<BackendCharacterSaveAckDto>(),
            };
        }

        return Execute(conn =>
        {
            var acknowledgements = new BackendCharacterSaveAckDto[records.Length];
            conn.RunInTransaction(() =>
            {
                long now = DateTime.UtcNow.Ticks;
                for (int i = 0; i < records.Length; ++i)
                {
                    BackendCharacterRecordDto request = records[i];
                    acknowledgements[i] = SaveOneCharacterCheckpoint(conn, request, now, content);
                }
            });

            return new BackendCharacterSaveBatchResponse
            {
                success = true,
                error = string.Empty,
                acknowledgements = acknowledgements,
            };
        });
    }

    private BackendCharacterSaveAckDto SaveOneCharacterCheckpoint(
        SQLiteConnection conn,
        BackendCharacterRecordDto request,
        long utcNowTicks,
        GameplayContentSnapshot content)
    {
        long characterId = request?.characterId ?? 0;
        long requestedRevision = request?.revision ?? 0;
        bool hasResourceState = request?.resources != null;
        bool hasAppearanceState = request?.appearance != null;
        bool hasPresentationState = request?.presentation != null;
        bool hasMagazineState = request?.magazines != null;
        bool hasProgressionState = request?.progression != null;
        if (request == null ||
            request.accountId <= 0 ||
            request.characterId <= 0 ||
            request.revision < 0 ||
            (hasResourceState && request.resourceRevision < 0) ||
            !IsValidLocation(request.location) ||
            (hasResourceState && !IsValidResourceState(request.resources)) ||
            (hasAppearanceState && !request.appearance.IsValid(out _)) ||
            (hasPresentationState && !request.presentation.IsValid(out _)) ||
            (hasProgressionState && !IsValidProgressionState(request.progression, content)))
        {
            return SaveRejected(characterId, requestedRevision, 0, "invalid checkpoint");
        }

        if (!_characterLeases.IsCurrentOwner(request.characterId, request.leaseOwnerToken))
            return SaveRejected(request.characterId, request.revision, 0, "character authority lease unavailable");

        CharacterRow row = conn.FindWithQuery<CharacterRow>(
            "SELECT * FROM characters WHERE characterId=? AND accountId=? LIMIT 1",
            request.characterId,
            request.accountId);
        if (row == null)
            return SaveRejected(request.characterId, request.revision, 0, "character not found");

        if (request.revision < row.revision)
            return SaveRejected(request.characterId, request.revision, row.revision, "stale revision");

        Dictionary<long, CharacterItemRow> magazineRows = null;
        if (hasMagazineState &&
            !TryValidateMagazineCheckpoint(
                conn,
                request.characterId,
                request.magazines,
                content,
                out magazineRows,
                out string magazineError))
        {
            return SaveRejected(
                request.characterId,
                request.revision,
                row.revision,
                magazineError);
        }

        // A location checkpoint may update durable location/schema version only. It is
        // deliberately not allowed to rename the character or change account ownership.
        // Missing resources means an older GameServer contract, so preserve the stored
        // resource payload rather than interpreting omission as "delete all resources".
        // Updated GameServers always send an explicit array.
        long resourceRevision = hasResourceState
            ? request.resourceRevision
            : Math.Max(0, row.resourceRevision);
        string resourceStateJson = hasResourceState
            ? JsonSerializer.Serialize(request.resources, StateJsonOptions)
            : (row.resourceStateJson ?? "[]");
        string appearanceStateJson = hasAppearanceState
            ? JsonSerializer.Serialize(request.appearance, StateJsonOptions)
            : (row.appearanceStateJson ?? JsonSerializer.Serialize(CharacterAppearanceRecipe.CreateDefault(), StateJsonOptions));
        string presentationStateJson = hasPresentationState
            ? JsonSerializer.Serialize(request.presentation, StateJsonOptions)
            : (row.presentationStateJson ?? JsonSerializer.Serialize(CharacterPresentationPreferences.CreateDefault(), StateJsonOptions));
        string progressionStateJson = hasProgressionState
            ? JsonSerializer.Serialize(request.progression, StateJsonOptions)
            : (row.progressionStateJson ?? JsonSerializer.Serialize(CharacterProgressionState.CreateDefault(), StateJsonOptions));

        int changed = conn.Execute(
            "UPDATE characters SET mapId=?, instanceId=?, positionX=?, positionY=?, positionZ=?, " +
            "yawDegrees=?, revision=?, schemaVersion=?, resourceRevision=?, resourceStateJson=?, appearanceStateJson=?, presentationStateJson=?, progressionStateJson=?, updatedUtcTicks=? " +
            "WHERE characterId=? AND accountId=? AND revision<=?",
            request.location.mapId,
            request.location.instanceId ?? string.Empty,
            request.location.positionX,
            request.location.positionY,
            request.location.positionZ,
            request.location.yawDegrees,
            request.revision,
            request.schemaVersion <= 0
                ? row.schemaVersion
                : Math.Max(row.schemaVersion, request.schemaVersion),
            resourceRevision,
            resourceStateJson,
            appearanceStateJson,
            presentationStateJson,
            progressionStateJson,
            utcNowTicks,
            request.characterId,
            request.accountId,
            request.revision);

        if (changed != 1)
        {
            long stored = conn.ExecuteScalar<long>(
                "SELECT revision FROM characters WHERE characterId=? AND accountId=? LIMIT 1",
                request.characterId,
                request.accountId);
            return SaveRejected(request.characterId, request.revision, stored, "checkpoint rejected");
        }

        if (hasMagazineState)
            ApplyMagazineCheckpoint(conn, request.characterId, request.magazines, magazineRows);

        return new BackendCharacterSaveAckDto
        {
            characterId = request.characterId,
            requestedRevision = request.revision,
            accepted = true,
            storedRevision = request.revision,
            error = string.Empty,
        };
    }

    private static bool TryValidateMagazineCheckpoint(
        SQLiteConnection conn,
        long characterId,
        BackendMagazineStateDto[] magazines,
        GameplayContentSnapshot content,
        out Dictionary<long, CharacterItemRow> storedById,
        out string error)
    {
        magazines ??= Array.Empty<BackendMagazineStateDto>();
        storedById = new Dictionary<long, CharacterItemRow>(magazines.Length);
        if (magazines.Length > 64)
            return FailMutation("magazine checkpoint is too large", out error);
        if (!GameplayContentSnapshotCache.TryValidate(content, out string contentError))
            return FailMutation(
                string.IsNullOrWhiteSpace(contentError) ? "gameplay content is unavailable" : contentError,
                out error);

        var seen = new HashSet<long>();
        for (int i = 0; i < magazines.Length; ++i)
        {
            BackendMagazineStateDto incoming = magazines[i];
            if (incoming == null ||
                incoming.itemInstanceId <= 0 ||
                incoming.magazineRevision < 0 ||
                incoming.loadedRounds < 0 ||
                !seen.Add(incoming.itemInstanceId))
                return FailMutation("invalid or duplicate magazine checkpoint entry", out error);

            CharacterItemRow stored = conn.FindWithQuery<CharacterItemRow>(
                "SELECT * FROM character_items WHERE characterId=? AND itemInstanceId=? LIMIT 1",
                characterId,
                incoming.itemInstanceId);

            // The item may have been dropped/transferred while a character checkpoint was
            // in flight. Item lifecycle persistence owns that newer location; skip this
            // stale soft-state entry rather than rejecting unrelated character state.
            if (stored == null)
                continue;

            storedById[stored.itemInstanceId] = stored;
            long storedRevision = Math.Max(0, stored.magazineRevision);
            int storedRounds = Math.Max(0, stored.loadedRounds);
            string storedAmmo = NormalizeLoadedAmmo(stored.loadedAmmoDefinitionId, storedRounds);
            string incomingAmmo = NormalizeLoadedAmmo(incoming.loadedAmmoDefinitionId, incoming.loadedRounds);

            if (incoming.magazineRevision < storedRevision)
                continue;

            if (incoming.magazineRevision == storedRevision)
            {
                if (incoming.loadedRounds != storedRounds ||
                    !string.Equals(incomingAmmo, storedAmmo, StringComparison.Ordinal))
                    return FailMutation("magazine checkpoint changed state without advancing revision", out error);
                continue;
            }

            ItemDefinition weapon = FindItemDefinition(content, stored.definitionId);
            if (!ValidateMagazinePayload(weapon, incomingAmmo, incoming.loadedRounds, content, out error))
                return false;

            // Soft checkpoints represent shot decrements only. They may never increase
            // rounds or change ammunition type. Reload is the dedicated atomic increase
            // transaction and remains the only authority for inventory->magazine value.
            if (incoming.loadedRounds > storedRounds)
                return FailMutation("soft checkpoint cannot increase loaded ammunition", out error);
            if (incoming.loadedRounds > 0 &&
                storedRounds > 0 &&
                !string.Equals(incomingAmmo, storedAmmo, StringComparison.Ordinal))
                return FailMutation("soft checkpoint cannot change loaded ammunition type", out error);
        }

        error = string.Empty;
        return true;
    }

    private static void ApplyMagazineCheckpoint(
        SQLiteConnection conn,
        long characterId,
        BackendMagazineStateDto[] magazines,
        Dictionary<long, CharacterItemRow> storedById)
    {
        magazines ??= Array.Empty<BackendMagazineStateDto>();
        storedById ??= new Dictionary<long, CharacterItemRow>();
        for (int i = 0; i < magazines.Length; ++i)
        {
            BackendMagazineStateDto incoming = magazines[i];
            if (incoming == null ||
                !storedById.TryGetValue(incoming.itemInstanceId, out CharacterItemRow stored) ||
                incoming.magazineRevision <= Math.Max(0, stored.magazineRevision))
            {
                continue;
            }

            stored.loadedAmmoDefinitionId = NormalizeLoadedAmmo(
                incoming.loadedAmmoDefinitionId,
                incoming.loadedRounds);
            stored.loadedRounds = Math.Max(0, incoming.loadedRounds);
            stored.magazineRevision = incoming.magazineRevision;
            if (conn.Update(stored) != 1)
                throw new InvalidOperationException("Failed to persist coalesced magazine state.");
        }
    }

    private static BackendCharacterRecordDto ToDto(CharacterRow row) =>
        new()
        {
            accountId = row.accountId,
            characterId = row.characterId,
            name = row.name,
            location = new BackendCharacterLocationDto
            {
                mapId = row.mapId,
                instanceId = row.instanceId ?? string.Empty,
                positionX = row.positionX,
                positionY = row.positionY,
                positionZ = row.positionZ,
                yawDegrees = row.yawDegrees,
            },
            revision = row.revision,
            schemaVersion = row.schemaVersion,
            resourceRevision = Math.Max(0, row.resourceRevision),
            resources = DeserializeResourceState(row.characterId, row.resourceStateJson),
            appearance = DeserializeAppearanceState(row.characterId, row.appearanceStateJson),
            presentation = DeserializePresentationState(row.characterId, row.presentationStateJson),
            progression = DeserializeProgressionState(row.characterId, row.progressionStateJson),
        };

    internal static BackendCharacterResourceDto[] DeserializeResourceState(long characterId, string json)
    {
        if (string.IsNullOrWhiteSpace(json))
            return Array.Empty<BackendCharacterResourceDto>();

        try
        {
            BackendCharacterResourceDto[] result =
                JsonSerializer.Deserialize<BackendCharacterResourceDto[]>(json, StateJsonOptions);
            if (result == null || !IsValidResourceState(result))
                throw CorruptCharacterState(characterId, "resources", null);
            return result;
        }
        catch (InvalidDataException)
        {
            throw;
        }
        catch (Exception ex) when (ex is JsonException || ex is NotSupportedException)
        {
            throw CorruptCharacterState(characterId, "resources", ex);
        }
    }

    internal static CharacterAppearanceRecipe DeserializeAppearanceState(long characterId, string json)
    {
        if (string.IsNullOrWhiteSpace(json))
            return CharacterAppearanceRecipe.CreateDefault();

        try
        {
            CharacterAppearanceRecipe result =
                JsonSerializer.Deserialize<CharacterAppearanceRecipe>(json, StateJsonOptions);
            if (result == null || !result.IsValid(out _))
                throw CorruptCharacterState(characterId, "appearance", null);
            return result;
        }
        catch (InvalidDataException)
        {
            throw;
        }
        catch (Exception ex) when (ex is JsonException || ex is NotSupportedException)
        {
            throw CorruptCharacterState(characterId, "appearance", ex);
        }
    }

    internal static CharacterPresentationPreferences DeserializePresentationState(long characterId, string json)
    {
        if (string.IsNullOrWhiteSpace(json))
            return CharacterPresentationPreferences.CreateDefault();

        try
        {
            CharacterPresentationPreferences result =
                JsonSerializer.Deserialize<CharacterPresentationPreferences>(json, StateJsonOptions);
            if (result == null || !result.IsValid(out _))
                throw CorruptCharacterState(characterId, "presentation", null);
            return result;
        }
        catch (InvalidDataException)
        {
            throw;
        }
        catch (Exception ex) when (ex is JsonException || ex is NotSupportedException)
        {
            throw CorruptCharacterState(characterId, "presentation", ex);
        }
    }

    internal static CharacterProgressionState DeserializeProgressionState(long characterId, string json)
    {
        if (string.IsNullOrWhiteSpace(json))
            return CharacterProgressionState.CreateDefault();

        try
        {
            CharacterProgressionState state =
                JsonSerializer.Deserialize<CharacterProgressionState>(json, StateJsonOptions);
            if (!IsValidProgressionState(state, null))
                throw CorruptCharacterState(characterId, "progression", null);
            return state;
        }
        catch (InvalidDataException)
        {
            throw;
        }
        catch (Exception ex) when (ex is JsonException || ex is NotSupportedException)
        {
            throw CorruptCharacterState(characterId, "progression", ex);
        }
    }

    private static InvalidDataException CorruptCharacterState(long characterId, string field, Exception inner) =>
        new InvalidDataException(
            $"Character {characterId} has corrupt persisted {field} state. Refusing to substitute defaults.",
            inner);

    internal static bool IsValidProgressionState(CharacterProgressionState state, GameplayContentSnapshot content)
    {
        if (state == null || state.revision < 0 || state.experience < 0 || state.level < 1 || state.level > 10000) return false;
        var trackIds = new HashSet<ushort>();
        ProgressTrackState[] tracks = state.tracks ?? Array.Empty<ProgressTrackState>();
        if (tracks.Length > 512) return false;
        for (int i = 0; i < tracks.Length; ++i)
        {
            ProgressTrackState track = tracks[i];
            if (track == null || track.dataId == 0 || !trackIds.Add(track.dataId) || track.value < 0 || (byte)track.mode > (byte)ProgressTrackMode.Decay) return false;
        }
        var repIds = new HashSet<ushort>();
        ReputationState[] rep = state.reputation ?? Array.Empty<ReputationState>();
        if (rep.Length > 512) return false;
        for (int i = 0; i < rep.Length; ++i) if (rep[i] == null || rep[i].factionDataId == 0 || !repIds.Add(rep[i].factionDataId)) return false;
        var heatIds = new HashSet<ushort>();
        HeatState[] heat = state.heat ?? Array.Empty<HeatState>();
        if (heat.Length > 256) return false;
        for (int i = 0; i < heat.Length; ++i) if (heat[i] == null || heat[i].jurisdictionDataId == 0 || !heatIds.Add(heat[i].jurisdictionDataId) || heat[i].value < 0 || heat[i].bounty < 0 || heat[i].evidence < 0) return false;
        ushort[] recipes = state.knownRecipeDataIds ?? Array.Empty<ushort>();
        if (recipes.Length > 4096) return false;
        for (int i = 0; i < recipes.Length; ++i) if (recipes[i] == 0 || (i > 0 && recipes[i] <= recipes[i - 1])) return false;
        return true;
    }

    internal static bool IsValidResourceState(BackendCharacterResourceDto[] resources)
    {
        resources ??= Array.Empty<BackendCharacterResourceDto>();
        if (resources.Length > 128)
            return false;
        var ids = new HashSet<ushort>();
        for (int i = 0; i < resources.Length; ++i)
        {
            BackendCharacterResourceDto resource = resources[i];
            if (resource == null)
                return false;
            ushort id = (ushort)resource.id;
            if (id == 0 || !ids.Add(id) || resource.maximum < resource.current)
                return false;
        }
        return true;
    }

    private static bool IsValidLocation(BackendCharacterLocationDto location) =>
        location != null &&
        !string.IsNullOrWhiteSpace(location.mapId) &&
        IsFinite(location.positionX) &&
        IsFinite(location.positionY) &&
        IsFinite(location.positionZ) &&
        IsFinite(location.yawDegrees);

    private static bool IsFinite(float value) => !float.IsNaN(value) && !float.IsInfinity(value);

    private static string NormalizeCharacterNameKey(string name) => name.Trim().ToUpperInvariant();

    private static BackendCharacterListResponse FailedCharacterList(string error) =>
        new()
        {
            success = false,
            error = error,
            characters = Array.Empty<BackendCharacterSummaryDto>(),
        };

    private static BackendCharacterLoadResponse FailedCharacterLoad(string error) =>
        new()
        {
            success = false,
            found = false,
            error = error,
            character = null,
        };

    private static BackendCharacterCreateResponse CharacterCreateFailed(
        CharacterCreateFailure failure,
        string error) =>
        new()
        {
            success = false,
            failure = (byte)failure,
            characterId = 0,
            error = error,
        };

    private static BackendCharacterSaveAckDto SaveRejected(
        long characterId,
        long requestedRevision,
        long storedRevision,
        string error) =>
        new()
        {
            characterId = characterId,
            requestedRevision = requestedRevision,
            accepted = false,
            storedRevision = storedRevision,
            error = error,
        };

    // -------------------------------------------------------------------------
    // Player item systems
    // -------------------------------------------------------------------------

    private const byte InventoryContainer = 1;
    private const byte EquipmentContainer = 2;
}
