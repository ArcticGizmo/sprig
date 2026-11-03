using Shop.Diagnostics;

var builder = WebApplication.CreateBuilder(args);
builder.Services.AddCors(o => o.AddDefaultPolicy(p => p.AllowAnyOrigin().AllowAnyHeader().AllowAnyMethod()));

var app = builder.Build();
app.UseCors();

Product[] products =
[
    new("tea", "Green tea", 4.50m, 12),
    new("mug", "Enamel mug", 9.00m, 4),
    new("pot", "Cast-iron teapot", 32.00m, 2),
];

app.MapGet("/", () => Results.Redirect("/config"));
app.MapGet("/products", () => products);
app.MapGet("/products/{id}", (string id) => products.FirstOrDefault(p => p.Id == id) is { } p ? Results.Ok(p) : Results.NotFound());

app.MapDiagnostics("catalog-api", "urls", "ConnectionStrings:Db", "Redis:Url", "Bus:Url");

app.Run();

record Product(string Id, string Name, decimal Price, int Stock);
