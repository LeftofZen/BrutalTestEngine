using Brutal;
using Brutal.VulkanApi;
using Brutal.VulkanApi.Abstractions;
using Brutal.VulkanApi.Vma;

namespace BrutalTestEngine.Compute;

/// <summary>
/// A GPU buffer holding <typeparamref name="T"/> elements, sized and allocated for compute use.
/// </summary>
/// <remarks>
/// <para>
/// Compute work is expressed in terms of buffers, not vertices: a weather grid, an NPC transform array, a
/// particle pool. This wraps the allocation, the upload and the read-back so a caller writes
/// <c>new ComputeBuffer&lt;float&gt;(allocator, 256 * 256, "weather")</c> and then <c>Upload</c> or
/// <c>Download</c>, rather than repeating the VMA dance at every call site.
/// </para>
/// <para>
/// Memory is host-visible and persistently mapped. That is the right default for the engine's compute work:
/// the simulation thread produces data on the CPU and the render thread consumes it on the GPU, so the
/// buffers are written and read every frame and a device-local staging copy would cost more than it saves.
/// A device-local variant can be added when a buffer becomes large enough that the transfer dominates.
/// </para>
/// </remarks>
public sealed unsafe class ComputeBuffer<T> : IDisposable
    where T : unmanaged
{
    private readonly VmaAllocator _allocator;
    private readonly VmaAllocationInfo _allocationInfo;
    private bool _disposed;

    /// <summary>
    /// Allocates a buffer for <paramref name="elementCount"/> elements.
    /// </summary>
    /// <param name="allocator">The renderer's allocator. Buffers must be created on the render thread.</param>
    /// <param name="elementCount">Number of elements. Must be positive.</param>
    /// <param name="name">A name for the allocation, so a validation message can identify it.</param>
    /// <param name="usage">
    /// Extra usage flags. <see cref="VkBufferUsageFlags.StorageBufferBit"/> is always added, because a
    /// compute buffer that cannot be bound as a storage buffer is not useful.
    /// </param>
    public ComputeBuffer(
        VmaAllocator allocator,
        int elementCount,
        string name,
        VkBufferUsageFlags usage = VkBufferUsageFlags.None
    )
    {
        ArgumentNullException.ThrowIfNull(allocator);
        ArgumentOutOfRangeException.ThrowIfLessThan(elementCount, 1);
        ArgumentException.ThrowIfNullOrWhiteSpace(name);

        _allocator = allocator;
        ElementCount = elementCount;
        Size = ByteSize.Of<T>(elementCount);

        allocator.CreateBuffer(
            new VkBufferCreateInfo
            {
                Size = Size,
                Usage = usage | VkBufferUsageFlags.StorageBufferBit | VkBufferUsageFlags.TransferSrcBit
                    | VkBufferUsageFlags.TransferDstBit,
            },
            new VmaAllocationCreateInfo
            {
                Usage = VmaMemoryUsage.Auto,
                Flags =
                    VmaAllocationCreateFlags.HostAccessSequentialWriteBit | VmaAllocationCreateFlags.MappedBit,
            },
            out var buffer,
            out var allocation,
            out _allocationInfo
        );

        Buffer = buffer;
        Allocation = allocation;
        Name = name;
    }

    /// <summary>The underlying Vulkan buffer, for descriptor writes.</summary>
    public VkBuffer Buffer { get; }

    /// <summary>The allocation backing <see cref="Buffer"/>.</summary>
    public VmaAllocation Allocation { get; }

    /// <summary>Number of elements the buffer holds.</summary>
    public int ElementCount { get; }

    /// <summary>Size of the buffer in bytes.</summary>
    public ByteSize Size { get; }

    /// <summary>The name given at construction.</summary>
    public string Name { get; }

    /// <summary>
    /// Writes <paramref name="data"/> into the buffer and makes it visible to the GPU.
    /// </summary>
    /// <remarks>
    /// The copy is a plain memory write into mapped memory, so it is cheap, but it is not synchronised
    /// against the GPU: the caller must not upload to a buffer the GPU is still reading. In this engine that
    /// is guaranteed by construction, because uploads happen on the render thread between frames.
    /// </remarks>
    public void Upload(ReadOnlySpan<T> data) => Upload(data, 0);

    /// <summary>
    /// Writes <paramref name="data"/> into the buffer starting at <paramref name="destinationOffset"/>.
    /// </summary>
    /// <remarks>
    /// The offset overload exists for pools that are written a piece at a time. A pool large enough to hold
    /// every resident object is far larger than what changes in a frame, so re-uploading all of it would
    /// spend the frame's bandwidth on bytes that did not move.
    /// </remarks>
    public void Upload(ReadOnlySpan<T> data, int destinationOffset)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        ArgumentOutOfRangeException.ThrowIfNegative(destinationOffset);
        ArgumentOutOfRangeException.ThrowIfGreaterThan(destinationOffset + data.Length, ElementCount);

        var mapped = new Span<T>((void*)_allocationInfo.PMappedData, ElementCount);
        data.CopyTo(mapped[destinationOffset..]);

        // A no-op on host-coherent memory, required otherwise. Flushing only the written range keeps the
        // cost proportional to the upload rather than to the pool.
        _allocator.FlushAllocation(
            Allocation,
            ByteSize.Of<T>(destinationOffset),
            ByteSize.Of<T>(data.Length)
        );
    }

    /// <summary>
    /// Reads the buffer's contents into <paramref name="destination"/>. Returns the number of elements read.
    /// </summary>
    /// <remarks>
    /// Invalidating first is what makes the read see the GPU's writes rather than a stale CPU cache line.
    /// </remarks>
    public int Download(Span<T> destination)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);

        _allocator.InvalidateAllocation(Allocation, ByteSize.Zero, Size);

        var count = Math.Min(ElementCount, destination.Length);
        new ReadOnlySpan<T>((void*)_allocationInfo.PMappedData, count).CopyTo(destination);
        return count;
    }

    /// <summary>
    /// A view over the mapped memory, for callers that want to read or write in place without a copy.
    /// </summary>
    /// <remarks>
    /// The span is only valid while the buffer is alive and the GPU is not using it. It exists so a
    /// simulation step can read a field it just dispatched without materialising an intermediate array.
    /// </remarks>
    public Span<T> Mapped()
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        return new Span<T>((void*)_allocationInfo.PMappedData, ElementCount);
    }

    public void Dispose()
    {
        if (_disposed)
            return;

        _disposed = true;
        _allocator.DestroyBuffer(Buffer, Allocation);
    }
}
