#:package Sprig.Sdk@0.1.0-exp
using Sprig;

var repo = Repo.Describe("shop-mobile");

var catalog = repo.Import<Url>("catalog-url");

// Ready when Vite says so, rather than when its port first opens.
var mobile = repo.Node("mobile", ".").Http().ReadyOnLog(@"ready in \d+");

mobile.EnvFile(".env.local", e => e
    .Set("PORT", mobile.Port)
    .Set("VITE_CATALOG_URL", catalog));

mobile.Run("npm run dev");

return repo.Emit(args);
