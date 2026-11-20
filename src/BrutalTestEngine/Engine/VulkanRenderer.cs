using Brutal;
using Brutal.GlfwApi;
using Brutal.Numerics;
using Brutal.ShaderCApi;
using Brutal.Strings;
using Brutal.VulkanApi;
using Brutal.VulkanApi.Abstractions;
using Brutal.VulkanApi.Vma;
using BrutalTestEngine.Shaders;
using BrutalTestEngine.Simulation;
using System.Runtime.InteropServices;

namespace BrutalTestEngine.Engine;

/// <summary>
/// The push constant block the triangle's vertex shader reads. Layout must match the block declared
/// in <see cref="TriangleShaders.Vertex"/>: three 32-bit floats padded out to 16 bytes.
/// </summary>
[StructLayout(LayoutKind.Sequential, Size = TriangleShaders.PushConstantSize)]
internal struct TrianglePushConstants
{
    public float Rotation;
    public float Scale;
    public float Aspect;
}

/// <summary>
/// Owns the Vulkan instance, device, swapchain, render pass and the demo triangle pipeline.
/// The swapchain is rebuilt whenever the window's framebuffer size changes, including when the
/// window switches between windowed and borderless fullscreen.
/// </summary>
public sealed unsafe class VulkanRenderer : IDisposable
{
    /// <summary>
    /// Frames the CPU may have in flight. Each one owns a command buffer, an acquire semaphore,
    /// a render-finished semaphore and a fence.
    /// </summary>
    private const int FramesInFlight = 2;

    private const int InitialWidth = 1280;
    private const int InitialHeight = 720;

    private readonly Instance _instance;
    private readonly VkSurfaceKHR _surface;
    private readonly PhysicalDevice _physicalDevice;
    private readonly DeviceEx _device;
    private readonly Queue _queue;
    private readonly int _queueFamily;

    private readonly VkCommandPool _commandPool;
    private readonly CommandBuffer[] _commandBuffers;
    private readonly VkSemaphore[] _imageAvailable;
    private readonly VkFence[] _frameFences;

        // Presentation operations do not retire a signal semaphore, so the render-finished semaphores
        // must be per swapchain image and rebuilt with it.
        private VkSemaphore[] _renderFinished = [];

    private readonly VmaAllocator _allocator;
    private readonly VkBuffer _vertexBuffer;
    private readonly VmaAllocation _vertexAllocation;

    private readonly ShaderStages _shaderStages;
    private readonly VkPipelineLayout _pipelineLayout;
    private readonly VkPipeline _trianglePipeline;

    private SwapchainState _swapchain;
    private int2 _framebufferSize;
    private int _frameIndex;
    private bool _swapchainOutOfDate;
    private bool _disposed;
    private readonly VkFormat _format;

    // The main thread requests resizes while the render thread renders, so the requested size is
    // handed over through these fields instead of being applied directly. The render thread picks it
    // up at the start of its next frame, which is the only place swapchain resources may be touched.
    private readonly object _resizeGate = new();
    private int2 _pendingResize;
    private bool _resizePending;

