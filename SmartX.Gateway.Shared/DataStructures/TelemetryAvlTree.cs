using SmartX.Gateway.Shared.Models;

namespace SmartX.Gateway.Shared.DataStructures;

public class TelemetryAvlTree
{
    private class AvlNode
    {
        public TelemetryReading<double> Data { get; }
        public int Height { get; set; } = 1;
        public AvlNode? Left { get; set; }
        public AvlNode? Right { get; set; }

        public AvlNode(TelemetryReading<double> data)
        {
            Data = data;
        }
    }

    private readonly object _lock = new();
    private readonly int _maxReadings;
    private AvlNode? _root;
    private int _count;

    public TelemetryAvlTree(int maxReadings = int.MaxValue)
    {
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(maxReadings);
        _maxReadings = maxReadings;
    }

    public int Count
    {
        get
        {
            lock (_lock)
            {
                return _count;
            }
        }
    }

    public void Insert(TelemetryReading<double> reading)
    {
        ArgumentNullException.ThrowIfNull(reading);

        lock (_lock)
        {
            _root = Insert(_root, reading);
            _count++;

            // Like SortedTelemetryLog, the index keeps only the newest readings; the leftmost node is always the oldest.
            while (_count > _maxReadings && _root is not null)
            {
                _root = RemoveMin(_root);
                _count--;
            }
        }
    }

    public void Clear()
    {
        lock (_lock)
        {
            _root = null;
            _count = 0;
        }
    }

    private static AvlNode? RemoveMin(AvlNode node)
    {
        if (node.Left is null)
        {
            return node.Right;
        }

        node.Left = RemoveMin(node.Left);
        return Balance(node);
    }

    public List<TelemetryReading<double>> GetRange(DateTime start, DateTime end)
    {
        var result = new List<TelemetryReading<double>>();

        lock (_lock)
        {
            CollectRange(_root, start, end, result);
        }

        return result;
    }

    private static AvlNode Insert(AvlNode? node, TelemetryReading<double> reading)
    {
        if (node is null)
        {
            return new AvlNode(reading);
        }

        if (reading.Timestamp < node.Data.Timestamp)
        {
            node.Left = Insert(node.Left, reading);
        }
        else
        {
            node.Right = Insert(node.Right, reading);
        }

        return Balance(node);
    }

    // Rotations can move a duplicate timestamp to either side of its twin, so equality must explore both subtrees.
    private static void CollectRange(AvlNode? node, DateTime start, DateTime end, List<TelemetryReading<double>> result)
    {
        if (node is null)
        {
            return;
        }

        var timestamp = node.Data.Timestamp;

        if (timestamp >= start)
        {
            CollectRange(node.Left, start, end, result);
        }

        if (timestamp >= start && timestamp <= end)
        {
            result.Add(node.Data);
        }

        if (timestamp <= end)
        {
            CollectRange(node.Right, start, end, result);
        }
    }

    private static int GetHeight(AvlNode? node) => node?.Height ?? 0;

    private static int GetBalanceFactor(AvlNode node) => GetHeight(node.Left) - GetHeight(node.Right);

    private static void UpdateHeight(AvlNode node)
    {
        node.Height = 1 + Math.Max(GetHeight(node.Left), GetHeight(node.Right));
    }

    private static AvlNode RotateRight(AvlNode node)
    {
        var pivot = node.Left!;
        node.Left = pivot.Right;
        pivot.Right = node;

        UpdateHeight(node);
        UpdateHeight(pivot);

        return pivot;
    }

    private static AvlNode RotateLeft(AvlNode node)
    {
        var pivot = node.Right!;
        node.Right = pivot.Left;
        pivot.Left = node;

        UpdateHeight(node);
        UpdateHeight(pivot);

        return pivot;
    }

    private static AvlNode Balance(AvlNode node)
    {
        UpdateHeight(node);
        var balanceFactor = GetBalanceFactor(node);

        if (balanceFactor > 1)
        {
            if (GetBalanceFactor(node.Left!) < 0)
            {
                node.Left = RotateLeft(node.Left!);
            }

            return RotateRight(node);
        }

        if (balanceFactor < -1)
        {
            if (GetBalanceFactor(node.Right!) > 0)
            {
                node.Right = RotateRight(node.Right!);
            }

            return RotateLeft(node);
        }

        return node;
    }
}
