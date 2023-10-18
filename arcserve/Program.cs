using QicUtils;
using System;
using System.IO;
using System.Text;


/// <summary>
/// 
/// Decoder for tape backup images made with ancient versions of ArcServeIT, circa 1999.
/// Extracts only uncompressed files, for now. The compression appears to be proprietary,
/// and is thus very difficult to RE.
/// 
/// Copyright Dmitry Brant, 2023.
/// 
/// </summary>
namespace arcserve
{
    class Program
    {
        private const uint FileHeaderBlock = 0xABBAABBA;
        private const uint ChunkMagic = 0xACCAACCA;
        private const uint FillerBlock = 0xCCCCCCCC;

        static void Main(string[] args)
        {
            string inFileName = "";
            string baseDirectory = "out";

            bool dryRun = false;

            for (int i = 0; i < args.Length; i++)
            {
                if (args[i] == "-f") { inFileName = args[i + 1]; }
                else if (args[i] == "-d") { baseDirectory = args[i + 1]; }
                else if (args[i] == "--dry") { dryRun = true; }
            }

            if (inFileName.Length == 0 || !File.Exists(inFileName))
            {
                Console.WriteLine("Usage:");
                return;
            }

            try
            {
                using var stream = new FileStream(inFileName, FileMode.Open, FileAccess.Read);
                while (stream.Position < stream.Length)
                {
                    // Make sure we're aligned properly.
                    if ((stream.Position % 0x100) > 0)
                    {
                        stream.Seek(0x100 - (stream.Position % 0x100), SeekOrigin.Current);
                    }


                    var header = new FileHeader(stream);
                    if (!header.Valid)
                    {
                        continue;
                    }


                    Console.WriteLine(stream.Position.ToString("X") + ": " + header.Name + " - " + header.Size.ToString() + " bytes");

                    if (header.IsDirectory || header.Name.Trim() == "")
                    {
                        continue;
                    }

                    if (header.Size == 0)
                    {
                        Console.WriteLine("Warning: skipping zero-length file.");
                        continue;
                    }

                    if (header.dataStream == null)
                    {
                        Console.WriteLine("Warning: no data extracted for file.");
                        continue;
                    }

                    string filePath = baseDirectory;
                    string[] dirArray = header.Name.Split("/");
                    string fileName = dirArray[^1];
                    for (int i = 0; i < dirArray.Length - 1; i++)
                    {
                        filePath = Path.Combine(filePath, dirArray[i]);
                    }

                    if (!dryRun)
                    {
                        Directory.CreateDirectory(filePath);

                        filePath = Path.Combine(filePath, fileName);

                        // Make sure the fully qualified name does not exceed 260 chars
                        if (filePath.Length >= 260)
                        {
                            filePath = filePath[..259];
                        }

                        while (File.Exists(filePath))
                        {
                            Console.WriteLine("Warning: file already exists (amending name): " + filePath);
                            filePath += "_";
                        }

                        Console.WriteLine(stream.Position.ToString("X") + ": " + filePath + " - " + header.Size.ToString() + " bytes - " + header.CreateDate.ToShortDateString());

                        using (var f = new FileStream(filePath, FileMode.Create, FileAccess.Write))
                        {
                            header.dataStream.Seek(0, SeekOrigin.Begin);
                            header.dataStream.CopyTo(f);
                            f.Flush();
                        }

                        try
                        {
                            //File.SetCreationTime(filePath, header.CreateDate);
                            //File.SetLastWriteTime(filePath, header.ModifyDate);
                            //File.SetAttributes(filePath, header.Attributes);
                        }
                        catch { }
                    }
                    else
                    {
                        filePath = Path.Combine(filePath, fileName);
                        Console.WriteLine(stream.Position.ToString("X") + ": " + filePath + " - " + header.Size.ToString() + " bytes - " + header.CreateDate.ToShortDateString());
                    }
                }
            }
            catch (Exception e)
            {
                Console.WriteLine("Error: " + e.Message);
                Console.Write(e.StackTrace);
            }
        }

        private class FileHeader
        {
            public long Size { get; }
            public string Name { get; }
            public string DosName { get; }
            public DateTime CreateDate { get; }
            public DateTime ModifyDate { get; }
            public FileAttributes Attributes { get; }
            public bool Valid { get; }
            public bool IsDirectory { get; }

            public Stream dataStream;

