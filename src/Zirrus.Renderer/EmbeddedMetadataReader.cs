using System.Buffers.Binary;
using System.IO.Compression;
using System.Text;
using System.Text.Json.Nodes;
using System.Xml;
using System.Xml.Linq;
using Zirrus.Domain.Assets;

namespace Zirrus.Renderer;

/// <summary>Reads the bounded EXIF, IPTC-IIM and XMP subset used by the ingestion MVP.</summary>
internal sealed class EmbeddedMetadataReader
{
    private const int MaxInputBytes = 100 * 1024 * 1024;
    private const int MaxXmpBytes = 1024 * 1024;
    private static readonly XNamespace Dc = "http://purl.org/dc/elements/1.1/";
    private static readonly XNamespace Xmp = "http://ns.adobe.com/xap/1.0/";
    private static readonly XNamespace Photoshop = "http://ns.adobe.com/photoshop/1.0/";
    private static readonly XNamespace Rdf = "http://www.w3.org/1999/02/22-rdf-syntax-ns#";
    private readonly Dictionary<string, JsonNode?> _values = new(StringComparer.Ordinal);

    public async Task<AssetMetadata?> ExtractAsync(Stream source, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(source);
        using var buffer = new MemoryStream();
        await source.CopyToAsync(buffer, cancellationToken);
        if (buffer.Length > MaxInputBytes)
        {
            throw new UnsupportedImageException("metadata input exceeds 100 MiB");
        }

        var bytes = buffer.ToArray();
        if (bytes.AsSpan().StartsWith([0xff, 0xd8]))
        {
            ReadJpeg(bytes);
        }
        else if (bytes.AsSpan().StartsWith([0x89, 0x50, 0x4e, 0x47, 0x0d, 0x0a, 0x1a, 0x0a]))
        {
            ReadPng(bytes);
        }
        else if (bytes.Length >= 12 && bytes.AsSpan(0, 4).SequenceEqual("RIFF"u8) && bytes.AsSpan(8, 4).SequenceEqual("WEBP"u8))
        {
            ReadWebP(bytes);
        }
        else
        {
            throw new UnsupportedImageException("unsupported metadata container");
        }

        return _values.Count == 0 ? null : AssetMetadata.Parse(new JsonObject(_values).ToJsonString());
    }

    private void ReadJpeg(ReadOnlySpan<byte> data)
    {
        var offset = 2;
        while (offset + 4 <= data.Length && data[offset] == 0xff)
        {
            while (offset < data.Length && data[offset] == 0xff) offset++;
            if (offset >= data.Length) break;
            var marker = data[offset++];
            if (marker is 0xd9 or 0xda) break;
            if (marker is 0x01 or >= 0xd0 and <= 0xd7) continue;
            if (offset + 2 > data.Length) break;
            var length = (int)BinaryPrimitives.ReadUInt16BigEndian(data.Slice(offset, 2));
            if (length < 2 || offset + length > data.Length) break;
            var segment = data.Slice(offset + 2, length - 2);
            if (marker == 0xe1)
            {
                if (segment.StartsWith("Exif\0\0"u8)) ReadTiff(segment[6..]);
                else if (segment.StartsWith("http://ns.adobe.com/xap/1.0/\0"u8)) ReadXmp(segment[29..]);
            }
            else if (marker == 0xed && segment.StartsWith("Photoshop 3.0\0"u8))
            {
                ReadPhotoshopIptc(segment[14..]);
            }

            offset += length;
        }
    }

    private void ReadPng(ReadOnlySpan<byte> data)
    {
        var offset = 8;
        while (offset + 12 <= data.Length)
        {
            var length = BinaryPrimitives.ReadUInt32BigEndian(data.Slice(offset, 4));
            if (length > MaxInputBytes || offset + 12L + length > data.Length) break;
            var type = data.Slice(offset + 4, 4);
            var chunk = data.Slice(offset + 8, (int)length);
            if (type.SequenceEqual("eXIf"u8)) ReadTiff(chunk);
            else if (type.SequenceEqual("iTXt"u8)) ReadPngText(chunk);
            offset += checked((int)length + 12);
            if (type.SequenceEqual("IEND"u8)) break;
        }
    }

