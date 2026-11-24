using Brutal;
using Brutal.VulkanApi;
using Brutal.VulkanApi.Abstractions;
using Brutal.VulkanApi.Vma;

namespace BrutalTestEngine.Compute;

/// <summary>
/// A storage buffer bound to a compute pipeline at a given descriptor binding.
/// </summary>
/// <param name="Binding">The descriptor set binding index, matching the shader's <c>layout(binding = N)</c>.</param>
/// <param name="Buffer">The buffer to bind.</param>
public readonly record struct ComputeBinding(int Binding, VkBuffer Buffer, ByteSize64 Size);

/// <summary>
/// A ready-to-dispatch compute pipeline: shader, descriptor set, layout and pipeline, created together.
/// </summary>
/// <remarks>
/// <para>
/// Vulkan requires a pipeline, a pipeline layout, a descriptor set layout and a descriptor set to be built
/// in a specific order and kept alive together. Doing that by hand at every compute call site is where the
/// boilerplate lives, so this type owns all four and exposes the two operations a caller actually wants:
/// bind some buffers, then dispatch.
/// </para>
/// <para>
/// The descriptor set is allocated once and rewritten in place by <see cref="SetBuffer"/>. That is safe
/// because the engine records compute work on the render thread between frames, so no dispatch is in flight
/// while the set is being updated; it avoids allocating a set per frame, which would exhaust the pool.
/// </para>
/// </remarks>
public sealed unsafe class ComputePipeline : IDisposable
{
    private readonly DeviceEx _device;
    private readonly ComputeShader _shader;
    private readonly DescriptorSetLayoutEx _setLayout;
    private readonly DescriptorPoolEx _pool;
    private readonly VkDescriptorSet _descriptorSet;
    private readonly VkPipelineLayout _layout;
    private readonly VkPipeline _pipeline;
    private readonly Dictionary<int, ComputeBinding> _bindings = [];
    private bool _disposed;

    /// <summary>
    /// Builds a compute pipeline from a shader and a set of storage-buffer bindings.
    /// </summary>
    /// <param name="device">The device to create the pipeline on.</param>
    /// <param name="allocator">The allocator for the pipeline layout.</param>
    /// <param name="shader">The compiled shader.</param>
    /// <param name="bindings">
    /// The storage buffers the shader reads or writes. The descriptor set layout is derived from these, so
    /// the shader and the layout cannot drift apart.
    /// </param>
    /// <param name="pushConstantSize">
    /// Size in bytes of the push constant block, or zero when the shader takes none. Push constants are the
    /// cheapest way to pass per-dispatch parameters such as a grid size or a time step.
    /// </param>
    public ComputePipeline(
        DeviceEx device,
        VmaAllocator allocator,
        ComputeShader shader,
        ReadOnlySpan<ComputeBinding> bindings,
        int pushConstantSize = 0
    )
    {
        ArgumentNullException.ThrowIfNull(device);
        ArgumentNullException.ThrowIfNull(allocator);
        ArgumentNullException.ThrowIfNull(shader);
        ArgumentOutOfRangeException.ThrowIfNegative(pushConstantSize);

        _device = device;
        _shader = shader;

        var layoutBindings = new VkDescriptorSetLayoutBinding[bindings.Length];

        // Pool sizes are per descriptor type, not per binding: two storage-buffer bindings need one pool
        // size with a count of two, and passing two entries of one each makes the second allocation fail.
        var countsByType = new Dictionary<VkDescriptorType, int>();

        for (var i = 0; i < bindings.Length; i++)
        {
            layoutBindings[i] = new VkDescriptorSetLayoutBinding
            {
                Binding = bindings[i].Binding,
                DescriptorType = VkDescriptorType.StorageBuffer,
                DescriptorCount = 1,
                StageFlags = VkShaderStageFlags.ComputeBit,
            };

            countsByType[VkDescriptorType.StorageBuffer] =
                countsByType.GetValueOrDefault(VkDescriptorType.StorageBuffer) + 1;
        }

        var poolSizes = new VkDescriptorPoolSize[countsByType.Count];
        var poolIndex = 0;
        foreach (var (type, count) in countsByType)
        {
            poolSizes[poolIndex++] = new VkDescriptorPoolSize { Type = type, DescriptorCount = count };
        }

        _setLayout = device.CreateDescriptorSetLayout(
            new DescriptorSetLayoutEx.CreateInfo { Bindings = layoutBindings },
            null
        );

        _pool = device.CreateDescriptorPool(
            new DescriptorPoolEx.CreateInfo { MaxSets = 1, PoolSizes = poolSizes },
            null
        );

        _descriptorSet = device.AllocateDescriptorSet(_pool, _setLayout);

        Span<VkDescriptorSetLayout> setLayouts = [_setLayout.Handle];
        Span<VkPushConstantRange> pushConstants =
            pushConstantSize > 0
                ?
                [
                    new VkPushConstantRange
                    {
                        StageFlags = VkShaderStageFlags.ComputeBit,
                        Offset = new ByteSize32(0),
                        Size = new ByteSize32((uint)pushConstantSize),
                    },
                ]
                : [];

        _layout = device.CreatePipelineLayout(setLayouts, pushConstants, null);

        _pipeline = device.CreateComputePipeline(
            VkPipelineCache.NullHandle,
            new VkComputePipelineCreateInfo
            {
                Stage = Presets.ShaderStage.Compute(shader.Module),
                Layout = _layout,
            },
            null
        );

        // Bind every buffer up front so the set is never left with an unbound descriptor, which validation
        // reports as an error even if the shader never reads it.
        foreach (var binding in bindings)
            SetBuffer(binding);
    }

