#:package Sprig.Sdk@0.1.0-exp
using Sprig;

// The catalog's API and the mobile app, named by their IDs on the Repos list rather than by folder, so this file
// holds no paths. It plans once those IDs are listed (see testbed/README.md): shop-services uses its own sprig.cs,
// and shop-mobile-sprig a copy kept in sprig.
var trellis = Trellis.Describe("catalog");

trellis.UseRepo("shop-services");
trellis.UseRepo("shop-mobile-sprig");

var mail = trellis.Mailpit("mail");
trellis.Bind("mail-smtp", mail.SmtpUrl);

return trellis.Emit(args);
