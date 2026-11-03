using Sprig.Engine.Hosting;
using Sprig.Engine.Store;

var storeAt = Array.IndexOf(args, "--store");
var paths = new SprigPaths(storeAt >= 0 && storeAt + 1 < args.Length ? args[storeAt + 1] : null);

var idleAt = Array.IndexOf(args, "--idle-minutes");
var idle = TimeSpan.FromMinutes(idleAt >= 0 && idleAt + 1 < args.Length && double.TryParse(args[idleAt + 1], out var m) ? m : 10);

return await new HostServer(paths, idle).RunAsync();
