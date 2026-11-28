extern alias renderer;

using System.Buffers.Binary;
using System.Text;
using System.Text.Json;
using Zirrus.Application.Ingestion;
using Zirrus.Domain.Assets;
using Renderer = renderer::Zirrus.Renderer;

namespace Zirrus.Api.Tests;

/// <summary>Embedded metadata parser behavior through its isolated child process.</summary>
public sealed class EmbeddedMetadataTests
{
    [Fact]
    public async Task Reads_xmp_fields_and_rdf_arrays_from_jpeg()
    {
        const string xmp = """
            <x:xmpmeta xmlns:x="adobe:ns:meta/">
              <rdf:RDF xmlns:rdf="http://www.w3.org/1999/02/22-rdf-syntax-ns#">
                <rdf:Description xmlns:dc="http://purl.org/dc/elements/1.1/"
                                 xmlns:xmp="http://ns.adobe.com/xap/1.0/"
                                 xmlns:photoshop="http://ns.adobe.com/photoshop/1.0/">
                  <dc:title><rdf:Alt><rdf:li xml:lang="en-US">Harbour</rdf:li><rdf:li xml:lang="x-default">Harbour title</rdf:li></rdf:Alt></dc:title>
                  <dc:creator><rdf:Seq><rdf:li>Jane Doe</rdf:li></rdf:Seq></dc:creator>
                  <dc:subject><rdf:Bag><rdf:li>sea</rdf:li><rdf:li>harbour</rdf:li></rdf:Bag></dc:subject>
                  <dc:rights><rdf:Alt><rdf:li xml:lang="x-default">Copyright 2026</rdf:li></rdf:Alt></dc:rights>
                  <xmp:CreateDate>2026-11-28T10:00:00Z</xmp:CreateDate>
                  <photoshop:Credit>Agency</photoshop:Credit>
                </rdf:Description>
              </rdf:RDF>
            </x:xmpmeta>
            """;
        var metadata = await ExtractAsync(JpegWithXmp(xmp));
        Assert.NotNull(metadata);
        using var document = JsonDocument.Parse(metadata.Json);
        var root = document.RootElement;
        Assert.Equal("Harbour title", root.GetProperty("title").GetString());
        Assert.Equal("Jane Doe", root.GetProperty("creator").GetString());
        Assert.Equal("Copyright 2026", root.GetProperty("copyright").GetString());
        Assert.Equal("2026-11-28T10:00:00Z", root.GetProperty("capture_date").GetString());
        Assert.Equal("Agency", root.GetProperty("credit").GetString());
        Assert.Equal("sea,harbour", string.Join(',', root.GetProperty("keywords").EnumerateArray().Select(v => v.GetString())));
    }

    [Fact]
    public async Task Reads_xmp_from_png_and_webp_containers()
    {
        const string xmp = "<x:xmpmeta xmlns:x=\"adobe:ns:meta/\"><rdf:RDF xmlns:rdf=\"http://www.w3.org/1999/02/22-rdf-syntax-ns#\"><rdf:Description xmlns:dc=\"http://purl.org/dc/elements/1.1/\" dc:title=\"Container title\" /></rdf:RDF></x:xmpmeta>";
        foreach (var image in new[] { PngWithXmp(xmp), WebPWithXmp(xmp) })
        {
            var metadata = await ExtractAsync(image);
            Assert.NotNull(metadata);
            using var document = JsonDocument.Parse(metadata.Json);
            Assert.Equal("Container title", document.RootElement.GetProperty("title").GetString());
        }
    }

    [Fact]
    public async Task Reads_exif_gps_coordinates()
    {
        var metadata = await ExtractAsync(JpegWithExifGps());
        Assert.NotNull(metadata);
        using var document = JsonDocument.Parse(metadata.Json);
        Assert.Equal(54.5, document.RootElement.GetProperty("gps_latitude").GetDouble(), 5);
        Assert.Equal(10.0, document.RootElement.GetProperty("gps_longitude").GetDouble(), 5);
    }