            ~FileHeader()
            {
                if (dataStream != null)
                {
                    try
                    {
                        dataStream.Close();
                        dataStream = null;
                    }
                    catch { }
                }
            }

            public FileHeader(Stream stream)
            {
                byte[] bytes = new byte[0x20000];

                stream.Read(bytes, 0, 4);
                if (Utils.LittleEndian(BitConverter.ToUInt32(bytes, 0)) != FileHeaderBlock) { return; }

                stream.Read(bytes, 0, 0xFA);

                Name = Utils.GetNullTerminatedString(Encoding.ASCII.GetString(bytes, 0, 0xFA));
                if (Name.Length == 0) { return; }
                DosName = Name;

                stream.Read(bytes, 0, 0x26); // short name + possibly date?

                stream.Read(bytes, 0, 0x37); // file size + other stuff?

                Size = Utils.LittleEndian(BitConverter.ToUInt32(bytes, 0));

                //DateTime = new DateTime(1970, 1, 1).AddSeconds(BitConverter.ToUInt32(bytes, 0x2E));
                //CreateDate = QicUtils.Utils.DateTimeFromTimeT(BitConverter.ToUInt32(bytes, 0x2E));
                //ModifyDate = QicUtils.Utils.DateTimeFromTimeT(BitConverter.ToUInt32(bytes, 0x3E));
                //DateTime = QicUtils.Utils.GetQicDateTime(BitConverter.ToUInt32(bytes, 0x2E));

                while (true)
                {
                    // read until next nonnull byte
                    while (true)
                    {
                        int b = stream.ReadByte();
                        if (b != 0)
                        {
                            stream.Position--;
                            break;
                        }
                    }


                    // read next chunk header
                    stream.Read(bytes, 0, 0x20);

                    if (Utils.LittleEndian(BitConverter.ToUInt32(bytes, 0)) != ChunkMagic)
                    {
                        Console.WriteLine("Warning: Incorrect chunk position.");
                    }

                    int chunkType = bytes[4];
                    int chunkLength;

                    if (chunkType == 0)
                    {
                        break;
                    }
                    else if (chunkType == 0x10)
                    {
                        chunkLength = Utils.LittleEndian(BitConverter.ToUInt16(bytes, 0x14));
                    }
                    else
                    {
                        chunkLength = Utils.LittleEndian(BitConverter.ToUInt16(bytes, 0xC));
                    }

                    stream.Read(bytes, 0, chunkLength);

                    if (chunkType == 0xC)
                    {
                        Name = Utils.GetNullTerminatedString(Encoding.Latin1.GetString(bytes, 0, chunkLength));
                    }
                    else if (chunkType == 0x1)
                    {
                        if (dataStream == null)
                        {
                            ProcessStartOfData(bytes, chunkLength);
                        }
                        else
                        {
                            dataStream.Write(bytes, 0, chunkLength);
                        }
                    }

                }

                Valid = true;
            }


            private void ProcessStartOfData(byte[] bytes, int chunkLength)
            {
                if (dataStream != null)
                {
                    Console.WriteLine("Warning: duplicate start of stream chunk.");
                    dataStream.Close();
                }
                dataStream = new MemoryStream();

                int bytePtr = 0;
                byte[] tempBytes = new byte[0x10000];

                while (bytePtr < chunkLength)
                {
                    int dataType = bytes[bytePtr++];

                    if ((dataType & 0x80) != 0)
                    {
                        dataType <<= 8;
                        dataType |= bytes[bytePtr++];
                    }

                    int dataLen = bytes[bytePtr++];

                    if (dataType == 0x1D && dataLen == 0)
                    {
                        // fill memory stream with the rest of the chunk.
                        dataStream.Write(bytes, bytePtr, chunkLength - bytePtr);

                        break;
                    }

                    if (dataType == 1)
                    {
                        for (int i = 0; i < 4; i++) { tempBytes[i] = 0; }
                        for (int i = 0; i < dataLen; i++)
                        {
                            tempBytes[i] = bytes[bytePtr + i];
                        }
                        bytePtr += dataLen;

                        // second-order length
                        int len = (int)Utils.LittleEndian(BitConverter.ToUInt32(tempBytes, 0));

                        bytePtr += len;
                    }
                    else
                    {
                        bytePtr += dataLen;
                    }
                }
            }

        }

    }
}
