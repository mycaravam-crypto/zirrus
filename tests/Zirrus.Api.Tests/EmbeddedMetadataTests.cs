extern alias renderer;

using System.Buffers.Binary;
using System.Text;
using System.Text.Json;
using Zirrus.Application.Ingestion;
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
        Assert.Equal(new[] { "sea", "harbour" }, root.GetProperty("keywords").EnumerateArray().Select(v => v.GetString()));
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

    private static async Task<Domain.Assets.AssetMetadata?> ExtractAsync(byte[] content)
    {
        var limits = new Renderer.ParserLimits { Timeout = TimeSpan.FromSeconds(10), MaxMemoryBytes = 512L * 1024 * 1024 };
        var parser = new Renderer.SubprocessRenderer(limits, typeof(Renderer.RenderEndpoint).Assembly.Location);
        return await parser.ExtractMetadataAsync(new MemoryStream(content, writable: false), CancellationToken.None);
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
