using System.Diagnostics.CodeAnalysis;

namespace SmartX.Gateway.Shared.DataStructures;

public class CommandHistoryStack<T> where T : class
{
    private readonly Stack<T> _stack = new();
    private readonly object _lock = new();

    public int Count
    {
        get
        {
            lock (_lock)
            {
                return _stack.Count;
            }
        }
    }

    public bool IsEmpty => Count == 0;

    public void Push(T command)
    {
        ArgumentNullException.ThrowIfNull(command);

        lock (_lock)
        {
            _stack.Push(command);
        }
    }

    public bool TryPop([NotNullWhen(true)] out T? command)
    {
        lock (_lock)
        {
            return _stack.TryPop(out command);
        }
    }

    public T? Peek()
    {
        lock (_lock)
        {
            return _stack.TryPeek(out var command) ? command : null;
        }
    }

    public IReadOnlyList<T> Snapshot()
    {
        lock (_lock)
        {
            // Stack<T> enumerates from the top, so the copy is already newest-first.
            return new List<T>(_stack);
        }
    }
}
