#:package Sprig.Sdk@0.1.0-exp
using Sprig;

// The monorepo on its own, with a mail catcher for its orders-api.
var trellis = Trellis.Describe("services");

trellis.Repo("shop-services");

var mail = trellis.Mailpit("mail");
trellis.Bind("mail-smtp", mail.SmtpUrl);

return trellis.Emit(args);
