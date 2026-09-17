using System.Text.Json;
using Game.Shared.Authentication;
using Game.Shared.Backend;
using Game.Shared.Characters;
using Game.Shared.Content;
using Game.Shared.Protocol;
using Game.Shared.Progression;
using SQLite;

namespace Game.BackendServer;

/// <summary>
/// Sole SQLite owner for the standalone backend server. Unity never opens this file.
/// All writes are serialized through one connection today; repository/API boundaries
/// allow this storage implementation to be replaced by PostgreSQL later.
/// </summary>
internal sealed partial class BackendDatabase : IDisposable
{
    private static readonly JsonSerializerOptions StateJsonOptions = new()
    {
        IncludeFields = true,
    };

    private const int DeletedCharacterArchiveSchemaVersion = 1;
    private const int DeletedCharacterRetentionDays = 90;
    private const int DeletedCharacterPurgeBatchSize = 128;
    private const int DeletedCharacterPurgeMaxBatches = 8;
    private static readonly long AdmissionCleanupIntervalTicks = TimeSpan.FromMinutes(1).Ticks;

    private readonly object _gate = new();
    private readonly CharacterLeaseStore _characterLeases;
    private SQLiteConnection _connection;
    private long _nextAdmissionCleanupUtcTicks;

    public string DatabasePath { get; }

    public BackendDatabase(string databasePath, CharacterLeaseStore characterLeases)
    {
        if (string.IsNullOrWhiteSpace(databasePath))
            throw new ArgumentException("Database path is required.", nameof(databasePath));
        _characterLeases = characterLeases ?? throw new ArgumentNullException(nameof(characterLeases));

        DatabasePath = Path.GetFullPath(databasePath);
        Directory.CreateDirectory(Path.GetDirectoryName(DatabasePath) ?? ".");

        try
        {
            _connection = new SQLiteConnection(DatabasePath, true)
            {
                BusyTimeout = TimeSpan.FromSeconds(5)
            };
            _connection.EnableWriteAheadLogging();
            InitializeSchema();
            PurgeExpiredDeletedCharactersOnStartup();
        }
        catch
        {
            _connection?.Dispose();
            _connection = null;
            throw;
        }
    }

    private void InitializeSchema()
    {
        Execute(conn =>
        {
            conn.Execute("PRAGMA foreign_keys=ON");
            conn.CreateTable<AccountRow>();
            InitializeAccountPolicySchema(conn);
            conn.CreateTable<CredentialRow>();
            conn.CreateTable<AdmissionRow>();
            conn.CreateTable<CharacterRow>();
            conn.CreateTable<DeletedCharacterRow>();
            conn.CreateTable<CharacterPlayerSystemsRow>();
            conn.CreateTable<CharacterItemRow>();
            conn.CreateTable<WorldItemRow>();
            conn.CreateTable<WorldItemWorldRow>();
            conn.CreateTable<ItemIdSequenceRow>();
            InitializeGuildSchema(conn);
            InitializeItemIdSequence(conn);

            conn.CreateIndex("idx_accounts_name_key", "accounts", "nameKey", true);
            conn.CreateIndex("idx_auth_admissions_expiry", "auth_admissions", "expiresUtcTicks", false);
            conn.CreateIndex("idx_auth_admissions_account", "auth_admissions", "accountId", false);
            conn.CreateIndex("idx_characters_name_key", "characters", "nameKey", true);
            conn.CreateIndex("idx_characters_account_id", "characters", "accountId", false);
            conn.CreateIndex("idx_deleted_characters_account", "deleted_characters", "accountId", false);
            conn.CreateIndex("idx_deleted_characters_purge_after", "deleted_characters", "purgeAfterUtcTicks", false);
            conn.CreateIndex("idx_character_items_character", "character_items", "characterId", false);
            conn.CreateIndex("idx_world_items_world_key", "world_items", "worldKey", false);
            conn.Execute("CREATE UNIQUE INDEX IF NOT EXISTS idx_character_inventory_slot " +
                         "ON character_items(characterId, containerKind, inventorySlot) WHERE containerKind=1");
            conn.Execute("CREATE UNIQUE INDEX IF NOT EXISTS idx_character_equipment_slot " +
                         "ON character_items(characterId, containerKind, equipmentSlotId) WHERE containerKind=2");
        });
    }

    // -------------------------------------------------------------------------
    // Connection ownership / disposal
    // -------------------------------------------------------------------------

    private T Execute<T>(Func<SQLiteConnection, T> operation)
    {
        lock (_gate)
        {
            if (_connection == null)
                throw new ObjectDisposedException(nameof(BackendDatabase));
            return operation(_connection);
        }
    }

    private void Execute(Action<SQLiteConnection> operation) =>
        Execute(conn =>
        {
            operation(conn);
            return true;
        });

    public void Dispose()
    {
        lock (_gate)
        {
            _connection?.Dispose();
            _connection = null;
        }
    }

