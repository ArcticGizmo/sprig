using Sprig.Cli;

// Sprig CLI — internal harness that drives Sprig.Core during development.
// Not the shipped product (the Avalonia app is); this exists so every milestone
// is runnable and testable end-to-end before the UI exists.
return CliApp.Run(args);