    /// <summary>The shader this pipeline runs.</summary>
    public ComputeShader Shader => _shader;

    /// <summary>
    /// Points a binding at a buffer, rewriting the descriptor set in place.
    /// </summary>
    /// <remarks>
    /// The buffer must outlive the dispatch. Nothing here keeps it alive, because the caller owns the
    /// buffers and the engine's compute buffers live for the lifetime of the pass that uses them.
    /// </remarks>
    public void SetBuffer(in ComputeBinding binding)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);

        _bindings[binding.Binding] = binding;

        var info = new VkDescriptorBufferInfo
        {
            Buffer = binding.Buffer,
            Offset = new ByteSize64(0),
            Range = binding.Size,
        };

        Span<VkWriteDescriptorSet> writes =
        [
            new VkWriteDescriptorSet
            {
                DstSet = _descriptorSet,
                DstBinding = binding.Binding,
                DstArrayElement = 0,
                DescriptorCount = 1,
                DescriptorType = VkDescriptorType.StorageBuffer,
                BufferInfo = &info,
            },
        ];

        _device.UpdateDescriptorSets(writes, []);
    }

    /// <summary>
    /// Records the pipeline bind, the descriptor set bind and a dispatch into <paramref name="commandBuffer"/>.
    /// </summary>
    /// <param name="commandBuffer">The command buffer being recorded on the render thread.</param>
    /// <param name="groupCountX">Number of workgroups in X.</param>
    /// <param name="groupCountY">Number of workgroups in Y.</param>
    /// <param name="groupCountZ">Number of workgroups in Z.</param>
    public void Dispatch(CommandBuffer commandBuffer, int groupCountX, int groupCountY = 1, int groupCountZ = 1)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        ArgumentOutOfRangeException.ThrowIfLessThan(groupCountX, 1);
        ArgumentOutOfRangeException.ThrowIfLessThan(groupCountY, 1);
        ArgumentOutOfRangeException.ThrowIfLessThan(groupCountZ, 1);

        commandBuffer.BindPipeline(VkPipelineBindPoint.Compute, _pipeline);
        commandBuffer.BindDescriptorSet(VkPipelineBindPoint.Compute, _layout, 0, _descriptorSet, []);
        commandBuffer.Dispatch(groupCountX, groupCountY, groupCountZ);
    }

    /// <summary>
    /// Records a dispatch sized to cover <paramref name="elementCount"/> elements at
    /// <paramref name="localSizeX"/> invocations per workgroup.
    /// </summary>
    /// <remarks>
    /// The rounding up is the part that is easy to get wrong by hand: a dispatch that covers fewer
    /// workgroups than the data needs silently leaves the tail unprocessed, and one that covers more relies
    /// on the shader's own bounds check. This makes the intent explicit at the call site.
    /// </remarks>
    public void DispatchFor(
        CommandBuffer commandBuffer,
        int elementCount,
        int localSizeX,
        int localSizeY = 1,
        int localSizeZ = 1
    )
    {
        ArgumentOutOfRangeException.ThrowIfLessThan(elementCount, 0);
        ArgumentOutOfRangeException.ThrowIfLessThan(localSizeX, 1);
        ArgumentOutOfRangeException.ThrowIfLessThan(localSizeY, 1);
        ArgumentOutOfRangeException.ThrowIfLessThan(localSizeZ, 1);

        if (elementCount == 0)
            return;

        Dispatch(
            commandBuffer,
            DivideRoundUp(elementCount, localSizeX * localSizeY * localSizeZ)
        );
    }

    /// <summary>Records a push constant write for this pipeline's layout.</summary>
    public void PushConstants<T>(CommandBuffer commandBuffer, in T data)
        where T : unmanaged =>
        commandBuffer.PushConstants(_layout, VkShaderStageFlags.ComputeBit, ByteSize.Zero, data);

    /// <summary>
    /// The number of workgroups needed to cover <paramref name="elementCount"/> elements.
    /// </summary>
    /// <remarks>
    /// Exposed because the same arithmetic is needed to size a dispatch and to size the buffer a shader
    /// writes, and having one implementation means the two cannot disagree.
    /// </remarks>
    public static int DivideRoundUp(int elementCount, int elementsPerGroup)
    {
        ArgumentOutOfRangeException.ThrowIfLessThan(elementCount, 0);
        ArgumentOutOfRangeException.ThrowIfLessThan(elementsPerGroup, 1);

        return (elementCount + elementsPerGroup - 1) / elementsPerGroup;
    }

    public void Dispose()
    {
        if (_disposed)
            return;

        _disposed = true;
        _device.DestroyPipeline(_pipeline, null);
        _device.DestroyPipelineLayout(_layout, null);
        _pool.Dispose();
        _setLayout.Dispose();
    }
}
