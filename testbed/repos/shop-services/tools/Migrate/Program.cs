using Shop.Diagnostics;

// A stand-in migration job: it waits for the database to accept connections, reports what it would apply, and
// exits. It exists so runners have a one-off job to sequence before the APIs start.
var connection = Environment.GetEnvironmentVariable("ConnectionStrings__Db")
    ?? "Host=localhost;Port=5432;Database=catalog;Username=shop;Password=shop";
var target = Probe.Find(connection);
if (target is null)
{
    Console.Error.WriteLine("migrate: ConnectionStrings__Db has no host and port");
    return 2;
}

Console.WriteLine($"migrate: waiting for the database at {target}");
var deadline = DateTime.UtcNow.AddSeconds(30);
while (!await Probe.ReachableAsync(target, TimeSpan.FromSeconds(1)))
{
    if (DateTime.UtcNow > deadline)
    {
        Console.Error.WriteLine($"migrate: nothing answered at {target} within 30s");
        return 1;
    }
    await Task.Delay(500);
}

foreach (var step in new[] { "001_create_products", "002_create_orders" })
    Console.WriteLine($"migrate: applied {step}");
Console.WriteLine("migrate: done");
return 0;
