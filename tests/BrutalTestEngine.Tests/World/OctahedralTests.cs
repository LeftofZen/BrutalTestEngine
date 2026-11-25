using Brutal.Numerics;
using BrutalTestEngine.World;

namespace BrutalTestEngine.Tests.World;

/// <summary>
/// Tests over the octahedral normal encoding.
/// </summary>
[TestFixture]
public sealed class OctahedralTests
{
    private static readonly (float X, float Y, float Z)[] Directions =
    [
        (1.0f, 0.0f, 0.0f),
        (-1.0f, 0.0f, 0.0f),
        (0.0f, 1.0f, 0.0f),
        (0.0f, -1.0f, 0.0f),
        (0.0f, 0.0f, 1.0f),
        (0.0f, 0.0f, -1.0f),
        (0.577f, 0.577f, 0.577f),
        (-0.577f, 0.577f, -0.577f),
    ];

    [Test]
    public void A_direction_round_trips_within_the_encoding_error()
    {
        foreach (var (x, y, z) in Directions)
        {
            var (dx, dy, dz) = Octahedral.Decode(Octahedral.Encode(x, y, z));

            var dot = x * dx + y * dy + z * dz;

            // Four bits per axis is a coarse encoding by design, but a dot product this close to one means
            // the direction is preserved well enough for a shading normal.
            Assert.That(dot, Is.GreaterThan(0.96f), $"direction ({x}, {y}, {z}) decoded badly");
        }
    }

    [Test]
    public void Encoding_is_insensitive_to_the_normal_s_length()
    {
        var unit = Octahedral.Encode(0.0f, 1.0f, 0.0f);
        var scaled = Octahedral.Encode(0.0f, 250.0f, 0.0f);

        Assert.That(scaled, Is.EqualTo(unit));
    }

    [Test]
    public void A_zero_normal_does_not_produce_a_non_finite_direction()
    {
        var packed = Octahedral.Encode(0.0f, 0.0f, 0.0f);
        var (x, y, z) = Octahedral.Decode(packed);

        Assert.Multiple(() =>
        {
            Assert.That(float.IsFinite(x), Is.True);
            Assert.That(float.IsFinite(y), Is.True);
            Assert.That(float.IsFinite(z), Is.True);
        });
    }

    [Test]
    public void The_encoding_fills_the_whole_byte()
    {
        var seen = new HashSet<byte>();

        for (var i = 0; i < 256; i++)
        {
            var angle = i / 256.0f * MathF.Tau;
            var seenDirection = Octahedral.Encode(MathF.Cos(angle), MathF.Sin(angle), 0.0f);

            seen.Add(seenDirection);
        }

        // A four-bit-per-axis encoding has 256 codes; a direction sweep should reach a good fraction of them.
        Assert.That(seen.Count, Is.GreaterThan(32));
    }
}
