using System;
using System.Collections.Generic;
using Game.Server.Domain.Inventory;

namespace Game.Server.Domain.Equipment
{
    public sealed class EquippedItemState
    {
        public string SlotId { get; }
        public ItemInstanceState Item { get; }

        public EquippedItemState(string slotId, ItemInstanceState item)
        {
            if (string.IsNullOrWhiteSpace(slotId)) throw new ArgumentException("Equipment slot id is required.", nameof(slotId));
            Item = item ?? throw new ArgumentNullException(nameof(item));
            SlotId = slotId;
        }

        public EquippedItemState Copy() => new EquippedItemState(SlotId, Item.Copy());
    }

    public sealed class EquipmentState
    {
        private readonly Dictionary<string, ItemInstanceState> _items;
        public long Revision { get; }

        public EquipmentState(long revision, IEnumerable<EquippedItemState> items = null)
        {
            if (revision < 0) throw new ArgumentOutOfRangeException(nameof(revision));
            Revision = revision;
            _items = new Dictionary<string, ItemInstanceState>(StringComparer.Ordinal);
            if (items == null) return;
            foreach (EquippedItemState entry in items)
            {
                if (entry == null || entry.Item == null || string.IsNullOrWhiteSpace(entry.SlotId)) continue;
                if (_items.ContainsKey(entry.SlotId)) throw new ArgumentException("Duplicate equipment slot.", nameof(items));
                _items.Add(entry.SlotId, entry.Item.Copy());
            }
        }

        public ItemInstanceState Get(string slotId) =>
            !string.IsNullOrWhiteSpace(slotId) && _items.TryGetValue(slotId, out ItemInstanceState item) ? item : null;

        public EquippedItemState[] Snapshot()
        {
            var result = new EquippedItemState[_items.Count];
            int i = 0;
            foreach (KeyValuePair<string, ItemInstanceState> pair in _items)
                result[i++] = new EquippedItemState(pair.Key, pair.Value.Copy());
            return result;
        }
    }
}
