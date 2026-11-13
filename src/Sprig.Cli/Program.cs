using System.Text;
using Sprig.Cli;

// On Windows the default is often a legacy codepage, which makes Spectre treat the terminal as non-Unicode
// and substitute '?' for glyphs.
try { Console.OutputEncoding = new UTF8Encoding(false); } catch { /* no console attached */ }

return CliApp.Run(args);
