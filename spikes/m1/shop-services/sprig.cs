#:project ../../../src/Sprig.Sdk/Sprig.Sdk.csproj
using Sprig;

var repo = Repo.Describe("shop-services");

var pg = repo.Container("pg", "postgres:17")
    .Endpoint("tcp", targetPort: 5432);

var bus = repo.Container("bus", "rabbitmq:4-management")
    .Endpoint("amqp", targetPort: 5672);

var smtp = repo.Import<Url>("mail-smtp");                // filled in by the system

var catalog = repo.DotNet("catalog-api", "src/Catalog.Api")
    .Http("http")
    .Env("ConnectionStrings__Db", $"Host=localhost;Port={pg.Port};Database=catalog")
    .Env("Bus__Url", $"amqp://localhost:{bus.Port}")
    .WaitFor(pg, bus);

var orders = repo.DotNet("orders-api", "src/Orders.Api")
    .Http("http")
    .Env("ConnectionStrings__Db", $"Host=localhost;Port={pg.Port};Database=orders")
    .Env("Smtp__Url", smtp)
    .WaitFor(pg, bus, catalog);

repo.Export<Url>("catalog-url", catalog.Url);
repo.Export<Url>("orders-url", orders.Url);

return repo.Emit(args);
