namespace BrutalTestEngine.Shaders;

/// <summary>
/// GLSL for the engine's compute passes, compiled at runtime by ShaderC.
/// </summary>
/// <remarks>
/// Kept as UTF-8 literals next to the graphics shaders so a shader and the C# that drives it are read
/// together. The alternative — shipping SPIR-V files — would mean a build step and a binary artefact that
/// cannot be reviewed in a diff.
/// </remarks>
public static class ComputeShaders
{
    /// <summary>
    /// Number of invocations per workgroup in the field-step shader. Must match the
    /// <c>local_size_x</c> declared in the shader, because the dispatch arithmetic is derived from it.
    /// </summary>
    public const int FieldStepLocalSize = 64;

    /// <summary>
    /// Size in bytes of the field-step push constant block: the element count, the time step and two
    /// floats of padding to keep the block 16-byte aligned.
    /// </summary>
    public const int FieldStepPushConstantSize = 16;

    /// <summary>
    /// Advances a scalar field by one step of a diffusion kernel.
    /// </summary>
    /// <remarks>
    /// <para>
    /// This is the shape every simulation pass in the engine will take: read a field, write a field, take
    /// the step size from a push constant. Weather, heat, crowd density and NPC influence maps are all the
    /// same operation with a different kernel, so proving the plumbing here means the later passes are a
    /// shader swap rather than new infrastructure.
    /// </para>
    /// <para>
    /// The bounds check is not optional. A dispatch covers whole workgroups, so the last group almost always
    /// has more invocations than there are elements, and without the check those invocations would write
    /// past the end of the buffer.
    /// </para>
    /// </remarks>
    public static ReadOnlySpan<byte> FieldStep =>
        """
        #version 450

        layout(local_size_x = 64) in;

        layout(binding = 0) readonly buffer InputField
        {
            float values[];
        } inputField;

        layout(binding = 1) writeonly buffer OutputField
        {
            float values[];
        } outputField;

        layout(push_constant) uniform PushConstants
        {
            uint elementCount;
            float step;
            float padding0;
            float padding1;
        } push;

        void main()
        {
            uint index = gl_GlobalInvocationID.x;
            if (index >= push.elementCount)
                return;

            // A one-dimensional diffusion step: each element relaxes towards the mean of its neighbours.
            // The clamp at the edges keeps the field from reading outside the buffer.
            uint last = push.elementCount - 1u;
            uint left = index == 0u ? 0u : index - 1u;
            uint right = index == last ? last : index + 1u;

            float mean = (inputField.values[left] + inputField.values[index] + inputField.values[right]) / 3.0;
            outputField.values[index] = mix(inputField.values[index], mean, push.step);
        }
        """u8;

    /// <summary>
    /// Number of invocations per workgroup in the brick classification shader. Must match the
    /// <c>local_size_x</c> declared in the shader, because the dispatch arithmetic is derived from it.
    /// </summary>
    /// <remarks>
    /// One. The pass parallelises across bricks rather than within one, so a workgroup is a single brick and
    /// the workgroup count is the brick count.
    /// </remarks>
    public const int BrickClassifyLocalSize = 1;

    /// <summary>
    /// Size in bytes of the brick classification push constant block: an <c>ivec4</c> of integer shape, then
    /// two <c>vec4</c>s for the level's metric and origin.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Four-component strides rather than the scalars they carry, because a push constant block's layout has
    /// to match between GLSL and C# byte for byte and a four-component stride is the alignment both languages
    /// agree on without consulting the specification.
    /// </para>
    /// <para>
    /// The origin is a separate term rather than being folded into the brick's lattice coordinate because the
    /// shader works in single precision. A lattice coordinate at the edge of a large world multiplied by a
    /// brick size overflows a float's exact integer range, so the CPU subtracts the level's own origin first
    /// and uploads a coordinate relative to it. Inside a level the numbers are then small enough that the
    /// arithmetic is exact, and the level's absolute position survives as its own term.
    /// </para>
    /// </remarks>
    public const int BrickClassifyPushConstantSize = 48;

