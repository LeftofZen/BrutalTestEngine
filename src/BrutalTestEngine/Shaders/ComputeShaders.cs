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
}
