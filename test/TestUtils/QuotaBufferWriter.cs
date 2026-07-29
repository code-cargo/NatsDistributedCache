using System.Buffers;

namespace CodeCargo.Nats.DistributedCache.TestUtils;

/// <summary>
/// An <see cref="IBufferWriter{T}"/> that throws once more than <see cref="_maxLength"/> bytes are written,
/// mirroring the quota behaviour of the <c>RecyclableArrayBufferWriter</c> HybridCache hands to
/// <c>IBufferDistributedCache.TryGetAsync</c> (whose <c>Advance</c> throws
/// <see cref="InvalidOperationException"/> "Max length exceeded" past its payload limit). Used to exercise
/// the destination-writer-failure read path.
/// </summary>
public sealed class QuotaBufferWriter : IBufferWriter<byte>
{
    private readonly ArrayBufferWriter<byte> _inner = new();
    private readonly int _maxLength;

    public QuotaBufferWriter(int maxLength) => _maxLength = maxLength;

    /// <summary>Gets the number of bytes committed so far (a throwing Advance commits nothing).</summary>
    public int WrittenCount => _inner.WrittenCount;

    public void Advance(int count)
    {
        if (_inner.WrittenCount + count > _maxLength)
        {
            throw new InvalidOperationException("Max length exceeded");
        }

        _inner.Advance(count);
    }

    public Memory<byte> GetMemory(int sizeHint = 0) => _inner.GetMemory(sizeHint);

    public Span<byte> GetSpan(int sizeHint = 0) => _inner.GetSpan(sizeHint);
}
