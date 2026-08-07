using Sprig.Cli;

// Sprig CLI — the terminal front-end onto Sprig.Core, shipped on PATH alongside
// the desktop app and covering the same surface. Both drive one engine; neither
// is the "real" one. Its --json output is a supported contract (scripts depend
// on it), so treat shape changes there as breaking.
return CliApp.Run(args);
