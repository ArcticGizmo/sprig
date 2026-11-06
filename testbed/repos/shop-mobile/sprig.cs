#:package Sprig.Sdk@0.1.0-exp
using Sprig;

var repo = Repo.Describe("shop-mobile");

// shop-services' catalog-api when it runs in place; a workspace or an orchestration can point it elsewhere.
var catalog = repo.Setting<Url>("catalog-url", "http://localhost:5100");

// Ready when Vite says so, rather than when its port first opens.
var mobile = repo.Node("mobile", ".").Http().ReadyOnLog(@"ready in \d+");

mobile.EnvFile(".env.local", e => e
    .Set("PORT", mobile.Port)
    .Set("VITE_CATALOG_URL", catalog));

mobile.Run("npm run dev");

return repo.Emit(args);
