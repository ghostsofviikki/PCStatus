namespace PCStatus;

/// <summary>Fixed-size ring buffer of the last N samples (0–100).</summary>
public sealed class History(int capacity = 60)
{
    private readonly float[] _values = new float[capacity];
    private int _start;

    public int Capacity => capacity;
    public int Count { get; private set; }

    public void Add(float v)
    {
        if (Count < capacity)
        {
            _values[(_start + Count) % capacity] = v;
            Count++;
        }
        else
        {
            _values[_start] = v;
            _start = (_start + 1) % capacity;
        }
    }

    /// <summary>Oldest to newest.</summary>
    public float this[int i] => _values[(_start + i) % capacity];
}
