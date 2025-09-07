using System;
using System.IO;

namespace BusouShinki_BND
{
    class Program
    {
        static void Main(string[] args)
        {
            if (args.Length < 2)
            {
                PrintUsage();
                return;
            }

            string command = args[0].ToLower();
            string inputFile = args[1];

            if (!File.Exists(inputFile))
            {
                Console.WriteLine($"Error: Input file not found: '{inputFile}'");
                return;
            }

            try
            {
                switch (command)
                {
                    case "-x": // Extract
                        if (args.Length != 2)
                        {
                            PrintUsage();
                            return;
                        }
                        Console.WriteLine($"--- Unpacking {Path.GetFileName(inputFile)} ---");
                        BNDUnpack unpacker = new BNDUnpack();
                        unpacker.Load(inputFile);
                        unpacker.PrintInfo();
                        unpacker.Extract();
                        Console.WriteLine("--- Unpacking Complete ---");
                        break;

                    case "-r": // Repack
                        if (args.Length != 3)
                        {
                            PrintUsage();
                            return;
                        }
                        string inputFolder = args[2];
                        if (!Directory.Exists(inputFolder))
                        {
                            Console.WriteLine($"Error: Input folder not found: '{inputFolder}'");
                            return;
                        }
                        Console.WriteLine($"--- Repacking {Path.GetFileName(inputFile)} from folder '{inputFolder}' ---");
                        BNDRepack repacker = new BNDRepack();
                        repacker.Repack(inputFile, inputFolder);
                        break;

                    default:
                        Console.WriteLine($"Unknown command: '{command}'");
                        PrintUsage();
                        break;
                }
            }
            catch (Exception ex)
            {
                Console.WriteLine($"An error occurred: {ex.Message}");
                Console.WriteLine(ex.StackTrace);
            }
        }

        static void PrintUsage()
        {
            Console.WriteLine("Busou ShinkBattle Masters + Mk2 *.BND Tool [Experimental]");
            Console.WriteLine("Usage:");
            Console.WriteLine(" [Extraction] -x [input.bnd]");
            Console.WriteLine(" [Repacking] -r [input.bnd] [input folder for that bnd file]");
        }
    }
}
