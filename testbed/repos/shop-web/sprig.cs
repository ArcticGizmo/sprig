#:package Sprig.Sdk@0.1.0-exp
using Sprig;

var repo = Repo.Describe("shop-web");

var catalog = repo.Import<Url>("catalog-url");
var orders  = repo.Import<Url>("orders-url");

// Pinned, the way an auth provider's registered callback ports would be.
var web = repo.Node("web", ".").Http(pin: "9170-9179");

web.EnvFile(".env.local", e => e
    .Set("PORT", web.Port)
    .Set("VITE_CATALOG_URL", catalog)
    .Set("VITE_ORDERS_URL", orders));

web.Run("npm run dev");

return repo.Emit(args);