    [Fact]
    public async Task Reads_iptc_fields_and_keyword_arrays()
    {
        var records = new byte[][]
        {
            IptcRecord(5, "Press photo"),
            IptcRecord(25, "coast"),
            IptcRecord(25, "sea"),
            IptcRecord(80, "Alex Example"),
            IptcRecord(116, "Agency 2026"),
        }.SelectMany(value => value).ToArray();
        var photoshop = Encoding.ASCII.GetBytes("Photoshop 3.0\0")
            .Concat(Encoding.ASCII.GetBytes("8BIM"))
            .Concat(new byte[] { 0x04, 0x04, 0x00, 0x00 })
            .Concat(Int32BigEndian(records.Length))
            .Concat(records)
            .Concat(records.Length % 2 == 0 ? Array.Empty<byte>() : new byte[] { 0x00 })
            .ToArray();
        var metadata = await ExtractAsync(JpegWithSegment(0xed, photoshop));
        Assert.NotNull(metadata);
        using var document = JsonDocument.Parse(metadata.Json);
        var root = document.RootElement;
        Assert.Equal("Press photo", root.GetProperty("title").GetString());
        Assert.Equal("Alex Example", root.GetProperty("creator").GetString());
        Assert.Equal("Agency 2026", root.GetProperty("copyright").GetString());
        Assert.Equal("coast,sea", string.Join(',', root.GetProperty("keywords").EnumerateArray().Select(value => value.GetString())));
    }

    [Fact]
    public async Task Rejects_xxe_and_malformed_xmp()
    {
        const string xxe = """
            <!DOCTYPE x [<!ENTITY secret SYSTEM "file:///etc/passwd">]>
            <x:xmpmeta xmlns:x="adobe:ns:meta/"><rdf:RDF xmlns:rdf="http://www.w3.org/1999/02/22-rdf-syntax-ns#"><rdf:Description xmlns:dc="http://purl.org/dc/elements/1.1/" dc:title="&secret;"/></rdf:RDF></x:xmpmeta>
            """;
        await Assert.ThrowsAsync<UnsupportedImageException>(() => ExtractAsync(JpegWithXmp(xxe)));
        await Assert.ThrowsAsync<UnsupportedImageException>(() => ExtractAsync(JpegWithXmp("<broken")));
    }

    private static async Task<AssetMetadata?> ExtractAsync(byte[] content)
    {
        var limits = new Renderer.ParserLimits { Timeout = TimeSpan.FromSeconds(10), MaxMemoryBytes = 512L * 1024 * 1024 };
        var parser = new Renderer.SubprocessRenderer(limits, typeof(Renderer.RenderEndpoint).Assembly.Location);
        return await parser.ExtractMetadataAsync(new MemoryStream(content, writable: false), CancellationToken.None);
    }


    private static byte[] PngWithXmp(string xml)
    {
        using var output = new MemoryStream();
        output.Write([0x89, 0x50, 0x4e, 0x47, 0x0d, 0x0a, 0x1a, 0x0a]);
        var text = Encoding.UTF8.GetBytes("XML:com.adobe.xmp\0\0\0\0\0").Concat(Encoding.UTF8.GetBytes(xml)).ToArray();
        WritePngChunk(output, "iTXt", text);
        WritePngChunk(output, "IEND", []);
        return output.ToArray();
    }

    private static void WritePngChunk(Stream output, string type, byte[] payload)
    {
        Span<byte> length = stackalloc byte[4];
        BinaryPrimitives.WriteInt32BigEndian(length, payload.Length);
        output.Write(length);
        output.Write(Encoding.ASCII.GetBytes(type));
        output.Write(payload);
        output.Write(new byte[4]);
    }

    private static byte[] WebPWithXmp(string xml)
    {
        var payload = Encoding.UTF8.GetBytes(xml);
        using var output = new MemoryStream();
        output.Write(Encoding.ASCII.GetBytes("RIFF"));
        Span<byte> size = stackalloc byte[4];
        BinaryPrimitives.WriteUInt32LittleEndian(size, (uint)(4 + 8 + payload.Length + (payload.Length & 1)));
        output.Write(size);
        output.Write(Encoding.ASCII.GetBytes("WEBP"));
        output.Write(Encoding.ASCII.GetBytes("XMP "));
        BinaryPrimitives.WriteUInt32LittleEndian(size, (uint)payload.Length);
        output.Write(size);
        output.Write(payload);
        if ((payload.Length & 1) != 0) output.WriteByte(0);
        return output.ToArray();
    }

