#:package Sprig.Sdk@0.1.0-exp
using Sprig;

var repo = Repo.Describe("shop-services");

// Infra comes from the repo's own compose file; sprig gives each service its own host ports per sprout.
var compose = repo.ComposeFile("docker-compose.yml");
var pg     = compose.Service("postgres").Endpoint("tcp", 5432).Health("pg_isready -h 127.0.0.1 -U shop");
var redis  = compose.Service("redis").Endpoint("tcp", 6379).Health("redis-cli ping");
var rabbit = compose.Service("rabbitmq").Endpoint("amqp", 5672).Endpoint("ui", 15672, scheme: "http")
    .Health("rabbitmq-diagnostics -q ping");

var smtp = repo.Import<Url>("mail-smtp");                 // the trellis supplies the mail server

// Urls rather than ASPNETCORE_URLS: appsettings.json sets "urls", and only the unprefixed env var outranks it.
var catalog = repo.DotNet("catalog-api", "src/Catalog.Api")
    .Http(urlEnv: "Urls")
    .Env("ConnectionStrings__Db", $"Host=localhost;Port={pg.Port};Database=catalog;Username=shop;Password=shop")
    .Env("Redis__Url", $"localhost:{redis.Port}")
    .Env("Bus__Url", $"amqp://shop:shop@localhost:{rabbit.Port}")
    .Output(EnvOutput.LaunchSettings())                   // so `dotnet run` (and an IDE) gets the sprout's values
    .WaitFor(pg, redis, rabbit);

var orders = repo.DotNet("orders-api", "src/Orders.Api")
    .Http(urlEnv: "Urls")
    .Env("ConnectionStrings__Db", $"Host=localhost;Port={pg.Port};Database=orders;Username=shop;Password=shop")
    .Env("Bus__Url", $"amqp://shop:shop@localhost:{rabbit.Port}")
    .Env("Smtp__Url", smtp)
    .Env("Catalog__Url", catalog.Url)                     // a sibling in this repo: no import needed
    .Output(EnvOutput.LaunchSettings())
    .WaitFor(pg, rabbit, catalog);

repo.Export<Url>("catalog-url", catalog.Url);
repo.Export<Url>("orders-url", orders.Url);

return repo.Emit(args);
