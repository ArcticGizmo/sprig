#:package ArcticGizmo.Sprig.Sdk@0.*
using Sprig;

var orchestration = Orchestration.Describe("storefront");

var services = orchestration.Repo("shop-services");
var web      = orchestration.Repo("shop-web");
var mobile   = orchestration.Repo("shop-mobile");

// One mail catcher per workspace, shared by every repo that sends mail.
var mail = orchestration.Mailpit("mail");
orchestration.Bind("mail-smtp", mail.SmtpUrl);

orchestration.Group("web", web, services);
orchestration.Group("mobile", mobile, services.Only("catalog-api"));

return orchestration.Emit(args);