    private void ReadPngText(ReadOnlySpan<byte> data)
    {
        var keywordEnd = data.IndexOf((byte)0);
        if (keywordEnd < 0 || Encoding.Latin1.GetString(data[..keywordEnd]) != "XML:com.adobe.xmp") return;
        var index = keywordEnd + 1;
        if (index + 2 > data.Length) return;
        var compressed = data[index++] == 1;
        index++;
        var languageEnd = data[index..].IndexOf((byte)0);
        if (languageEnd < 0) return;
        index += languageEnd + 1;
        var translatedEnd = data[index..].IndexOf((byte)0);
        if (translatedEnd < 0) return;
        index += translatedEnd + 1;
        var text = data[index..];
        if (compressed)
        {
            using var input = new MemoryStream(text.ToArray());
            using var zlib = new ZLibStream(input, CompressionMode.Decompress);
            using var output = new MemoryStream();
            zlib.CopyTo(output);
            if (output.Length > MaxXmpBytes) return;
            ReadXmp(output.ToArray());
        }
        else
        {
            ReadXmp(text);
        }
    }

    private void ReadWebP(ReadOnlySpan<byte> data)
    {
        var offset = 12;
        while (offset + 8 <= data.Length)
        {
            var size = BinaryPrimitives.ReadUInt32LittleEndian(data.Slice(offset + 4, 4));
            if (size > MaxInputBytes || offset + 8L + size > data.Length) break;
            var chunk = data.Slice(offset + 8, (int)size);
            if (data.Slice(offset, 4).SequenceEqual("EXIF"u8))
            {
                ReadTiff(chunk.StartsWith("Exif\0\0"u8) ? chunk[6..] : chunk);
            }
            else if (data.Slice(offset, 4).SequenceEqual("XMP "u8))
            {
                ReadXmp(chunk);
            }

            offset += 8 + (int)size + ((int)size & 1);
        }
    }

