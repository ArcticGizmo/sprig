#:package ArcticGizmo.Sprig.Sdk@0.*
using Sprig;

var repo = Repo.Describe("shop-services");

// Infra comes from the repo's own compose file; sprig gives each service its own host ports per workspace.
var compose = repo.ComposeFile("docker-compose.yml");
var pg     = compose.Service("postgres").Endpoint("tcp", 5432).Health("pg_isready -h 127.0.0.1 -U shop");
var redis  = compose.Service("redis").Endpoint("tcp", 6379).Health("redis-cli ping");
// Health checks run as root: pinging before the server writes its Erlang cookie leaves a root-owned one the server
// can't read, and it exits. Waiting for the server's cookie first avoids that.
var rabbit = compose.Service("rabbitmq").Endpoint("amqp", 5672).Endpoint("ui", 15672, scheme: "http")
    .Health("test -f /var/lib/rabbitmq/.erlang.cookie && rabbitmq-diagnostics -q ping");

// Run on its own, the repo brings a mail server. One given by an orchestration (or the workspace) replaces it, and
// then this one isn't started.
var smtp = repo.Setting<Url>("mail-smtp", repo.Mailpit("mail").SmtpUrl);

// Runs to completion each time the apps start; the APIs wait for it to succeed. Retried, since Postgres can still be
// settling in the first second after it reports healthy. It reads the env, so its value goes in a launch profile,
// which dotnet run and IDEs read; .gitignore covers it.
var migrate = repo.DotNetJob("migrate", "tools/Migrate")
    .LaunchSettings(s => s.Set("ConnectionStrings__Db", $"Host=localhost;Port={pg.Port};Database=catalog;Username=shop;Password=shop"))
    .WaitFor(pg)
    .Retry(max: 3);

// Ready once /health answers, rather than as soon as Kestrel opens the port.
var catalog = repo.DotNet("catalog-api", "src/Catalog.Api")
    .Http(health: "/health")
    .Restart(RestartPolicy.OnFailure)
    .WaitFor(pg, redis, rabbit, migrate);

// Urls rather than ASPNETCORE_URLS: appsettings.json sets "urls", and only the unprefixed name outranks it.
catalog.LaunchSettings(s => s
    .Set("Urls", catalog.Url)
    .Set("ConnectionStrings__Db", $"Host=localhost;Port={pg.Port};Database=catalog;Username=shop;Password=shop")
    .Set("Redis__Url", $"localhost:{redis.Port}")
    .Set("Bus__Url", $"amqp://shop:shop@localhost:{rabbit.Port}"));

var orders = repo.DotNet("orders-api", "src/Orders.Api")
    .Http(health: "/health")
    .Restart(RestartPolicy.OnFailure)
    .WaitFor(pg, rabbit, migrate, catalog);

// Program.cs loads appsettings.Local.json last; .gitignore covers it.
orders.AppSettings("appsettings.Local.json", s => s
    .Set("Urls", orders.Url)
    .Set("ConnectionStrings__Db", $"Host=localhost;Port={pg.Port};Database=orders;Username=shop;Password=shop")
    .Set("Bus__Url", $"amqp://shop:shop@localhost:{rabbit.Port}")
    .Set("Smtp__Url", smtp)
    .Set("Catalog__Url", catalog.Url));                   // a sibling in this repo: no import needed

repo.Export<Url>("catalog-url", catalog.Url);
repo.Export<Url>("orders-url", orders.Url);

return repo.Emit(args);
