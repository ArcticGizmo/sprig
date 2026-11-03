#:package Sprig.Sdk@0.1.0-exp
using Sprig;

var repo = Repo.Describe("shop-web");

var catalog = repo.Import<Url>("catalog-url");

var web = repo.Node("web", ".")
    .Http("http", pin: "5170-5173")
    .EnvFile(".env.local", e => e
        .Set("VITE_CATALOG_URL", catalog));

web.Run($"npm run dev -- --port {web.Port}");

return repo.Emit(args);
