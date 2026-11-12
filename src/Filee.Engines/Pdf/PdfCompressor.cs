// Makes a PDF smaller while it stays a PDF with PDFsharp:
// deflates every stream at the highest level, page thumbnails and other applications' private data are left out,
// and when the preset drops metadata, unreferenced XMP metadata packets are removed.
// Text, fonts and vector drawings stay intact.

using System.IO.Compression;
using Filee.Core.Presets;
using PdfSharp.Pdf;
using PdfSharp.Pdf.Advanced;
using PdfSharp.Pdf.IO;

namespace Filee.Engines.Pdf;

/// <summary>Shrinks the streams and metadata of a PDF document in place (before it is saved).</summary>
internal static class PdfCompressor
{
    public static (int Dpi, int Quality) Settings(PdfCompression level) => level switch
    {
        PdfCompression.Screen => (72, 55),
        PdfCompression.Print => (300, 85),
        _ => (150, 65),
    };

    /// <summary>Shrinks <paramref name="document"/>; save it afterwards.</summary>
    /// <param name="keepMetadata">False drops the XMP metadata of pages, pictures and forms.</param>
    public static void Compress(PdfDocument document, PdfCompression level, bool keepMetadata, CancellationToken cancellationToken)
    {
        foreach (var page in document.Pages)
        {
            page.Elements.Remove("/Thumb");
            page.Elements.Remove("/PieceInfo");
        }
        document.Internals.Catalog.Elements.Remove("/PieceInfo");
        document.Internals.Catalog.Elements.Remove("/Metadata");

        foreach (var item in document.Internals.GetAllObjects())
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (item is not PdfDictionary dictionary)
                continue;
            if (!keepMetadata && dictionary.Elements.ContainsKey("/Metadata"))
                dictionary.Elements.Remove("/Metadata");
            if (dictionary.Stream is not null)
                Deflate(dictionary);
        }

        document.Options.CompressContentStreams = true;
        document.Options.FlateEncodeMode = PdfFlateEncodeMode.BestCompression;
        document.Options.Layout = PdfWriterLayout.Compact;
    }

    /// <summary>Deflates an uncompressed stream, or deflates a Flate stream again at the highest level, when smaller.</summary>
    private static void Deflate(PdfDictionary dictionary)
    {
        var type = dictionary.Elements.GetName("/Type");
        if (type is "/Metadata" or "/ObjStm" or "/XRef" || dictionary.Elements.ContainsKey("/F"))
            return;
        var filters = Filters(dictionary);
        var data = dictionary.Stream.Value;
        if (data.Length < 64)
            return;
        try
        {
            if (filters.Length == 0)
            {
                var packed = Deflate(data);
                if (packed.Length >= data.Length)
                    return;
                dictionary.Stream.Value = packed;
                dictionary.Elements.SetName("/Filter", "/FlateDecode");
            }
            else if (filters is ["/FlateDecode"])
            {
                var packed = Deflate(Inflate(data));
                if (packed.Length < data.Length)
                    dictionary.Stream.Value = packed;
            }
            else if (filters is ["/RunLengthDecode"])
            {
                var packed = Deflate(UndoRunLength(data));
                if (packed.Length >= data.Length)
                    return;
                dictionary.Stream.Value = packed;
                dictionary.Elements.SetName("/Filter", "/FlateDecode");
            }
        }
        catch (InvalidDataException)
        {
        }
    }

    private static string[] Filters(PdfDictionary dictionary) => Resolve(dictionary.Elements["/Filter"]) switch
    {
        PdfName name => [name.Value],
        PdfArray array => [.. array.Elements.Select(e => Resolve(e) is PdfName n ? n.Value : "?")],
        _ => [],
    };

    internal static PdfItem? Resolve(PdfItem? item) => item is PdfReference reference
        ? reference.Value is PdfNameObject name ? new PdfName(name.Value) : reference.Value
        : item;

    private static byte[] Inflate(byte[] data)
    {
        using var input = new ZLibStream(new MemoryStream(data), CompressionMode.Decompress);
        using var output = new MemoryStream();
        input.CopyTo(output);
        return output.ToArray();
    }

    private static byte[] Deflate(byte[] data)
    {
        using var output = new MemoryStream();
        using (var zlib = new ZLibStream(output, CompressionLevel.SmallestSize, leaveOpen: true))
            zlib.Write(data);
        return output.ToArray();
    }

    private static byte[] UndoRunLength(byte[] data)
    {
        using var output = new MemoryStream(data.Length * 2);
        var i = 0;
        while (i < data.Length)
        {
            var length = data[i++];
            if (length == 128)
                break;
            if (length < 128)
            {
                var count = Math.Min(length + 1, data.Length - i);
                output.Write(data, i, count);
                i += count;
            }
            else if (i < data.Length)
            {
                var value = data[i++];
                for (var n = 0; n < 257 - length; n++)
                    output.WriteByte(value);
            }
        }
        return output.ToArray();
    }
}
