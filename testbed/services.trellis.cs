#:package Sprig.Sdk@0.1.0-exp
using Sprig;

// The monorepo on its own, run where it's checked out: no worktrees, so there's one sprout of it at a time.
var trellis = Trellis.Describe("services");

trellis.Repo("shop-services");

var mail = trellis.Mailpit("mail");
trellis.Bind("mail-smtp", mail.SmtpUrl);

return trellis.Emit(args);
