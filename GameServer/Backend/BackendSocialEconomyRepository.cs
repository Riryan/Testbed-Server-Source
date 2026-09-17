using System;
using System.Threading;
using System.Threading.Tasks;
using Game.Server.Application.Persistence;
using Game.Server.Domain.Players;
using Game.Shared.Backend;
using Game.Shared.Identity;
using Game.UnityIntegration.Backend;

namespace Game.GameServer.Backend
{
    /// <summary>
    /// GameServer adapter for persistent friends, atomic player trade, and character bank storage.
    /// Lease proof always comes from the existing BackendCharacterLeaseService.
    /// </summary>
    internal sealed class BackendSocialEconomyRepository
    {
        private readonly BackendInternalClient _backend;
        private readonly BackendCharacterLeaseService _leases;

        public BackendSocialEconomyRepository(BackendInternalClient backend, BackendCharacterLeaseService leases)
        {
            _backend = backend ?? throw new ArgumentNullException(nameof(backend));
            _leases = leases ?? throw new ArgumentNullException(nameof(leases));
        }

        public Task<BackendFriendResponse> LoadFriendsAsync(long characterId, CancellationToken ct) =>
            _backend.LoadFriendsAsync(new BackendFriendLoadRequest { characterId = characterId }, ct);

        public Task<BackendFriendResponse> AddFriendAsync(PlayerRuntime actor, long otherCharacterId, CancellationToken ct) =>
            _backend.AddFriendAsync(BuildFriendRequest(actor, otherCharacterId), ct);

        public Task<BackendFriendResponse> RemoveFriendAsync(PlayerRuntime actor, long otherCharacterId, CancellationToken ct) =>
            _backend.RemoveFriendAsync(BuildFriendRequest(actor, otherCharacterId), ct);

        public Task<BackendStorageLoadResponse> LoadStorageAsync(PlayerRuntime actor, CancellationToken ct) =>
            _backend.LoadStorageAsync(new BackendStorageLoadRequest
            {
                accountId = actor.AccountId.Value,
                characterId = actor.CharacterId.Value,
            }, ct);

        public Task<BackendStorageTransferResponse> TransferStorageAsync(
            PlayerRuntime actor,
            long expectedInventoryRevision,
            long expectedEquipmentRevision,
            long expectedStorageRevision,
            bool deposit,
            long sourceItemInstanceId,
            int quantity,
            CancellationToken ct)
        {
            return _backend.TransferStorageAsync(new BackendStorageTransferRequest
            {
                accountId = actor.AccountId.Value,
                characterId = actor.CharacterId.Value,
                leaseOwnerToken = GetLease(actor.CharacterId),
                expectedInventoryRevision = expectedInventoryRevision,
                expectedEquipmentRevision = expectedEquipmentRevision,
                expectedStorageRevision = expectedStorageRevision,
                deposit = deposit,
                sourceItemInstanceId = sourceItemInstanceId,
                quantity = quantity,
            }, ct);
        }

        public Task<BackendTradeCommitResponse> CommitTradeAsync(
            PlayerRuntime left,
            PlayerItemSystemsRuntimeSnapshot leftState,
            BackendTradeOfferDto[] leftOffers,
            PlayerRuntime right,
            PlayerItemSystemsRuntimeSnapshot rightState,
            BackendTradeOfferDto[] rightOffers,
            CancellationToken ct)
        {
            return _backend.CommitTradeAsync(new BackendTradeCommitRequest
            {
                leftAccountId = left.AccountId.Value,
                leftCharacterId = left.CharacterId.Value,
                leftLeaseOwnerToken = GetLease(left.CharacterId),
                leftExpectedInventoryRevision = leftState.Inventory.Revision,
                leftExpectedEquipmentRevision = leftState.Equipment.Revision,
                leftOffers = leftOffers ?? Array.Empty<BackendTradeOfferDto>(),
                rightAccountId = right.AccountId.Value,
                rightCharacterId = right.CharacterId.Value,
                rightLeaseOwnerToken = GetLease(right.CharacterId),
                rightExpectedInventoryRevision = rightState.Inventory.Revision,
                rightExpectedEquipmentRevision = rightState.Equipment.Revision,
                rightOffers = rightOffers ?? Array.Empty<BackendTradeOfferDto>(),
            }, ct);
        }

        public static PlayerSystemsPersistenceRecord ToPlayerState(BackendPlayerSystemsSnapshotDto dto) =>
            BackendPlayerSystemsRepository.FromDto(dto);

        private BackendFriendMutationRequest BuildFriendRequest(PlayerRuntime actor, long otherCharacterId) => new BackendFriendMutationRequest
        {
            accountId = actor.AccountId.Value,
            characterId = actor.CharacterId.Value,
            leaseOwnerToken = GetLease(actor.CharacterId),
            otherCharacterId = otherCharacterId,
        };

        private string GetLease(CharacterId characterId)
        {
            if (!_leases.TryGetPersistenceLeaseOwnerToken(characterId, out string token) || string.IsNullOrWhiteSpace(token))
                throw new InvalidOperationException("character authority lease is not held");
            return token;
        }
    }
}
