using Brutal;
using Brutal.GlfwApi;
using Brutal.Numerics;
using Brutal.ShaderCApi;
using Brutal.Strings;
using Brutal.VulkanApi;
using Brutal.VulkanApi.Abstractions;
using Brutal.VulkanApi.Vma;
using BrutalTestEngine.Compute;
using BrutalTestEngine.Jobs;
using BrutalTestEngine.Shaders;
using BrutalTestEngine.Logging;
using BrutalTestEngine.Simulation;
using BrutalTestEngine.World;
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
/// The push constant block the demo compute shader reads: the element count, the step size, and two floats
/// of padding. Mirrors the block declared in <see cref="ComputeShaders.FieldStep"/>.
/// </summary>
[StructLayout(LayoutKind.Sequential, Size = ComputeShaders.FieldStepPushConstantSize)]
internal struct FieldStepPushConstants
{
    public uint ElementCount;
    public float Step;
}

/// <summary>
/// The push constant block the brick classification compute shader reads. Mirrors the block declared in
/// <see cref="ComputeShaders.BrickClassify"/>: an integer shape and a float metric pair, both padded to a
/// four-component stride so the GLSL and C# layouts agree byte for byte.
/// </summary>
[StructLayout(LayoutKind.Sequential, Size = ComputeShaders.BrickClassifyPushConstantSize)]
internal struct BrickClassifyPushConstants
{
    public int VoxelCount;
    public int SampleCount;
    public int BrickCount;
    public int ReservedShape;

    public float BrickSize;
    public float VoxelSize;
    public float ReservedMetricX;
    public float ReservedMetricY;

    public float OriginX;
    public float OriginY;
    public float OriginZ;
    public float ReservedOrigin;
}

/// <summary>
/// The push constant block the terrain's vertex shader reads. Mirrors the block declared in
/// <see cref="TerrainShaders.Vertex"/>: a 4x4 view-projection matrix, the current brick's origin and
/// decode scale, and the camera position for fog.
/// </summary>
/// <remarks>
/// The view-projection is sent as sixteen separate floats rather than as a matrix type. GLSL's default 4x4
/// layout is column-major, while the managed matrix's <c>X</c>/<c>Y</c>/<c>Z</c>/<c>W</c> fields are its
/// <em>rows</em> under the row-vector convention (<c>v' = v * M</c>). Writing those rows down in order and
/// letting GLSL read them as columns is already the transpose, which is why <c>M_glsl * v</c> ends up
/// meaning <c>v * M_managed</c>. Transposing the matrix as well would apply the inverse rotation and put
/// the world behind the camera.
/// </remarks>
[StructLayout(LayoutKind.Sequential, Size = TerrainShaders.PushConstantSize)]
internal struct TerrainPushConstants
{
    public float M00,
        M01,
        M02,
        M03;
    public float M10,
        M11,
        M12,
        M13;
    public float M20,
        M21,
        M22,
        M23;
    public float M30,
        M31,
        M32,
        M33;

    public float OriginX;
    public float OriginY;
    public float OriginZ;
    public float DecodeScale;

    public float CameraX;
    public float CameraY;
    public float CameraZ;
    public float Reserved;
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

    /// <summary>
    /// Elements in the demo compute field. Small enough that the read-back is free, large enough that the
    /// dispatch spans more than one workgroup, which is the case the group arithmetic has to get right.
    /// </summary>
    private const int FieldElementCount = 4096;

    /// <summary>
    /// How many bricks the classification self-test submits. Small enough that the read-back is invisible,
    /// large enough to span many workgroups and several bricks that contain terrain and several that do not.
    /// </summary>
    private const int BrickClassifyBrickCount = 256;

    /// <summary>How many classification disagreements are logged individually before the summary.</summary>
    private const int DisagreementSampleLimit = 8;

    // Label colours are for a human reading a GPU capture: they make the passes visually distinct in
    // RenderDoc's timeline and Tracy's graph. Values are RGBA in 0..1.
    private static readonly float4 FrameLabelColor = new(0.35f, 0.45f, 0.65f, 1.0f);
    private static readonly float4 TriangleLabelColor = new(0.85f, 0.55f, 0.25f, 1.0f);
    private static readonly float4 ImGuiLabelColor = new(0.35f, 0.75f, 0.45f, 1.0f);
    private static readonly float4 TerrainLabelColor = new(0.85f, 0.55f, 0.30f, 1.0f);
    private static readonly float4 ComputeLabelColor = new(0.65f, 0.45f, 0.85f, 1.0f);

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

    // The terrain pipeline. It shares the render pass with the triangle and ImGui but is a separate pipeline
    // because it needs a depth attachment, no vertex input, and its own push constant layout.
    private readonly ShaderStages _terrainShaderStages;
    private readonly VkPipelineLayout _terrainPipelineLayout;
    private readonly VkPipeline _terrainPipeline;

    // The depth attachment. Without it the terrain's triangles would be drawn in whatever order the arena
    // handed them out, so a hill behind a ridge would be painted over it and the world would look inside out.
    private ImageEx _depthImage;
    private ImageViewEx _depthView;
    private readonly VkFormat _depthFormat;

    // The terrain's geometry, uploaded from the job system's committed pools. Both are uploaded only when the
    // job system's commit counter has moved, so a frame in which the workers produced nothing costs nothing
    // on the render thread.
    private readonly ComputeBuffer<byte> _terrainVertexBuffer;
    private readonly ComputeBuffer<uint> _terrainIndexBuffer;

    // The world the renderer draws. Set once by the host before the first frame and never mutated
    // afterwards, so the render thread reads it without a lock.
    private WorldJobSystem? _world;

    // The draw list, rebuilt on the frame thread and handed to the render thread through a gate. The
    // renderer must not walk the job system's brick records while workers are writing them, so the frame
    // thread publishes a copy of exactly the fields a draw needs.
    private readonly object _worldGate = new();
    private BrickDraw[] _draws = [];
    private int _drawCount;
    private long _publishedCommitCount = -1;

    // The camera, handed over by the main thread each frame through a gate because the render thread is the
    // only thread allowed to read it while it records.
    private readonly object _cameraGate = new();
    private float3 _cameraPosition = new(0.0f, 60.0f, 60.0f);
    private float3 _cameraTarget = new(0.0f, 20.0f, 0.0f);
    private float _cameraFieldOfView = 1.0472f;
    private bool _hasCamera;

    // The demo compute pass. It exists to prove the abstraction end to end: a real shader, a real dispatch,
    // and a real read-back, so the weather and NPC passes that follow are a shader swap rather than new
    // plumbing. It runs once, on the first frame, and then stops.
    private readonly ComputeShader _fieldStepShader;
    private readonly ComputePipeline _fieldStepPipeline;
    private readonly ComputeBuffer<float> _fieldInput;
    private readonly ComputeBuffer<float> _fieldOutput;
    private bool _fieldStepPending = true;
    private bool _fieldStepVerified;

    // The brick classification pass: the GPU half of the world pipeline. One workgroup per brick decides
    // whether the terrain passes through the brick at all, which is the pass that makes a clipmap of
    // millions of bricks affordable. It is verified against its CPU reference on the first frame.
    private readonly ComputeShader _brickClassifyShader;
    private readonly ComputePipeline _brickClassifyPipeline;
    private readonly ComputeBuffer<int4> _brickKeys;
    private readonly ComputeBuffer<int> _brickCrossings;
    private readonly BrickClassifyPushConstants _brickClassifyConstants;
    private bool _brickClassifySubmitted;
    private bool _brickClassifyVerified;

    private SwapchainState _swapchain;
    private int2 _framebufferSize;
    private int _frameIndex;
    private bool _swapchainOutOfDate;
    private bool _disposed;
    private readonly VkFormat _format;

