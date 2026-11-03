#:package Sprig.Sdk@0.1.0-exp
using Sprig;

var repo = Repo.Describe("shop-mobile");

var catalog = repo.Import<Url>("catalog-url");

var mobile = repo.Node("mobile", ".").Http();

mobile.EnvFile(".env.local", e => e
    .Set("PORT", mobile.Port)
    .Set("VITE_CATALOG_URL", catalog));

mobile.Run("npm run dev");

return repo.Emit(args);