    private VulkanRenderer(
        Instance instance,
        VkSurfaceKHR surface,
        PhysicalDevice physicalDevice,
        DeviceEx device,
        Queue queue,
        int queueFamily,
        int2 requestedSize
    )
    {
        _instance = instance;
        _surface = surface;
        _physicalDevice = physicalDevice;
        _device = device;
        _queue = queue;
        _queueFamily = queueFamily;

        var properties = physicalDevice.GetProperties();
        DeviceName = Utf8.GetString(properties.DeviceName);

        _commandPool = device.CreateCommandPool(
            new VkCommandPoolCreateInfo
            {
                QueueFamilyIndex = queueFamily,
                Flags = VkCommandPoolCreateFlags.ResetCommandBufferBit,
            },
            null
        );

        _commandBuffers = new CommandBuffer[FramesInFlight];
        for (var i = 0; i < _commandBuffers.Length; i++)
        {
            _commandBuffers[i] = device.AllocateCommandBuffer(
                new VkCommandBufferAllocateInfo
                {
                    CommandPool = _commandPool,
                    Level = VkCommandBufferLevel.Primary,
                }
            );
        }

        _imageAvailable = new VkSemaphore[FramesInFlight];
        _frameFences = new VkFence[FramesInFlight];
        for (var i = 0; i < FramesInFlight; i++)
        {
            _imageAvailable[i] = device.CreateSemaphore(new VkSemaphoreCreateInfo(), null);
            _frameFences[i] = device.CreateFence(VkFenceCreateFlags.SignaledBit, null);
        }

        var initialSwapchain =
            CreateSwapchain(default, requestedSize)
            ?? throw new InvalidOperationException(
                "The window surface has no usable area, so no swapchain could be created."
            );
        _format = initialSwapchain.Format;
        RenderPass = CreateRenderPass(initialSwapchain.Extent);
        _swapchain = CreateFramebuffers(initialSwapchain);
        _framebufferSize = requestedSize;
        _renderFinished = CreateRenderFinishedSemaphores(initialSwapchain.ImageViews.Length);

        _allocator = device.CreateVmaAllocator(
            new VmaAllocatorCreateInfo
            {
                Instance = instance,
                PhysicalDevice = physicalDevice,
                Device = device,
                VulkanApiVersion = (uint)VulkanHelpers.Api.VERSION_1_3,
            }
        );

        (_vertexBuffer, _vertexAllocation) = CreateTriangleVertexBuffer(_allocator);

        using var compiler = ShaderC.CreateCompiler();
        using var compileOptions = ShaderC.CreateCompilerOptions();
        _shaderStages = new ShaderStages(
            device,
            [
                Presets.ShaderStage.Vertex(
                    CompileShader(device, compiler, compileOptions, TriangleShaders.Vertex, ShaderKind.VertexShader)
                ),
                Presets.ShaderStage.Fragment(
                    CompileShader(device, compiler, compileOptions, TriangleShaders.Fragment, ShaderKind.FragmentShader)
                ),
            ],
            null
        );

        _pipelineLayout = device.CreatePipelineLayout([], [PushConstantRange], null);
        _trianglePipeline = CreateTrianglePipeline(device, _pipelineLayout, _shaderStages, RenderPass);
    }

    /// <summary>
    /// The push constant block the vertex shader reads: rotation, scale and aspect ratio, each a
    /// 32-bit float. Written per frame on the render thread, so no buffer upload is needed.
    /// </summary>
    private static VkPushConstantRange PushConstantRange =>
        new()
        {
            StageFlags = VkShaderStageFlags.VertexBit,
            Offset = new ByteSize32(0),
            Size = new ByteSize32(TriangleShaders.PushConstantSize),
        };

    public DeviceEx Device => _device;

    public Queue Queue => _queue;

    /// <summary>
    /// The name the driver reports for the selected physical device.
    /// </summary>
    public string DeviceName { get; }

    /// <summary>
    /// The single-subpass render pass that both the triangle and Dear ImGui record into. It has no
    /// size dependency, so it survives swapchain rebuilds. Clears on load and leaves the image in
    /// <see cref="VkImageLayout.PresentSrcKHR"/>.
    /// </summary>
    public VkRenderPass RenderPass { get; }

    public VkExtent2D Extent => _swapchain.Extent;

    public int ImageCount => _swapchain.ImageViews.Length;

    /// <summary>
    /// Lower bound on swapchain images, taken from the surface capabilities.
    /// </summary>
    public int MinImageCount { get; private set; }

    /// <summary>
    /// True when there is a swapchain that can be rendered into. A minimised window reports a
    /// zero-area surface, so frames are skipped until it comes back.
    /// </summary>
    public bool IsSwapchainUsable =>
        !_swapchainOutOfDate && _swapchain.Extent.Width > 0 && _swapchain.Extent.Height > 0;

    /// <summary>
    /// Raised after the swapchain is rebuilt, so renderer backends can pick up the new image count.
    /// </summary>
    public event Action? SwapchainRecreated;

