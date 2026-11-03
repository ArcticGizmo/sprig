#:package Sprig.Sdk@0.1.0-exp
using Sprig;

var trellis = Trellis.Describe("storefront");

var services = trellis.Repo("shop-services");
var web      = trellis.Repo("shop-web");
var mobile   = trellis.Repo("shop-mobile");

// One mail catcher per sprout, shared by every repo that sends mail.
var mail = trellis.Mailpit("mail");
trellis.Bind("mail-smtp", mail.SmtpUrl);

trellis.Worktrees(from: "main");
trellis.Ports(PortPolicy.Stable);

trellis.Group("web", web, services);
trellis.Group("mobile", mobile, services.Only("catalog-api"));

return trellis.Emit(args);
