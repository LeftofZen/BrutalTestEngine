namespace BrutalTestEngine.Shaders;

/// <summary>
/// GLSL for the demo triangle, compiled at runtime by ShaderC.
/// </summary>
/// <remarks>
/// The vertex shader reads a push constant carrying the simulation's rotation and scale. Push
/// constants are the cheapest way to get per-frame simulation data to the GPU: they are written
/// directly into the command buffer on the render thread, so no buffer upload or synchronisation is
/// needed between the simulation thread and the renderer.
/// </remarks>
public static class TriangleShaders
{
    /// <summary>
    /// Size in bytes of the push constant block. Must match the block declared in the vertex shader
    /// and the struct the renderer writes.
    /// </summary>
    public const int PushConstantSize = 16;

    public static ReadOnlySpan<byte> Vertex =>
        """
        #version 450

        layout(location = 0) in vec2 inPosition;
        layout(location = 1) in vec3 inColor;

        layout(location = 0) out vec3 fragColor;

        layout(push_constant) uniform PushConstants
        {
            float rotation;
            float scale;
            float aspect;
            float padding;
        } push;

        void main()
        {
            float c = cos(push.rotation);
            float s = sin(push.rotation);

            vec2 rotated = vec2(
                inPosition.x * c - inPosition.y * s,
                inPosition.x * s + inPosition.y * c
            );

            // Correct for the window's aspect ratio so the triangle is not stretched.
            vec2 scaled = rotated * push.scale;
            scaled.x /= push.aspect;

            gl_Position = vec4(scaled, 0.0, 1.0);
            fragColor = inColor;
        }
        """u8;

    public static ReadOnlySpan<byte> Fragment =>
        """
        #version 450

        layout(location = 0) in vec3 fragColor;
        layout(location = 0) out vec4 outColor;

        void main()
        {
            outColor = vec4(fragColor, 1.0);
        }
        """u8;
}
