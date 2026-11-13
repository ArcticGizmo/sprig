using Sprig.Engine.Hosting;
using Sprig.Engine.Store;

if (args.Length == 2 && args[0] == ConsoleSignal.HelperArgument) return ConsoleSignal.RunHelper(args[1]);

var storeAt = Array.IndexOf(args, "--store");
var paths = new SprigPaths(storeAt >= 0 && storeAt + 1 < args.Length ? args[storeAt + 1] : null);

var idleAt = Array.IndexOf(args, "--idle-minutes");
var idle = TimeSpan.FromMinutes(idleAt >= 0 && idleAt + 1 < args.Length && double.TryParse(args[idleAt + 1], out var m) ? m : 10);

ConsoleSignal.HelperPath = Environment.ProcessPath;
return await new HostServer(paths, idle).RunAsync();
