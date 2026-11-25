using System.IO.Compression;
using BrutalTestEngine.Engine;

namespace BrutalTestEngine.Tests.Engine;

/// <summary>
/// Tests over the render-target capture's encoding, which is the part of the MCP capture path that can be
/// wrong without any error being raised.
/// </summary>
/// <remarks>
/// A capture that produces a valid PNG of the wrong rows is worse than one that fails: the consumer has no
/// way to tell an upside-down world from a rendering bug, and the whole point of the capture is to tell those
/// apart. So the row order is asserted rather than eyeballed.
/// </remarks>
[TestFixture]
public sealed class RenderTargetImageTests
{
    [Test]
    public void A_capture_encodes_to_a_png_in_the_same_row_order()
    {
        // Two rows, one pixel each: row 0 is blue and row 1 is red. Vulkan hands over the image top-down, so
        // PNG row 0 must be the blue one. An encoder that flips here would make every capture upside-down,
        // and an upside-down terrain is impossible to tell from a broken one.
        var bgra = new byte[]
        {
                0xFF, 0x00, 0x00, 0xFF, // row 0: blue in BGRA
                0x00, 0x00, 0xFF, 0xFF, // row 1: red in BGRA
        };

        var image = new RenderTargetImage(1, 2, bgra, isSrgb: false);
        var rows = DecodeRows(image.ToPng(), 1, 2);

        Assert.Multiple(() =>
        {
            Assert.That(rows[0][0], Is.EqualTo(0xFF), "the first PNG row should be the blue pixel");
            Assert.That(rows[0][2], Is.EqualTo(0x00), "the first PNG row should have no red");

            Assert.That(rows[1][2], Is.EqualTo(0xFF), "the second PNG row should be the red pixel");
            Assert.That(rows[1][0], Is.EqualTo(0x00), "the second PNG row should have no blue");
        });
    }

    [Test]
    public void The_encoded_png_has_a_valid_signature_and_round_trips_its_pixels()
    {
        var bgra = new byte[3 * 2 * 4];

        for (var i = 0; i < bgra.Length; i++)
            bgra[i] = (byte)i;

        var image = new RenderTargetImage(3, 2, bgra, isSrgb: true);
        var png = image.ToPng();

        Assert.Multiple(() =>
        {
            Assert.That(
                png.Take(8),
                Is.EqualTo(new byte[] { 0x89, 0x50, 0x4E, 0x47, 0x0D, 0x0A, 0x1A, 0x0A }),
                "the PNG signature is wrong"
            );
            Assert.That(image.IsSrgb, Is.True, "the source transfer function was not recorded");
        });

        // Every pixel survives the encode, which is what makes the capture trustworthy: a lossy step here
        // would hide the single-pixel artefacts the tool exists to reveal.
        var rows = DecodeRows(png, 3, 2);

        for (var y = 0; y < 2; y++)
        {
            var sourceRow = y * 3 * 4;

            for (var x = 0; x < 3 * 4; x++)
            {
                Assert.That(
                    rows[y][x],
                    Is.EqualTo(bgra[sourceRow + x]),
                    $"pixel byte {x} of PNG row {y} did not round-trip"
                );
            }
        }
    }

    [Test]
    public void A_capture_smaller_than_its_dimensions_describe_is_rejected()
    {
        var tooSmall = new byte[4 * 3];

        Assert.That(
            () => new RenderTargetImage(4, 4, tooSmall, isSrgb: false),
            Throws.ArgumentException
        );
    }

    /// <summary>
    /// Decodes a PNG this encoder produced back into unfiltered rows.
    /// </summary>
    /// <remarks>
    /// Deliberately minimal and matching the encoder's own choices — 8-bit RGBA, filter 1 — rather than a
    /// general decoder, because a general decoder would be a second implementation to trust and this exists to
    /// check the first one.
    /// </remarks>
    private static byte[][] DecodeRows(byte[] png, int width, int height)
    {
        using var stream = new MemoryStream(png);

        var signature = new byte[8];
        stream.ReadExactly(signature);

        byte[]? compressed = null;

        while (stream.Position < stream.Length)
        {
            var lengthBytes = new byte[4];
            stream.ReadExactly(lengthBytes);

            var length = (lengthBytes[0] << 24) | (lengthBytes[1] << 16) | (lengthBytes[2] << 8) | lengthBytes[3];

            var typeBytes = new byte[4];
            stream.ReadExactly(typeBytes);
            var type = System.Text.Encoding.ASCII.GetString(typeBytes);

            var data = new byte[length];
            stream.ReadExactly(data);

            // The CRC is covered by the encoder's own round-trip, not by this helper.
            var crc = new byte[4];
            stream.ReadExactly(crc);

            if (type == "IDAT")
                compressed = data;

            if (type == "IEND")
                break;
        }

        Assert.That(compressed, Is.Not.Null, "the PNG had no IDAT chunk");

        using var inflate = new ZLibStream(new MemoryStream(compressed!), CompressionMode.Decompress);
        using var raw = new MemoryStream();
        inflate.CopyTo(raw);

        var data2 = raw.ToArray();
        var stride = width * 4;
        var rows = new byte[height][];

        for (var y = 0; y < height; y++)
        {
            var offset = y * (stride + 1);

            Assert.That(data2[offset], Is.EqualTo(1), $"row {y} did not use the Sub filter");

            var row = new byte[stride];

            for (var x = 0; x < stride; x++)
            {
                var left = x >= 4 ? row[x - 4] : (byte)0;
                row[x] = (byte)(data2[offset + 1 + x] + left);
            }

            rows[y] = row;
        }

        return rows;
    }
}
