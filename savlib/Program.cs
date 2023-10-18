using System;
using System.Collections.Generic;
using System.IO;
using System.Text;

/// <summary>
/// 
/// Decoder for tape images written using the SAVLIB command on AS/400 systems.
/// 
/// Copyright Dmitry Brant, 2022
/// 
/// This is far from a complete implementation, but just enough to recover data from tapes
/// I've seen in the wild so far.
/// 
/// The tape image contains files, written consecutively. Each file is a multiple of 512-byte
/// blocks. If the actual file data doesn't end at a block boundary, it's padded with zeros
/// until the next block. Therefore each file in the archive always begins on a 512-byte block
/// boundary.
/// 
/// Each file begins with several blocks' worth of metadata, which is then followed by the contents
/// of the file. (The file contents also always begin on a block bondary.)
/// 
/// NOTE: All text content is encoded as EBCDIC (IBM037), so set your hex editors accordingly.
/// 
/// NOTE: All multi-byte numeric values are big-endian.
/// 
/// NOTE: Some blocks in between files are filled with spaces (0x40) and may be skipped.
/// 
/// NOTE: Some blocks are some kind of special "TAPE ERROR RECOVERY PAGE" blocks, and should be
///       removed from the image, since these blocks don't count towards the block counts
///       specified in the file metadata.
/// 
/// NOTE: Many sources on the web seem to insist that the block size in these types of backups is
///       528 bytes (512 bytes of data, 4 byte RRN, 12 byte checksum), but that doesn't seem to be
///       the case for the tape backups that I've come across. The blocks are definitely 512 bytes,
///       with no RRN or checksum.
/// 
/// The first block of a file is a header that contains the following metadata, which can be used
/// to detect the beginning of a new file:
/// 
/// Offset    Count            Meaning
/// ----------------------------------------
/// 0         4                Magic value of 0xFFFFFFFF. NOTE: If the magic value begins with C4FF,
///                            it implies that the rest of the data is compressed with SNA encoding
///                            (see below).
/// 
/// 4         1E               File name. This is actually three names of 10 bytes each (total 30
///                            bytes). Unused bytes are padded with spaces. This is to accommodate
///                            a rudimentary "directory" structure. For example, "DOCUMENTS ALICE     DOC1      "
///                            can be interpreted as "DOCUMENTS\ALICE\DOC1" in modern terms.
/// 
/// 22        2                Type of this file. Some types relevant to us:
///                            19DB - "Save/restore descriptor space", with name "QSRDSSPC.1", in other
///                                   words a catalog file with metadata about all the contents.
///                            0B90 - "Data space", in other words text files, etc.
///                            0201 - executable program
///                            1903 - job description
///                            others:
///                            https://www.ibm.com/docs/en/i/7.2?topic=ssw_ibm_i_72/rzatk/conObject.htm
/// 
/// 96        15               Magic value of "L/D OBJECT DESCRIPTOR".
/// 
/// BA        4                Version code (?). Observed values have been "6380", "6341", "6346", "6347".
///                            This can be used to determine slight variations in the format.
/// 
/// CC        4                Total blocks in this file record (including this header block). If you skip
///                            forward this number of blocks, you should reach the next file record.
/// 
/// 124       4                Number of bytes of actual data in this file. To reach the beginning of the
///                            data, take the total blocks in this record (the field above) and subtract
///                            this field.
/// 
/// ----------------------------------------
/// 
/// Some tapes are compressed using SNA encoding. The first file record ("QSRDSSPC.1") seems to be always
/// uncompressed. However, if the next record starts with a value of 0xC4FF instead of 0xFFFFFFFF, it means
/// that the remainder of the image is compressed and should be decoded before being parsed.
/// https://www.ibm.com/docs/en/zos/2.1.0?topic=format-sna-transmission-buffer
/// https://www.ibm.com/docs/en/zos/2.1.0?topic=format-string-control-byte-scb
/// 
/// ----------------------------------------
/// 
/// The tape images themselves will have header and footer files (i.e. written onto the tape as separate files,
/// separated by file markers) that are in the Standard Label format.
/// https://www.ibm.com/support/pages/standard-label-format-tapes
/// 
/// These labels contain metadata about the overall volume of the backup.
/// 
/// ----------------------------------------
/// 
/// This tool provides three possible stages of operation:
/// 
/// - Stage 1: Uncompress from SNA encoding, if necessary. (use the --sna parameter)
/// 
/// - Stage 2: Remove "TAPE ERROR RECOVERY PAGE" blocks, if necessary. (use the --tep parameter)
/// 
/// - Stage 3: Extract actual files from the image.
/// 
/// </summary>
namespace savlib
{
    class Program
    {
        const int SAVLIB_BLOCK_SIZE = 0x200;
        const int EBCDIC_SPACE = 0x40;

