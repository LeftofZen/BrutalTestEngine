namespace BrutalTestEngine.Shaders;

/// <summary>
/// GLSL for drawing the meshed terrain, compiled at runtime by ShaderC.
/// </summary>
/// <remarks>
/// <para>
/// The vertex shader reads the built brick geometry out of two storage buffers — the four-byte-per-vertex
/// pool and the 32-bit index pool — rather than declaring a vertex input. That is deliberate, and it is what
/// keeps the vertex format at four bytes.
/// </para>
/// <para>
/// The alternative, a real vertex input description, would force the position to be a self-contained world
/// position: twelve bytes, and worse, one that loses precision far from the origin. The pool instead stores
/// the position as three quantised bytes relative to the brick, and the brick's origin arrives in the push
/// constant, so the decode is one multiply-add and stays exact at any distance from the origin.
/// </para>
/// <para>
/// The packing spans the brick's expanded local range <c>[-1, n]</c> — one voxel of neighbour ring on the
/// negative side — so a packed byte of zero is one voxel below the brick's minimum corner. The push constant
/// carries the scale that turns a packed byte straight into metres, which is why the shader needs no
/// knowledge of the brick's shape at all.
/// </para>
/// <para>
/// There is no vertex-input binding because there is no vertex input: the pipeline is created with an empty
/// vertex input, and <c>gl_VertexIndex</c> is the address into the index pool. The draw is recorded with a
/// zero vertex offset and a first-index at the brick's index base, so <c>gl_VertexIndex</c> covers exactly
/// that brick's indices.
/// </para>
/// </remarks>
public static class TerrainShaders
{
    /// <summary>
    /// Size in bytes of the push constant block: a view-projection matrix, the brick transform and the
    /// camera position. Must match the block declared in <see cref="Vertex"/> and the struct the renderer
    /// writes.
    /// </summary>
    public const int PushConstantSize = 96;

    /// <summary>The vertex shader: fetches a packed vertex through the index pool, unpacks it, projects it.</summary>
    public static ReadOnlySpan<byte> Vertex =>
        """
        #version 450

        layout(set = 0, binding = 0) readonly buffer VertexPool
        {
            uint packedVertices[];
        } vertexPool;

        layout(set = 0, binding = 1) readonly buffer IndexPool
        {
            uint indices[];
        } indexPool;

        layout(push_constant) uniform PushConstants
        {
            mat4 viewProjection;

            // xyz is the brick's world-space minimum corner; w is the metres-per-byte scale, which folds
            // the voxel size, the brick's expanded unit span and the byte range into one number.
            vec4 brickOriginAndScale;

            // xyz is the camera's world position, used for the fog term.
            vec4 camera;
        } push;

        layout(location = 0) out vec3 fragColor;
        layout(location = 1) out vec3 fragNormal;
        layout(location = 2) out float fragDistance;

        // Unpacks the octahedral normal from the top byte of a packed vertex: four bits per axis.
        vec3 decodeNormal(uint packedByte)
        {
            const float axisMax = 15.0;

            float u = float(packedByte >> 4u) / axisMax * 2.0 - 1.0;
            float v = float(packedByte & 15u) / axisMax * 2.0 - 1.0;

            vec3 n = vec3(u, v, 1.0 - abs(u) - abs(v));

            if (n.z < 0.0)
                n.xy = (1.0 - abs(n.yx)) * sign(n.xy);

            return normalize(n);
        }

        void main()
        {
            uint vertexIndex = indexPool.indices[gl_VertexIndex];
            uint vertex = vertexPool.packedVertices[vertexIndex];

            // Three position bytes, low to high, then the normal byte on top. Each position byte is a unorm,
            // so dividing by 255 and multiplying by the scale gives metres directly.
            const float byteMax = 255.0;

            vec3 quantised = vec3(
                float(vertex & 0xFFu),
                float((vertex >> 8u) & 0xFFu),
                float((vertex >> 16u) & 0xFFu)
            ) / byteMax;

            vec3 position = push.brickOriginAndScale.xyz + quantised * push.brickOriginAndScale.w;

            fragNormal = decodeNormal((vertex >> 24u) & 0xFFu);
            fragDistance = length(position - push.camera.xyz);

            gl_Position = push.viewProjection * vec4(position, 1.0);

            // A height-and-slope ramp, so the terrain reads as terrain with no texture at all. Steep faces
            // are rock, the lowest flat ground is green and the highest is snow. It is a diagnostic palette
            // rather than art: the point is to make the surface's shape and orientation legible, which is
            // exactly what needs checking while the meshing pipeline is still being built.
            float height = clamp((position.y - 20.0) / 120.0 * 0.5 + 0.5, 0.0, 1.0);
            float slope = clamp(fragNormal.y, 0.0, 1.0);

            vec3 rock = vec3(0.32, 0.30, 0.29);
            vec3 grass = vec3(0.24, 0.42, 0.19);
            vec3 snow = vec3(0.90, 0.92, 0.95);

            vec3 ground = mix(grass, snow, smoothstep(0.62, 0.95, height));
            fragColor = mix(rock, ground, smoothstep(0.35, 0.75, slope));
        }
        """u8;

    /// <summary>
    /// The fragment shader: one directional light plus a hemispheric ambient term, then distance fog.
    /// </summary>
    /// <remarks>
    /// Two light contributions, not one. A lone directional term leaves every surface facing away from it
    /// flat black, which makes the terrain's shape unreadable exactly where the shape matters most — in the
    /// folds and valleys. The ambient term is tinted by the surface's own vertical orientation, which is
    /// nearly free and gives low and high ground different ambient colours without a second light.
    /// </remarks>
    public static ReadOnlySpan<byte> Fragment =>
        """
        #version 450

        layout(location = 0) in vec3 fragColor;
        layout(location = 1) in vec3 fragNormal;
        layout(location = 2) in float fragDistance;

        layout(location = 0) out vec4 outColor;

        void main()
        {
            vec3 normal = normalize(fragNormal);

            vec3 lightDirection = normalize(vec3(0.42, 0.78, 0.31));
            float diffuse = max(dot(normal, lightDirection), 0.0);

            // Sky above, ground below: a face pointing up reflects the bright sky and one pointing down the
            // dark ground. This is what stops the shadowed side of a ridge from being uniform.
            vec3 skyAmbient = vec3(0.34, 0.40, 0.52);
            vec3 groundAmbient = vec3(0.16, 0.14, 0.12);
            vec3 ambient = mix(groundAmbient, skyAmbient, normal.y * 0.5 + 0.5);

            // Exponential fog toward the horizon, which reads as depth and softens the clipmap's outer edge
            // rather than showing it as a hard wall of triangles.
            float fog = 1.0 - exp(-fragDistance * 0.00035);
            vec3 horizon = vec3(0.30, 0.36, 0.45);

            vec3 lit = fragColor * (ambient + diffuse * vec3(1.0, 0.97, 0.90));

            outColor = vec4(mix(lit, horizon, clamp(fog, 0.0, 0.85)), 1.0);
        }
        """u8;
}
