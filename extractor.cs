using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Runtime.InteropServices;
using System.Text;
using System.Windows.Forms;

namespace PathExtractor
{
    internal static class Program
    {
        private static HashSet<string> Seen;
        private static List<string> Results;
        private static long Lines;
        private static long Candidates;
        private static long Files;
        private static long Missing;
        private static long Folders;
        private static long Junk;
        private static long Duplicates;
        private static Stream ProgressStream;
        private static long TotalBytes;
        private static int LastPct = -1;

        private static bool PathExists(string path)
        {
            return File.Exists(path) || Directory.Exists(path);
        }

        private static bool HasInvalidSemicolonPath(string text)
        {
            int pos = -1;
            while (true)
            {
                pos = text.IndexOf(';', pos + 1);
                if (pos < 0) return false;
                if (pos + 3 < text.Length && text[pos + 2] == ':' && text[pos + 3] == '\\') return true;
            }
        }

        private static string ExtractValidPath(string line, out int reason)
        {
            reason = 0;
            int p = line.IndexOf(":\\", StringComparison.Ordinal);
            int p2 = p;
            if (p < 0) p2 = line.IndexOf(":/", StringComparison.Ordinal);
            if (p2 < 0 || p2 == 0) return null;
            if (line.LastIndexOf(';', p2) >= 0)
            {
                reason = 1;
                return null;
            }
            char dl = line[p2 - 1];
            if (!((dl >= 'A' && dl <= 'Z') || (dl >= 'a' && dl <= 'z')))
            {
                reason = 1;
                return null;
            }
            string path = line.Substring(p2 - 1).Replace('/', '\\');
            if (path[path.Length - 1] == '\\' && !PathExists(path))
            {
                reason = 2;
                return null;
            }
            if (HasInvalidSemicolonPath(path))
            {
                reason = 1;
                return null;
            }
            reason = 3;
            return path;
        }

        private static void HandleLine(string line)
        {
            Lines++;
            if ((Lines & 8191) == 0)
            {
                int pct = (int)Math.Min(100, ProgressStream.Position * 100 / TotalBytes);
                if (pct != LastPct)
                {
                    LastPct = pct;
                    Render(pct);
                }
            }
            int reason;
            string cand = ExtractValidPath(line.TrimEnd(' ', '\t'), out reason);
            if (reason == 0) return;
            Candidates++;
            if (reason == 1)
            {
                Junk++;
                return;
            }
            if (reason == 2)
            {
                Folders++;
                return;
            }
            if (!Seen.Add(cand))
            {
                Duplicates++;
                return;
            }
            if (Directory.Exists(cand))
            {
                Folders++;
                return;
            }
            Results.Add(cand);
            Files++;
            if (!File.Exists(cand)) Missing++;
        }

        private static string PickOutputPath(string defaultPath)
        {
            using (SaveFileDialog dialog = new SaveFileDialog())
            {
                dialog.Title = "save results as";
                dialog.Filter = "text files (*.txt)|*.txt|all files (*.*)|*.*";
                dialog.FileName = Path.GetFileName(defaultPath);
                dialog.InitialDirectory = Environment.GetFolderPath(Environment.SpecialFolder.Desktop);
                dialog.RestoreDirectory = true;
                return dialog.ShowDialog() == DialogResult.OK ? dialog.FileName : null;
            }
        }

        private static void PrintStats(string savedPath, double seconds)
        {
            Console.WriteLine();
            if (savedPath != null)
            {
                Red("output          : " + savedPath);
            }
            Red("time            : " + seconds.ToString("0.0") + " s");
        }

        private static Encoding GetFileEncoding(string path)
        {
            long length = new FileInfo(path).Length;
            using (FileStream stream = File.OpenRead(path))
            {
                byte[] head = new byte[4];
                int read = stream.Read(head, 0, 4);
                if (read >= 3 && head[0] == 0xEF && head[1] == 0xBB && head[2] == 0xBF) return new UTF8Encoding(false);
                if (read >= 4 && head[0] == 0xFF && head[1] == 0xFE && head[2] == 0 && head[3] == 0) return new UTF32Encoding(false, true);
                if (read >= 2 && head[0] == 0xFF && head[1] == 0xFE) return Encoding.Unicode;
                if (read >= 2 && head[0] == 0xFE && head[1] == 0xFF) return Encoding.BigEndianUnicode;
                int chunk = (int)Math.Min(4L * 1024 * 1024, length);
                byte[] buf = new byte[chunk];
                UTF8Encoding strict = new UTF8Encoding(false, true);
                stream.Position = 0;
                read = stream.Read(buf, 0, chunk);
                try
                {
                    strict.GetString(buf, 0, read);
                }
                catch (DecoderFallbackException)
                {
                    return Encoding.Default;
                }
                if (length > chunk)
                {
                    stream.Position = length - chunk;
                    read = stream.Read(buf, 0, chunk);
                    try
                    {
                        strict.GetString(buf, 0, read);
                    }
                    catch (DecoderFallbackException)
                    {
                        return Encoding.Default;
                    }
                }
                return new UTF8Encoding(false);
            }
        }

