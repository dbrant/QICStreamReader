using System;
using System.Collections.Generic;
using System.IO;
using System.Text;

namespace savlib
{
    class Program
    {
        const int EBCDIC_SPACE = 0x40;

        static void Main(string[] args)
        {
            string inFileName = "0.bin";
            string outFileName = "out.bin";
            string baseDirectory = "out";
            long initialOffset = 0;
            bool uncompressSna = false;
            bool removeTapeErrorPages = false;

            for (int i = 0; i < args.Length; i++)
            {
                if (args[i] == "-f") { inFileName = args[i + 1]; }
                else if (args[i] == "-d") { baseDirectory = args[i + 1]; }
                else if (args[i] == "-o") { outFileName = args[i + 1]; }
                else if (args[i] == "--offset") { initialOffset = Convert.ToInt64(args[i + 1]); }
                else if (args[i] == "--sna") { uncompressSna = true; }
                else if (args[i] == "--tep") { removeTapeErrorPages = true; }
            }

            try
            {
                using (var stream = new FileStream(inFileName, FileMode.Open, FileAccess.Read))
                {
                    if (initialOffset > 0)
                    {
                        stream.Seek(initialOffset, SeekOrigin.Begin);
                    }


                    byte[] bytes = new byte[stream.Length];


                    if (uncompressSna)
                    {
                        using (var outStream = new FileStream(outFileName, FileMode.Create, FileAccess.Write))
                        {
                            while (stream.Position < stream.Length)
                            {
                                stream.Read(bytes, 0, 0x200);

                                uint magic4 = QicUtils.Utils.BigEndian(BitConverter.ToUInt32(bytes, 0));
                                int magic2 = QicUtils.Utils.BigEndian(BitConverter.ToUInt16(bytes, 0));
                                string objDescStr = EbcdicToAscii(bytes, 0x96, 0x15);

                                if (magic4 == 0xFFFFFFFF && objDescStr == "L/D OBJECT DESCRIPTOR")
                                {
                                    string objName = EbcdicToAscii(bytes, 0x4, 0x1E).Trim();
                                    int objType = QicUtils.Utils.BigEndian(BitConverter.ToUInt16(bytes, 0x22));
                                    int numBlocks = (int)QicUtils.Utils.BigEndian(BitConverter.ToUInt32(bytes, 0xCC));
                                    uint objIndex = QicUtils.Utils.BigEndian(BitConverter.ToUInt32(bytes, 0xD0));
                                    int dataSize = (int)QicUtils.Utils.BigEndian(BitConverter.ToUInt32(bytes, 0x124));

                                    stream.Seek(-0x200, SeekOrigin.Current);
                                    stream.Read(bytes, 0, numBlocks * 0x200);
                                    outStream.Write(bytes, 0, numBlocks * 0x200);
                                }
                                else if (magic2 == 0xC4FF)
                                {
                                    Console.WriteLine("Compressed block detected. Entering decompress stage. Position: " + (stream.Position - 0x200).ToString("X") + " (" + (stream.Position - 0x200) + ")");
                                    stream.Seek(-0x200, SeekOrigin.Current);
                                    break;
                                }
                                else if (magic4 == 0x40404040)
                                {
                                    Console.WriteLine("Space filler detected.");
                                    outStream.Write(bytes, 0, 0x200);
                                }
                                else
                                {
                                    Console.WriteLine("Warning: unknown block detected.");
                                    outStream.Write(bytes, 0, 0x200);
                                }
                            }

                            while (stream.Position < stream.Length)
                            {
                                int b = stream.ReadByte();

                                int count = b & 0x3F;
                                int code = (b >> 6) & 0x3;

                                if (code == 3)
                                {
                                    // repeat following byte x times
                                    stream.Read(bytes, 0, 1);
                                    for (int i = 0; i < count; i++)
                                    {
                                        outStream.WriteByte(bytes[0]);
                                    }
                                }
                                else if (code == 2)
                                {
                                    // add blanks
                                    for (int i = 0; i < count; i++)
                                    {
                                        outStream.WriteByte(EBCDIC_SPACE);
                                    }
                                }
                                else
                                {
                                    if (code == 1)
                                    {
                                        // compacted characters?
                                        Console.WriteLine("Warning: compacted characters?");
                                    }
                                    if (count == 0)
                                    {
                                        Console.WriteLine("Warning: count of zero?");
                                    }

                                    // uncompressed characters
                                    Array.Clear(bytes, 0, 0x200);
                                    stream.Read(bytes, 0, count);

                                    outStream.Write(bytes, 0, count);
                                }
                            }
                        }

                        return;
                    }
                    else if (removeTapeErrorPages)
                    {
                        using (var outStream = new FileStream(outFileName, FileMode.Create, FileAccess.Write))
                        {
                            while (stream.Position < stream.Length)
                            {
                                stream.Read(bytes, 0, 0x200);

                                var blockStr = EbcdicToAscii(bytes, 0, 0x200);

                                if (blockStr.Contains("L/D TAPE ERROR RECOVERY PAGE"))
                                {
                                    continue;
                                }

                                outStream.Write(bytes, 0, 0x200);
                            }
                        }

                        return;
                    }



                    var namesAndExtensionsList = new List<KeyValuePair<string, string>>();


                    while (stream.Position < stream.Length)
                    {
                        stream.Read(bytes, 0, 0x200);

                        uint magic4 = QicUtils.Utils.BigEndian(BitConverter.ToUInt32(bytes, 0));
                        int magic2 = QicUtils.Utils.BigEndian(BitConverter.ToUInt16(bytes, 0));
                        string objDescStr = EbcdicToAscii(bytes, 0x96, 0x15);

                        if (magic4 == 0xFFFFFFFF && objDescStr == "L/D OBJECT DESCRIPTOR")
                        {

                            string objName = EbcdicToAscii(bytes, 0x4, 0x1E);
                            int objType = QicUtils.Utils.BigEndian(BitConverter.ToUInt16(bytes, 0x22));
                            string descVersion = EbcdicToAscii(bytes, 0xBA, 4);
                            int numBlocks = (int)QicUtils.Utils.BigEndian(BitConverter.ToUInt32(bytes, 0xCC));
                            uint objIndex = QicUtils.Utils.BigEndian(BitConverter.ToUInt32(bytes, 0xD0));
                            int dataSize = descVersion == "6380" ? ((numBlocks - 0x10) * 0x200) :
                                 (int)QicUtils.Utils.BigEndian(BitConverter.ToUInt32(bytes, 0x124));

                            Console.WriteLine(objName);
                            Console.WriteLine("> type: " + objType.ToString("X4") + ", blocks: " + numBlocks.ToString("X4") + ", size: " + dataSize);

                            if (objName.Contains("QSRDSSPC.1"))
                            {
                                Console.WriteLine("Parsing catalog...");
                                stream.Read(bytes, 0, (numBlocks - 1) * 0x200);
                                namesAndExtensionsList = ParseCatalog(bytes, 0, (numBlocks - 1) * 0x200);
                                continue;
                            }

                            if (dataSize == 0)
                            {
                                stream.Read(bytes, 0, (numBlocks - 1) * 0x200);
                                continue;
                            }



                            string subDir = objName.Substring(0, 10).Trim();
                            string fileName = objName.Substring(10).Trim();

                            if (fileName.Length == 0)
                            {
                                Console.WriteLine("Warning: File name is empty, using dir name as file name.");
                                fileName = subDir;
                                subDir = "";
                            }

                            // Append extension to file name, if available from our catalog.
                            if (subDir.Length > 0)
                            {
                                var pair = namesAndExtensionsList.Find(c => c.Key == fileName);
                                if (pair.Key == fileName)
                                {
                                    fileName += "." + pair.Value;
                                    namesAndExtensionsList.Remove(pair);
                                }
                                else if (pair.Key == null)
                                {
                                    // Infer extension from subdirectory
                                    if (subDir.Contains("DOCUMENT") || subDir.Contains("TEXT") || subDir.Contains("TXT") || subDir == "MANUAL") { fileName += ".TXT"; }
                                    else if (subDir == "MENU") { fileName += ".MNUDDS"; }
                                    else if (subDir == "QCLSRC") { fileName += ".CLP"; }
                                    else if (subDir == "QCMDSRC") { fileName += ".CMD"; }
                                    else if (subDir == "QDDSSRC") { fileName += ".DSPF"; }
                                    else if (subDir == "QLBLSRC") { fileName += ".CBL"; }
                                    else if (subDir == "QQMQRYSRC") { fileName += ".QMQRY"; }
                                }
                            }


                            string filePath = baseDirectory;

                            if (subDir.Length > 0)
                            {
                                filePath = Path.Combine(filePath, subDir);
                            }

                            Directory.CreateDirectory(filePath);

                            string finalFileName = fileName;
                            int existFileNo = 1;
                            while (File.Exists(GetFullFileName(filePath, finalFileName)))
                            {
                                finalFileName = fileName + (existFileNo++).ToString();
                                Console.WriteLine("Warning: file already exists (amending name): " + finalFileName);
                            }

                            Console.WriteLine(GetFullFileName(filePath, finalFileName));

                            if (dataSize % 0x200 != 0)
                            {
                                Console.WriteLine("Warning: data size is not multiple of block size.");
                            }

                            stream.Seek((numBlocks - 1) * 0x200 - dataSize, SeekOrigin.Current);
                            stream.Read(bytes, 0, dataSize);
                            ConvertAndWriteFile(bytes, 0, dataSize, GetFullFileName(filePath, finalFileName));

                        }
                        else if (magic2 == 0xC4FF)
                        {
                            Console.WriteLine("Compressed data detected. Please decompress first. Position: " + (stream.Position - 0x200).ToString("X") + " (" + (stream.Position - 0x200) + ")");
                            break;
                        }
                        else if (magic4 == 0x40404040)
                        {
                            Console.WriteLine("Space filler detected.");
                        }
                        else
                        {
                            Console.WriteLine("Warning: unknown block detected.");
                        }
                    }





                    /*


                    string fileName = "";
                    int pos = curText.IndexOf("PROGRAM-ID.");
                    if (pos > 0)
                    {
                        pos += 11;
                        int end = curText.IndexOf('.', pos);
                        if (end - pos < 256)
                        {
                            fileName = curText.Substring(pos, end - pos).Trim();
                        }
                    }

                    if (fileName.Length == 0)
                    {
                        Console.WriteLine("Warning: couldn't find program ID.");
                        fileName = "PROGRAM";
                    }


                    string filePath = baseDirectory;

                    
                    //if (Subdirectory.Length > 0)
                    //{
                    //    string[] dirArray = Subdirectory.Split('\0');
                    //    for (int i = 0; i < dirArray.Length; i++)
                    //    {
                    //        filePath = Path.Combine(filePath, dirArray[i]);
                    //    }
                    //}
                    

                    Directory.CreateDirectory(filePath);

                    string finalFileName = fileName;
                    int existFileNo = 1;
                    while (File.Exists(GetFullFileName(filePath, finalFileName)))
                    {
                        finalFileName = fileName + (existFileNo++).ToString();
                        Console.WriteLine("Warning: file already exists (amending name): " + finalFileName);
                    }

                    Console.WriteLine(GetFullFileName(filePath, finalFileName));

                    using (var outStream = new FileStream(GetFullFileName(filePath, finalFileName), FileMode.Create, FileAccess.Write))
                    {
                        var writer = new StreamWriter(outStream);
                        writer.Write(curText);
                        writer.Flush();
                    }
                    */


                }
            }
            catch (Exception e)
            {
                Console.WriteLine("Error: " + e.Message);
            }
        }

