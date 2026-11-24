using BrutalTestEngine.Compute;
using BrutalTestEngine.Shaders;
using NUnit.Framework;

namespace BrutalTestEngine.Tests.Compute;

/// <summary>
/// Tests for the compute abstraction's managed-side arithmetic.
/// </summary>
/// <remarks>
/// The GPU side cannot be unit-tested without a device, and the engine's headless smoke run covers it. What
/// can be tested here is the arithmetic that decides how many workgroups a dispatch needs, because getting
/// it wrong is silent: too few groups leaves the tail of the data unprocessed and produces no error at all.
/// </remarks>
[TestFixture]
public sealed class ComputePipelineTests
{
    [Test]
    public void DivideRoundUp_covers_an_exact_multiple()
    {
        Assert.That(ComputePipeline.DivideRoundUp(256, 64), Is.EqualTo(4));
    }

    [Test]
    public void DivideRoundUp_rounds_a_partial_group_up()
    {
        // 257 elements at 64 per group needs 5 groups, not 4: the last group holds one element.
        Assert.That(ComputePipeline.DivideRoundUp(257, 64), Is.EqualTo(5));
    }

    [Test]
    public void DivideRoundUp_of_one_element_is_one_group()
    {
        Assert.That(ComputePipeline.DivideRoundUp(1, 64), Is.EqualTo(1));
    }

    [Test]
    public void DivideRoundUp_of_nothing_is_no_groups()
    {
        Assert.That(ComputePipeline.DivideRoundUp(0, 64), Is.Zero);
    }

    [Test]
    public void DivideRoundUp_rejects_a_non_positive_group_size()
    {
        Assert.That(() => ComputePipeline.DivideRoundUp(10, 0), Throws.TypeOf<ArgumentOutOfRangeException>());
    }

    [Test]
    public void DivideRoundUp_rejects_a_negative_element_count()
    {
        Assert.That(() => ComputePipeline.DivideRoundUp(-1, 64), Throws.TypeOf<ArgumentOutOfRangeException>());
    }

    [Test]
    public void The_shader_local_size_matches_the_declared_workgroup_size()
    {
        // The dispatch arithmetic is derived from this constant, so if it drifts from the shader's
        // local_size_x the tail of every field is silently left unprocessed.
        var source = System.Text.Encoding.UTF8.GetString(ComputeShaders.FieldStep);

        Assert.That(source, Does.Contain($"local_size_x = {ComputeShaders.FieldStepLocalSize}"));
    }

    [Test]
    public void The_push_constant_block_is_sixteen_byte_aligned()
    {
        // Vulkan requires a push constant block to be a multiple of four bytes, and the shader's block is
        // laid out as a uint followed by three floats.
        Assert.That(ComputeShaders.FieldStepPushConstantSize % 16, Is.Zero);
    }
}