    public static VulkanRenderer Create(GlfwWindow window, int2 requestedSize, bool enableValidation)
    {
        // GLFW looks for Brutal's own Vulkan loader (libVulkanEx), so touch Vulkan first to load it.
        _ = Vulkan.GetInstanceProcAddr;

        if (!Glfw.VulkanSupported())
            throw new InvalidOperationException("Vulkan is not available: no loader or ICD was found");

        var instanceExtensions = new HashSet<string>();
        VulkanHelpers.AddPlatformRequiredExtensions(instanceExtensions);

        var glfwExtensions = Glfw.GetRequiredInstanceExtensions(out var glfwExtensionCount);
        for (var i = 0; i < glfwExtensionCount; i++)
            instanceExtensions.Add(Utf8.GetString(glfwExtensions[i]));

        var layers = new HashSet<string>();
        if (enableValidation && VulkanHelpers.Validation.IsSupported)
            VulkanHelpers.AddValidationLayers(layers);

        var instance = Vulkan.CreateInstance(
            new Instance.CreateInfo
            {
                AppInfo = new VkApplicationInfo { ApiVersion = (int)VulkanHelpers.Api.VERSION_1_3 },
                Flags = VulkanHelpers.GetPlatformRequiredInstanceCreateFlags(),
                EnabledExtensions = instanceExtensions,
                EnabledLayers = layers,
            }
        );

        try
        {
            var surface = CreateSurface(instance, window);
            var (physicalDevice, queueFamily) = SelectPhysicalDevice(instance, surface);

            var deviceExtensions = new HashSet<string>();
            VulkanHelpers.AddSwapchainExtensions(deviceExtensions);
            VulkanHelpers.AddMandatoryIfSupportedDeviceExtensions(deviceExtensions, physicalDevice);

            var priority = 1.0f;
            var device = physicalDevice.CreateDevice(
                new DeviceEx.CreateInfo
                {
                    Extensions = deviceExtensions,
                    QueueCreateInfos =
                    [
                        new VkDeviceQueueCreateInfo
                        {
                            QueueFamilyIndex = queueFamily,
                            QueueCount = 1,
                            QueuePriorities = &priority,
                        },
                    ],
                }
            );

            var queue = device.GetQueue(queueFamily, 0);

            return new VulkanRenderer(instance, surface, physicalDevice, device, queue, queueFamily, requestedSize);
        }
        catch
        {
            instance.Dispose();
            throw;
        }
    }

    /// <summary>
    /// Requests a swapchain rebuild for the given framebuffer size. Safe to call from the main thread
    /// while the render thread is rendering: the request is recorded and applied by the render thread
    /// at the start of its next frame, because swapchain resources may only be touched there.
    /// Does nothing while the framebuffer has no area, e.g. while minimised.
    /// </summary>
    public void RequestResize(int2 framebufferSize)
    {
        lock (_resizeGate)
        {
            _pendingResize = framebufferSize;
            _resizePending = true;
        }
    }

    /// <summary>
    /// Applies a pending resize. Must only be called from the render thread, before a frame is
    /// recorded. Returns true when the swapchain was rebuilt.
    /// </summary>
    private bool ApplyPendingResize()
    {
        int2 requested;
        lock (_resizeGate)
        {
            if (!_resizePending)
                return false;

            requested = _pendingResize;
            _resizePending = false;
        }

        if (requested.X <= 0 || requested.Y <= 0)
        {
            // Minimised: there is nothing to draw into. Keep the old swapchain, and skip frames
            // until a real size comes back.
            _swapchainOutOfDate = true;
            return false;
        }

        if (requested == _framebufferSize && !_swapchainOutOfDate)
            return false;

        RecreateSwapchain(requested);
        return true;
    }

