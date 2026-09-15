using System;
using System.Collections.Generic;

namespace Game.Server.Application.Scheduling
{
    /// <summary>
    /// Small allocation-stable binary min-heap used by shared server code that must compile
    /// both in Unity's .NET profile and in the standalone .NET GameServer. This intentionally
    /// exposes only the queue operations currently required by Population and transient drops.
    /// </summary>
    internal sealed class MinPriorityQueue<TElement, TPriority>
    {
        private struct Entry
        {
            public TElement Element;
            public TPriority Priority;

            public Entry(TElement element, TPriority priority)
            {
                Element = element;
                Priority = priority;
            }
        }

        private readonly List<Entry> _heap = new List<Entry>();
        private readonly IComparer<TPriority> _comparer;

        public MinPriorityQueue()
            : this(Comparer<TPriority>.Default)
        {
        }

        public MinPriorityQueue(IComparer<TPriority> comparer)
        {
            _comparer = comparer ?? throw new ArgumentNullException(nameof(comparer));
        }

        public int Count => _heap.Count;

        public void Enqueue(TElement element, TPriority priority)
        {
            int index = _heap.Count;
            _heap.Add(new Entry(element, priority));
            SiftUp(index);
        }

        public bool TryPeek(out TElement element, out TPriority priority)
        {
            if (_heap.Count == 0)
            {
                element = default(TElement);
                priority = default(TPriority);
                return false;
            }

            Entry root = _heap[0];
            element = root.Element;
            priority = root.Priority;
            return true;
        }

        public TElement Dequeue()
        {
            if (_heap.Count == 0)
                throw new InvalidOperationException("The priority queue is empty.");

            TElement element = _heap[0].Element;
            RemoveRoot();
            return element;
        }

        public bool TryDequeue(out TElement element, out TPriority priority)
        {
            if (_heap.Count == 0)
            {
                element = default(TElement);
                priority = default(TPriority);
                return false;
            }

            Entry root = _heap[0];
            element = root.Element;
            priority = root.Priority;
            RemoveRoot();
            return true;
        }

        private void RemoveRoot()
        {
            int lastIndex = _heap.Count - 1;
            if (lastIndex == 0)
            {
                _heap.RemoveAt(0);
                return;
            }

            _heap[0] = _heap[lastIndex];
            _heap.RemoveAt(lastIndex);
            SiftDown(0);
        }

        private void SiftUp(int index)
        {
            while (index > 0)
            {
                int parent = (index - 1) >> 1;
                if (_comparer.Compare(_heap[index].Priority, _heap[parent].Priority) >= 0)
                    return;

                Swap(index, parent);
                index = parent;
            }
        }

        private void SiftDown(int index)
        {
            int count = _heap.Count;
            while (true)
            {
                int left = (index << 1) + 1;
                if (left >= count)
                    return;

                int right = left + 1;
                int smallest = left;
                if (right < count && _comparer.Compare(_heap[right].Priority, _heap[left].Priority) < 0)
                    smallest = right;

                if (_comparer.Compare(_heap[smallest].Priority, _heap[index].Priority) >= 0)
                    return;

                Swap(index, smallest);
                index = smallest;
            }
        }

        private void Swap(int a, int b)
        {
            Entry temp = _heap[a];
            _heap[a] = _heap[b];
            _heap[b] = temp;
        }
    }
}