    [Table("accounts")]
    private sealed class AccountRow
    {
        [PrimaryKey, AutoIncrement]
        public long accountId { get; set; }
        [NotNull]
        public string name { get; set; }
        [NotNull]
        public string nameKey { get; set; }
        [NotNull]
        public string passwordVerifier { get; set; }
        public long createdUtcTicks { get; set; }
        public long lastLoginUtcTicks { get; set; }
    }

    [Table("account_credentials")]
    private sealed class CredentialRow
    {
        [PrimaryKey]
        public long accountId { get; set; }
        [NotNull]
        public string algorithm { get; set; }
        public int iterations { get; set; }
        [NotNull]
        public string saltBase64 { get; set; }
        [NotNull]
        public string hashBase64 { get; set; }
        public int version { get; set; }
        public long updatedUtcTicks { get; set; }

        public static CredentialRow From(
            long accountId,
            PasswordCredential credential,
            long utcNowTicks) =>
            new()
            {
                accountId = accountId,
                algorithm = credential.Algorithm,
                iterations = credential.Iterations,
                saltBase64 = credential.SaltBase64,
                hashBase64 = credential.HashBase64,
                version = credential.Version,
                updatedUtcTicks = utcNowTicks,
            };
    }

    [Table("auth_admissions")]
    private sealed class AdmissionRow
    {
        [PrimaryKey]
        public string tokenHash { get; set; }
        public long accountId { get; set; }
        public long issuedUtcTicks { get; set; }
        public long expiresUtcTicks { get; set; }
        public long consumedUtcTicks { get; set; }
    }

    [Table("deleted_characters")]
    private sealed class DeletedCharacterRow
    {
        [PrimaryKey]
        public long characterId { get; set; }
        public long accountId { get; set; }
        [NotNull]
        public string originalName { get; set; }
        public int archiveSchemaVersion { get; set; }
        [NotNull]
        public string snapshotJson { get; set; }
        public long deletedUtcTicks { get; set; }
        public long purgeAfterUtcTicks { get; set; }
    }

    private sealed class DeletedCharacterSnapshot
    {
        public ArchivedCharacterState character { get; set; }
        public ArchivedPlayerSystemsState playerSystems { get; set; }
        public ArchivedItemState[] items { get; set; }
        public ArchivedGuildMembershipState guildMembership { get; set; }
    }

    private sealed class ArchivedCharacterState
    {
        public long characterId { get; set; }
        public long accountId { get; set; }
        public string name { get; set; }
        public string nameKey { get; set; }
        public string mapId { get; set; }
        public string instanceId { get; set; }
        public float positionX { get; set; }
        public float positionY { get; set; }
        public float positionZ { get; set; }
        public float yawDegrees { get; set; }
        public long revision { get; set; }
        public int schemaVersion { get; set; }
        public long resourceRevision { get; set; }
        public string resourceStateJson { get; set; }
        public string appearanceStateJson { get; set; }
        public string presentationStateJson { get; set; }
        public string progressionStateJson { get; set; }
        public long createdUtcTicks { get; set; }
        public long updatedUtcTicks { get; set; }

        public static ArchivedCharacterState From(CharacterRow row) => row == null ? null : new ArchivedCharacterState
        {
            characterId = row.characterId,
            accountId = row.accountId,
            name = row.name ?? string.Empty,
            nameKey = row.nameKey ?? string.Empty,
            mapId = row.mapId ?? string.Empty,
            instanceId = row.instanceId ?? string.Empty,
            positionX = row.positionX,
            positionY = row.positionY,
            positionZ = row.positionZ,
            yawDegrees = row.yawDegrees,
            revision = row.revision,
            schemaVersion = row.schemaVersion,
            resourceRevision = row.resourceRevision,
            resourceStateJson = row.resourceStateJson ?? "[]",
            appearanceStateJson = row.appearanceStateJson ?? string.Empty,
            presentationStateJson = row.presentationStateJson ?? string.Empty,
            progressionStateJson = row.progressionStateJson ?? string.Empty,
            createdUtcTicks = row.createdUtcTicks,
            updatedUtcTicks = row.updatedUtcTicks,
        };
    }

    private sealed class ArchivedPlayerSystemsState
    {
        public long characterId { get; set; }
        public int inventoryCapacity { get; set; }
        public long inventoryRevision { get; set; }
        public long equipmentRevision { get; set; }
        public long updatedUtcTicks { get; set; }

        public static ArchivedPlayerSystemsState From(CharacterPlayerSystemsRow row) => row == null ? null : new ArchivedPlayerSystemsState
        {
            characterId = row.characterId,
            inventoryCapacity = row.inventoryCapacity,
            inventoryRevision = row.inventoryRevision,
            equipmentRevision = row.equipmentRevision,
            updatedUtcTicks = row.updatedUtcTicks,
        };
    }

