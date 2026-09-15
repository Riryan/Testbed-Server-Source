using System;

namespace Game.Server.Domain.Inventory
{
    public sealed class InventoryState
    {
        private readonly ItemInstanceState[] _slots;

        public int Capacity => _slots.Length;
        public long Revision { get; }

        public InventoryState(int capacity, long revision, ItemInstanceState[] slots = null)
        {
            if (capacity < 1) throw new ArgumentOutOfRangeException(nameof(capacity));
            if (revision < 0) throw new ArgumentOutOfRangeException(nameof(revision));
            if (slots != null && slots.Length != capacity) throw new ArgumentException("Slot array length must match capacity.", nameof(slots));

            Revision = revision;
            _slots = new ItemInstanceState[capacity];
            if (slots != null)
                for (int i = 0; i < capacity; ++i)
                    _slots[i] = slots[i]?.Copy();
        }

        public ItemInstanceState Get(int index) =>
            index >= 0 && index < _slots.Length ? _slots[index] : null;

        public ItemInstanceState[] CopySlots()
        {
            var copy = new ItemInstanceState[_slots.Length];
            for (int i = 0; i < _slots.Length; ++i) copy[i] = _slots[i]?.Copy();
            return copy;
        }

        public int FindFirstEmptySlot()
        {
            for (int i = 0; i < _slots.Length; ++i)
                if (_slots[i] == null) return i;
            return -1;
        }

        public InventoryState WithSlots(ItemInstanceState[] slots, long revision) =>
            new InventoryState(Capacity, revision, slots);
    }
}
