using System.Collections.Concurrent;
using System.Net.Mail;
using Shop.Diagnostics;

var builder = WebApplication.CreateBuilder(args);
builder.Services.AddCors(o => o.AddDefaultPolicy(p => p.AllowAnyOrigin().AllowAnyHeader().AllowAnyMethod()));
builder.Services.AddHttpClient("catalog", (sp, c) =>
    c.BaseAddress = new Uri(sp.GetRequiredService<IConfiguration>()["Catalog:Url"] ?? "http://localhost:5100"));

var app = builder.Build();
app.UseCors();

var orders = new ConcurrentQueue<Order>();

app.MapGet("/", () => Results.Redirect("/config"));
app.MapGet("/orders", () => orders.ToArray());

// Checks the product with catalog-api (a sibling in this repo), then emails a confirmation through the SMTP server
// the system supplies. Either can fail without losing the order, and the response says which did.
app.MapPost("/orders", async (NewOrder input, IHttpClientFactory http, IConfiguration config) =>
{
    var catalog = http.CreateClient("catalog");
    HttpResponseMessage product;
    try { product = await catalog.GetAsync($"/products/{Uri.EscapeDataString(input.ProductId)}"); }
    catch (HttpRequestException e) { return Results.Problem($"couldn't reach catalog-api at {catalog.BaseAddress}: {e.Message}", statusCode: 502); }
    if (!product.IsSuccessStatusCode) return Results.NotFound(new { error = $"no product '{input.ProductId}'" });

    var order = new Order(Guid.NewGuid().ToString("N")[..8], input.ProductId, input.Quantity, input.Email, DateTimeOffset.UtcNow);
    orders.Enqueue(order);

    string? mailError = null;
    try { await SendConfirmationAsync(config["Smtp:Url"], order); }
    catch (Exception e) when (e is SmtpException or InvalidOperationException or IOException) { mailError = e.Message; }

    return Results.Ok(new { order, mailSent = mailError is null, mailError });
});

app.MapDiagnostics("orders-api", "urls", "ConnectionStrings:Db", "Bus:Url", "Smtp:Url", "Catalog:Url");

app.Run();

static async Task SendConfirmationAsync(string? smtpUrl, Order order)
{
    var target = Probe.Find(smtpUrl) ?? throw new InvalidOperationException("Smtp:Url isn't set");
    using var smtp = new SmtpClient(target.Host, target.Port) { EnableSsl = false };
    using var mail = new MailMessage("orders@shop.example", order.Email,
        $"Order {order.Id} confirmed", $"{order.Quantity} × {order.ProductId}, placed {order.PlacedAt:u}.");
    await smtp.SendMailAsync(mail);
}

record NewOrder(string ProductId, int Quantity, string Email);

record Order(string Id, string ProductId, int Quantity, string Email, DateTimeOffset PlacedAt);
