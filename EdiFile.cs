using System.Net.Mime;
using System.Text;
using System.Xml;
using System.Xml.Linq;
using underware.Edi;
using underware.Edi.Common;
using underware.Edifact;

namespace underware.Edi.Processing;

public class EdiFile
{
    public string FileName { get; set; }
    public byte[] Content { get; set; }
    public Encoding Encoding { get; set; }
    public string Text { get; set; }
    
    public string ParseError { get; set; }

    public IEdiInterchange Interchange { get; set; }

    public static EdiFile Load(string fileName="", bool throwException = false, Encoding enc = null)
    {
        var content = File.ReadAllBytes(fileName);
        return Load(content, Path.GetFileName(fileName), throwException, enc);
    }

    public static EdiFile Load(byte[] content, string fileName="", bool throwException = false, Encoding enc = null)
    {
        var contentWoBOM = RemoveBOMIfExists(content);
        
        //test if content is xml
        if (IsXml(contentWoBOM, out var xmlEncoding))
        {
            enc = xmlEncoding;
        }
        else
        {
            enc ??= GuessEncoding(contentWoBOM);    
        }
        
        var ediFile = new EdiFile()
        {
            Encoding = enc,
            Content = contentWoBOM,
            FileName = fileName,
            Text = enc.GetString(contentWoBOM)
        };

        try
        {
            ediFile.Interchange = ParseInterchange(ediFile.Text);
        }
        catch (Exception ex)
        {
            ediFile.ParseError = ex.Message;
            
            if (throwException)
                throw new ParseException(ex.Message, ex);
        }

        return ediFile;
    }

    public string GetPrettyText()
    {
        try
        {
            if (Text.StartsWith("<?xml"))
                return PrettifyXml();

            switch (Text.Substring(0, 3))
            {
                case "UNA":
                case "UNB":
                    return PrettifyEdifact();
                case "511":
                case "551":
                case "661":
                case "711":
                case "821":
                    return PrettifyVDA();
            }
        }
        catch (Exception ex)
        {
            
        }

        return Text;
    }

    private static bool IsXml(byte[] data, out Encoding enc)
    {
        enc = null;
        
        try
        {
            using var stream = new MemoryStream(data);
            var settings = new XmlReaderSettings
            {
                ConformanceLevel = ConformanceLevel.Auto
            };
            using var reader = XmlReader.Create(stream, settings);
            while (reader.Read())
            {
                // Průchod XML daty
                if (reader.NodeType == XmlNodeType.XmlDeclaration)
                {
                    var encodingName = reader.GetAttribute("encoding");
                    if (!string.IsNullOrEmpty(encodingName))
                    {
                        enc = Encoding.GetEncoding(encodingName);
                    }
                }
            } 
            return enc != null; // Pokud analýza proběhla bez chyb, pak to může být XML
        }
        catch (Exception)
        {
            return false; // Chyba při analýze, není to XML
        }
    }
    
    private string PrettifyVDA()
    {
        var ln = 128;
        var pos = 0;

        var sb = new StringBuilder();

        while (pos < Text.Length - 1)
        {
            try
            {
                sb.AppendLine(Text.Substring(pos, ln));
            }
            catch
            {
            }

            pos += ln;
        } 
            
        return sb.ToString();
    }

    private string PrettifyEdifact()
    {
        var charSpec = CharSpec.GetFromUNAOrDefault(Text);
        return string.Join("\r\n", Text.SplitSegments(charSpec));
    }

    private string PrettifyXml()
    {
        var x = XDocument.Load(new MemoryStream(Content));
        return x.ToString();
        
    }


    private static IEdiInterchange ParseInterchange(string text)
    {
        if (text.Contains("<biztalk_1"))
            return XmlBiztalk.Interchange.Parse(text);
        
        if(text.Contains("<Document-"))
            return underware.XmlECOD.Interchange.Parse(text);

        switch (text.Substring(0, 3))
        {
            case "SYS":
                return Inhouse.Interchange.Parse(text);
            case "EDI":
            //throw NotImplementedException();
            case "UNA":
            case "UNB":
               return Edifact.Interchange.Parse(text);
                break;
            case "HDR":
            //return GetProvider(typeof(CSV_ORION.CSV_ORION_DocumentProvider), logger);
            case "511":
            case "551":
            case "661":
            case "711":
            case "821":
                return VDA.Interchange.Parse(text);
            case "ISA":
            //return GetProvider(typeof(X12.X12DocumentProvider), logger);
            default:
                return null;
        }
    }

    private static Encoding GuessEncoding(byte[] content)
    {
        // *** Use Default of Encoding.Default (Ansi CodePage) 
        var enc = Encoding.GetEncoding(1250);
        
        
        //test utf8
        var utf8 = new UTF8Encoding(encoderShouldEmitUTF8Identifier: false, throwOnInvalidBytes: true);
        try
        {
            // If decoding succeeds without exception, it's valid UTF-8
            _ = utf8.GetString(content);
            return utf8;
        }
        catch (DecoderFallbackException)
        {
            // Thrown when invalid UTF-8 sequences are encountered
        }

        // *** Detect byte order mark if any - otherwise assume default 
        var buffer = new byte[6];
        using (var memStream = new MemoryStream(content))
        {
            memStream.Read(buffer, 0, 6);
        }

        switch (buffer[0])
        {
            case 0xef when buffer[1] == 0xbb && buffer[2] == 0xbf:
                enc = Encoding.UTF8;
                break;
            case 0xfe when buffer[1] == 0xff:
                enc = Encoding.BigEndianUnicode;
                break;
            case 0 when buffer[1] == 0 && buffer[2] == 0xfe && buffer[3] == 0xff:
                enc = Encoding.UTF32;
                break;
            case 0x2b when buffer[1] == 0x2f && buffer[2] == 0x76:
                enc = Encoding.UTF7;
                break;
            // 1201 unicodeFFFE Unicode (Big-Endian) 
            case 0xFE when buffer[1] == 0xFF:
                enc = Encoding.GetEncoding(1201);
                break;
            // 1200 utf-16 Unicode 
            case 0xFF when buffer[1] == 0xFE:
                enc = Encoding.GetEncoding(1200);
                break;
            default:
            {
                if (buffer[1] == 0x00 && buffer[3] == 0x00 && buffer[5] == 0x00)
                    enc = Encoding.Unicode;
                break;
            }
        }

        return enc;
    }
    
    private static byte[] RemoveBOMIfExists(byte[] buffer)
    {
        return buffer[0] switch
        {
            0xef when buffer[1] == 0xbb && buffer[2] == 0xbf => CopyArray(buffer, 3),
            0xfe when buffer[1] == 0xff => CopyArray(buffer, 2),
            0 when buffer[1] == 0 && buffer[2] == 0xfe && buffer[3] == 0xff => CopyArray(buffer, 4),
            0x2b when buffer[1] == 0x2f && buffer[2] == 0x76 => CopyArray(buffer, 3),
            // 1201 unicodeFFFE Unicode (Big-Endian) 
            0xFE when buffer[1] == 0xFF => CopyArray(buffer, 2),
            // 1200 utf-16 Unicode 
            0xFF when buffer[1] == 0xFE => CopyArray(buffer, 2),
            _ => buffer
        };
    }

    private static byte[] CopyArray(byte[] array, int start)
    {
        var retArr = new byte[array.Length - start];
        Array.Copy(array, start, retArr, 0, retArr.Length);
        //array.CopyTo(retArr, start);

        return retArr;
    }

}