#:sdk Aspire.AppHost.Sdk@13.6.1
#:property AspireUseCliBundle=true
#:package Aspire.Hosting.PostgreSQL@13.6.1
#:package Aspire.Hosting.Redis@13.6.1
#:package Aspire.Hosting.RabbitMQ@13.6.1
#:package Aspire.Hosting.JavaScript@13.6.1

// The storefront testbed under Aspire, for comparison with storefront.orchestration.cs. Aspire has no notion of
// separate repos, so this one file names all three by folder. ShopRoot moves it onto another set of checkouts (worktrees made
// by hand), e.g. `aspire run --isolated -- --ShopRoot ../storefront--feat-a`.

var builder = DistributedApplication.CreateBuilder(args);
var root = Path.GetFullPath(Path.Combine(builder.AppHostDirectory, builder.Configuration["ShopRoot"] ?? ".."));
string In(string relative) => Path.Combine(root, relative);

// Infra is declared again here: Aspire runs its own containers rather than the repo's docker-compose.yml.
var pg = builder.AddPostgres("postgres").WithDataVolume();
var catalogDb = pg.AddDatabase("catalog");
var ordersDb = pg.AddDatabase("orders");
var redis = builder.AddRedis("redis");
var rabbit = builder.AddRabbitMQ("rabbitmq").WithManagementPlugin();
// No Mailpit integration in Aspire itself (the Community Toolkit has one), so a plain container.
var mail = builder.AddContainer("mail", "axllent/mailpit", "v1.27")
    .WithEndpoint(targetPort: 1025, name: "smtp", scheme: "tcp")
    .WithHttpEndpoint(targetPort: 8025, name: "ui");

var redisEndpoint = redis.GetEndpoint("tcp");
var smtp = mail.GetEndpoint("smtp");

var migrate = builder.AddProject("migrate", In("shop-services/tools/Migrate/Migrate.csproj"))
    .WithReference(catalogDb, "Db")
    .WaitFor(catalogDb);

// The APIs' appsettings.json sets "urls", which beats the ASPNETCORE_URLS Aspire sets, so pass Urls as well.
var catalog = builder.AddProject("catalog-api", In("shop-services/src/Catalog.Api/Catalog.Api.csproj"))
    .WithHttpEndpoint(name: "http")
    .WithHttpHealthCheck("/health")
    .WithReference(catalogDb, "Db")
    .WithEnvironment("Redis__Url", ReferenceExpression.Create($"{redisEndpoint.Property(EndpointProperty.Host)}:{redisEndpoint.Property(EndpointProperty.Port)}"))
    .WithEnvironment("Bus__Url", rabbit.Resource.ConnectionStringExpression)
    .WaitFor(catalogDb).WaitFor(redis).WaitFor(rabbit).WaitForCompletion(migrate);
var catalogHttp = catalog.GetEndpoint("http");
catalog.WithEnvironment("Urls", ReferenceExpression.Create($"http://localhost:{catalogHttp.Property(EndpointProperty.TargetPort)}"));

var orders = builder.AddProject("orders-api", In("shop-services/src/Orders.Api/Orders.Api.csproj"))
    .WithHttpEndpoint(name: "http")
    .WithHttpHealthCheck("/health")
    .WithReference(ordersDb, "Db")
    .WithEnvironment("Bus__Url", rabbit.Resource.ConnectionStringExpression)
    .WithEnvironment("Smtp__Url", ReferenceExpression.Create($"smtp://{smtp.Property(EndpointProperty.Host)}:{smtp.Property(EndpointProperty.Port)}"))
    .WithEnvironment("Catalog__Url", catalogHttp)
    .WaitFor(ordersDb).WaitFor(rabbit).WaitForCompletion(migrate).WaitFor(catalog);
var ordersHttp = orders.GetEndpoint("http");
orders.WithEnvironment("Urls", ReferenceExpression.Create($"http://localhost:{ordersHttp.Property(EndpointProperty.TargetPort)}"));

builder.AddViteApp("web", In("shop-web"))
    .WithEnvironment("VITE_CATALOG_URL", catalogHttp)
    .WithEnvironment("VITE_ORDERS_URL", ordersHttp)
    .WaitFor(catalog).WaitFor(orders);

builder.AddViteApp("mobile", In("shop-mobile"))
    .WithEnvironment("VITE_CATALOG_URL", catalogHttp)
    .WaitFor(catalog);

builder.Build().Run();