        private static void ConvertAndWriteFile(byte[] bytes, int offset, int count, string fileName)
        {
            if (count < 0x10)
            {
                Console.WriteLine("File size too small, skipping: " + fileName);
                return;
            }

            using (var outStream = new FileStream(fileName, FileMode.Create, FileAccess.Write))
            {
                if ((bytes[0] == 0x3 && bytes[1] == 0xB4) || (bytes[1] == 0x3 && bytes[5] == 0xB4))
                {
                    // text content
                    // TODO: investigate the correctness of this:
                    for (int i = offset; i < offset + count; i++)
                    {
                        // Replace the EBCDIC 0x80 character with a newline, since that's what we seem to be getting.
                        if (bytes[i] == 0x80) { bytes[i] = 0x25; }
                    }

                    // Explicitly bump the offset by 0x20, since the first 0x20 bytes appear to be additional metadata.
                    int metadataCount = count > 0x20 ? 0x20 : 0;

                    string fileStr = EbcdicToAscii(bytes, offset + metadataCount, count - metadataCount);

                    // Remove null characters (usually present at the end, to fill to block size)
                    fileStr = fileStr.Replace("\0", "");

                    var writer = new StreamWriter(outStream);
                    writer.Write(fileStr);
                    writer.Flush();
                }
                else
                {
                    // binary content
                    outStream.Write(bytes, offset, count);
                }
            }
        }