    private void ReadTiff(ReadOnlySpan<byte> tiff)
    {
        if (tiff.Length < 8) return;
        var little = tiff[..2].SequenceEqual("II"u8);
        if (!little && !tiff[..2].SequenceEqual("MM"u8)) return;
        ushort U16(int p) => p < 0 || p + 2 > tiff.Length ? (ushort)0 : little ? BinaryPrimitives.ReadUInt16LittleEndian(tiff[p..]) : BinaryPrimitives.ReadUInt16BigEndian(tiff[p..]);
        uint U32(int p) => p < 0 || p + 4 > tiff.Length ? 0 : little ? BinaryPrimitives.ReadUInt32LittleEndian(tiff[p..]) : BinaryPrimitives.ReadUInt32BigEndian(tiff[p..]);
        if (U16(2) != 42) return;
        var ifd0 = checked((int)U32(4));
        var exif = 0;
        var gps = 0;
        ReadIfd(ifd0, (tag, type, count, valueOffset, inline) =>
        {
            if (tag == 0x010e) Set("title", ReadAscii(type, count, valueOffset, inline));
            if (tag == 0x013b) Set("creator", ReadAscii(type, count, valueOffset, inline));
            if (tag == 0x8298) Set("copyright", ReadAscii(type, count, valueOffset, inline));
            if (tag == 0x0132) Set("capture_date", ReadAscii(type, count, valueOffset, inline));
            if (tag == 0x8769) exif = checked((int)valueOffset);
            if (tag == 0x8825) gps = checked((int)valueOffset);
        });
        if (exif > 0)
        {
            ReadIfd(exif, (tag, type, count, valueOffset, inline) =>
            {
                if (tag == 0x9003 || tag == 0x9004) Set("capture_date", ReadAscii(type, count, valueOffset, inline));
            });
        }

        if (gps > 0) ReadGps(gps);

        void ReadIfd(int start, Action<ushort, ushort, uint, uint, int> visit)
        {
            if (start < 8 || start + 2 > tiff.Length) return;
            var count = Math.Min(U16(start), (ushort)256);
            for (var i = 0; i < count; i++)
            {
                var entry = start + 2 + i * 12;
                if (entry + 12 > tiff.Length) break;
                var type = U16(entry + 2);
                var items = U32(entry + 4);
                var size = type switch { 1 or 2 or 6 or 7 => 1, 3 or 8 => 2, 4 or 9 or 11 => 4, 5 or 10 or 12 => 8, _ => 0 };
                var byteCount = (long)items * size;
                var value = U32(entry + 8);
                var inline = entry + 8;
                if (byteCount > 4 && (value > tiff.Length || value + byteCount > tiff.Length)) continue;
                visit(U16(entry), type, items, value, inline);
            }
        }

        string? ReadAscii(ushort type, uint count, uint valueOffset, int inline)
        {
            if (type != 2 || count == 0 || count > 4096) return null;
            var start = count <= 4 ? inline : checked((int)valueOffset);
            if (start < 0 || start + count > tiff.Length) return null;
            return Encoding.ASCII.GetString(tiff.Slice(start, (int)count)).TrimEnd('\0', ' ');
        }

        void ReadGps(int start)
        {
            string? latRef = null, lonRef = null;
            double? lat = null, lon = null;
            ReadIfd(start, (tag, type, count, valueOffset, inline) =>
            {
                if (tag is 1 or 3)
                {
                    var value = ReadAscii(type, count, valueOffset, inline);
                    if (tag == 1) latRef = value; else lonRef = value;
                }
                else if (tag is 2 or 4 && type == 5 && count == 3)
                {
                    var p = checked((int)valueOffset);
                    if (p + 24 > tiff.Length) return;
                    double Rational(int at)
                    {
                        var numerator = U32(at);
                        var denominator = U32(at + 4);
                        return denominator == 0 ? 0 : numerator / (double)denominator;
                    }

                    var degrees = Rational(p);
                    var minutes = Rational(p + 8);
                    var seconds = Rational(p + 16);
                    var coordinate = degrees + minutes / 60 + seconds / 3600;
                    if (tag == 2) lat = coordinate; else lon = coordinate;
                }
            });
            if (lat is { } latitude && latRef == "S") lat = -latitude;
            if (lon is { } longitude && lonRef == "W") lon = -longitude;
            if (lat is { } finalLat) Set("gps_latitude", finalLat);
            if (lon is { } finalLon) Set("gps_longitude", finalLon);
        }
    }

    private void ReadPhotoshopIptc(ReadOnlySpan<byte> data)
    {
        var offset = 0;
        while (offset + 7 <= data.Length)
        {
            if (!data.Slice(offset, 4).SequenceEqual("8BIM"u8)) break;
            var resource = BinaryPrimitives.ReadUInt16BigEndian(data.Slice(offset + 4, 2));
            offset += 6;
            if (offset >= data.Length) break;
            var nameLength = data[offset++];
            offset += nameLength + ((nameLength + 1) & 1);
            if (offset + 4 > data.Length) break;
            var size = BinaryPrimitives.ReadUInt32BigEndian(data.Slice(offset, 4));
            offset += 4;
            if (size > data.Length - offset) break;
            if (resource == 0x0404) ReadIptc(data.Slice(offset, (int)size));
            offset += (int)size + ((int)size & 1);
        }
    }