    /// <summary>
    /// Records and submits one frame: clears the acquired swapchain image, draws the triangle using
    /// the simulation's latest state, then lets ImGui record its draw lists into the same render pass
    /// before presenting. Returns false when the frame could not be presented and the swapchain is
    /// about to be rebuilt.
    /// </summary>
    /// <param name="gui">Records the ImGui draw data into the open render pass.</param>
    /// <param name="snapshot">The simulation state to draw. Read only; never mutated here.</param>
    public bool RenderFrame(IImGuiRenderer gui, SimulationSnapshot snapshot)
    {
        // Swapchain resources may only be touched on this thread, so a resize requested by the main
        // thread is applied here rather than where it was requested.
        ApplyPendingResize();

        if (!IsSwapchainUsable)
            return false;

        var frame = _frameIndex;
        _frameIndex = (_frameIndex + 1) % FramesInFlight;

        var fence = _frameFences[frame];
        var imageAvailable = _imageAvailable[frame];

        _device.WaitForFence(fence, -1);

        // Acquire before resetting the fence, so an out-of-date swapchain doesn't leave it unsignalled.
        int imageIndex;
        try
        {
            _device.AcquireNextImageKHR(_swapchain.Swapchain, -1, imageAvailable, VkFence.NullHandle, out imageIndex);
        }
        catch (VkResultError error) when (error.Result == VkResult.ErrorOutOfDateKHR)
        {
            _swapchainOutOfDate = true;
            return false;
        }

        _device.ResetFences([fence]);

        var commandBuffer = _commandBuffers[frame];
        commandBuffer.Reset();
        commandBuffer.Begin(VkCommandBufferUsageFlags.OneTimeSubmitBit);

        var extent = _swapchain.Extent;
        var clearValue = new VkClearValue
        {
            Color = new VkClearColorValue { Float32 = new float4(0.09f, 0.10f, 0.13f, 1.0f) },
        };

        commandBuffer.BeginRenderPass(
            new VkRenderPassBeginInfo
            {
                RenderPass = RenderPass,
                Framebuffer = _swapchain.Framebuffers[imageIndex],
                RenderArea = new VkRect2D(extent),
                ClearValueCount = 1,
                ClearValues = &clearValue,
            },
            VkSubpassContents.Inline
        );

        // Viewport and scissor are dynamic, so a resize doesn't invalidate the pipeline.
        var viewport = new VkViewport
        {
            Width = extent.Width,
            Height = extent.Height,
            MaxDepth = 1,
        };
        var scissor = new VkRect2D(extent);

        commandBuffer.BindPipeline(VkPipelineBindPoint.Graphics, _trianglePipeline);
        commandBuffer.SetViewport(0, [viewport]);
        commandBuffer.SetScissor(0, [scissor]);

        // The simulation's state reaches the shader through a push constant, so the render thread
        // never has to wait on a buffer upload or share memory with the simulation thread.
        var pushConstants = new TrianglePushConstants
        {
            Rotation = snapshot.RotationRadians,
            Scale = snapshot.Scale,
            Aspect = extent.Height == 0 ? 1.0f : (float)extent.Width / extent.Height,
        };
        commandBuffer.PushConstants(_pipelineLayout, VkShaderStageFlags.VertexBit, ByteSize.Zero, pushConstants);

        commandBuffer.BindVertexBuffers(0, [_vertexBuffer], [new ByteSize64(0)]);
        commandBuffer.Draw(3, 1, 0, 0);

        gui.Render(commandBuffer);

        commandBuffer.EndRenderPass();
        commandBuffer.End();

        var signalSemaphore = _renderFinished[imageIndex];
        _queue.Submit(
            [imageAvailable],
            [VkPipelineStageFlags.ColorAttachmentOutputBit],
            [commandBuffer],
            [signalSemaphore],
            fence
        );

        var swapchain = _swapchain.Swapchain;
        var presentInfo = new VkPresentInfoKHR
        {
            WaitSemaphoreCount = 1,
            WaitSemaphores = &signalSemaphore,
            SwapchainCount = 1,
            Swapchains = &swapchain,
            ImageIndices = &imageIndex,
        };

        try
        {
            _queue.PresentKHR(presentInfo);
        }
        catch (VkResultError error)
            when (error.Result is VkResult.ErrorOutOfDateKHR or VkResult.ErrorSurfaceLostKHR)
        {
            // The image was submitted; the swapchain just needs rebuilding before the next frame.
            _swapchainOutOfDate = true;
            return false;
        }

        return true;
    }

    public void WaitIdle() => _device.WaitIdle();

