using System;
using System.Collections.Generic;
using System.IO;
using System.Text;
using System.Linq;

namespace BusouShinki_BND
{
    public class BNDUnpack
    {
        
        private int magic;
        private int version;
        private int infoOffset;
        private int tableOffset;
        private int entryInfoArea;
        private int chunks;
        private byte[] padding = new byte[8];
        private int totalFiles;
        private int totalEntries;
        private List<Entry> entries = new List<Entry>();
        private string filePath;
        private Crc32 crc = new Crc32();
   

        
        public void Load(string path)
        {
            filePath = path;
            using (var fs = new FileStream(path, FileMode.Open, FileAccess.Read))
            using (var br = new BinaryReader(fs))
            {
                // Read header (16 bytes)
                magic = br.ReadInt32();
                if (magic != 0x00444E42) // Verify magic
                    throw new Exception("Invalid BND magic value.");

                version = br.ReadInt32();
                infoOffset = br.ReadInt32(); 
                tableOffset = br.ReadInt32();

                // Read info section (16 bytes)
                fs.Position = infoOffset;
                entryInfoArea = br.ReadInt32();
                chunks = br.ReadInt32();
                padding = br.ReadBytes(8); // Skip padding

                // Read table of contents (TOC)
                fs.Position = tableOffset;
                totalFiles = br.ReadInt32(); // Files excluding directories
                totalEntries = br.ReadInt32(); // All entries including directories

                // Read each entry in TOC
                for (int i = 0; i < totalEntries; i++)
                {
                    long pos = fs.Position;
                    Entry e = new Entry
                    {
                        TablePosition = pos,
                        Hash = br.ReadUInt32(),
                        EntryInfoOffset = br.ReadInt32(),
                        FileOffset = br.ReadInt32(),
                        FileSize = br.ReadInt32()
                    };
                    entries.Add(e);
                }

                // Parse entry info for each entry
                foreach (var e in entries)
                {
                    fs.Position = e.EntryInfoOffset;
                    e.Unknown3 = br.ReadBytes(3); 
                    e.EntryOffsetBack = br.ReadInt32(); // Pointer back to table entry

                    // Read null-terminated UTF-8 string for name
                    List<byte> nameBytes = new List<byte>();
                    byte b;
                    while ((b = br.ReadByte()) != 0)
                    {
                        nameBytes.Add(b);
                    }
                    e.Name = Encoding.UTF8.GetString(nameBytes.ToArray());
                }
            }
        }

        
        public void PrintInfo()
        {
            // Print header and info
            Console.WriteLine($"[0x{magic:X8}] | [{version}] | [0x{infoOffset:X}] | [0x{tableOffset:X}]");
            Console.WriteLine($"[0x{entryInfoArea:X}] | [0x{chunks:X}]");

            
            for (int i = 0; i < entries.Count; i++)
            {
                var e = entries[i];
                string unkHex = string.Join(" ", e.Unknown3.Select(b => b.ToString("X2"))); 
                string typeIndicator = e.FileSize == 0 ? "[DIR]" : "[FILE]";
                Console.WriteLine($"Entry({i + 1}) -> Hash: 0x{e.Hash:X8} | EntryInfo: 0x{e.EntryInfoOffset:X} | FileOff: 0x{e.FileOffset:X} | Size: {e.FileSize} | Unk: {unkHex} | Back: 0x{e.EntryOffsetBack:X} | Name: \"{e.Name}\" {typeIndicator}");
            }
        }

        
        public void Extract()
        {
            // Create output folder named after input file (without extension)
            string outputFolder = Path.Combine(Directory.GetCurrentDirectory(), Path.GetFileNameWithoutExtension(filePath));
            Directory.CreateDirectory(outputFolder);

            Console.WriteLine($"[DEBUG] Starting extraction to folder: {outputFolder}");

            // Separate directories (size == 0) and files (size > 0)
            var dirs = entries.FindAll(e => e.FileSize == 0);
            var files = entries.FindAll(e => e.FileSize > 0);

            using (var fs = new FileStream(filePath, FileMode.Open, FileAccess.Read))
            using (var br = new BinaryReader(fs))
            {
                foreach (var f in files)
                {
                    Console.WriteLine($"[DEBUG] Processing file: {f.Name} (Hash: 0x{f.Hash:X8})");

                    string relPath = f.Name; // Default to root if no directory match
                    bool matchFound = false;

                    foreach (var d in dirs)
                    {
                        // Try computing hash with directory + "/" + filename
                        string testPath = d.Name + "/" + f.Name;
                        byte[] testBytes = Encoding.UTF8.GetBytes(testPath);
                        uint computed = crc.Compute(testBytes);
                        if (computed == f.Hash)
                        {
                            relPath = testPath;
                            Console.WriteLine($"[MATCH] >> {testPath} = 0x{computed:X8}");
                            matchFound = true;
                            break;
                        }

                        // Try without "/" if previous fails
                        testPath = d.Name + f.Name;
                        testBytes = Encoding.UTF8.GetBytes(testPath);
                        computed = crc.Compute(testBytes);
                        if (computed == f.Hash)
                        {
                            relPath = d.Name + "/" + f.Name; // Use "/" for path consistency
                            Console.WriteLine($"[MATCH] >> {testPath} = 0x{computed:X8}");
                            matchFound = true;
                            break;
                        }
                    }

                    if (!matchFound)
                    {
                        Console.WriteLine($"[DEBUG] No directory match found for {f.Name}, extracting to root.");
                    }

                    // Construct full output path and create directories if needed
                    string fullPath = Path.Combine(outputFolder, relPath);
                    Directory.CreateDirectory(Path.GetDirectoryName(fullPath));

                    // Extract file data
                    fs.Position = f.FileOffset;
                    byte[] data = br.ReadBytes(f.FileSize);
                    File.WriteAllBytes(fullPath, data);

                    Console.WriteLine($"[DEBUG] Extracted: {fullPath} (Size: {f.FileSize} bytes)");
                }
            }

            Console.WriteLine("[DEBUG] Extraction completed.");
        }
    }

    // Entry structure for TOC and info data
    public class Entry
    {
        public long TablePosition { get; set; } // Position in TOC
        public uint Hash { get; set; } // CRC32 hash
        public int EntryInfoOffset { get; set; } // Offset to entry info
        public int FileOffset { get; set; } // Offset to file data
        public int FileSize { get; set; } // Size of file data
        public byte[] Unknown3 { get; set; } // 3 unknown bytes
        public int EntryOffsetBack { get; set; } // Pointer back to TOC entry
        public string Name { get; set; } // Null-terminated name
    }


    // CRC32
    public class Crc32
    {
        private readonly uint[] _table = new uint[256];

        public Crc32()
        {
            const uint poly = 0xEDB88320u; // Standard CRC32 polynomial
            for (uint i = 0; i < 256; i++)
            {
                uint res = i;
                for (int j = 0; j < 8; j++)
                {
                    if ((res & 1) == 1)
                        res = (res >> 1) ^ poly;
                    else
                        res >>= 1;
                }
                _table[i] = res;
            }
        }

        public uint Compute(byte[] bytes)
        {
            uint crc = 0xFFFFFFFFu;
            for (int i = 0; i < bytes.Length; i++)
            {
                byte index = (byte)((crc & 0xFF) ^ bytes[i]);
                crc = (crc >> 8) ^ _table[index];
            }
            return ~crc;
        }
    }
}