    private sealed class ArchivedItemState
    {
        public long itemInstanceId { get; set; }
        public long characterId { get; set; }
        public byte containerKind { get; set; }
        public int inventorySlot { get; set; }
        public string equipmentSlotId { get; set; }
        public string definitionId { get; set; }
        public int quantity { get; set; }
        public int durability { get; set; }
        public long revision { get; set; }
        public string loadedAmmoDefinitionId { get; set; }
        public int loadedRounds { get; set; }
        public long magazineRevision { get; set; }

        public static ArchivedItemState[] From(List<CharacterItemRow> rows)
        {
            rows ??= new List<CharacterItemRow>();
            var result = new ArchivedItemState[rows.Count];
            for (int i = 0; i < rows.Count; ++i)
            {
                CharacterItemRow row = rows[i];
                result[i] = new ArchivedItemState
                {
                    itemInstanceId = row.itemInstanceId,
                    characterId = row.characterId,
                    containerKind = row.containerKind,
                    inventorySlot = row.inventorySlot,
                    equipmentSlotId = row.equipmentSlotId ?? string.Empty,
                    definitionId = row.definitionId ?? string.Empty,
                    quantity = row.quantity,
                    durability = row.durability,
                    revision = row.revision,
                    loadedAmmoDefinitionId = row.loadedAmmoDefinitionId ?? string.Empty,
                    loadedRounds = row.loadedRounds,
                    magazineRevision = row.magazineRevision,
                };
            }
            return result;
        }
    }

    private sealed class ArchivedGuildMembershipState
    {
        public long characterId { get; set; }
        public long guildId { get; set; }
        public byte role { get; set; }
        public long joinedUtcTicks { get; set; }

        public static ArchivedGuildMembershipState From(GuildMemberRow row) => row == null ? null : new ArchivedGuildMembershipState
        {
            characterId = row.characterId,
            guildId = row.guildId,
            role = row.role,
            joinedUtcTicks = row.joinedUtcTicks,
        };
    }

    [Table("character_player_systems")]
    private sealed class CharacterPlayerSystemsRow
    {
        [PrimaryKey]
        public long characterId { get; set; }
        public int inventoryCapacity { get; set; }
        public long inventoryRevision { get; set; }
        public long equipmentRevision { get; set; }
        public long updatedUtcTicks { get; set; }
    }

    [Table("character_items")]
    private sealed class CharacterItemRow
    {
        [PrimaryKey, AutoIncrement]
        public long itemInstanceId { get; set; }
        public long characterId { get; set; }
        public byte containerKind { get; set; }
        public int inventorySlot { get; set; }
        [NotNull]
        public string equipmentSlotId { get; set; }
        [NotNull]
        public string definitionId { get; set; }
        public int quantity { get; set; }
        public int durability { get; set; }
        public long revision { get; set; }
        public string loadedAmmoDefinitionId { get; set; }
        public int loadedRounds { get; set; }
        public long magazineRevision { get; set; }
    }

    [Table("world_items")]
    private sealed class WorldItemRow
    {
        [PrimaryKey] public long itemInstanceId { get; set; }
        [NotNull] public string worldKey { get; set; }
        [NotNull] public string mapId { get; set; }
        [NotNull] public string instanceId { get; set; }
        [NotNull] public string definitionId { get; set; }
        public int quantity { get; set; }
        public int durability { get; set; }
        public long revision { get; set; }
        public string loadedAmmoDefinitionId { get; set; }
        public int loadedRounds { get; set; }
        public long magazineRevision { get; set; }
        public float positionX { get; set; }
        public float positionY { get; set; }
        public float positionZ { get; set; }
    }

    [Table("world_item_worlds")]
    private sealed class WorldItemWorldRow
    {
        [PrimaryKey, NotNull] public string worldKey { get; set; }
        [NotNull] public string mapId { get; set; }
        [NotNull] public string instanceId { get; set; }
        public long revision { get; set; }
    }

    [Table("item_id_sequence")]
    private sealed class ItemIdSequenceRow
    {
        [PrimaryKey] public int sequenceId { get; set; }
        public long lastItemInstanceId { get; set; }
    }

    [Table("characters")]
    private sealed class CharacterRow
    {
        [PrimaryKey, AutoIncrement]
        public long characterId { get; set; }
        public long accountId { get; set; }
        [NotNull]
        public string name { get; set; }
        [NotNull]
        public string nameKey { get; set; }
        [NotNull]
        public string mapId { get; set; }
        [NotNull]
        public string instanceId { get; set; }
        public float positionX { get; set; }
        public float positionY { get; set; }
        public float positionZ { get; set; }
        public float yawDegrees { get; set; }
        public long revision { get; set; }
        public int schemaVersion { get; set; }
        public long resourceRevision { get; set; }
        public string resourceStateJson { get; set; }
        public string appearanceStateJson { get; set; }
        public string presentationStateJson { get; set; }
        public string progressionStateJson { get; set; }
        public long createdUtcTicks { get; set; }
        public long updatedUtcTicks { get; set; }
    }
}

internal sealed record AccountSnapshot(
    long AccountId,
    string Name,
    string LegacyPasswordVerifier,
    PasswordCredential Credential);