    /// <summary>The largest brick the classification shader can hold in shared memory, in samples per axis.</summary>
    public const int BrickClassifyMaxSamples = 10;
    /// <summary>
    /// Classifies a clipmap level's brick grid: for each brick, how many of its cells the terrain surface
    /// passes through, and therefore whether the brick is worth allocating at all.
    /// </summary>
    /// <remarks>
    /// <para>
    /// This is the pass the pipeline hangs off. A level has 1728 bricks of 1000 terrain samples each, which is
    /// 1.7 million distance evaluations per level — the frame on the CPU, a millisecond on the GPU. It is
    /// also the one stage whose cost is proportional to the size of the world rather than to what changed,
    /// which is precisely the shape of work a GPU is for.
    /// </para>
    /// <para>
    /// One invocation per brick, and the parallelism is across bricks rather than within one. A brick's 1000
    /// samples shared across a workgroup is marginally faster for a single brick and much harder to be sure
    /// of: the cooperative fill and the per-lane reduction both depend on the driver's actual workgroup
    /// size, and a mismatch there produces a wrong count rather than an error. With 1728 bricks per level
    /// there is more than enough work to fill the machine without splitting a brick.
    /// </para>
    /// <para>
    /// The field lives in shared memory rather than in registers. A thousand floats is four kilobytes, which
    /// a compiler will spill out of registers anyway; shared memory holds it outright and the bank conflicts
    /// for a walk this regular are not worth avoiding.
    /// </para>
    /// <para>
    /// The output is one integer per brick: the number of cells the surface passes through, or zero for a
    /// brick that is entirely air or entirely solid. Zero is what keeps the brick out of the pool, and it is
    /// the number the CPU reference is checked against — a count rather than a boolean, so a pass that
    /// merely detects "something is here" for every brick cannot pass by accident.
    /// </para>
    /// <para>
    /// The terrain is a closed-form plateau of three superposed sines, chosen so the GLSL and its C# reference
    /// evaluate the identical function on the identical inputs. Real terrain generation will replace it
    /// without changing anything around it — the dispatch, the shared-memory layout, the crossing test and
    /// the verification all stay.
    /// </para>
    /// <para>
    /// A sample of exactly zero is treated as outside the surface, matching <c>SdfBrick.HasCrossing</c>. That
    /// tie-break is what makes classification and extraction agree about which cells own the surface, and
    /// without it the two disagree on every cell that lands exactly on the terrain.
    /// </para>
    /// </remarks>
    public static ReadOnlySpan<byte> BrickClassify =>
        """
        #version 450

        layout(local_size_x = 1) in;

        layout(binding = 0) readonly buffer BrickKeys
        {
            ivec4 keys[];   // xyz is the brick's lattice coordinate; w is unused
        } brickKeys;

        layout(binding = 1) writeonly buffer BrickCrossings
        {
            int counts[];   // one entry per brick: the number of cells the surface passes through
        } brickCrossings;

        layout(push_constant) uniform PushConstants
        {
            ivec4 shape;    // x = voxels per brick, y = samples per axis, z = brick count, w = unused
            vec4 metric;    // x = brick size in metres, y = voxel size in metres, z/w unused
            vec4 origin;    // the level's origin in metres, added after the local position
        } push;

        const int MAX_SAMPLES = 10;

        // The eight corners of a cell, indexed exactly as SdfBrick.CellCorners is on the CPU.
        const ivec3 CORNERS[8] = ivec3[8](
            ivec3(0, 0, 0), ivec3(1, 0, 0), ivec3(0, 1, 0), ivec3(1, 1, 0),
            ivec3(0, 0, 1), ivec3(1, 0, 1), ivec3(0, 1, 1), ivec3(1, 1, 1)
        );

        // The twelve edges of a cell, from SdfBrick.CellEdgeCorners, in the same order.
        const ivec2 EDGES[12] = ivec2[12](
            ivec2(0, 1), ivec2(2, 3), ivec2(4, 5), ivec2(6, 7),
            ivec2(0, 2), ivec2(1, 3), ivec2(4, 6), ivec2(5, 7),
            ivec2(0, 4), ivec2(1, 5), ivec2(2, 6), ivec2(3, 7)
        );

        shared float field[MAX_SAMPLES][MAX_SAMPLES][MAX_SAMPLES];

        // The terrain height, in metres. Every term is a function of world position alone, so a brick's
        // field depends only on where the brick is and never on when it was evaluated.
        float heightAt(float x, float z)
        {
            return 80.0 * sin(x * 0.0125) * cos(z * 0.0115)
                 + 30.0 * sin(x * 0.0310) * sin(z * 0.0270)
                 + 12.0 * cos(x * 0.0700) * sin(z * 0.0650);
        }

        void main()
        {
            uint brickIndex = gl_WorkGroupID.x;
            if (brickIndex >= uint(push.shape.z))
                return;

            int voxels = push.shape.x;
            int samples = push.shape.y;
            float brickSize = push.metric.x;
            float voxelSize = push.metric.y;

            // The brick's minimum corner is its lattice coordinate relative to the level's origin, plus that
            // origin. Keeping the two terms apart is what keeps the arithmetic inside single precision even
            // when the level itself is hundreds of kilometres from the world origin.
            vec3 brickOrigin = push.origin.xyz + vec3(brickKeys.keys[brickIndex].xyz) * brickSize;

            for (int z = 0; z < samples; z++)
            {
                for (int y = 0; y < samples; y++)
                {
                    for (int x = 0; x < samples; x++)
                    {
                        vec3 world = brickOrigin + vec3(float(x - 1), float(y - 1), float(z - 1)) * voxelSize;

                        // Positive above the terrain, negative below, in metres.
                        field[z][y][x] = world.y - heightAt(world.x, world.z);
                    }
                }
            }

            int crossed = 0;

            for (int z = 0; z < voxels; z++)
            {
                for (int y = 0; y < voxels; y++)
                {
                    for (int x = 0; x < voxels; x++)
                    {
                        for (int edge = 0; edge < 12; edge++)
                        {
                            ivec3 ca = CORNERS[EDGES[edge].x];
                            ivec3 cb = CORNERS[EDGES[edge].y];

                            float da = field[z + ca.z][y + ca.y][x + ca.x];
                            float db = field[z + cb.z][y + cb.y][x + cb.x];

                            if ((da < 0.0) != (db < 0.0))
                            {
                                crossed++;
                                break;
                            }
                        }
                    }
                }
            }

            brickCrossings.counts[brickIndex] = crossed;
        }
        """u8;
}
