using System;
using System.Buffers;
using System.Buffers.Binary;
using System.Collections.Generic;

namespace BITKit.Multiplayer
{
// Bounded scratch space ONLY. CopyOwned transfers exact-length bytes to IRoomWire/state;
// the scratch rental is returned even when MemoryPack/packet encoding throws.
internal sealed class BinaryBufferWriter : IBufferWriter<byte>, IDisposable
{
    private readonly ArrayPool<byte> _pool;
    private int _maximum;
    private byte[]? _buffer;
    private int _count;
    internal int WrittenCount => _count;
    internal ReadOnlySpan<byte> WrittenSpan => _buffer == null ? throw new ObjectDisposedException(nameof(BinaryBufferWriter)) : _buffer.AsSpan(0, _count);
    [ThreadStatic] private static Stack<BinaryBufferWriter>? _cache;
    private bool _cachedLease;
    internal static BinaryBufferWriter Rent(int maximum)
    {
        if (maximum < 32) throw new ArgumentOutOfRangeException(nameof(maximum));
        var cache = _cache;
        var writer = cache != null && cache.Count != 0 ? cache.Pop() : new BinaryBufferWriter(maximum);
        writer._maximum = maximum; writer._count = 0;
        if (writer._buffer == null) writer._buffer = writer._pool.Rent(32);
        writer._cachedLease = true;
        return writer;
    }
    internal void PatchInt32(int offset, int value)
    {
        if (_buffer == null) throw new ObjectDisposedException(nameof(BinaryBufferWriter));
        if (offset < 0 || offset > _count - 4) throw new ArgumentOutOfRangeException(nameof(offset));
        BinaryPrimitives.WriteInt32LittleEndian(_buffer.AsSpan(offset, 4), value);
    }

    internal BinaryBufferWriter(int maximum, int initialSize = 32, ArrayPool<byte>? pool = null)
    {
        if (maximum < 1 || initialSize < 1 || initialSize > maximum) throw new ArgumentOutOfRangeException(nameof(maximum));
        _pool = pool ?? ArrayPool<byte>.Shared;
        _maximum = maximum;
        _buffer = _pool.Rent(initialSize);
    }
    private void Ensure(int hint)
    {
        if (_buffer == null) throw new ObjectDisposedException(nameof(BinaryBufferWriter));
        if (hint < 0 || Math.Max(hint, 1) > _maximum - _count)
            throw new RpcException(RpcError.LimitExceeded, "Binary buffer exceeds configured limit");
        var needed = _count + Math.Max(hint, 1);
        if (needed <= _buffer.Length) return;
        var desired = Math.Min(_maximum, Math.Max(needed, _buffer.Length <= _maximum / 2 ? _buffer.Length * 2 : _maximum));
        var replacement = _pool.Rent(desired);
        Buffer.BlockCopy(_buffer, 0, replacement, 0, _count);
        _pool.Return(_buffer);
        _buffer = replacement;
    }
    public Span<byte> GetSpan(int sizeHint = 0) { Ensure(sizeHint); return _buffer!.AsSpan(_count); }
    public Memory<byte> GetMemory(int sizeHint = 0) { Ensure(sizeHint); return _buffer!.AsMemory(_count); }
    public void Advance(int count)
    {
        if (_buffer == null) throw new ObjectDisposedException(nameof(BinaryBufferWriter));
        if (count < 0 || count > _maximum - _count || count > _buffer.Length - _count)
            throw new RpcException(RpcError.LimitExceeded, "Invalid binary buffer advance");
        _count += count;
    }
    internal void Write(ReadOnlySpan<byte> source)
    {
        source.CopyTo(GetSpan(source.Length));
        Advance(source.Length);
    }
    internal byte[] CopyOwned()
    {
        if (_buffer == null) throw new ObjectDisposedException(nameof(BinaryBufferWriter));
        return _buffer.AsSpan(0, _count).ToArray();
    }
    public void Dispose()
    {
        var buffer = _buffer;
        _buffer = null;
        if (buffer != null)
        {
            _pool.Return(buffer);
            if (_cachedLease)
            {
                _cachedLease = false;
                var cache = _cache ??= new Stack<BinaryBufferWriter>(8);
                if (cache.Count < 8) cache.Push(this);
            }
        }
    }
}
}