    public void Dispose()
    {
        if (_disposed)
            return;

        _disposed = true;

        _device.WaitIdle();

        _allocator.DestroyBuffer(_vertexBuffer, _vertexAllocation);
        _device.DestroyVmaAllocator(_allocator);

        foreach (var fence in _frameFences)
            _device.DestroyFence(fence, null);
        foreach (var semaphore in _imageAvailable)
            _device.DestroySemaphore(semaphore, null);
        foreach (var semaphore in _renderFinished)
            _device.DestroySemaphore(semaphore, null);
        _device.DestroyCommandPool(_commandPool, null);
        _shaderStages.Dispose();
        _device.DestroyPipeline(_trianglePipeline, null);
        _device.DestroyPipelineLayout(_pipelineLayout, null);

        DestroyFramebuffers(_swapchain);
        _device.DestroyRenderPass(RenderPass, null);

        _instance.DestroySurfaceKHR(_surface, null);

        _device.Dispose();
        _instance.Dispose();
    }

    private void RecreateSwapchain(int2 framebufferSize)
    {
        _device.WaitIdle();

        var oldSwapchain = _swapchain;
        var next = CreateSwapchain(oldSwapchain.Swapchain, framebufferSize);
        if (next is null)
        {
            // The surface still has no area (the window is minimised or mid-restore). Keep the
            // existing swapchain and retry once a real size arrives.
            _swapchainOutOfDate = true;
            return;
        }

        DestroyFramebuffers(oldSwapchain);

        // The signal semaphores are indexed by image, so they are rebuilt with the images.
        foreach (var semaphore in _renderFinished)
            _device.DestroySemaphore(semaphore, null);
        _renderFinished = CreateRenderFinishedSemaphores(next.ImageViews.Length);

        _swapchain = CreateFramebuffers(next);
        _framebufferSize = framebufferSize;
        _swapchainOutOfDate = false;
        SwapchainRecreated?.Invoke();
    }

    private VkSemaphore[] CreateRenderFinishedSemaphores(int imageCount)
    {
        var semaphores = new VkSemaphore[imageCount];
        for (var i = 0; i < semaphores.Length; i++)
            semaphores[i] = _device.CreateSemaphore(new VkSemaphoreCreateInfo(), null);
        return semaphores;
    }

    /// <summary>
    /// Creates a swapchain, its images and image views. Framebuffers are added by
    /// <see cref="CreateFramebuffers"/>, because they need the render pass.
    /// </summary>
    private SwapchainState? CreateSwapchain(VkSwapchainKHR oldSwapchain, int2 requestedSize)
    {
        var capabilities = _physicalDevice.GetSurfaceCapabilitiesKHR(_surface);
            var surfaceFormat =
                _swapchain is null
                    ? SelectSurfaceFormat(_physicalDevice, _surface)
                    : new VkSurfaceFormatKHR { Format = _format, ColorSpace = VkColorSpaceKHR.SRGBNonlinearKHR };

        MinImageCount = Math.Max(capabilities.MinImageCount, 2);
        var imageCount = MinImageCount;
        if (capabilities.MaxImageCount > 0)
            imageCount = Math.Min(imageCount, capabilities.MaxImageCount);

        // A minimised window reports a zero-area current extent, and neither a swapchain nor a
        // framebuffer may be created with zero width or height. Hold the last usable size instead:
        // the frame is skipped and nothing is presented until a real size comes back.
        var extent = ResolveExtent(capabilities, requestedSize.X, requestedSize.Y);
        if (extent.Width <= 0 || extent.Height <= 0)
            extent = ResolveExtent(capabilities, _framebufferSize.X, _framebufferSize.Y);
        if (extent.Width <= 0 || extent.Height <= 0)
            extent = ResolveExtent(capabilities, InitialWidth, InitialHeight);
        if (extent.Width <= 0 || extent.Height <= 0)
            return null;

        var swapchain = _device.CreateSwapchainKHR(
            new VkSwapchainCreateInfoKHR
            {
                Surface = _surface,
                MinImageCount = imageCount,
                ImageFormat = surfaceFormat.Format,
                ImageColorSpace = surfaceFormat.ColorSpace,
                ImageExtent = extent,
                ImageArrayLayers = 1,
                ImageUsage = VkImageUsageFlags.ColorAttachmentBit,
                ImageSharingMode = VkSharingMode.Exclusive,
                PreTransform = capabilities.CurrentTransform,
                CompositeAlpha = SelectCompositeAlpha(capabilities),
                PresentMode = VkPresentModeKHR.FifoKHR,
                Clipped = true,
                OldSwapchain = oldSwapchain,
            },
            null
        );

        var images = new VkImage[_device.GetSwapchainImagesKHR_Count(swapchain)];
        _device.GetSwapchainImagesKHR_Write(swapchain, images);

        var colorRange = new VkImageSubresourceRange
        {
            AspectMask = VkImageAspectFlags.ColorBit,
            LevelCount = 1,
            LayerCount = 1,
        };

        var imageViews = new VkImageView[images.Length];
        for (var i = 0; i < images.Length; i++)
        {
            imageViews[i] = _device.CreateImageView(
                new VkImageViewCreateInfo
                {
                    Image = images[i],
                    ViewType = VkImageViewType._2D,
                    Format = surfaceFormat.Format,
                    SubresourceRange = colorRange,
                },
                null
            );
        }

        return new SwapchainState
        {
            Swapchain = swapchain,
            ImageViews = imageViews,
            Framebuffers = [],
            Extent = extent,
                    Format = surfaceFormat.Format,
        };
    }

