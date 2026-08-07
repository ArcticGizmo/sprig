using System.Text;
using Sprig.Cli;

// Force UTF-8 console output before anything renders. On Windows the default is often a legacy
// codepage, which makes Spectre detect the terminal as non-Unicode — it then substitutes '?' for
// spinner/check glyphs and its live-render cursor maths drifts, corrupting the checklist layout.
try { Console.OutputEncoding = new UTF8Encoding(false); } catch { /* no console attached — leave it */ }

// Sprig CLI — the terminal front-end onto Sprig.Core, shipped on PATH alongside
// the desktop app and covering the same surface. Both drive one engine; neither
// is the "real" one. Its --json output is a supported contract (scripts depend
// on it), so treat shape changes there as breaking.
return CliApp.Run(args);
