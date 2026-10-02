using System.Text;
using DepGraph;

// Labels carry the extract's own file names (Danish, German, ...); without this
// a Windows console shows them in its legacy code page.
Console.OutputEncoding = Encoding.UTF8;
return Cli.Run(args, Console.Error, Console.Out);