    private SwapchainState CreateFramebuffers(SwapchainState state)
    {
        var framebuffers = new VkFramebuffer[state.ImageViews.Length];
        for (var i = 0; i < state.ImageViews.Length; i++)
        {
            var attachment = state.ImageViews[i];
            framebuffers[i] = _device.CreateFramebuffer(
                new VkFramebufferCreateInfo
                {
                    RenderPass = RenderPass,
                    AttachmentCount = 1,
                    Attachments = &attachment,
                    Width = state.Extent.Width,
                    Height = state.Extent.Height,
                    Layers = 1,
                },
                null
            );
        }

        return new SwapchainState
        {
            Swapchain = state.Swapchain,
            ImageViews = state.ImageViews,
            Framebuffers = framebuffers,
            Extent = state.Extent,
                    Format = state.Format,
        };
    }

    private void DestroyFramebuffers(SwapchainState state)
    {
        foreach (var framebuffer in state.Framebuffers)
            _device.DestroyFramebuffer(framebuffer, null);
        foreach (var imageView in state.ImageViews)
            _device.DestroyImageView(imageView, null);
        _device.DestroySwapchainKHR(state.Swapchain, null);
    }

    private static VkSurfaceKHR CreateSurface(Instance instance, GlfwWindow window)
    {
        var result = Glfw.CreateWindowSurface(instance.VkHandle, window, out var surfaceHandle);
        if (result != (int)VkResult.Success)
            throw new InvalidOperationException($"Failed to create a Vulkan surface for the window ({result})");

        return new VkSurfaceKHR((nint)surfaceHandle);
    }

    private static (PhysicalDevice Device, int QueueFamily) SelectPhysicalDevice(
        Instance instance,
        VkSurfaceKHR surface
    )
    {
        foreach (var candidate in instance.PhysicalDevices)
        {
            var families = new VkQueueFamilyProperties[candidate.GetQueueFamilyProperties_Count()];
            candidate.GetQueueFamilyProperties_Write(families);

            for (var i = 0; i < families.Length; i++)
            {
                if (
                    (families[i].QueueFlags & VkQueueFlags.GraphicsBit) != 0
                    && candidate.GetSurfaceSupportKHR(i, surface)
                )
                    return (candidate, i);
            }
        }

        throw new InvalidOperationException(
            "No Vulkan device has a queue family that can both draw and present to the window"
        );
    }

    private static VkSurfaceFormatKHR SelectSurfaceFormat(PhysicalDevice physicalDevice, VkSurfaceKHR surface)
    {
        VkFormat[] preferred = [VkFormat.B8G8R8A8UNorm, VkFormat.R8G8B8A8UNorm, VkFormat.B8G8R8A8SRGB];

        var count = physicalDevice.GetSurfaceFormatsKHR_Count(surface);
        Span<VkSurfaceFormatKHR> formats = stackalloc VkSurfaceFormatKHR[count];
        physicalDevice.GetSurfaceFormatsKHR_Write(surface, formats);

        // A single VkFormat.Undefined entry means any format is acceptable.
        if (formats.Length == 1 && formats[0].Format == VkFormat.Undefined)
            return new VkSurfaceFormatKHR { Format = preferred[0], ColorSpace = VkColorSpaceKHR.SRGBNonlinearKHR };

        foreach (var format in preferred)
        foreach (var available in formats)
            if (available.Format == format && available.ColorSpace == VkColorSpaceKHR.SRGBNonlinearKHR)
                return available;

        return formats[0];
    }