    private static byte[] JpegWithExifGps()
    {
        var tiff = new byte[128];
        tiff[0] = (byte)'I';
        tiff[1] = (byte)'I';
        BinaryPrimitives.WriteUInt16LittleEndian(tiff.AsSpan(2), 42);
        BinaryPrimitives.WriteUInt32LittleEndian(tiff.AsSpan(4), 8);
        BinaryPrimitives.WriteUInt16LittleEndian(tiff.AsSpan(8), 1);
        BinaryPrimitives.WriteUInt16LittleEndian(tiff.AsSpan(10), 0x8825);
        BinaryPrimitives.WriteUInt16LittleEndian(tiff.AsSpan(12), 4);
        BinaryPrimitives.WriteUInt32LittleEndian(tiff.AsSpan(14), 1);
        BinaryPrimitives.WriteUInt32LittleEndian(tiff.AsSpan(18), 26);
        BinaryPrimitives.WriteUInt16LittleEndian(tiff.AsSpan(26), 4);
        WriteGpsEntry(tiff, 28, 1, 2, 2, 0x004e);
        WriteGpsEntry(tiff, 40, 2, 5, 3, 80);
        WriteGpsEntry(tiff, 52, 3, 2, 2, 0x0045);
        WriteGpsEntry(tiff, 64, 4, 5, 3, 104);
        BinaryPrimitives.WriteUInt32LittleEndian(tiff.AsSpan(76), 0);
        WriteRational(tiff, 80, 54, 1);
        WriteRational(tiff, 88, 30, 1);
        WriteRational(tiff, 96, 0, 1);
        WriteRational(tiff, 104, 10, 1);
        WriteRational(tiff, 112, 0, 1);
        WriteRational(tiff, 120, 0, 1);
        return JpegWithSegment(0xe1, Encoding.ASCII.GetBytes("Exif\0\0").Concat(tiff).ToArray());
    }

    private static void WriteGpsEntry(byte[] tiff, int offset, ushort tag, ushort type, uint count, uint value)
    {
        BinaryPrimitives.WriteUInt16LittleEndian(tiff.AsSpan(offset), tag);
        BinaryPrimitives.WriteUInt16LittleEndian(tiff.AsSpan(offset + 2), type);
        BinaryPrimitives.WriteUInt32LittleEndian(tiff.AsSpan(offset + 4), count);
        BinaryPrimitives.WriteUInt32LittleEndian(tiff.AsSpan(offset + 8), value);
    }

    private static void WriteRational(byte[] tiff, int offset, uint numerator, uint denominator)
    {
        BinaryPrimitives.WriteUInt32LittleEndian(tiff.AsSpan(offset), numerator);
        BinaryPrimitives.WriteUInt32LittleEndian(tiff.AsSpan(offset + 4), denominator);
    }

    private static byte[] IptcRecord(byte dataset, string value)
    {
        var text = Encoding.Latin1.GetBytes(value);
        return [0x1c, 0x02, dataset, (byte)(text.Length >> 8), (byte)text.Length, .. text];
    }

    private static IEnumerable<byte> Int32BigEndian(int value)
    {
        Span<byte> bytes = stackalloc byte[4];
        BinaryPrimitives.WriteInt32BigEndian(bytes, value);
        return bytes.ToArray();
    }

    private static byte[] JpegWithSegment(byte marker, byte[] payload)
    {
        using var output = new MemoryStream();
        output.Write([0xff, 0xd8, 0xff, marker]);
        Span<byte> length = stackalloc byte[2];
        BinaryPrimitives.WriteUInt16BigEndian(length, checked((ushort)(payload.Length + 2)));
        output.Write(length);
        output.Write(payload);
        output.Write([0xff, 0xd9]);
        return output.ToArray();
    }

    private static byte[] JpegWithXmp(string xml)
    {
        var prefix = Encoding.ASCII.GetBytes("http://ns.adobe.com/xap/1.0/\0");
        var payload = prefix.Concat(Encoding.UTF8.GetBytes(xml)).ToArray();
        using var output = new MemoryStream();
        output.Write([0xff, 0xd8, 0xff, 0xe1]);
        Span<byte> length = stackalloc byte[2];
        BinaryPrimitives.WriteUInt16BigEndian(length, checked((ushort)(payload.Length + 2)));
        output.Write(length);
        output.Write(payload);
        output.Write([0xff, 0xd9]);
        return output.ToArray();
    }
}
