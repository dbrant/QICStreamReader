using QicUtils;
using System;
using System.IO;
using System.Text;


/// <summary>
/// 
/// Copyright Dmitry Brant, 2023.
/// 
/// </summary>
namespace arcserve
{
    class Program
    {
        private const uint FileHeaderBlock = 0x3A3A3A3A;

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

                    if (header.IsDirectory || header.Name.Trim() == "")
                    {
                        Console.WriteLine(stream.Position.ToString("X") + ": " + header.Name + " - " + header.Size.ToString() + " bytes");
                        continue;
                    }

                    if (header.Size == 0)
                    {
                        Console.WriteLine(stream.Position.ToString("X") + ": " + header.Name);
                        Console.WriteLine("Warning: skipping zero-length file.");
                        continue;
                    }



                    int firstByte = stream.ReadByte();
                    bool compressed = firstByte == 0;

                    stream.Seek(-1, SeekOrigin.Current);

                    string filePath = baseDirectory;
                    string[] dirArray = header.Name.Split("\\");
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

                        Console.WriteLine("(" + (compressed ? "c" : " ") + ")" + stream.Position.ToString("X") + ": " + filePath + " - "
                            + header.Size.ToString() + " bytes - " + header.CreateDate.ToShortDateString());

                        long posBeforeRead = stream.Position;

                        // read the whole file into a memory buffer, so that we can pass it into the decompressor when ready.
                        using (var memStream = new MemoryStream())
                        {
                            int bytesPerChunk = 0x200;

                            // initialize, subtracting the initial size of the header.
                            int bytesToRead = bytesPerChunk - FileHeader.HEADER_SIZE;
                            int bytesToWrite = 0;
                            long bytesLeft = header.Size;
                            var bytes = new byte[bytesPerChunk];

                            while (bytesLeft > 0)
                            {
                                stream.Read(bytes, 0, bytesToRead);
                                bytesToWrite = bytesToRead - 2;
                                bytesToRead = bytesPerChunk;

                                if (bytesToWrite > bytesLeft)
                                {
                                    bytesToWrite = (int)bytesLeft;
                                }
                                memStream.Write(bytes, 0, bytesToWrite);

                                bytesLeft -= bytesToWrite;
                            }
                            memStream.Seek(0, SeekOrigin.Begin);

                            // TODO: uncompress if necessary

                            using var f = new FileStream(filePath, FileMode.Create, FileAccess.Write);
                            memStream.WriteTo(f);
                            f.Flush();
                        }

                        // Rewind the stream back to the beginning of the current file.
                        // TODO: remove this when decompression is working.
                        if (compressed)
                        {
                            stream.Seek(posBeforeRead, SeekOrigin.Begin);
                        }

                        try
                        {
                            File.SetCreationTime(filePath, header.CreateDate);
                            File.SetLastWriteTime(filePath, header.ModifyDate);
                            File.SetAttributes(filePath, header.Attributes);
                        }
                        catch { }
                    }
                    else
                    {
                        filePath = Path.Combine(filePath, fileName);
                        Console.WriteLine("(" + (compressed ? "c" : " ") + ")" + stream.Position.ToString("X") + ": " + filePath + " - "
                            + header.Size.ToString() + " bytes - " + header.CreateDate.ToShortDateString());
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
            public DateTime AccessDate { get; }
            public FileAttributes Attributes { get; }
            public bool Valid { get; }
            public bool IsDirectory { get; }

            public const int HEADER_SIZE = 0x60;

            public FileHeader(Stream stream)
            {
                byte[] bytes = new byte[HEADER_SIZE];

                stream.Read(bytes, 0, bytes.Length);
                if (Utils.LittleEndian(BitConverter.ToUInt32(bytes, 0)) != FileHeaderBlock) { return; }

                Size = Utils.LittleEndian(BitConverter.ToUInt32(bytes, 4));

                Name = Utils.GetNullTerminatedString(Encoding.ASCII.GetString(bytes, 0x10, 0x50));
                if (Name.Length == 0) { return; }

                // remove leading drive letter, if any
                if (Name[1] == ':' && Name[2] == '\\')
                {
                    Name = Name[3..];
                }

                DosName = Name;

                int date = Utils.LittleEndian(BitConverter.ToUInt16(bytes, 0x8));
                int time = Utils.LittleEndian(BitConverter.ToUInt16(bytes, 0xA));

                CreateDate = Utils.GetDosDateTime(date, time);
                ModifyDate = CreateDate;
                AccessDate = ModifyDate;

                Attributes = (FileAttributes)bytes[0xC];

                Valid = true;
            }

        }

    }
}