        static void Main(string[] args)
        {
            string inFileName = "0.bin";
            string outFileName = "out.bin";
            string baseDirectory = "out";
            long initialOffset = 0;
            bool uncompressSna = false;
            bool removeTapeErrorPages = false;
            bool dryRun = false;

            for (int i = 0; i < args.Length; i++)
            {
                if (args[i] == "-f") { inFileName = args[i + 1]; }
                else if (args[i] == "-d") { baseDirectory = args[i + 1]; }
                else if (args[i] == "-o") { outFileName = args[i + 1]; }
                else if (args[i] == "--offset") { initialOffset = QicUtils.Utils.StringOrHexToLong(args[i + 1]); }
                else if (args[i] == "--sna") { uncompressSna = true; }
                else if (args[i] == "--tep") { removeTapeErrorPages = true; }
                else if (args[i] == "--dryrun") { dryRun = true; }
            }

            try
            {
                using var stream = new FileStream(inFileName, FileMode.Open, FileAccess.Read);
                if (initialOffset > 0)
                {
                    stream.Seek(initialOffset, SeekOrigin.Begin);
                }

                byte[] bytes = new byte[stream.Length];

                if (uncompressSna)
                {
                    using var outStream = new FileStream(outFileName, FileMode.Create, FileAccess.Write);
                    while (stream.Position < stream.Length)
                    {
                        stream.Read(bytes, 0, SAVLIB_BLOCK_SIZE);

                        uint magic4 = QicUtils.Utils.BigEndian(BitConverter.ToUInt32(bytes, 0));
                        int magic2 = QicUtils.Utils.BigEndian(BitConverter.ToUInt16(bytes, 0));
                        string objDescStr = QicUtils.Utils.EbcdicToAscii(bytes, 0x96, 0x15);

                        if (magic4 == 0xFFFFFFFF && objDescStr == "L/D OBJECT DESCRIPTOR")
                        {
                            string objName = QicUtils.Utils.EbcdicToAscii(bytes, 0x4, 0x1E).Trim();
                            int objType = QicUtils.Utils.BigEndian(BitConverter.ToUInt16(bytes, 0x22));
                            int numBlocks = (int)QicUtils.Utils.BigEndian(BitConverter.ToUInt32(bytes, 0xCC));
                            uint objIndex = QicUtils.Utils.BigEndian(BitConverter.ToUInt32(bytes, 0xD0));
                            int dataSize = (int)QicUtils.Utils.BigEndian(BitConverter.ToUInt32(bytes, 0x124));

                            stream.Seek(-SAVLIB_BLOCK_SIZE, SeekOrigin.Current);
                            stream.Read(bytes, 0, numBlocks * SAVLIB_BLOCK_SIZE);
                            outStream.Write(bytes, 0, numBlocks * SAVLIB_BLOCK_SIZE);
                        }
                        else if (magic2 == 0xC4FF)
                        {
                            Console.WriteLine("Compressed block detected. Entering decompress stage. Position: " + (stream.Position - SAVLIB_BLOCK_SIZE).ToString("X") + " (" + (stream.Position - SAVLIB_BLOCK_SIZE) + ")");
                            stream.Seek(-SAVLIB_BLOCK_SIZE, SeekOrigin.Current);
                            break;
                        }
                        else if (magic4 == 0x40404040)
                        {
                            Console.WriteLine("Space filler detected.");
                            outStream.Write(bytes, 0, SAVLIB_BLOCK_SIZE);
                        }
                        else
                        {
                            Console.WriteLine("Warning: unknown block detected.");
                            outStream.Write(bytes, 0, SAVLIB_BLOCK_SIZE);
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
                            Array.Clear(bytes, 0, SAVLIB_BLOCK_SIZE);
                            stream.Read(bytes, 0, count);

                            outStream.Write(bytes, 0, count);
                        }
                    }

                    return;
                }
                else if (removeTapeErrorPages)
                {
                    using var outStream = new FileStream(outFileName, FileMode.Create, FileAccess.Write);
                    while (stream.Position < stream.Length)
                    {
                        stream.Read(bytes, 0, SAVLIB_BLOCK_SIZE);

                        var blockStr = QicUtils.Utils.EbcdicToAscii(bytes, 0, SAVLIB_BLOCK_SIZE);

                        if (blockStr.Contains("L/D TAPE ERROR RECOVERY PAGE"))
                        {
                            continue;
                        }

                        outStream.Write(bytes, 0, SAVLIB_BLOCK_SIZE);
                    }

                    return;
                }

                var namesAndExtensionsList = new List<KeyValuePair<string, string>>();

                while (stream.Position < stream.Length)
                {
                    stream.Read(bytes, 0, SAVLIB_BLOCK_SIZE);

                    uint magic4 = QicUtils.Utils.BigEndian(BitConverter.ToUInt32(bytes, 0));
                    int magic2 = QicUtils.Utils.BigEndian(BitConverter.ToUInt16(bytes, 0));
                    string objDescStr = QicUtils.Utils.EbcdicToAscii(bytes, 0x96, 0x15);

                    if (magic4 == 0xFFFFFFFF && objDescStr == "L/D OBJECT DESCRIPTOR")
                    {

                        string objName = QicUtils.Utils.EbcdicToAscii(bytes, 0x4, 0x1E);
                        int objType = QicUtils.Utils.BigEndian(BitConverter.ToUInt16(bytes, 0x22));
                        string descVersion = QicUtils.Utils.EbcdicToAscii(bytes, 0xBA, 4);
                        int numBlocks = (int)QicUtils.Utils.BigEndian(BitConverter.ToUInt32(bytes, 0xCC));
                        uint objIndex = QicUtils.Utils.BigEndian(BitConverter.ToUInt32(bytes, 0xD0));
                        int dataSize = descVersion == "6380" ? ((numBlocks - 0x10) * SAVLIB_BLOCK_SIZE) :
                             (int)QicUtils.Utils.BigEndian(BitConverter.ToUInt32(bytes, 0x124));

                        Console.WriteLine(objName);
                        Console.WriteLine("> type: " + objType.ToString("X4") + ", blocks: " + numBlocks.ToString("X4") + ", size: " + dataSize);

                        if (objName.Contains("QSRDSSPC.1"))
                        {
                            Console.WriteLine("Parsing catalog...");
                            stream.Read(bytes, 0, (numBlocks - 1) * SAVLIB_BLOCK_SIZE);
                            namesAndExtensionsList = ParseCatalog(bytes, 0, (numBlocks - 1) * SAVLIB_BLOCK_SIZE);
                            continue;
                        }

                        if (dataSize == 0)
                        {
                            stream.Read(bytes, 0, (numBlocks - 1) * SAVLIB_BLOCK_SIZE);
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

                        if (!dryRun)
                        {
                            Directory.CreateDirectory(filePath);
                        }

                        string finalFileName = fileName;
                        int existFileNo = 1;
                        while (File.Exists(GetFullFileName(filePath, finalFileName)))
                        {
                            finalFileName = fileName + (existFileNo++).ToString();
                            Console.WriteLine("Warning: file already exists (amending name): " + finalFileName);
                        }

                        Console.WriteLine(GetFullFileName(filePath, finalFileName));

                        stream.Seek((numBlocks - 1) * SAVLIB_BLOCK_SIZE - dataSize, SeekOrigin.Current);
                        stream.Read(bytes, 0, dataSize);

                        if (!dryRun)
                        {
                            ConvertAndWriteFile(bytes, 0, dataSize, GetFullFileName(filePath, finalFileName));
                        }

                    }
                    else if (magic2 == 0xC4FF)
                    {
                        Console.WriteLine("Compressed data detected. Please decompress first. Position: " + (stream.Position - SAVLIB_BLOCK_SIZE).ToString("X") + " (" + (stream.Position - SAVLIB_BLOCK_SIZE) + ")");
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

                    string fileStr = QicUtils.Utils.EbcdicToAscii(bytes, offset + metadataCount, count - metadataCount);

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
                    var name = QicUtils.Utils.EbcdicToAscii(bytes, ptr + 4, 10).Trim();
                    var description = QicUtils.Utils.EbcdicToAscii(bytes, ptr + 0x16, 0x32).Trim();
                    var extension = QicUtils.Utils.EbcdicToAscii(bytes, ptr + 0x60, 10).Trim();

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
    }
}
