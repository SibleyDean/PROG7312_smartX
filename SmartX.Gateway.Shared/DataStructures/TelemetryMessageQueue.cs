namespace SmartX.Gateway.Shared.DataStructures;

public class TelemetryMessageQueue<T>
{
    private readonly Queue<T> _queue = new();
    private readonly object _lock = new();

    public int Count
    {
        get
        {
            lock (_lock)
            {
                return _queue.Count;
            }
        }
    }

    public bool IsEmpty => Count == 0;

    public void Enqueue(T item)
    {
        lock (_lock)
        {
            _queue.Enqueue(item);
        }
    }

    public bool TryDequeue(out T? item)
    {
        lock (_lock)
        {
            return _queue.TryDequeue(out item);
        }
    }

    public T Peek()
    {
        lock (_lock)
        {
            if (!_queue.TryPeek(out var item))
            {
                throw new InvalidOperationException("Queue is empty.");
            }

            return item;
        }
    }
}
