#:package ArcticGizmo.Sprig.Sdk@0.*
using Sprig;

// The catalog's API and the mobile app, named by their IDs on the Repos list rather than by folder, so this file
// holds no paths. It plans once those IDs are listed (see testbed/README.md): shop-services uses its own sprig.cs,
// and shop-mobile-sprig a copy kept in sprig.
var orchestration = Orchestration.Describe("catalog");

orchestration.UseRepo("shop-services");
orchestration.UseRepo("shop-mobile-sprig");

var mail = orchestration.Mailpit("mail");
orchestration.Bind("mail-smtp", mail.SmtpUrl);

return orchestration.Emit(args);