    private static VkExtent2D ResolveExtent(
        VkSurfaceCapabilitiesKHR capabilities,
        int requestedWidth,
        int requestedHeight
    )
    {
        // 0xFFFFFFFF (-1 as a signed int) means the surface lets the swapchain pick its own size.
        // A defined but zero extent means the surface currently has no area (a minimised window),
        // so fall through to the requested size instead of returning an unusable 0x0 extent.
        if (capabilities.CurrentExtent.Width > 0 && capabilities.CurrentExtent.Height > 0)
            return capabilities.CurrentExtent;

        return new VkExtent2D
        {
            Width = Math.Clamp(requestedWidth, capabilities.MinImageExtent.Width, capabilities.MaxImageExtent.Width),
            Height = Math.Clamp(requestedHeight, capabilities.MinImageExtent.Height, capabilities.MaxImageExtent.Height),
        };
    }

    private static VkCompositeAlphaFlagsKHR SelectCompositeAlpha(VkSurfaceCapabilitiesKHR capabilities)
    {
        VkCompositeAlphaFlagsKHR[] modes =
        [
            VkCompositeAlphaFlagsKHR.OpaqueBitKHR,
            VkCompositeAlphaFlagsKHR.PreMultipliedBitKHR,
            VkCompositeAlphaFlagsKHR.PostMultipliedBitKHR,
            VkCompositeAlphaFlagsKHR.InheritBitKHR,
        ];

        foreach (var mode in modes)
            if ((capabilities.SupportedCompositeAlpha & mode) != 0)
                return mode;

        return VkCompositeAlphaFlagsKHR.OpaqueBitKHR;
    }

    private VkRenderPass CreateRenderPass(VkExtent2D extent)
    {
            // A render pass has no size dependency, only a format one; the extent parameter documents
            // that the initial swapchain created it, and rebuilds reuse it as-is.
        _ = extent;

        var colorAttachment = new VkAttachmentDescription
        {
                Format = _format,
            Samples = VkSampleCountFlags._1Bit,
            LoadOp = VkAttachmentLoadOp.Clear,
            StoreOp = VkAttachmentStoreOp.Store,
            StencilLoadOp = VkAttachmentLoadOp.DontCare,
            StencilStoreOp = VkAttachmentStoreOp.DontCare,
            InitialLayout = VkImageLayout.Undefined,
            FinalLayout = VkImageLayout.PresentSrcKHR,
        };

        var colorReference = new VkAttachmentReference
        {
            Attachment = 0,
            Layout = VkImageLayout.ColorAttachmentOptimal,
        };

        var subpass = new VkSubpassDescription
        {
            PipelineBindPoint = VkPipelineBindPoint.Graphics,
            ColorAttachmentCount = 1,
            ColorAttachments = &colorReference,
        };

        // Wait for the previous use of the swapchain image before writing to it...
        var toAttachment = new VkSubpassDependency
        {
            SrcSubpass = VK.SUBPASS_EXTERNAL,
            DstSubpass = 0,
            SrcStageMask = VkPipelineStageFlags.ColorAttachmentOutputBit,
            DstStageMask = VkPipelineStageFlags.ColorAttachmentOutputBit,
            SrcAccessMask = VkAccessFlags.None,
            DstAccessMask = VkAccessFlags.ColorAttachmentWriteBit,
        };

        // ...and make those writes visible to the presentation engine afterwards.
        var toPresent = new VkSubpassDependency
        {
            SrcSubpass = 0,
            DstSubpass = VK.SUBPASS_EXTERNAL,
            SrcStageMask = VkPipelineStageFlags.ColorAttachmentOutputBit,
            DstStageMask = VkPipelineStageFlags.BottomOfPipeBit,
            SrcAccessMask = VkAccessFlags.ColorAttachmentWriteBit,
            DstAccessMask = VkAccessFlags.None,
        };

        Span<VkSubpassDependency> dependencies = [toAttachment, toPresent];
        fixed (VkSubpassDependency* dependenciesPtr = dependencies)
        {
            return _device.CreateRenderPass(
                new VkRenderPassCreateInfo
                {
                    AttachmentCount = 1,
                    Attachments = &colorAttachment,
                    SubpassCount = 1,
                    Subpasses = &subpass,
                    DependencyCount = dependencies.Length,
                    Dependencies = dependenciesPtr,
                },
                null
            );
        }
    }

