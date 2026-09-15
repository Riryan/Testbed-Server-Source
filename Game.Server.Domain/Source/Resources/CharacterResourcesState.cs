using System;
using System.Collections.Generic;
using Game.Shared.Resources;

namespace Game.Server.Domain.Resources
{
    /// <summary>Owner-contained authoritative resource set. Full copies are created only for snapshot/persistence boundaries.</summary>
    public sealed class CharacterResourcesState
    {
        private readonly Dictionary<CharacterResourceId, CharacterResourceState> _resources;

        public long Revision { get; private set; }

        public CharacterResourcesState(long revision, IEnumerable<CharacterResourceState> resources)
        {
            if (revision < 0)
                throw new ArgumentOutOfRangeException(nameof(revision));

            Revision = revision;
            _resources = new Dictionary<CharacterResourceId, CharacterResourceState>();
            if (resources == null)
                return;

            foreach (CharacterResourceState resource in resources)
            {
                if (resource.Id == CharacterResourceId.None)
                    continue;
                if (_resources.ContainsKey(resource.Id))
                    throw new ArgumentException($"Duplicate resource '{resource.Id}'.", nameof(resources));
                _resources.Add(resource.Id, resource);
            }
        }

        public bool TryGet(CharacterResourceId id, out CharacterResourceState resource) =>
            _resources.TryGetValue(id, out resource);

        public CharacterResourceState Get(CharacterResourceId id) =>
            _resources.TryGetValue(id, out CharacterResourceState resource) ? resource : default;

        public CharacterResourceState[] Snapshot()
        {
            var result = new CharacterResourceState[_resources.Count];
            int i = 0;
            foreach (CharacterResourceState resource in _resources.Values)
                result[i++] = resource;
            Array.Sort(result, (a, b) => ((ushort)a.Id).CompareTo((ushort)b.Id));
            return result;
        }

        internal bool ReplaceInPlace(CharacterResourceState replacement)
        {
            if (replacement.Id == CharacterResourceId.None || !_resources.ContainsKey(replacement.Id))
                return false;
            if (Revision == long.MaxValue)
                throw new InvalidOperationException("Character resource revision exhausted.");

            _resources[replacement.Id] = replacement;
            Revision++;
            return true;
        }

    }
}
