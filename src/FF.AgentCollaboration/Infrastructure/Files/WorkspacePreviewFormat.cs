using System.Text;
using System.Xml;

namespace FF.AgentCollaboration.Infrastructure.Files;

internal static class WorkspacePreviewFormat
{
    internal static (string Kind, string Mime)? Detect(Stream stream, string path)
    {
        Span<byte> bytes = stackalloc byte[(int)Math.Min(stream.Length, 512)];
        try
        {
            stream.ReadExactly(bytes);
            ReadOnlySpan<byte> head = bytes;
            string ext = Path.GetExtension(path).ToLowerInvariant();
            switch (ext)
            {
                case ".png" when head.StartsWith((ReadOnlySpan<byte>)[137,80,78,71,13,10,26,10]): return ("image", "image/png");
                case ".jpg" or ".jpeg" when head.StartsWith((ReadOnlySpan<byte>)[255,216,255]): return ("image", "image/jpeg");
                case ".gif" when head.StartsWith("GIF89a"u8) || head.StartsWith("GIF87a"u8): return ("image", "image/gif");
                case ".webp" when head.StartsWith("RIFF"u8) && head.Length >= 12 && head[8..].StartsWith("WEBP"u8): return ("image","image/webp");
                case ".ico" when head.StartsWith((ReadOnlySpan<byte>)[0,0,1,0]): return ("image","image/x-icon");
                case ".bmp" when head.StartsWith("BM"u8): return ("image","image/bmp");
                case ".svg":
                    stream.Position=0;
                    using (var xml=XmlReader.Create(stream,new XmlReaderSettings { DtdProcessing=DtdProcessing.Prohibit, XmlResolver=null, MaxCharactersInDocument=ProjectFileBrowser.PreviewLimit }))
                        if (xml.MoveToContent()==XmlNodeType.Element && xml.LocalName=="svg" && xml.NamespaceURI=="http://www.w3.org/2000/svg") return ("image","image/svg+xml");
                    break;
                case ".pdf" when head.StartsWith("%PDF-"u8): return ("pdf","application/pdf");
                case ".mp4" when head.Length >= 12 && head[4..].StartsWith("ftyp"u8):
                    return ("video", "video/mp4");
                case ".webm" when head.StartsWith((ReadOnlySpan<byte>)[0x1a, 0x45, 0xdf, 0xa3]) && head.IndexOf("webm"u8) >= 0:
                    return ("video", "video/webm");
                case ".xls" or ".ppt" when head.StartsWith((ReadOnlySpan<byte>)[208,207,17,224,161,177,26,225]):
                    return ext==".xls" ? ("excel","application/vnd.ms-excel") : ("legacy-ppt","application/vnd.ms-powerpoint");
                case ".csv" when head.IndexOf((byte)0)<0 || head.StartsWith(Encoding.Unicode.Preamble) || head.StartsWith(Encoding.BigEndianUnicode.Preamble):
                    return ("excel","text/csv");
                case ".xlsx" or ".pptx" when head.StartsWith((ReadOnlySpan<byte>)[80,75,3,4]):
                    // 这里只确认 ZIP 容器；具体工作簿与演示文稿在按需预览时按资源上限解析。
                    return ext==".xlsx" ? ("excel","application/vnd.openxmlformats-officedocument.spreadsheetml.sheet")
                        : ("ppt","application/vnd.openxmlformats-officedocument.presentationml.presentation");
            }
        }
        catch (Exception error) when (error is InvalidDataException or XmlException) { /* 无效文件不进入富预览。 */ }
        finally {stream.Position=0;}
        return null;
    }
}
