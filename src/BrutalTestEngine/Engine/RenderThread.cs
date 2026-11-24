using System.Diagnostics;
using Brutal.ImGuiApi;
using Brutal.Numerics;
using BrutalTestEngine.Simulation;

namespace BrutalTestEngine.Engine;

/// <summary>
/// Owns the render thread. It owns the Vulkan renderer and the ImGui Vulkan backend, and is the only
/// thread that records or submits GPU work.
/// </summary>
/// <remarks>
/// <para>
/// Rendering is separated from the simulation so that a slow frame (a swapchain rebuild, a driver
/// stall, a vsync wait) cannot slow the simulation down, and so that the simulation's fixed timestep
/// stays deterministic regardless of frame rate.
/// </para>
/// <para>
/// The thread runs a strict handshake with the main thread, because ImGui is a single-context library
/// and its Vulkan backend reads that context:
/// </para>
/// <list type="number">
/// <item>The main thread builds an ImGui frame and calls <see cref="SubmitFrame"/>.</item>
/// <item>This thread records the draw data and signals <see cref="WaitForFrameConsumed"/>.</item>
/// <item>The main thread may then build the next frame, while this thread submits and presents.</item>
/// </list>
/// <para>
/// That ordering keeps the two threads out of ImGui at the same time while still overlapping the
/// submit and present with the next frame's build.
/// </para>
/// </remarks>
public sealed class RenderThread : IDisposable
{
    private const int WaitTimeoutMilliseconds = 100;

    private readonly VulkanRenderer _renderer;
    private readonly ImGuiVulkanRenderer _gui;
    private readonly Thread _thread;
    private readonly CancellationTokenSource _cancellation = new();
    private readonly SemaphoreSlim _frameAvailable = new(0);
    private readonly SemaphoreSlim _frameConsumed = new(0);
    private readonly object _frameGate = new();
    private readonly ThreadMetrics _frameMetrics;

    private ImDrawDataPtr _pendingDrawData;
    private SimulationSnapshot _pendingSnapshot = SimulationSnapshot.Empty;
    private int2 _pendingSize;
    private bool _hasPendingFrame;
    private long _framesRendered;
    private volatile bool _captureRequested;
    private volatile bool _disposed;

    public RenderThread(VulkanRenderer renderer, ThreadTimerRegistry timers)
    {
        ArgumentNullException.ThrowIfNull(renderer);
        ArgumentNullException.ThrowIfNull(timers);

        _renderer = renderer;
        _gui = new ImGuiVulkanRenderer(renderer);
        _frameMetrics = timers.Get(ThreadTimerRegistry.Keys.RenderFrame);

        _thread = new Thread(Run)
        {
            Name = "Render",
            IsBackground = true,
            Priority = ThreadPriority.AboveNormal,
        };
        _thread.Start();
    }

    /// <summary>Number of frames presented.</summary>
    public long FramesRendered => Interlocked.Read(ref _framesRendered);

    /// <summary>
    /// Hands a built ImGui frame to the render thread. Called by the main thread. Returns immediately;
    /// the caller must wait on <see cref="WaitForFrameConsumed"/> before building the next frame.
    /// </summary>
    public void SubmitFrame(ImDrawDataPtr drawData, SimulationSnapshot snapshot, int2 framebufferSize)
    {
        lock (_frameGate)
        {
            _pendingDrawData = drawData;
            _pendingSnapshot = snapshot;
            _pendingSize = framebufferSize;
            _hasPendingFrame = true;
        }

        _frameAvailable.Release();
    }

    /// <summary>
    /// Blocks until the render thread has finished with the submitted draw data, which is the point at
    /// which the main thread may safely build the next ImGui frame.
    /// </summary>
    public bool WaitForFrameConsumed(int timeoutMilliseconds) => _frameConsumed.Wait(timeoutMilliseconds);

    /// <summary>Requests a swapchain rebuild for the given framebuffer size.</summary>
    public void RequestResize(int2 framebufferSize) => _renderer.RequestResize(framebufferSize);

    /// <summary>
    /// Asks RenderDoc to capture the next frame.
    /// </summary>
    /// <remarks>
    /// The request is a flag rather than a direct call, so the capture is triggered on the render thread at a
    /// frame boundary. Calling <c>StartFrameCapture</c> from the main thread would race the render thread's own
    /// frame and could open a capture in the middle of one. Safe from any thread; returns false when RenderDoc
    /// is not attached.
    /// </remarks>
    public bool RequestCapture()
    {
        if (!_renderer.Capture.IsAvailable)
            return false;

        _captureRequested = true;
        return true;
    }

    private void Run()
    {
        var token = _cancellation.Token;

        while (!token.IsCancellationRequested)
        {
            if (!_frameAvailable.Wait(WaitTimeoutMilliseconds))
                continue;

            ImDrawDataPtr drawData;
            SimulationSnapshot snapshot;
            int2 framebufferSize;
            lock (_frameGate)
            {
                if (!_hasPendingFrame)
                    continue;

                drawData = _pendingDrawData;
                snapshot = _pendingSnapshot;
                framebufferSize = _pendingSize;
                _hasPendingFrame = false;
            }

            _renderer.RequestResize(framebufferSize);
            _gui.SetDrawData(drawData);

            // Consumed here, immediately before the frame is recorded, so the capture brackets exactly one
            // complete frame on the thread that owns it.
            if (_captureRequested)
            {
                _captureRequested = false;
                _renderer.Capture.TriggerCapture();
            }

            var frameStartedAt = Stopwatch.GetTimestamp();

            try
            {
                if (_renderer.RenderFrame(_gui, snapshot))
                    Interlocked.Increment(ref _framesRendered);
            }
            finally
            {
                // Recorded in finally so a frame that threw still contributes its cost: an exception on the
                // render thread is exactly the case where the timings matter most.
                _frameMetrics.RecordSince(frameStartedAt);

                // Always release the main thread, even when the frame was skipped because the
                // swapchain was out of date or the window was minimised. Otherwise the main thread
                // would block forever waiting for a frame that was never recorded.
                _frameConsumed.Release();
            }
        }
    }

    public void Dispose()
    {
        if (_disposed)
            return;

        _disposed = true;
        _cancellation.Cancel();
        _frameAvailable.Release();
        _thread.Join(TimeSpan.FromSeconds(5));

        // The renderer and its ImGui backend are only safe to tear down once the render thread has
        // stopped touching them.
        _renderer.WaitIdle();
        _gui.Dispose();

        _frameAvailable.Dispose();
        _frameConsumed.Dispose();
        _cancellation.Dispose();
    }
}
