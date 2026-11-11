#:package ArcticGizmo.Sprig.Sdk@0.*
using Sprig;

var repo = Repo.Describe("shop-web");

// shop-services' APIs when it runs in place; a workspace or an orchestration can point them elsewhere.
var catalog = repo.Setting<Url>("catalog-url", "http://localhost:5100");
var orders  = repo.Setting<Url>("orders-url", "http://localhost:5200");

// Pinned, the way an auth provider's registered callback ports would be.
var web = repo.Node("web", ".").Http(pin: "9170-9179");

web.EnvFile(".env.local", e => e
    .Set("PORT", web.Port)
    .Set("VITE_CATALOG_URL", catalog)
    .Set("VITE_ORDERS_URL", orders));

web.Run("npm run dev");

return repo.Emit(args);