    private static (VkBuffer Buffer, VmaAllocation Allocation) CreateTriangleVertexBuffer(VmaAllocator allocator)
    {
        ReadOnlySpan<Vertex> vertices =
        [
            new(new float2(0.0f, -0.5f), new float3(1.0f, 0.05f, 0.05f)),
            new(new float2(0.5f, 0.5f), new float3(0.05f, 1.0f, 0.05f)),
            new(new float2(-0.5f, 0.5f), new float3(0.05f, 0.05f, 1.0f)),
        ];

        var size = ByteSize.Of(vertices);

        allocator.CreateBuffer(
            new VkBufferCreateInfo { Size = size, Usage = VkBufferUsageFlags.VertexBufferBit },
            new VmaAllocationCreateInfo
            {
                Usage = VmaMemoryUsage.Auto,
                Flags =
                    VmaAllocationCreateFlags.HostAccessSequentialWriteBit | VmaAllocationCreateFlags.MappedBit,
            },
            out var buffer,
            out var allocation,
            out var allocationInfo
        );

        vertices.CopyTo(new Span<Vertex>((void*)allocationInfo.PMappedData, vertices.Length));
        // A no-op on host-coherent memory, required otherwise.
        allocator.FlushAllocation(allocation, ByteSize.Zero, size);

        return (buffer, allocation);
    }

    private static VkShaderModule CompileShader(
        DeviceEx device,
        Compiler compiler,
        CompileOptions options,
        ReadOnlySpan<byte> glsl,
        ShaderKind kind
    )
    {
        using var spirv = compiler.Compile(glsl, kind, "triangle"u8, "main"u8, options);
        return ShaderModuleExtensions.FromSpirv(
            device,
            new ReadOnlySpan<byte>(spirv.GetBytes(), (int)spirv.GetLength()),
            null
        );
    }

    private static VkPipeline CreateTrianglePipeline(
        DeviceEx device,
        VkPipelineLayout layout,
        ShaderStages stages,
        VkRenderPass renderPass
    )
    {
        var vertexInput = new VertexInput(1, 2).AddBindingWithAttributes<Vertex>(0, 0, VkVertexInputRate.Vertex);

        // Viewport and scissor are dynamic: the extent changes whenever the swapchain is rebuilt.
        Span<VkDynamicState> dynamicStates = [VkDynamicState.Viewport, VkDynamicState.Scissor];
                fixed (VkDynamicState* dynamicStatesPtr = dynamicStates)
        {
                    var dynamicState = new VkPipelineDynamicStateCreateInfo
                    {
                        DynamicStateCount = dynamicStates.Length,
                        DynamicStates = dynamicStatesPtr,
                    };

                    // Counts must be set even with dynamic state, but the pointers are ignored.
                    var viewportState = new VkPipelineViewportStateCreateInfo { ViewportCount = 1, ScissorCount = 1 };

                    return device.CreateGraphicsPipeline(
                        VkPipelineCache.NullHandle,
                        new VkGraphicsPipelineCreateInfo
                        {
                            StageCount = stages.Count,
                            Stages = stages,
                            VertexInputState = vertexInput,
                            InputAssemblyState = Presets.InputAssembly.TriangleList,
                            ViewportState = &viewportState,
                            RasterizationState = Presets.Rasterization.Fill.CullNone,
                            MultisampleState = Presets.Multisample.None,
                            ColorBlendState = Presets.BlendState.BlendNone,
                            DynamicState = &dynamicState,
                            Layout = layout,
                            RenderPass = renderPass,
                            Subpass = 0,
                        },
                        null
                    );
                }
    }
}