    private void ReadIptc(ReadOnlySpan<byte> data)
    {
        var offset = 0;
        var keywords = new List<string>();
        while (offset + 5 <= data.Length)
        {
            if (data[offset++] != 0x1c) break;
            var record = data[offset++];
            var dataset = data[offset++];
            var length = BinaryPrimitives.ReadUInt16BigEndian(data.Slice(offset, 2));
            offset += 2;
            if ((length & 0x8000) != 0)
            {
                var bytes = length & 0x7fff;
                if (bytes is 0 or > 4 || offset + bytes > data.Length) break;
                length = 0;
                for (var i = 0; i < bytes; i++) length = checked((length << 8) | data[offset++]);
            }

            if (offset + length > data.Length) break;
            var value = Encoding.Latin1.GetString(data.Slice(offset, length)).Trim();
            offset += length;
            if (record != 2 || value.Length == 0) continue;
            switch (dataset)
            {
                case 5: Set("title", value); break;
                case 25 when keywords.Count < 128: keywords.Add(value.Length > 256 ? value[..256] : value); break;
                case 55: Set("capture_date", value); break;
                case 80: Set("creator", value); break;
                case 116: Set("copyright", value); break;
            }
        }

        if (keywords.Count > 0) Set("keywords", new JsonArray(keywords.Select(value => (JsonNode?)JsonValue.Create(value)).ToArray()));
    }

    private void ReadXmp(ReadOnlySpan<byte> data)
    {
        if (data.Length is 0 or > MaxXmpBytes) return;
        try
        {
            using var input = new MemoryStream(data.ToArray(), writable: false);
            using var reader = XmlReader.Create(input, new XmlReaderSettings
            {
                DtdProcessing = DtdProcessing.Prohibit,
                XmlResolver = null,
                MaxCharactersInDocument = MaxXmpBytes,
                MaxCharactersFromEntities = MaxXmpBytes,
                IgnoreComments = true,
                IgnoreProcessingInstructions = true,
            });
            var document = XDocument.Load(reader, LoadOptions.None);
            var descriptions = document.Descendants(Rdf + "Description").Take(16).ToArray();
            if (descriptions.Length == 0) return;
            foreach (var description in descriptions)
            {
                Set("title", Property(description, Dc + "title"));
                Set("creator", Property(description, Dc + "creator"));
                Set("copyright", Property(description, Dc + "rights"));
                Set("capture_date", Property(description, Xmp + "CreateDate"));
                Set("credit", Property(description, Photoshop + "Credit"));
            }

            var subject = descriptions.SelectMany(description => PropertyValues(description, Dc + "subject"))
                .Distinct(StringComparer.Ordinal).Take(128).ToList();
            if (subject.Count > 0) Set("keywords", new JsonArray(subject.Select(value => (JsonNode?)JsonValue.Create(value)).ToArray()));
        }
        catch (XmlException)
        {
            throw new UnsupportedImageException("malformed XMP metadata");
        }
    }

    private static string? Property(XElement description, XName name)
    {
        var attribute = description.Attribute(name)?.Value;
        if (!string.IsNullOrWhiteSpace(attribute)) return attribute.Trim();
        var property = description.Element(name);
        if (property is null) return null;
        if (property.Elements().FirstOrDefault(e => e.Name == Rdf + "Alt") is { } alt)
        {
            return alt.Elements(Rdf + "li").OrderByDescending(e => (string?)e.Attribute(XNamespace.Xml + "lang") == "x-default").Select(e => e.Value.Trim()).FirstOrDefault(v => v.Length > 0);
        }

        var item = property.Descendants(Rdf + "li").Select(e => e.Value.Trim()).FirstOrDefault(v => v.Length > 0);
        if (item is not null) return item;
        var text = property.Value.Trim();
        return text.Length > 0 ? text : null;
    }

    private static List<string> PropertyValues(XElement description, XName name)
    {
        var property = description.Element(name);
        if (property is null) return [];
        var values = property.Descendants(Rdf + "li").Select(e => e.Value.Trim()).Where(v => v.Length > 0).Select(v => v.Length > 256 ? v[..256] : v).Distinct(StringComparer.Ordinal).Take(128).ToList();
        if (values.Count > 0) return values;
        var text = property.Value.Trim();
        return text.Length > 0 ? [text] : [];
    }

    private void Set(string key, string? value)
    {
        if (!string.IsNullOrWhiteSpace(value)) _values[key] = JsonValue.Create(value.Trim().Length > 4096 ? value.Trim()[..4096] : value.Trim());
    }

    private void Set(string key, double value) => _values[key] = JsonValue.Create(value);

    private void Set(string key, JsonArray value)
    {
        if (value.Count > 0) _values[key] = value;
    }
}
