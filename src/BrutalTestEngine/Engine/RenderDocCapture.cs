using Brutal.Logging;
using Brutal.RenderDocApi;
using BrutalTestEngine.Logging;
using Microsoft.Extensions.Logging;

namespace BrutalTestEngine.Engine;

/// <summary>
/// The engine's view of RenderDoc, delegating entirely to BRUTAL's <see cref="RenderDoc"/> binding.
/// </summary>
/// <remarks>
/// <para>
/// BRUTAL already ships a complete binding for RenderDoc's in-application API, so there is nothing to
/// hand-roll here: this type exists only to give the engine a stable, always-safe surface (a capture request
/// that quietly reports "not attached" instead of throwing) and to name the one thing BRUTAL leaves to the
/// host, which is when to load the API.
/// </para>
/// <para>
/// Ordering is the part that matters. RenderDoc's own documentation is explicit that the API must be loaded
/// before the graphics device is created, otherwise RenderDoc attaches but captures fail because it never saw
/// the instance and device come up. <see cref="Load"/> therefore runs before any Vulkan object exists.
/// </para>
/// </remarks>
public sealed class RenderDocCapture
{
    private static readonly LogCategory Log = EngineLog.Categories.Capture;

    private RenderDocCapture(bool available) => IsAvailable = available;

    /// <summary>
    /// A capture surface that reports itself unavailable, for the paths that never asked for RenderDoc and for
    /// tests, which have no graphics device and no interest in installing hooks.
    /// </summary>
    public static RenderDocCapture Disabled { get; } = new(false);

    /// <summary>True when RenderDoc is loaded in this process and its API was obtained.</summary>
    /// <remarks>
    /// Says nothing about whether a capture will be written: a loaded RenderDoc writes one only when
    /// <see cref="TriggerCapture"/> asks it to. Conflating the two is how a metric ends up reporting a capture
    /// per frame when none happened.
    /// </remarks>
    public bool IsAvailable { get; }

    /// <summary>
    /// The API version RenderDoc reported, or <c>null</c> when it is not loaded.
    /// </summary>
    public Version? ApiVersionNumber { get; private set; }

    /// <summary>Captures taken so far, according to RenderDoc.</summary>
    public uint CaptureCount => IsAvailable ? RenderDoc.NumCaptures : 0;

    /// <summary>True while a capture is in progress.</summary>
    public bool IsCapturing => IsAvailable && RenderDoc.IsFrameCapturing;

    /// <summary>
    /// Loads RenderDoc and installs its hooks. Never throws: a missing capture tool must not stop the engine
    /// from running, and RenderDoc is absent on every machine that did not launch the process.
    /// </summary>
    /// <param name="captureDirectory">
    /// Where captures are written. RenderDoc defaults to the working directory, which for a game installed
    /// under Program Files is not writable, so the engine names the directory explicitly.
    /// </param>
    public static RenderDocCapture Load(string? captureDirectory = null)
    {
        try
        {
            if (!RenderDoc.LoadApi(ApiVersions.V1_0_0, []))
            {
                if (Log.IsEnabled(LogLevel.Debug))
                    Log.Debug("RenderDoc is not loaded in this process");

                return new RenderDocCapture(false);
            }

            var capture = new RenderDocCapture(true) { ApiVersionNumber = ToVersion(RenderDoc.Version) };

            if (captureDirectory is { Length: > 0 })
                capture.SetCaptureDirectory(captureDirectory);

            if (Log.IsEnabled(LogLevel.Information))
            {
                // Worded as "attached", not as capturing: the hooks are installed and a `capture` command will now
                // work, but nothing is being recorded until one arrives.
                Log.Info(
                    $"RenderDoc {capture.ApiVersionNumber} attached; `capture` writes to "
                        + $"{captureDirectory ?? "the RenderDoc default directory"}"
                );
            }

            return capture;
        }
        catch (Exception exception)
        {
            // RenderDoc can fail to load for reasons that are none of the engine's business: a stale DLL on the
            // path, a mismatched bitness, a driver that refuses the hooks. All of them degrade to "no capture".
            if (Log.IsEnabled(LogLevel.Warning))
                Log.Warning($"failed to load the RenderDoc API: {exception.Message}");

            return new RenderDocCapture(false);
        }
    }