    // A render-target capture is requested from another thread (the MCP server) and serviced on the render
    // thread, because it needs the frame's colour image while it is still readable. The request is a flag
    // plus a task the render thread completes; 0 means "no capture pending".
    //
    // Not volatile: every access is an Interlocked or Volatile call, which already supplies the barrier, and a
    // volatile modifier on a field passed by ref is a contradiction the compiler warns about (CS0420).
    private int _capturePending;
    private TaskCompletionSource<RenderTargetImage?>? _captureCompletion;
    private ComputeBuffer<byte>? _captureReadback;
    private int _captureReadbackBytes;

    // Tracing is decided once at creation: either the instance was built with VK_EXT_debug_utils or it was
    // not, and there is no way to add an instance extension afterwards.
    private readonly bool _tracingEnabled;
    private readonly Brutal.Logging.LogCategory _log = EngineLog.Categories.Vulkan;
    private VkDebugUtilsMessengerEXT _debugMessenger;

    /// <summary>
    /// RenderDoc's in-application API, or a no-op instance when RenderDoc is not loaded.
    /// </summary>
    public RenderDocCapture Capture { get; }

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
            int2 requestedSize,
            bool tracingEnabled,
            RenderDocCapture capture
    )
    {
        _instance = instance;
        _surface = surface;
        _physicalDevice = physicalDevice;
        _device = device;
        _queue = queue;
        _queueFamily = queueFamily;
        _tracingEnabled = tracingEnabled;
        Capture = capture;

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

        _allocator = device.CreateVmaAllocator(
            new VmaAllocatorCreateInfo
            {
                Instance = instance,
                PhysicalDevice = physicalDevice,
                Device = device,
                VulkanApiVersion = (uint)VulkanHelpers.Api.VERSION_1_3,
            }
        );

        // The depth attachment must exist before the render pass, because the pass declares its format, and
        // before the framebuffers, because they bind it. It is created at the initial swapchain's size and
        // recreated with it, since a framebuffer's attachments all have to match.
        _depthFormat = SelectDepthFormat();
        (_depthImage, _depthView) = CreateDepthResources(device, _allocator, _depthFormat, initialSwapchain.Extent);

        RenderPass = CreateRenderPass(initialSwapchain.Extent);
        _swapchain = CreateFramebuffers(initialSwapchain);
        _framebufferSize = requestedSize;
        _renderFinished = CreateRenderFinishedSemaphores(initialSwapchain.ImageViews.Length);

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

        _terrainShaderStages = new ShaderStages(
            device,
            [
                Presets.ShaderStage.Vertex(
                    CompileShader(
                        device,
                        compiler,
                        compileOptions,
                        TerrainShaders.Vertex,
                        ShaderKind.VertexShader
                    )
                ),
                Presets.ShaderStage.Fragment(
                    CompileShader(
                        device,
                        compiler,
                        compileOptions,
                        TerrainShaders.Fragment,
                        ShaderKind.FragmentShader
                    )
                ),
            ],
            null
        );

        // The terrain's descriptor set: two read-only storage buffers. Created before the pipeline layout,
        // because the layout declares the set and both must agree on the bindings.
        Span<VkDescriptorSetLayoutBinding> terrainBindings =
        [
            new VkDescriptorSetLayoutBinding
            {
                Binding = 0,
                DescriptorType = VkDescriptorType.StorageBuffer,
                DescriptorCount = 1,
                StageFlags = VkShaderStageFlags.VertexBit,
            },
            new VkDescriptorSetLayoutBinding
            {
                Binding = 1,
                DescriptorType = VkDescriptorType.StorageBuffer,
                DescriptorCount = 1,
                StageFlags = VkShaderStageFlags.VertexBit,
            },
        ];

        _terrainSetLayout = device.CreateDescriptorSetLayout(
            new DescriptorSetLayoutEx.CreateInfo { Bindings = terrainBindings },
            null
        );

        _terrainPool = device.CreateDescriptorPool(
            new DescriptorPoolEx.CreateInfo
            {
                MaxSets = 1,
                PoolSizes =
                [
                    new VkDescriptorPoolSize
                    {
                        Type = VkDescriptorType.StorageBuffer,
                        DescriptorCount = terrainBindings.Length,
                    },
                ],
            },
            null
        );

        _terrainDescriptors = device.AllocateDescriptorSet(_terrainPool, _terrainSetLayout);

        _terrainPipelineLayout = device.CreatePipelineLayout(
            [_terrainSetLayout],
            [TerrainPushConstantRange],
            null
        );

        _terrainPipeline = CreateTerrainPipeline(
            device,
            _terrainPipelineLayout,
            _terrainShaderStages,
            RenderPass
        );

        _terrainVertexBuffer = new ComputeBuffer<byte>(
            _allocator,
            TerrainVertexPoolBytes,
            "terrain-vertices",
            VkBufferUsageFlags.VertexBufferBit | VkBufferUsageFlags.StorageBufferBit
        );
        _terrainIndexBuffer = new ComputeBuffer<uint>(
            _allocator,
            TerrainIndexPoolWords,
            "terrain-indices",
            VkBufferUsageFlags.IndexBufferBit | VkBufferUsageFlags.StorageBufferBit
        );

        WriteTerrainDescriptors();

        _fieldStepShader = new ComputeShader(device, ComputeShaders.FieldStep, "field-step");
        _fieldInput = new ComputeBuffer<float>(_allocator, FieldElementCount, "field-input");
        _fieldOutput = new ComputeBuffer<float>(_allocator, FieldElementCount, "field-output");
        _fieldStepPipeline = new ComputePipeline(
            device,
            _allocator,
            _fieldStepShader,
            [
                new ComputeBinding(0, _fieldInput.Buffer, _fieldInput.Size),
                new ComputeBinding(1, _fieldOutput.Buffer, _fieldOutput.Size),
            ],
            ComputeShaders.FieldStepPushConstantSize
        );

        _fieldInput.Upload(CreateInitialField());

        _brickClassifyShader = new ComputeShader(device, ComputeShaders.BrickClassify, "brick-classify");
        _brickKeys = new ComputeBuffer<int4>(_allocator, BrickClassifyBrickCount, "brick-keys");
        _brickCrossings = new ComputeBuffer<int>(_allocator, BrickClassifyBrickCount, "brick-crossings");
        _brickClassifyPipeline = new ComputePipeline(
            device,
            _allocator,
            _brickClassifyShader,
            [
                new ComputeBinding(0, _brickKeys.Buffer, _brickKeys.Size),
                new ComputeBinding(1, _brickCrossings.Buffer, _brickCrossings.Size),
            ],
            ComputeShaders.BrickClassifyPushConstantSize
        );

        _brickClassifyConstants = BuildBrickClassifyConstants();
        _brickKeys.Upload(BuildBrickClassifyKeys());
        _brickCrossings.Upload(new int[BrickClassifyBrickCount]);

        // Installed last, so a validation message raised while the renderer is being built is reported by the
        // same pipeline everything else uses.
        if (tracingEnabled)
            _debugMessenger = VulkanDebugMessenger.Install(instance);
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

    /// <summary>
    /// The descriptor set layout the terrain's shaders read through: the packed vertex pool and the index
    /// pool, both as read-only storage buffers.
    /// </summary>
    /// <remarks>
    /// Storage buffers rather than vertex and index inputs, because the vertex format is four bytes of
    /// brick-local quantised data. Declaring a real vertex input would force a world-space position into the
    /// vertex, which is twelve bytes and which loses precision far from the origin; the pool keeps the vertex
    /// small and the brick's origin in a push constant, where it stays exact.
    /// </remarks>
    private readonly DescriptorSetLayoutEx _terrainSetLayout;
    private readonly DescriptorPoolEx _terrainPool;
    private readonly VkDescriptorSet _terrainDescriptors;

    /// <summary>
    /// The push constant block the terrain's vertex shader reads: a view-projection matrix, the brick being
    /// drawn and the camera position for fog. Written per brick on the render thread.
    /// </summary>
    private static VkPushConstantRange TerrainPushConstantRange =>
        new()
        {
            StageFlags = VkShaderStageFlags.VertexBit,
            Offset = new ByteSize32(0),
            Size = new ByteSize32(TerrainShaders.PushConstantSize),
        };

    /// <summary>
    /// How many bytes the terrain's vertex pool holds.
    /// </summary>
    /// <remarks>
    /// Sized from the arena's capacity rather than from a brick's worst case. A brick can theoretically
    /// produce <see cref="BrickShape.MaxMeshletsForBrick"/> meshlets, but real terrain produces one to four,
    /// so reserving the worst case per brick would waste roughly sixteen times the memory. The arena hands
    /// out runs from a pool of exactly this many meshlets, so this is the true upper bound.
    /// </remarks>
    private static int TerrainVertexPoolBytes =>
        WorldJobSystemOptions.Default.MaxResidentBricks
        * WorldJobSystemOptions.Default.MeshletsPerResidentBrick
        * MeshletBuilder.VertexStride
        * Meshlet.BytesPerVertex;

    /// <summary>How many indices the terrain's index pool holds.</summary>
    private static int TerrainIndexPoolWords =>
        WorldJobSystemOptions.Default.MaxResidentBricks
        * WorldJobSystemOptions.Default.MeshletsPerResidentBrick
        * MeshletBuilder.IndexStride;

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

    /// <summary>
    /// Creates a renderer for <paramref name="window"/>.
    /// </summary>
    /// <param name="window">The window whose surface is rendered into.</param>
    /// <param name="requestedSize">The initial framebuffer size.</param>
    /// <param name="enableValidation">Enables the Vulkan validation layer when it is installed.</param>
    /// <param name="enableTracing">
    /// Enables <c>VK_EXT_debug_utils</c>, the debug messenger and command-buffer debug labels. This is what
    /// makes a GPU capture readable: RenderDoc and Tracy both group work by the labels the application emits.
    /// </param>
    public static VulkanRenderer Create(
        GlfwWindow window,
        int2 requestedSize,
        bool enableValidation,
        bool enableTracing = false,
        bool enableRenderDoc = false,
        string? captureDirectory = null
    )
    {
        // GLFW looks for Brutal's own Vulkan loader (libVulkanEx), so touch Vulkan first to load it.
        _ = Vulkan.GetInstanceProcAddr;

        if (!Glfw.VulkanSupported())
            throw new InvalidOperationException("Vulkan is not available: no loader or ICD was found");

        // RenderDoc installs its hooks into the Vulkan loader, so it has to happen before the instance exists.
        // Loading it later attaches but produces captures with no device creation in them, which is the failure
        // its own documentation warns about. Loading is opt-in because the hooks cost performance and interfere
        // with the validation layers.
        var capture = enableRenderDoc ? RenderDocCapture.Load(captureDirectory) : RenderDocCapture.Disabled;

        var instanceExtensions = new HashSet<string>();
        VulkanHelpers.AddPlatformRequiredExtensions(instanceExtensions);

        var glfwExtensions = Glfw.GetRequiredInstanceExtensions(out var glfwExtensionCount);
        for (var i = 0; i < glfwExtensionCount; i++)
            instanceExtensions.Add(Utf8.GetString(glfwExtensions[i]));

        // The extension has to be enabled at instance creation and cannot be added later, so whether tracing
        // was asked for is settled here even though the messenger itself is created afterwards.
        var tracingEnabled = enableTracing;
        if (tracingEnabled)
            VulkanHelpers.AddDebugExtensions(instanceExtensions);

        var layers = new HashSet<string>();
        var validationAvailable = enableValidation && VulkanHelpers.Validation.IsSupported;
        if (validationAvailable)
            VulkanHelpers.AddValidationLayers(layers);

        var instanceLogger = EngineLog.Categories.Vulkan;
        if (enableValidation && !validationAvailable)
            instanceLogger.Warning(
                "validation layers were requested but are not installed; run with --trace for debug labels only"
            );

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

            return new VulkanRenderer(
                instance,
                surface,
                physicalDevice,
                device,
                queue,
                queueFamily,
                requestedSize,
                tracingEnabled,
                capture
            );
        }
        catch
        {
            instance.Dispose();
            throw;
        }
    }

    /// <summary>
    /// Attaches the world the renderer draws. Called once, before the first frame.
    /// </summary>
    /// <remarks>
    /// The renderer reads the job system's pools directly, because copying a multi-megabyte pool every
    /// frame to hand it over would cost more than the draw. That is safe because the pools are only ever
    /// written by the frame thread, and the render thread only reads them between frames.
    /// </remarks>
    public void AttachWorld(WorldJobSystem world)
    {
        ArgumentNullException.ThrowIfNull(world);

        _world = world;
    }

    /// <summary>
    /// Publishes the frame's draw list for the render thread to consume.
    /// </summary>
    /// <remarks>
    /// Called by the frame thread after committing world work. The list is copied rather than referenced
    /// because the job system reuses its array, and the render thread may still be reading the previous
    /// frame's copy when the next one is built.
    /// </remarks>
    public void PublishDraws(ReadOnlySpan<BrickDraw> draws, long commitCount)
    {
        lock (_worldGate)
        {
            if (_draws.Length < draws.Length)
                _draws = new BrickDraw[Math.Max(draws.Length, _draws.Length * 2)];

            draws.CopyTo(_draws);
            _drawCount = draws.Length;
            _publishedCommitCount = commitCount;
        }
    }

    /// <summary>
    /// Sets the camera the terrain is drawn from. Safe to call from the main thread at any time.
    /// </summary>
    public void SetCamera(float3 position, float3 target, float fieldOfViewRadians)
    {
        lock (_cameraGate)
        {
            _cameraPosition = position;
            _cameraTarget = target;
            _cameraFieldOfView = fieldOfViewRadians;
            _hasCamera = true;
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

        // The demo compute pass is verified once, on the frame after it was submitted, by reading the result
        // back and checking it against the same step computed on the CPU. A dispatch that silently does
        // nothing — a bad descriptor, a zero group count, a shader that returns early — produces no error and
        // no visible symptom, so the only way to know the abstraction works is to compare the output.
        if (_fieldStepPending == false && _fieldStepVerified == false)
        {
            _device.WaitIdle();
            VerifyFieldStep();
            _fieldStepVerified = true;
        }

        // The classification pass is verified the same way, on the frame after it was submitted.
        if (_brickClassifySubmitted && !_brickClassifyVerified)
        {
            _device.WaitIdle();
            VerifyBrickClassify();
            _brickClassifyVerified = true;
        }

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

        // Compute runs before the render pass opens, because a render pass is a graphics-only construct and
        // a dispatch cannot be recorded inside one. The barrier afterwards is what makes the render pass see
        // the compute pass's writes.
        if (_fieldStepPending)
        {
            RecordFieldStep(commandBuffer);
            _fieldStepPending = false;
        }

        // The classification pass runs once too, on the same first frame, and for the same reason: it is the
        // GPU half of the world pipeline and a dispatch that silently does nothing would not be noticed until
        // the terrain was missing.
        if (!_brickClassifySubmitted)
        {
            _brickClassifySubmitted = true;
            RecordBrickClassify(commandBuffer);
        }
        // The world's geometry is uploaded before the render pass opens, because a transfer cannot be
        // recorded inside one. Only the range the workers actually wrote is copied, so a frame in which
        // nothing was committed costs nothing here.
        UploadWorld();

        // Deliberately no StartFrameCapture here. Bracketing every frame writes a capture file per frame —
        // a 120-frame smoke run produced 120 .rdc files. Captures are requested instead, through the
        // `capture` command, and RenderDoc picks its own frame boundary for them.
        var extent = _swapchain.Extent;

        // Two clear values, in attachment order: colour then depth. The depth clear is what resets the
        // buffer each frame; without it the previous frame's depths would reject this frame's geometry.
        Span<VkClearValue> clearValues =
        [
            new VkClearValue { Color = new VkClearColorValue { Float32 = new float4(0.09f, 0.10f, 0.13f, 1.0f) } },
            new VkClearValue { DepthStencil = new VkClearDepthStencilValue { Depth = 1.0f, Stencil = 0 } },
        ];

        fixed (VkClearValue* clearValuesPtr = clearValues)
        {
            commandBuffer.BeginRenderPass(
                new VkRenderPassBeginInfo
                {
                    RenderPass = RenderPass,
                    Framebuffer = _swapchain.Framebuffers[imageIndex],
                    RenderArea = new VkRect2D(extent),
                    ClearValueCount = clearValues.Length,
                    ClearValues = clearValuesPtr,
                },
                VkSubpassContents.Inline
            );
        }

        // Debug labels bracket the work a GPU capture shows. Without them a RenderDoc or Tracy capture is an
        // undifferentiated list of draws; with them each pass is named and each is a separate row in the
        // timeline.
        //
        // Opened and closed explicitly rather than with `using`: DebugLabelScope is a struct whose Dispose
        // calls straight into vkCmdEndDebugUtilsLabelEXT, so the default value is not a safe no-op and cannot
        // stand in for "tracing is off". The paired calls below keep that unambiguous.
        if (_tracingEnabled)
            commandBuffer.BeginDebugLabel("Main pass"u8, FrameLabelColor);

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

        if (_tracingEnabled)
            commandBuffer.BeginDebugLabel("Triangle"u8, TriangleLabelColor);

        commandBuffer.Draw(3, 1, 0, 0);

        if (_tracingEnabled)
            commandBuffer.EndDebugLabel();

        RecordTerrain(commandBuffer, viewport, scissor);

        if (_tracingEnabled)
            commandBuffer.BeginDebugLabel("ImGui"u8, ImGuiLabelColor);

        gui.Render(commandBuffer);

        if (_tracingEnabled)
        {
            commandBuffer.EndDebugLabel();
            commandBuffer.EndDebugLabel();
        }

        commandBuffer.EndRenderPass();

        // Recorded after the render pass has closed so the copy is a transfer outside it, but inside the same
        // command buffer so the frame's fence also covers the copy.
        var captureRecorded = RecordCapture(commandBuffer, imageIndex);

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

        // A capture is a debug request, not part of the frame, so it is acceptable to block here until this
        // submission retires. Waiting on the fence that guards it is the only way to know the copy has
        // finished without reading stale bytes from a buffer the GPU is still writing.
        if (captureRecorded)
        {
            _device.WaitForFence(fence, -1);
            CompleteCapture();
        }

        return true;
    }

    public void WaitIdle() => _device.WaitIdle();

    /// <summary>
    /// Asks the render thread to capture the next completed frame and read it back as pixels.
    /// </summary>
    /// <param name="timeout">How long to wait for the capture to complete.</param>
    /// <param name="cancellationToken">Cancels the wait.</param>
    /// <returns>The captured frame, or null when the capture could not be taken.</returns>
    /// <remarks>
    /// <para>
    /// Safe to call from any thread: it publishes a request and waits. The render thread is the only one that
    /// touches swapchain images, so the copy is recorded inside <see cref="RenderFrame"/> and the read-back
    /// happens after that frame's fence has signalled. Capturing synchronously from the caller would mean
    /// recording a command buffer against an image index the render thread might be presenting.
    /// </para>
    /// <para>
    /// This is a request/response, not a stream. Only one capture is in flight at a time, because two would
    /// contend for the same readback buffer and there is no reason to ask for two frames at once.
    /// </para>
    /// </remarks>
    public Task<RenderTargetImage?> CaptureFrameAsync(
        TimeSpan timeout,
        CancellationToken cancellationToken = default
    )
    {
        if (_disposed)
            return Task.FromResult<RenderTargetImage?>(null);

        var completion = new TaskCompletionSource<RenderTargetImage?>(TaskCreationOptions.RunContinuationsAsynchronously);

        // A second request while one is pending replaces the waiter rather than queueing: the caller wants to
        // see the newest frame, and two captures of adjacent frames would be identical. The abandoned waiter
        // is completed with null so it cannot be left hanging.
        var previous = Interlocked.Exchange(ref _captureCompletion, completion);
        previous?.TrySetResult(null);

        Interlocked.Exchange(ref _capturePending, 1);

        return RenderTargetImage.WaitForCaptureAsync(completion.Task, timeout, cancellationToken);
    }

    /// <summary>
    /// Records the copy that fulfils a pending capture, if there is one, and returns whether it did.
    /// </summary>
    /// <remarks>
    /// Called inside the render pass's command buffer after the render pass has ended but before the buffer
    /// is submitted, so the copy lands in the same submission as the frame it captures and the fence that
    /// guards the frame also guards the copy.
    /// </remarks>
    private bool RecordCapture(CommandBuffer commandBuffer, int imageIndex)
    {
        if (Volatile.Read(ref _capturePending) == 0)
            return false;

        var extent = _swapchain.Extent;

        if (extent.Width == 0 || extent.Height == 0)
            return false;

        // The swapchain may have been rebuilt since the buffer was last used, so the readback buffer is
        // resized on demand rather than being trusted to match.
        var required = extent.Width * extent.Height * 4;

        if (_captureReadback is null || _captureReadbackBytes < required)
        {
            _captureReadback?.Dispose();
            _captureReadback = new ComputeBuffer<byte>(_allocator, required, "capture-readback");
            _captureReadbackBytes = required;
        }

        var image = _swapchain.Images[imageIndex];

        // The render pass leaves the image in PresentSrcKHR. Copying from it needs TransferSrcOptimal, and
        // the transition has to happen here because the render pass's own final layout is fixed.
        Span<VkImageMemoryBarrier> toTransfer =
        [
            new VkImageMemoryBarrier
            {
                SrcAccessMask = VkAccessFlags.ColorAttachmentWriteBit,
                DstAccessMask = VkAccessFlags.TransferReadBit,
                OldLayout = VkImageLayout.PresentSrcKHR,
                NewLayout = VkImageLayout.TransferSrcOptimal,
                SrcQueueFamilyIndex = -1,
                DstQueueFamilyIndex = -1,
                Image = image,
                SubresourceRange = new VkImageSubresourceRange
                {
                    AspectMask = VkImageAspectFlags.ColorBit,
                    BaseMipLevel = 0,
                    LevelCount = 1,
                    BaseArrayLayer = 0,
                    LayerCount = 1,
                },
            },
        ];

        commandBuffer.PipelineBarrier(
            VkPipelineStageFlags.ColorAttachmentOutputBit,
            VkPipelineStageFlags.TransferBit,
            VkDependencyFlags.None,
            [],
            [],
            toTransfer
        );

        Span<VkBufferImageCopy> regions =
        [
            new VkBufferImageCopy
            {
                BufferOffset = new ByteSize64(0),
                BufferRowLength = 0,
                BufferImageHeight = 0,
                ImageSubresource = new VkImageSubresourceLayers
                {
                    AspectMask = VkImageAspectFlags.ColorBit,
                    MipLevel = 0,
                    BaseArrayLayer = 0,
                    LayerCount = 1,
                },
                ImageOffset = new VkOffset3D(),
                ImageExtent = new VkExtent3D(extent.Width, extent.Height, 1),
            },
        ];

        commandBuffer.CopyImageToBuffer(
            image,
            VkImageLayout.TransferSrcOptimal,
            _captureReadback.Buffer,
            regions
        );

        // Return the image to the layout the presentation engine expects, because it is presented
        // immediately after this submission. Skipping this makes the next acquire fail validation.
        Span<VkImageMemoryBarrier> toPresent =
        [
            new VkImageMemoryBarrier
            {
                SrcAccessMask = VkAccessFlags.TransferReadBit,
                DstAccessMask = VkAccessFlags.None,
                OldLayout = VkImageLayout.TransferSrcOptimal,
                NewLayout = VkImageLayout.PresentSrcKHR,
                SrcQueueFamilyIndex = -1,
                DstQueueFamilyIndex = -1,
                Image = image,
                SubresourceRange = new VkImageSubresourceRange
                {
                    AspectMask = VkImageAspectFlags.ColorBit,
                    BaseMipLevel = 0,
                    LevelCount = 1,
                    BaseArrayLayer = 0,
                    LayerCount = 1,
                },
            },
        ];

        commandBuffer.PipelineBarrier(
            VkPipelineStageFlags.TransferBit,
            VkPipelineStageFlags.BottomOfPipeBit,
            VkDependencyFlags.None,
            [],
            [],
            toPresent
        );

        return true;
    }

    /// <summary>
    /// Reads a recorded capture back into managed memory and completes the waiting request.
    /// </summary>
    /// <remarks>
    /// Runs after the frame's fence has signalled, so the copy has finished and the mapped buffer holds this
    /// frame's pixels rather than the previous frame's.
    /// </remarks>
    private void CompleteCapture()
    {
        var completion = Interlocked.Exchange(ref _captureCompletion, null);

        if (Volatile.Read(ref _capturePending) == 0 || _captureReadback is null)
        {
            completion?.TrySetResult(null);
            return;
        }

        Volatile.Write(ref _capturePending, 0);

        var extent = _swapchain.Extent;
        var bytes = new byte[extent.Width * extent.Height * 4];

        _captureReadback.Download(bytes.AsSpan(0, bytes.Length));

        var isSrgb = _swapchain.Format
            is VkFormat.B8G8R8A8SRGB
                or VkFormat.R8G8B8A8SRGB;

        completion?.TrySetResult(new RenderTargetImage(extent.Width, extent.Height, bytes, isSrgb));
    }

    public void Dispose()
    {
        if (_disposed)
            return;

        _disposed = true;

        _device.WaitIdle();

        _allocator.DestroyBuffer(_vertexBuffer, _vertexAllocation);

        _fieldStepPipeline.Dispose();
        _fieldStepShader.Dispose();
        _fieldInput.Dispose();
        _fieldOutput.Dispose();

        _brickClassifyPipeline.Dispose();
        _brickClassifyShader.Dispose();
        _brickKeys.Dispose();
        _brickCrossings.Dispose();

        _terrainVertexBuffer.Dispose();
        _terrainIndexBuffer.Dispose();

        _captureReadback?.Dispose();
        _captureCompletion?.TrySetResult(null);

        // The depth image is freed through the allocator that created it, and its view through the device.
        // Order matters: the view refers to the image, so it goes first.
        _device.DestroyImageView(_depthView.VkImageView, null);
        _allocator.FreeAllocation(_depthImage.AllocationInfo);

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

        _terrainShaderStages.Dispose();
        _device.DestroyPipeline(_terrainPipeline, null);
        _device.DestroyPipelineLayout(_terrainPipelineLayout, null);
        _terrainPool.Dispose();
        _terrainSetLayout.Dispose();

        DestroyFramebuffers(_swapchain);
        _device.DestroyRenderPass(RenderPass, null);

        _instance.DestroySurfaceKHR(_surface, null);

        // The messenger must outlive every call that can trigger it, so it is destroyed after the device and
        // surface but before the instance itself; destroying the instance first would leave a dangling handle.
        if (!_debugMessenger.IsNull())
            VulkanDebugMessenger.Uninstall(_instance, _debugMessenger);

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

        // The depth image is sized to the swapchain, so it is rebuilt with it. The old one is destroyed
        // first: the device is idle, so nothing can still be reading it.
        _device.DestroyImageView(_depthView.VkImageView, null);
        _allocator.FreeAllocation(_depthImage.AllocationInfo);

        (_depthImage, _depthView) = CreateDepthResources(_device, _allocator, _depthFormat, next.Extent);

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
            Images = images,
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
            // The depth view is shared across every framebuffer: it is one image, reused each frame, because
            // only one frame's depth is ever needed and the fence for that frame guarantees the previous use
            // has finished. A per-image depth image would triple the memory for no gain.
            Span<VkImageView> attachments = [state.ImageViews[i], _depthView.VkImageView];

            fixed (VkImageView* attachmentsPtr = attachments)
            {
                framebuffers[i] = _device.CreateFramebuffer(
                    new VkFramebufferCreateInfo
                    {
                        RenderPass = RenderPass,
                        AttachmentCount = attachments.Length,
                        Attachments = attachmentsPtr,
                        Width = state.Extent.Width,
                        Height = state.Extent.Height,
                        Layers = 1,
                    },
                    null
                );
            }
        }

        return new SwapchainState
        {
            Swapchain = state.Swapchain,
            Images = state.Images,
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

        // The depth attachment is what makes the terrain's triangles occlude one another. Its layout is
        // cleared and left as a depth-stencil attachment, which is what the ImGui backend's pipeline expects
        // because it is created against this same pass.
        var depthAttachment = new VkAttachmentDescription
        {
            Format = _depthFormat,
            Samples = VkSampleCountFlags._1Bit,
            LoadOp = VkAttachmentLoadOp.Clear,
            StoreOp = VkAttachmentStoreOp.DontCare,
            StencilLoadOp = VkAttachmentLoadOp.DontCare,
            StencilStoreOp = VkAttachmentStoreOp.DontCare,
            InitialLayout = VkImageLayout.Undefined,
            FinalLayout = VkImageLayout.DepthStencilAttachmentOptimal,
        };

        var depthReference = new VkAttachmentReference
        {
            Attachment = 1,
            Layout = VkImageLayout.DepthStencilAttachmentOptimal,
        };

        var subpass = new VkSubpassDescription
        {
            PipelineBindPoint = VkPipelineBindPoint.Graphics,
            ColorAttachmentCount = 1,
            ColorAttachments = &colorReference,
            DepthStencilAttachment = &depthReference,
        };

        // Wait for the previous use of the swapchain image before writing to it...
        var toAttachment = new VkSubpassDependency
        {
            SrcSubpass = VK.SUBPASS_EXTERNAL,
            DstSubpass = 0,
            SrcStageMask = VkPipelineStageFlags.ColorAttachmentOutputBit | VkPipelineStageFlags.EarlyFragmentTestsBit,
            DstStageMask = VkPipelineStageFlags.ColorAttachmentOutputBit | VkPipelineStageFlags.EarlyFragmentTestsBit,
            SrcAccessMask = VkAccessFlags.None,
            DstAccessMask = VkAccessFlags.ColorAttachmentWriteBit | VkAccessFlags.DepthStencilAttachmentWriteBit,
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

        Span<VkAttachmentDescription> attachments = [colorAttachment, depthAttachment];

        fixed (VkAttachmentDescription* attachmentsPtr = attachments)
        fixed (VkSubpassDependency* dependenciesPtr = dependencies)
        {
            return _device.CreateRenderPass(
                new VkRenderPassCreateInfo
                {
                    AttachmentCount = attachments.Length,
                    Attachments = attachmentsPtr,
                    SubpassCount = 1,
                    Subpasses = &subpass,
                    DependencyCount = dependencies.Length,
                    Dependencies = dependenciesPtr,
                },
                null
            );
        }
    }

    /// <summary>
    /// Copies the world's newly committed geometry into the GPU pools.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Only the range the workers wrote since the last upload is copied. The pools are sized for every
    /// resident brick, which is far more than changes in a frame, so re-uploading all of them would spend
    /// the frame's bandwidth on bytes that did not move.
    /// </para>
    /// <para>
    /// The upload is a plain write into mapped memory, which is safe here because the render thread is
    /// between frames: the fence for this frame's command buffer has already been waited on, so no
    /// submission is still reading the pool.
    /// </para>
    /// </remarks>
    private void UploadWorld()
    {
        if (_world is not { } world)
            return;

        if (!world.TryTakeDirtyRange(out var firstMeshlet, out var meshletCount))
            return;

        var vertexFirst = firstMeshlet * world.MeshletVertexPoolStride;
        var vertexCount = meshletCount * world.MeshletVertexPoolStride;

        _terrainVertexBuffer.Upload(world.VertexPool.AsSpan(vertexFirst, vertexCount), vertexFirst);

        var indexFirst = firstMeshlet * world.MeshletIndexPoolStride;
        var indexCount = meshletCount * world.MeshletIndexPoolStride;

        _terrainIndexBuffer.Upload(world.IndexPool.AsSpan(indexFirst, indexCount), indexFirst);
    }

    /// <summary>
    /// Records the terrain: one indexed draw per meshlet of every resident brick.
    /// </summary>
    /// <remarks>
    /// <para>
    /// The view-projection is built once per frame and pushed per brick, because only the brick's origin
    /// and decode scale change between draws. It is <em>not</em> transposed on the way out: the managed
    /// matrix is row-vector, so its <c>X</c>/<c>Y</c>/<c>Z</c>/<c>W</c> fields are its rows, and uploading
    /// them into GLSL's column-major <c>mat4</c> performs the transpose implicitly. See
    /// <see cref="TerrainPushConstants"/>.
    /// </para>
    /// <para>
    /// A draw per meshlet rather than per brick is deliberate. A brick's meshlets are contiguous in the
    /// arena, but they are not contiguous in the index pool in the sense a single draw needs: each meshlet
    /// has its own vertex range, and the index pool's values are already rebased to arena coordinates, so
    /// one draw covering several meshlets would work — but only if their vertex ranges happened to be
    /// adjacent, which the arena does not guarantee. One draw per meshlet is exact and costs a command
    /// buffer entry, which is the cheap resource here.
    /// </para>
    /// </remarks>
    private void RecordTerrain(CommandBuffer commandBuffer, VkViewport viewport, VkRect2D scissor)
    {
        if (_world is null || !_hasCamera)
            return;

        BrickDraw[] draws;
        int drawCount;

        lock (_worldGate)
        {
            draws = _draws;
            drawCount = _drawCount;
        }

        if (drawCount == 0)
            return;

        float3 position;
        float3 target;
        float fieldOfView;

        lock (_cameraGate)
        {
            position = _cameraPosition;
            target = _cameraTarget;
            fieldOfView = _cameraFieldOfView;
        }

        var aspect = viewport.Height <= 0.0f ? 1.0f : viewport.Width / viewport.Height;

        // The near plane is a metre and the far plane is the clipmap's full view distance, so the depth
        // range covers the whole world. Depth-32-float is what makes that ratio survivable: a 24-bit depth
        // buffer over a range this wide z-fights visibly on distant slopes.
        //
        // No transpose. The managed matrix is row-vector — a point transforms as v * M — so its X, Y, Z and W
        // fields are the matrix's rows. GLSL's mat4 is column-major, so uploading those four fields as the
        // four columns makes M_glsl the transpose of M_managed, and M_glsl * v is then exactly v * M_managed.
        // Transposing as well would apply the inverse rotation and put the geometry behind the camera.
        var viewProjection =
            float4x4.CreateLookAt(position, target, new float3(0.0f, 1.0f, 0.0f))
            * float4x4.CreatePerspectiveFieldOfView(fieldOfView, aspect, 1.0f, 20000.0f);

        if (_tracingEnabled)
            commandBuffer.BeginDebugLabel("Terrain"u8, TerrainLabelColor);

        commandBuffer.BindPipeline(VkPipelineBindPoint.Graphics, _terrainPipeline);
        commandBuffer.SetViewport(0, [viewport]);
        commandBuffer.SetScissor(0, [scissor]);
        commandBuffer.BindDescriptorSet(
            VkPipelineBindPoint.Graphics,
            _terrainPipelineLayout,
            0,
            _terrainDescriptors,
            []
        );

        var meshlets = _world.Meshlets;

        for (var i = 0; i < drawCount; i++)
        {
            var draw = draws[i];

            var pushConstants = new TerrainPushConstants
            {
                M00 = viewProjection.X.X,
                M01 = viewProjection.X.Y,
                M02 = viewProjection.X.Z,
                M03 = viewProjection.X.W,
                M10 = viewProjection.Y.X,
                M11 = viewProjection.Y.Y,
                M12 = viewProjection.Y.Z,
                M13 = viewProjection.Y.W,
                M20 = viewProjection.Z.X,
                M21 = viewProjection.Z.Y,
                M22 = viewProjection.Z.Z,
                M23 = viewProjection.Z.W,
                M30 = viewProjection.W.X,
                M31 = viewProjection.W.Y,
                M32 = viewProjection.W.Z,
                M33 = viewProjection.W.W,
                OriginX = draw.Origin.X,
                OriginY = draw.Origin.Y,
                OriginZ = draw.Origin.Z,
                DecodeScale = draw.DecodeScale,
                CameraX = position.X,
                CameraY = position.Y,
                CameraZ = position.Z,
            };

            commandBuffer.PushConstants(
                _terrainPipelineLayout,
                VkShaderStageFlags.VertexBit,
                ByteSize.Zero,
                pushConstants
            );

            for (var m = 0; m < draw.MeshletCount; m++)
            {
                var meshlet = meshlets[draw.MeshletOffset + m];

                if (meshlet.IndexCount == 0)
                    continue;

                // A non-indexed draw, deliberately. The vertex shader fetches its own index out of the index
                // pool and then fetches the vertex that index names, so the fixed-function index fetch would
                // be a second, redundant indirection over the same data — and it would force an index buffer
                // binding that the shader never reads. firstVertex is the meshlet's offset into the pool,
                // which is what makes gl_VertexIndex land on the right index.
                commandBuffer.Draw(meshlet.IndexCount, 1, meshlet.IndexOffset, 0);
            }
        }

        if (_tracingEnabled)
            commandBuffer.EndDebugLabel();
    }

    /// <summary>
    /// Points the terrain's two storage-buffer descriptors at the geometry pools.
    /// </summary>
    /// <remarks>
    /// The buffers are created once and never reallocated, so this runs once at construction. If the pools
    /// ever need to grow, this is the method that has to run again after the new buffers exist.
    /// </remarks>
    private void WriteTerrainDescriptors()
    {
        // The vertex pool is declared as a uint array in the shader, so the descriptor's range has to be a
        // whole number of uints. The pool is sized in bytes from a meshlet count, which is not guaranteed to
        // be a multiple of four, so the range is rounded down rather than left to the driver to reject.
        var vertexInfo = new VkDescriptorBufferInfo
        {
            Buffer = _terrainVertexBuffer.Buffer,
            Offset = new ByteSize64(0),
            Range = new ByteSize64(_terrainVertexBuffer.Size.Bytes & ~(nuint)3),
        };

        var indexInfo = new VkDescriptorBufferInfo
        {
            Buffer = _terrainIndexBuffer.Buffer,
            Offset = new ByteSize64(0),
            Range = _terrainIndexBuffer.Size,
        };

        Span<VkWriteDescriptorSet> writes =
        [
            new VkWriteDescriptorSet
            {
                DstSet = _terrainDescriptors,
                DstBinding = 0,
                DescriptorCount = 1,
                DescriptorType = VkDescriptorType.StorageBuffer,
                BufferInfo = &vertexInfo,
            },
            new VkWriteDescriptorSet
            {
                DstSet = _terrainDescriptors,
                DstBinding = 1,
                DescriptorCount = 1,
                DescriptorType = VkDescriptorType.StorageBuffer,
                BufferInfo = &indexInfo,
            },
        ];

        _device.UpdateDescriptorSets(writes, []);
    }

    /// <summary>Picks the depth format used for the scene's depth attachment.</summary>
    /// <remarks>
    /// Depth-32-float is chosen unconditionally, because the Vulkan specification requires every implementation
    /// to support it for optimal-tiling depth-stencil attachments, so there is no device for which asking is
    /// necessary. It is preferred over a packed depth-stencil format because the terrain is drawn over a
    /// multi-kilometre view distance, where 24 bits of depth is close to exhausted and produces visible
    /// z-fighting on distant slopes. No stencil is needed, so the stencil-less variant is also less work.
    /// </remarks>
    private static VkFormat SelectDepthFormat() => VkFormat.D32SFloat;

    /// <summary>
    /// Allocates the depth attachment at the swapchain's extent.
    /// </summary>
    /// <remarks>
    /// The image is created through the framework's own allocator abstraction rather than by calling into
    /// VMA directly, so the allocation is tracked by the same bookkeeping as every other image in the
    /// engine and is released by the same <c>FreeAllocation</c> path. The view is created from the image
    /// rather than alongside it, which is what keeps the two from being able to disagree about the format.
    /// </remarks>
    private static (ImageEx Image, ImageViewEx View) CreateDepthResources(
        DeviceEx device,
        VmaAllocator allocator,
        VkFormat format,
        VkExtent2D extent
    )
    {
        var image = allocator.CreateImage(
            new ImageEx.CreateInfo
            {
                Name = "depth",
                ImageType = VkImageType._2D,
                ImageFormat = format,
                ImageExtent = new VkExtent3D
                {
                    Width = Math.Max(1, extent.Width),
                    Height = Math.Max(1, extent.Height),
                    Depth = 1,
                },
                ImageMipLevels = 1,
                ImageArrayLayers = 1,
                ImageSamples = VkSampleCountFlags._1Bit,
                ImageTiling = VkImageTiling.Optimal,
                ImageUsage = VkImageUsageFlags.DepthStencilAttachmentBit,
                ImageSharingMode = VkSharingMode.Exclusive,
                ImageInitialLayout = VkImageLayout.Undefined,
                AllocPreference = MemoryPreference.PreferGpu,
                AllocRequiredProperties = VkMemoryPropertyFlags.DeviceLocalBit,
            }
        );

        // The third argument is the host allocation callbacks, not the image allocator: passing null asks
        // the driver for its default allocator, which is what every other view in the engine uses.
        var view = image.CreateImageView(
            VkImageViewType._2D,
            image.GetFullRange(VkImageAspectFlags.DepthBit),
            null
        );

        return (image, view);
    }

    /// <summary>
    /// Reads the demo compute pass's output back and compares it against the same step computed on the CPU.
    /// </summary>
    /// <remarks>
    /// This is a self-test, not a feature: it runs once and logs the result. It exists because a compute
    /// dispatch that does nothing is indistinguishable from one that works until something downstream reads
    /// the buffer, and by then the cause is several layers away. Doing the comparison here means a broken
    /// abstraction fails loudly at startup instead of producing a subtly wrong simulation later.
    /// </remarks>
    private void VerifyFieldStep()
    {
        var actual = new float[_fieldOutput.ElementCount];
        _fieldOutput.Download(actual);

        var expected = new float[_fieldInput.ElementCount];
        _fieldInput.Download(expected);

        // The same kernel as the shader, on the CPU.
        var reference = new float[expected.Length];
        var last = expected.Length - 1;
        for (var i = 0; i < expected.Length; i++)
        {
            var left = i == 0 ? 0 : i - 1;
            var right = i == last ? last : i + 1;
            var mean = (expected[left] + expected[i] + expected[right]) / 3.0f;
            reference[i] = expected[i] + (mean - expected[i]) * 0.5f;
        }

        var mismatches = 0;
        var maxError = 0.0f;
        for (var i = 0; i < reference.Length; i++)
        {
            var error = MathF.Abs(actual[i] - reference[i]);
            maxError = MathF.Max(maxError, error);
            // A float comparison, so an exact match is not expected; the tolerance is far below the
            // difference a wrong kernel or a missing dispatch would produce.
            if (error > 1e-5f)
                mismatches++;
        }

        if (mismatches == 0)
            _log.Info($"compute self-test passed: {reference.Length} element(s), max error {maxError:e2}");
        else
            _log.Error(
                $"compute self-test FAILED: {mismatches} of {reference.Length} element(s) differ, "
                    + $"max error {maxError:e2}"
            );
    }

    /// <summary>
    /// Records the demo compute pass: a diffusion step over a scalar field, then a barrier so the result is
    /// visible to whatever reads it next.
    /// </summary>
    /// <remarks>
    /// The barrier is the part that is easy to omit and hard to debug. Without it the dispatch and the
    /// subsequent read are unordered, and the failure is a wrong value rather than an error, which is why
    /// the abstraction makes the caller state the dependency explicitly instead of hiding it.
    /// </remarks>
    private void RecordFieldStep(CommandBuffer commandBuffer)
    {
        if (_tracingEnabled)
            commandBuffer.BeginDebugLabel("Field step"u8, ComputeLabelColor);

        var pushConstants = new FieldStepPushConstants
        {
            ElementCount = (uint)_fieldInput.ElementCount,
            Step = 0.5f,
        };

        _fieldStepPipeline.PushConstants(commandBuffer, pushConstants);
        _fieldStepPipeline.DispatchFor(
            commandBuffer,
            _fieldInput.ElementCount,
            ComputeShaders.FieldStepLocalSize
        );

        // The shader writes a storage buffer; the next reader may be a transfer or a draw. Both are covered
        // by naming the shader-write and shader-read stages rather than the narrower transfer stage.
        Span<VkMemoryBarrier> barriers =
        [
            new VkMemoryBarrier
            {
                SrcAccessMask = VkAccessFlags.ShaderWriteBit,
                DstAccessMask = VkAccessFlags.ShaderReadBit | VkAccessFlags.TransferReadBit,
            },
        ];

        commandBuffer.PipelineBarrier(
            VkPipelineStageFlags.ComputeShaderBit,
            VkPipelineStageFlags.ComputeShaderBit | VkPipelineStageFlags.TransferBit,
            VkDependencyFlags.None,
            barriers,
            [],
            []
        );

        if (_tracingEnabled)
            commandBuffer.EndDebugLabel();
    }

    /// <summary>
    /// The starting contents of the demo field: a single spike in the middle, so a diffusion step has
    /// something to visibly spread.
    /// </summary>
    private static float[] CreateInitialField()
    {
        var field = new float[FieldElementCount];
        field[FieldElementCount / 2] = 1.0f;
        return field;
    }

    /// <summary>
    /// The push constants for the classification self-test, describing the demo brick shape the keys were
    /// generated for.
    /// </summary>
    /// <remarks>
    /// The origin is zero because the self-test's bricks are centred on the world origin, so their lattice
    /// coordinates are already relative to it. A real level uploads its own origin here instead, which is what
    /// lets a level sit anywhere in a world without the shader losing its position to a float.
    /// </remarks>
    private static BrickClassifyPushConstants BuildBrickClassifyConstants()
    {
        var settings = WorldSettings.Default;

        return new BrickClassifyPushConstants
        {
            VoxelCount = settings.BrickVoxelCount,
            SampleCount = settings.BrickVoxelCount + 2,
            BrickCount = BrickClassifyBrickCount,
            BrickSize = settings.BrickSize(0),
            VoxelSize = settings.VoxelSize(0),
        };
    }

    /// <summary>
    /// The brick keys the classification self-test submits: a cube of bricks spanning enough world space to
    /// contain terrain, air and solid rock.
    /// </summary>
    /// <remarks>
    /// The bricks deliberately span a range that puts the terrain surface through the middle of the set. A
    /// run where every brick is empty would compare equal on both sides and prove nothing, so the shape of
    /// the input is part of the test.
    /// </remarks>
    private static int4[] BuildBrickClassifyKeys()
    {
        var keys = new int4[BrickClassifyBrickCount];
        var side = (int)Math.Cbrt(BrickClassifyBrickCount);
        var index = 0;

        // Centred on the origin and offset down, because the demo terrain's height is centred near zero.
        var offset = side / 2;

        for (var z = 0; z < side && index < keys.Length; z++)
        {
            for (var y = 0; y < side && index < keys.Length; y++)
            {
                for (var x = 0; x < side && index < keys.Length; x++)
                    keys[index++] = new int4(x - offset, y - offset, z - offset, 0);
            }
        }

        return keys;
    }

    /// <summary>
    /// Records the brick classification pass: one workgroup per brick, then a barrier so the counts are
    /// visible to the read-back.
    /// </summary>
    private void RecordBrickClassify(CommandBuffer commandBuffer)
    {
        if (_tracingEnabled)
            commandBuffer.BeginDebugLabel("Brick classify"u8, ComputeLabelColor);

        _brickClassifyPipeline.PushConstants(commandBuffer, _brickClassifyConstants);

        // One workgroup per brick, not one workgroup per 64 bricks: the shader parallelises *within* a brick,
        // so the workgroup count is the brick count. Using DispatchFor here would divide by the local size
        // and classify a sixty-fourth of the bricks, silently, with no error anywhere.
        _brickClassifyPipeline.Dispatch(commandBuffer, _brickClassifyConstants.BrickCount);

        Span<VkMemoryBarrier> barriers =
        [
            new VkMemoryBarrier
            {
                SrcAccessMask = VkAccessFlags.ShaderWriteBit,
                DstAccessMask = VkAccessFlags.ShaderReadBit | VkAccessFlags.TransferReadBit,
            },
        ];

        commandBuffer.PipelineBarrier(
            VkPipelineStageFlags.ComputeShaderBit,
            VkPipelineStageFlags.ComputeShaderBit | VkPipelineStageFlags.TransferBit,
            VkDependencyFlags.None,
            barriers,
            [],
            []
        );

        if (_tracingEnabled)
            commandBuffer.EndDebugLabel();
    }

    /// <summary>
    /// Compares the classification pass's counts against the CPU reference for the same bricks.
    /// </summary>
    /// <remarks>
    /// The two things this catches are the ones a compute pass fails silently at: a dispatch that is one
    /// workgroup short and drops the tail, and an indexing bug that makes the pass classify the wrong brick.
    /// Neither produces a validation error, and both produce a world that is subtly wrong several frames
    /// later. Comparing a count rather than a boolean means a pass that reports every brick as occupied also
    /// fails, which is the failure mode that would quietly exhaust the brick pool.
    /// </remarks>
    private void VerifyBrickClassify()
    {
        var actual = new int[_brickCrossings.ElementCount];
        _brickCrossings.Download(actual);

        var keys = BuildBrickClassifyKeys();
        var mismatches = 0;
        var occupied = 0;

        for (var i = 0; i < keys.Length; i++)
        {
            var expected = BrickClassifier.CountCrossedCells(
                new int3(keys[i].X, keys[i].Y, keys[i].Z),
                WorldSettings.Default,
                level: 0
            );

            if (expected > 0)
                occupied++;

            if (actual[i] == expected)
                continue;

            mismatches++;

            // The first few disagreements are logged with their inputs, because a mismatch tells you the pass
            // is wrong but not which brick exposed it, and without that detail the only next step is to
            // reproduce the whole thing by hand.
            if (mismatches <= DisagreementSampleLimit)
            {
                _log.Warning(
                    $"classify disagreement at brick ({keys[i].X},{keys[i].Y},{keys[i].Z}): "
                        + $"gpu {actual[i]}, cpu {expected}"
                );
            }
        }

        _brickClassifyVerified = mismatches == 0;

        if (mismatches == 0)
        {
            _log.Info(
                $"brick classify self-test passed: {keys.Length} brick(s), {occupied} containing surface"
            );
        }
        else
        {
            _log.Error(
                $"brick classify self-test FAILED: {mismatches} of {keys.Length} brick(s) disagree with the "
                    + "CPU reference"
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

                    // The render pass has a depth attachment, so a depth-stencil state is mandatory even for
                    // a pipeline that does not want depth testing. The triangle is a debug overlay drawn
                    // after the terrain, so it tests nothing and writes nothing: it is always visible.
                    DepthStencilState = Presets.DepthStencil.NoDepthTest,
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

    /// <summary>
    /// Creates the terrain pipeline: no vertex input, an empty vertex-input state, and depth testing on.
    /// </summary>
    /// <remarks>
    /// <para>
    /// There is no vertex input because the shader fetches its vertices from the storage buffers instead. An
    /// empty <see cref="VertexInput"/> is what tells Vulkan not to expect a binding, and the draw is recorded
    /// with a zero vertex offset so <c>gl_VertexIndex</c> is the address into the index pool.
    /// </para>
    /// <para>
    /// Depth writes are on and the comparison is less-than, which is what makes a nearer surface win. Without
    /// this the terrain would be drawn in arena order: a hill in front would be overpainted by a ridge behind
    /// it, and the world would appear inside out. Back-face culling is on because the extractor emits quads
    /// with consistent winding, so a triangle facing away from the camera is always one the camera cannot see.
    /// </para>
    /// </remarks>
    private static VkPipeline CreateTerrainPipeline(
        DeviceEx device,
        VkPipelineLayout layout,
        ShaderStages stages,
        VkRenderPass renderPass
    )
    {
        // No bindings and no attributes. The count arguments are zero, which is the only valid way to declare
        // that this pipeline reads no vertex input at all.
        var vertexInput = new VertexInput(0, 0);

        Span<VkDynamicState> dynamicStates = [VkDynamicState.Viewport, VkDynamicState.Scissor];
        fixed (VkDynamicState* dynamicStatesPtr = dynamicStates)
        {
            var dynamicState = new VkPipelineDynamicStateCreateInfo
            {
                DynamicStateCount = dynamicStates.Length,
                DynamicStates = dynamicStatesPtr,
            };

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
                    RasterizationState = Presets.Rasterization.Fill.CullBack,
                    MultisampleState = Presets.Multisample.None,
                    DepthStencilState = Presets.DepthStencil.DepthTestWrite,
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
