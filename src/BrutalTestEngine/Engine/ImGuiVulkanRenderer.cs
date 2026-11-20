using Brutal;
using Brutal.ImGuiApi;
using Brutal.ImGuiApi.Backends;
using Brutal.VulkanApi;

namespace BrutalTestEngine.Engine;

/// <summary>
/// Records Dear ImGui draw lists into the render pass the engine already opened, so the UI composites
/// over the scene.
/// </summary>
public interface IImGuiRenderer
{
    void Render(CommandBuffer commandBuffer);
}

/// <summary>
/// Owns the ImGui Vulkan renderer backend. This half of ImGui lives on the render thread, while the
/// context and the GLFW platform backend live on the main thread.
/// </summary>
/// <remarks>
/// <para>
/// ImGui is a single-context library, so the two halves must never run at the same time. The engine
/// enforces that with a strict handshake: the main thread builds a frame and publishes its draw data,
/// then waits; the render thread records that draw data and signals the main thread, which only then
/// builds the next frame.
/// </para>
/// <para>
/// The backend captures the swapchain image count when it is constructed and sizes its per-image
/// buffers from it, so it is rebuilt when that count changes. That happens on this thread, which is
/// also the only thread that touches the backend.
/// </para>
/// </remarks>
public sealed class ImGuiVulkanRenderer : IImGuiRenderer, IDisposable
{
    private readonly VulkanRenderer _renderer;
    private VulkanBackend _backend;
    private ImDrawDataPtr _drawData;
    private int _backendImageCount;
    private int _backendMinImageCount;
    private bool _skipNextRender;
    private bool _disposed;

    public ImGuiVulkanRenderer(VulkanRenderer renderer)
    {
        _renderer = renderer;
        _backend = CreateBackend();
        _backendImageCount = renderer.ImageCount;
        _backendMinImageCount = renderer.MinImageCount;
        renderer.SwapchainRecreated += OnSwapchainRecreated;
    }

    /// <summary>
    /// Sets the draw data to record on the next <see cref="Render"/> call. Called by the render thread
    /// with the data the main thread published.
    /// </summary>
    public void SetDrawData(ImDrawDataPtr drawData) => _drawData = drawData;

    public void Render(CommandBuffer commandBuffer)
    {
        if (_skipNextRender)
        {
            // ImGui resolves a draw command's texture through the ImTextureData it points at, and the
            // draw data in flight was built against the previous backend's textures, which no longer
            // exist. Recording it would fault, so drop it. The next frame is built after ImGui has
            // re-requested its textures, so the new backend uploads them before they are referenced.
            _skipNextRender = false;
            return;
        }

        _backend.RenderDrawData(_drawData, commandBuffer);
    }

    public void Dispose()
    {
        if (_disposed)
            return;

        _disposed = true;
        _renderer.SwapchainRecreated -= OnSwapchainRecreated;
        _backend.Dispose();
    }

    private VulkanBackend CreateBackend() =>
        new(
            new VulkanBackend.CreateInfo
            {
                Device = _renderer.Device,
                GraphicsQueue = _renderer.Queue,
                RenderPass = _renderer.RenderPass,
                SubPass = 0,
                MinImageCount = _renderer.MinImageCount,
                ImageCount = _renderer.ImageCount,
                SampleCount = VkSampleCountFlags._1Bit,
                DescriptorPoolSize = 8,
                MinAllocationSize = new ByteSize32(1024 * 1024),
            }
        );

    private void OnSwapchainRecreated()
    {
        // RenderDrawData sizes its per-image buffers from the image count it read at construction and
        // asserts on it every frame, so the backend only has to be rebuilt when that count changes. A
        // plain resize keeps the same count, and rebuilding for it would needlessly throw away the
        // textures ImGui has already uploaded.
        if (_renderer.ImageCount == _backendImageCount && _renderer.MinImageCount == _backendMinImageCount)
            return;

        _backend.Dispose();

        // Disposing the backend frees the GPU textures it created, but ImGui still holds their
        // identifiers. Marking them destroyed makes ImGui request them again, so the new backend
        // uploads them instead of handing out identifiers that no longer resolve.
        InvalidateTextures();

        _backend = CreateBackend();
        _backendImageCount = _renderer.ImageCount;
        _backendMinImageCount = _renderer.MinImageCount;
        _skipNextRender = true;
    }

    private static void InvalidateTextures()
    {
        var platformIo = ImGui.GetPlatformIO();
        if (platformIo.IsNull())
            return;

        foreach (var texture in platformIo.Textures)
        {
            if (texture.Status is ImTextureStatus.Destroyed)
                continue;

            texture.SetTexID(ImTextureID.Invalid);
            texture.BackendUserData = 0;
            texture.SetStatus(ImTextureStatus.Destroyed);
        }
    }
}