        private static void Render(int pct)
        {
            Console.ForegroundColor = ConsoleColor.Red;
            Console.Write("\r" + pct + "%   ");
            Console.ResetColor();
        }

        private static void Red(string text)
        {
            Console.ForegroundColor = ConsoleColor.Red;
            Console.WriteLine(text);
            Console.ResetColor();
        }

        private static void PressEnter()
        {
            Console.WriteLine();
            Console.WriteLine("press any key to exit...");
            Console.ReadKey(true);
        }

        private static void MakeWindowBlack()
        {
            try
            {
                Console.BackgroundColor = ConsoleColor.Black;
                Console.ForegroundColor = ConsoleColor.Gray;
                Console.Clear();
            }
            catch { }
        }

        [STAThread]
        private static int Main(string[] args)
        {
            MakeWindowBlack();
            string inputPath = args.Length > 0 ? args[0] : @"C:\Search results.txt";
            string outputPath = args.Length > 1 ? args[1] : @"C:\extractor_results.txt";
            bool choosePath = args.Length < 2;
            try
            {
                Console.ForegroundColor = ConsoleColor.Red;
                Console.WriteLine("paths extractor");
                Console.ResetColor();
                Console.WriteLine();
                Console.WriteLine("input  : " + inputPath);
                Console.WriteLine();

                if (!File.Exists(inputPath))
                {
                    Red("error: file not found - " + inputPath);
                    PressEnter();
                    return 1;
                }

                if (!choosePath)
                {
                    string writeTest = outputPath + ".writetest";
                    try
                    {
                        File.WriteAllText(writeTest, "");
                        File.Delete(writeTest);
                    }
                    catch
                    {
                        string dir = Path.GetDirectoryName(outputPath);
                        Red("error: no write access to " + (string.IsNullOrEmpty(dir) ? outputPath : dir) + "\\");
                        PressEnter();
                        return 1;
                    }
                }

                Console.Write("do you want to extract paths? (y/n): ");
                string answer = (Console.ReadLine() ?? "").Trim().ToLowerInvariant();
                if (answer != "y")
                {
                    return 0;
                }

                Encoding encoding = GetFileEncoding(inputPath);
                Seen = new HashSet<string>(StringComparer.Ordinal);
                Results = new List<string>();
                Lines = 0;
                Candidates = 0;
                Files = 0;
                Missing = 0;
                Folders = 0;
                Junk = 0;
                Duplicates = 0;
                TotalBytes = Math.Max(1L, new FileInfo(inputPath).Length);
                LastPct = -1;

                Console.WriteLine();
                Console.WriteLine("scanning file...");

                Stopwatch sw = Stopwatch.StartNew();
                using (StreamReader reader = new StreamReader(inputPath, encoding, true, 1024 * 1024))
                {
                    ProgressStream = reader.BaseStream;
                    Render(0);
                    char[] buf = new char[1024 * 1024];
                    StringBuilder lineBuf = new StringBuilder(512);
                    int read;
                    while ((read = reader.Read(buf, 0, buf.Length)) > 0)
                    {
                        for (int i = 0; i < read; i++)
                        {
                            char ch = buf[i];
                            if (ch == '\n')
                            {
                                int len = lineBuf.Length;
                                if (len > 0 && lineBuf[len - 1] == '\r') lineBuf.Length = len - 1;
                                HandleLine(lineBuf.ToString());
                                lineBuf.Length = 0;
                            }
                            else
                            {
                                lineBuf.Append(ch);
                            }
                        }
                    }
                    if (lineBuf.Length > 0) HandleLine(lineBuf.ToString());
                }
                Render(100);
                Console.WriteLine();
                sw.Stop();

                if (choosePath)
                {
                    Console.WriteLine("choose save location...");
                    string chosen = PickOutputPath(outputPath);
                    if (chosen == null)
                    {
                        PrintStats(null, sw.Elapsed.TotalSeconds);
                        Console.WriteLine();
                        Console.WriteLine("save cancelled.");
                        PressEnter();
                        return 0;
                    }
                    outputPath = chosen;
                }

                try
                {
                    File.WriteAllLines(outputPath, Results.ToArray(), new UTF8Encoding(false));
                }
                catch
                {
                    string dir = Path.GetDirectoryName(outputPath);
                    Red("error: no write access to " + (string.IsNullOrEmpty(dir) ? outputPath : dir) + "\\");
                    PressEnter();
                    return 1;
                }

                PrintStats(outputPath, sw.Elapsed.TotalSeconds);
                PressEnter();
                return 0;
            }
            catch (Exception ex)
            {
                Console.WriteLine();
                Red("error: " + ex.Message);
                PressEnter();
                return 1;
            }
        }
    }
}