        private static List<KeyValuePair<string, string>> ParseCatalog(byte[] bytes, int offset, int count)
        {
            var list = new List<KeyValuePair<string, string>>();
            int ptr = offset;

            while (ptr < count)
            {
                if (bytes[ptr] == 0 && bytes[ptr + 1] == 0 && (bytes[ptr + 2] == 0 || bytes[ptr + 2] == 1) && bytes[ptr + 0x10] == 0x80 && bytes[ptr + 0x11] == 0
                    && bytes[ptr + 0x48] >= 0xF0 && bytes[ptr + 0x48] <= 0xF9 && bytes[ptr + 0x49] >= 0xF0 && bytes[ptr + 0x49] <= 0xF9)
                {
                    var name = EbcdicToAscii(bytes, ptr + 4, 10).Trim();
                    var description = EbcdicToAscii(bytes, ptr + 0x16, 0x32).Trim();
                    var extension = EbcdicToAscii(bytes, ptr + 0x60, 10).Trim();

                    list.Add(new KeyValuePair<string, string>(name, extension));

                    ptr += 0x80;
                }
                else
                {
                    ptr += 0x10;
                }
            }
            return list;
        }

        private static string GetFullFileName(string path, string name)
        {
            return Path.Combine(path, name /* + ".CBL" */);
        }

        public static string EbcdicToAscii(byte[] ebcdicData, int index, int count)
        {
            return Encoding.ASCII.GetString(Encoding.Convert(Encoding.GetEncoding("IBM037"), Encoding.ASCII, ebcdicData, index, count));
        }
    }
}
