// A stand-in for 7z.exe / 7zr.exe. It parses arguments the way 7z does (a command, switches until "--", then the
// archive and optional file names) and records them, one per line, to "<archive>.args" so tests can check exactly
// what was passed. The archive's content picks the outcome:
//   FAIL      -> "ERROR: Data Error : <archive>", exit 2
//   PASSWORD  -> "ERROR: Wrong password : secret.txt", exit 2
//   WARN      -> extracts as normal, "WARNING: Headers Error", exit 1
//   SLOW      -> sleeps 60 s, exit 0
//   otherwise -> needs -o<dir>; writes the named files (the installer mode the 7-Zip setup uses) or <dir>\hello.txt, exit 0
// With -bsp1 it first prints progress (0%, 37%, 37%, 82%, 100%) to stdout the way 7-Zip does, with backspaces.
using System.Text;

Console.OutputEncoding = new UTF8Encoding(false);
var stderr = Console.Error;

var dash = Array.IndexOf(args, "--");
if (args.Length == 0 || dash < 0 || dash == args.Length - 1)
{
    stderr.WriteLine("ERROR: missing archive name");
    return 7;
}

var archive = args[dash + 1];
var names = args[(dash + 2)..];
var switches = args[1..dash];

File.WriteAllLines(archive + ".args", args, new UTF8Encoding(false));

if (!File.Exists(archive))
{
    stderr.WriteLine("ERROR: The system cannot find the file specified.");
    stderr.WriteLine(archive);
    return 2;
}

// Latin-1 keeps every byte as one char, so the markers are found in binary files too.
var content = Encoding.Latin1.GetString(File.ReadAllBytes(archive));

if (content.Contains("FAIL", StringComparison.Ordinal))
{
    stderr.WriteLine($"ERROR: Data Error : {archive}");
    return 2;
}
if (content.Contains("PASSWORD", StringComparison.Ordinal))
{
    stderr.WriteLine("ERROR: Wrong password : secret.txt");
    return 2;
}
if (content.Contains("SLOW", StringComparison.Ordinal))
{
    Thread.Sleep(TimeSpan.FromSeconds(60));
    return 0;
}

var output = switches.FirstOrDefault(s => s.StartsWith("-o", StringComparison.Ordinal))?[2..];
if (string.IsNullOrEmpty(output))
{
    stderr.WriteLine("ERROR: missing -o");
    return 7;
}

// With -bsp1, progress goes to stdout the way 7-Zip writes it: one line redrawn in place with backspaces, never ended.
var progress = switches.Contains("-bsp1");
void Progress(string text)
{
    if (!progress) return;
    Console.Out.Write(text + new string('\b', text.Length));
    Console.Out.Flush();
    Thread.Sleep(20);
}
Progress("  0%");
Progress(" 37% 1 - hello.txt");
Progress(" 37% 1 - hello.txt"); // a repeat: the extractor reports each percentage once
Progress(" 82% 1 - hello.txt");
Progress("                  "); // 7-Zip wipes the line with spaces
Progress("100%");

Directory.CreateDirectory(output);
if (names.Length > 0)
{
    foreach (var name in names)
        File.WriteAllBytes(Path.Combine(output, name), Encoding.ASCII.GetBytes("dummy " + name));
}
else
{
    File.WriteAllText(Path.Combine(output, "hello.txt"), "extracted from " + Path.GetFileName(archive), new UTF8Encoding(false));
}

if (content.Contains("WARN", StringComparison.Ordinal))
{
    stderr.WriteLine("WARNING: Headers Error");
    return 1;
}
return 0;
