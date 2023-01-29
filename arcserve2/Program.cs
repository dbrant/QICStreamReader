using QicUtils;
using System;
using System.IO;
using System.Security.Cryptography;
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
        private const uint FileHeaderBlock = 0x55555557;
        private const uint FileEndBlock = 0xCCCCCCCC;

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
                using (var stream = new FileStream(inFileName, FileMode.Open, FileAccess.Read))
                {
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

                            Console.WriteLine(stream.Position.ToString("X") + ": " + filePath + " - " + header.Size.ToString() + " bytes - " + header.CreateDate.ToShortDateString());

                            using (var f = new FileStream(filePath, FileMode.Create, FileAccess.Write))
                            {
                                var bytes = new byte[header.Size];
                                stream.Read(bytes, 0, bytes.Length);
                                f.Write(bytes);
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
                            stream.Seek(header.Size, SeekOrigin.Current);
                            filePath = Path.Combine(filePath, fileName);
                            Console.WriteLine(stream.Position.ToString("X") + ": " + filePath + " - " + header.Size.ToString() + " bytes - " + header.CreateDate.ToShortDateString());
                        }
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

            public FileHeader(Stream stream)
            {
                byte[] bytes = new byte[0xA00];

                // Read the first four bytes
                stream.Read(bytes, 0, 4);
                if (Utils.LittleEndian(BitConverter.ToUInt32(bytes, 0)) != FileHeaderBlock) {
                    return;
                }

                // Now go back and read the whole header.
                stream.Seek(-4, SeekOrigin.Current);
                stream.Read(bytes, 0, bytes.Length);

                Name = Utils.GetNullTerminatedString(Encoding.ASCII.GetString(bytes, 4, 0x80));
                if (Name.Length == 0) { return; }
                DosName = Name;

                //int sizeLoc = 0x7C0 + bytes[0x7B3] + 7;
                Size = Utils.LittleEndian(BitConverter.ToUInt32(bytes, 0x17B));

                //DateTime = new DateTime(1970, 1, 1).AddSeconds(BitConverter.ToUInt32(bytes, 0x2E));
                //CreateDate = QicUtils.Utils.DateTimeFromTimeT(BitConverter.ToUInt32(bytes, 0x2E));
                //ModifyDate = QicUtils.Utils.DateTimeFromTimeT(BitConverter.ToUInt32(bytes, 0x3E));
                //DateTime = QicUtils.Utils.GetQicDateTime(BitConverter.ToUInt32(bytes, 0x2E));

                Valid = true;
            }

        }

    }
}
