namespace SmartX.Gateway.Shared.DataStructures;

public class CriticalAlertPriorityQueue<T>
{
    private readonly List<(T Item, int Priority)> _heap = new();
    private readonly object _lock = new();

    public int Count
    {
        get
        {
            lock (_lock)
            {
                return _heap.Count;
            }
        }
    }

    public bool IsEmpty => Count == 0;

    public void Enqueue(T item, int priority)
    {
        lock (_lock)
        {
            _heap.Add((item, priority));
            BubbleUp(_heap.Count - 1);
        }
    }

    public bool TryDequeue(out T? item)
    {
        lock (_lock)
        {
            if (_heap.Count == 0)
            {
                item = default;
                return false;
            }

            item = _heap[0].Item;
            var lastIndex = _heap.Count - 1;
            _heap[0] = _heap[lastIndex];
            _heap.RemoveAt(lastIndex);

            if (_heap.Count > 0)
            {
                BubbleDown(0);
            }

            return true;
        }
    }

    public T Peek()
    {
        lock (_lock)
        {
            if (_heap.Count == 0)
            {
                throw new InvalidOperationException("Queue is empty.");
            }

            return _heap[0].Item;
        }
    }

    private void BubbleUp(int index)
    {
        while (index > 0)
        {
            var parent = (index - 1) / 2;
            if (_heap[parent].Priority <= _heap[index].Priority)
            {
                return;
            }

            (_heap[parent], _heap[index]) = (_heap[index], _heap[parent]);
            index = parent;
        }
    }

    private void BubbleDown(int index)
    {
        while (true)
        {
            var left = (2 * index) + 1;
            var right = (2 * index) + 2;
            var smallest = index;

            if (left < _heap.Count && _heap[left].Priority < _heap[smallest].Priority)
            {
                smallest = left;
            }

            if (right < _heap.Count && _heap[right].Priority < _heap[smallest].Priority)
            {
                smallest = right;
            }

            if (smallest == index)
            {
                return;
            }

            (_heap[smallest], _heap[index]) = (_heap[index], _heap[smallest]);
            index = smallest;
        }
    }
}
