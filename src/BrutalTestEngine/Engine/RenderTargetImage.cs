namespace BrutalTestEngine.Engine;

/// <summary>
/// A captured frame: the render target's pixels, plus what is needed to interpret them.
/// </summary>
/// <remarks>
/// <para>
/// This exists so an external observer — today the MCP server, so an AI can see the game — can look at what
/// the renderer actually produced rather than at a window screenshot. The difference matters: a screenshot
/// of the desktop can be occluded, scaled by the compositor, captured mid-present, or show a different
/// window entirely, and none of those failures are distinguishable from a rendering bug when all you have
/// is pixels. Reading the swapchain image back before it is presented is the only way to see exactly what
/// the renderer drew.
/// </para>
/// <para>
/// The pixels arrive top-down, which is also PNG's row order, so the encode is a straight re-pack. Vulkan's
/// framebuffer coordinate system puts <c>(0,0)</c> at the upper-left corner, and an image-to-buffer copy
/// writes image row <c>y</c> to buffer row <c>y</c> — so the buffer's first row is already the top of the
/// picture. Flipping here would produce an upside-down capture, which is indistinguishable from a rendering
/// bug when finding rendering bugs is the capture's entire purpose.
/// </para>
/// </remarks>
public sealed class RenderTargetImage
{
    /// <summary>Creates a captured frame.</summary>
    /// <param name="width">Width in pixels.</param>
    /// <param name="height">Height in pixels.</param>
    /// <param name="bgra">Pixels, four bytes each, in BGRA order, top-down.</param>
    /// <param name="isSrgb">Whether the source format was an sRGB format.</param>
    public RenderTargetImage(int width, int height, byte[] bgra, bool isSrgb)
    {
        ArgumentOutOfRangeException.ThrowIfLessThan(width, 1);
        ArgumentOutOfRangeException.ThrowIfLessThan(height, 1);
        ArgumentNullException.ThrowIfNull(bgra);

        if (bgra.Length < width * height * 4)
            throw new ArgumentException("The pixel buffer is smaller than the dimensions describe.", nameof(bgra));

        Width = width;
        Height = height;
        Bgra = bgra;
        IsSrgb = isSrgb;
    }

    /// <summary>Width in pixels.</summary>
    public int Width { get; }

    /// <summary>Height in pixels.</summary>
    public int Height { get; }

    /// <summary>The raw pixels: four bytes per pixel, BGRA order, bottom-up.</summary>
    public byte[] Bgra { get; }

    /// <summary>
    /// Whether the swapchain format carried an sRGB transfer function.
    /// </summary>
    /// <remarks>
    /// Recorded rather than applied, because the value already in the image is whatever the shader wrote. It
    /// is reported so a consumer knows whether the buffer is linear or encoded, which decides whether a
    /// colour looks washed out when compared against the real window.
    /// </remarks>
    public bool IsSrgb { get; }

    /// <summary>Encodes the capture as a PNG.</summary>
    /// <remarks>
    /// PNG rather than a raw dump because the consumer is usually a program that wants to show the image, and
    /// an uncompressed 1920×1080 frame is 8 MB of base64 — too large to pass through a tool result. PNG is
    /// also lossless, which matters here: the point of the capture is to judge rendering, and lossy
    /// compression would blur exactly the single-pixel artefacts worth looking for.
    /// </remarks>
    public byte[] ToPng()
    {
        var stride = Width * 4;

        // PNG filters each row against the one above it. Sub is used rather than None because the image is a
        // shaded gradient and vertical prediction costs one byte per pixel while shrinking the output by much
        // more than that.
        var raw = new byte[(stride + 1) * Height];

        for (var y = 0; y < Height; y++)
        {
            var sourceRow = y * stride;
            var destinationRow = y * (stride + 1);

            raw[destinationRow] = 1; // Filter type 1: Sub.

            for (var x = 0; x < stride; x++)
            {
                var left = x >= 4 ? Bgra[sourceRow + x - 4] : (byte)0;
                raw[destinationRow + 1 + x] = (byte)(Bgra[sourceRow + x] - left);
            }
        }

        var compressed = PngEncoder.Deflate(raw);

        return PngEncoder.Build(Width, Height, compressed);
    }

    /// <summary>
    /// Waits for a capture request to complete, reporting a timeout or cancellation as "no image".
    /// </summary>
    /// <remarks>
    /// Lives here rather than in the renderer because the renderer is an <c>unsafe</c> class, and C# forbids
    /// <c>await</c> in an unsafe context. A capture that times out is not an error worth propagating: the
    /// caller asked for a picture of a running game, and "not right now" is a legitimate answer that a
    /// screenshot tool should report rather than throw.
    /// </remarks>
    public static async Task<RenderTargetImage?> WaitForCaptureAsync(
        Task<RenderTargetImage?> capture,
        TimeSpan timeout,
        CancellationToken cancellationToken
    )
    {
        try
        {
            return await capture.WaitAsync(timeout, cancellationToken).ConfigureAwait(false);
        }
        catch (TimeoutException)
        {
            return null;
        }
        catch (OperationCanceledException)
        {
            return null;
        }
    }
}