    /// <summary>
    /// Points the capture path template at <paramref name="directory"/>, so captures land somewhere the
    /// developer chose rather than wherever the working directory happened to be.
    /// </summary>
    public void SetCaptureDirectory(string directory)
    {
        if (!IsAvailable || directory.Length == 0)
            return;

        Directory.CreateDirectory(directory);

        // RenderDoc appends its own frame number and extension, so the template is a bare stem.
        RenderDoc.SetCaptureFilePathTemplate(
            System.Text.Encoding.UTF8.GetBytes(Path.Combine(directory, "bte"))
        );

        if (Log.IsEnabled(LogLevel.Information))
            Log.Info($"RenderDoc captures will be written to {directory}");
    }

    /// <summary>
    /// Opens an explicit capture region around one frame. Pair with <see cref="EndFrame"/>.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Not used by the render loop. It exists for a caller that needs a capture of one specific frame it is
    /// about to draw — a regression harness comparing a frame against a reference capture, for instance — where
    /// "the next complete frame RenderDoc happens to pick" is not precise enough.
    /// </para>
    /// <para>
    /// The device and window handles are passed as null, which RenderDoc treats as a wildcard. The engine has
    /// exactly one device and one window, so naming them adds no precision, and for Vulkan the device pointer
    /// RenderDoc wants is the dispatch-table pointer inside the instance handle, which is easy to get wrong for
    /// no benefit.
    /// </para>
    /// </remarks>
    public void BeginFrame()
    {
        // The guard matters: StartFrameCapture without a matching EndFrameCapture leaves RenderDoc holding an
        // open region, and the next EndFrameCapture would write a capture of everything in between.
        if (IsAvailable && !IsCapturing)
            RenderDoc.StartFrameCapture(default, default);
    }

    /// <summary>
    /// Closes the region opened by <see cref="BeginFrame"/> and writes the capture.
    /// </summary>
    /// <remarks>
    /// Guarded on <see cref="IsCapturing"/> so an unpaired call is a no-op rather than asking RenderDoc to end a
    /// capture that was never started.
    /// </remarks>
    public void EndFrame()
    {
        if (!IsAvailable || !IsCapturing)
            return;

        if (RenderDoc.EndFrameCapture(default, default) == 0)
        {
            if (Log.IsEnabled(LogLevel.Warning))
                Log.Warning("RenderDoc reported an error writing the capture");
        }
    }

    /// <summary>
    /// Asks RenderDoc to capture the next single frame at the next opportunity.
    /// </summary>
    /// <remarks>
    /// Preferred over <see cref="BeginFrame"/>/<see cref="EndFrame"/> for an externally triggered capture,
    /// because RenderDoc picks a complete frame boundary rather than whatever moment the request arrived. A
    /// capture that starts mid-frame is the usual reason a replay looks broken.
    /// </remarks>
    public bool TriggerCapture()
    {
        if (!IsAvailable)
        {
            if (Log.IsEnabled(LogLevel.Debug))
                Log.Debug("capture requested but RenderDoc is not attached");

            return false;
        }

        RenderDoc.TriggerCapture();

        if (Log.IsEnabled(LogLevel.Information))
            Log.Info("RenderDoc capture triggered for the next frame");

        return true;
    }

    /// <summary>Shows the replay UI if it is connected to this process.</summary>
    public void ShowReplayUi()
    {
        if (IsAvailable)
            RenderDoc.ShowReplayUI();
    }

    private static Version ToVersion(ApiVersion version) => new(version.Major, version.Minor, version.Patch);
}
