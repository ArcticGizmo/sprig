#:package Sprig.Sdk@0.1.0-exp
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
// settling in the first second after it reports healthy.
var migrate = repo.DotNetJob("migrate", "tools/Migrate")
    .Env("ConnectionStrings__Db", $"Host=localhost;Port={pg.Port};Database=catalog;Username=shop;Password=shop")
    .WaitFor(pg)
    .Retry(max: 3);

// Urls rather than ASPNETCORE_URLS: appsettings.json sets "urls", and only the unprefixed env var outranks it.
// Ready once /health answers, rather than as soon as Kestrel opens the port.
var catalog = repo.DotNet("catalog-api", "src/Catalog.Api")
    .Http(urlEnv: "Urls", health: "/health")
    .Restart(RestartPolicy.OnFailure)
    .Env("ConnectionStrings__Db", $"Host=localhost;Port={pg.Port};Database=catalog;Username=shop;Password=shop")
    .Env("Redis__Url", $"localhost:{redis.Port}")
    .Env("Bus__Url", $"amqp://shop:shop@localhost:{rabbit.Port}")
    .Output(EnvOutput.LaunchSettings())                   // dotnet run and IDEs read it; .gitignore covers it
    .WaitFor(pg, redis, rabbit, migrate);

var orders = repo.DotNet("orders-api", "src/Orders.Api")
    .Http(urlEnv: "Urls", health: "/health")
    .Restart(RestartPolicy.OnFailure)
    .Env("ConnectionStrings__Db", $"Host=localhost;Port={pg.Port};Database=orders;Username=shop;Password=shop")
    .Env("Bus__Url", $"amqp://shop:shop@localhost:{rabbit.Port}")
    .Env("Smtp__Url", smtp)
    .Env("Catalog__Url", catalog.Url)                     // a sibling in this repo: no import needed
    .Output(EnvOutput.AppSettings("appsettings.Local.json")) // Program.cs loads it; .gitignore covers it
    .WaitFor(pg, rabbit, migrate, catalog);

repo.Export<Url>("catalog-url", catalog.Url);
repo.Export<Url>("orders-url", orders.Url);

return repo.Emit(args);
