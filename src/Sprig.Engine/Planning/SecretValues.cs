using System.Text.RegularExpressions;

namespace Sprig.Engine.Planning;

/// <summary>
/// What of a plan's values sprig can show. A value is secret when an input of <c>type: secret</c> gives it (or any part
/// of it), or its name says so (<c>API_KEY</c>, <c>Auth__ClientSecret</c>). Anything else has its passwords hidden
/// (<see cref="ValueMask"/>). Output that leaves the screen, such as <c>--json</c>, goes further and keeps only values
/// that are plainly addresses, ports or flags (<see cref="Trimmed"/>), since a secret the definition doesn't declare
/// can't be told from any other text.
/// </summary>
public static partial class SecretValues
{
    public const string Hidden = "••••••";

    /// <summary>Stands in for a value <c>--json</c> leaves out; null already means a value couldn't be worked out.</summary>
    public const string LeftOut = "(not shown: --unmask-secrets shows it)";

    /// <summary>The name says it's a secret, whatever separators and case it uses.</summary>
    public static bool LooksSecret(string key) => SecretName().IsMatch(Regex.Replace(key, "[^A-Za-z0-9]", ""));

    [GeneratedRegex("password|passwd|pwd|secret|token|apikey|privatekey|accesskey|credential", RegexOptions.IgnoreCase)]
    private static partial Regex SecretName();

    /// <summary>A port, a flag or an address: worth showing, and nothing that could be a secret once
    /// <see cref="ValueMask"/> has had its passwords.</summary>
    public static bool IsPlain(string value)
        => value.Length == 0 || int.TryParse(value, out _) || bool.TryParse(value, out _) || WorkspaceTopology.LooksLikeAddress(value);

    /// <summary>A setting's value, by the setting's name.</summary>
    public static bool IsSecret(Plan? plan, string setting) => plan?.IsSecretSetting(setting) == true || LooksSecret(setting);

    public static bool IsSecret(Plan plan, PlannedEnv env)
        => LooksSecret(env.Key) || (env.Value.Sources ?? []).Any(s => s.Setting is { } name && plan.IsSecretSetting(name));

    /// <summary>For the screen: hidden when secret, else with its passwords masked.</summary>
    public static string Show(Plan plan, PlannedEnv env, string value) => IsSecret(plan, env) ? Hidden : ValueMask.Apply(value);

    /// <summary>For the screen: a setting's value, by the setting's name.</summary>
    public static string Show(Plan? plan, string setting, string value)
        => value.Length > 0 && IsSecret(plan, setting) ? Hidden : ValueMask.Apply(value);

    /// <summary>For output that leaves the screen: hidden when secret, kept (with passwords masked) when plain, else
    /// <see cref="LeftOut"/>. Null stays null.</summary>
    public static string? Trim(string? value, bool secret)
        => value is null ? null : secret && value.Length > 0 ? Hidden : IsPlain(value) ? ValueMask.Apply(value) : LeftOut;

    /// <summary>
    /// <paramref name="plan"/> as <c>--json</c> shows it: every value, and the template it came from, put through
    /// <see cref="Trim"/>. Where each value came from (its sources and how it was reached) stays.
    /// </summary>
    public static Plan Trimmed(Plan plan)
    {
        PlannedValue Value(PlannedValue v, bool secret) => v with
        {
            Value = Trim(v.Value, secret),
            // A template that's only references (e.g. "{api.http.url}") holds no value of its own, so it's kept.
            Template = !secret && v.Template.Contains('{') && !IsPlain(v.Template) && Literal().Replace(v.Template, "").Length == 0
                ? v.Template : Trim(v.Template, secret)!,
            Via = v.Via.Select(ValueMask.Apply).ToArray(),
        };
        PlannedEnv Env(PlannedEnv e) => e with { Value = Value(e.Value, IsSecret(plan, e)) };

        return plan with
        {
            Apps = plan.Apps.Select(a => a with
            {
                Env = a.Env.Select(Env).ToArray(),
                // A command can't carry values (the definition says so), so only its passwords need hiding.
                Run = a.Run is { } run ? run with { Value = run.Value is { } r ? ValueMask.Apply(r) : null, Template = ValueMask.Apply(run.Template) } : null,
                Links = a.Links?.Select(l => l with { Url = ValueMask.Apply(l.Url) }).ToArray(),
            }).ToArray(),
            Containers = plan.Containers.Select(c => c with { Env = c.Env.Select(Env).ToArray() }).ToArray(),
            Compose = plan.Compose.Select(c => c with
            {
                Overrides = c.Overrides.Select(o => o with { Value = Value(o.Value, LooksSecret(o.Path)) }).ToArray(),
            }).ToArray(),
            Links = plan.Links?.Select(l => l with { Url = ValueMask.Apply(l.Url) }).ToArray(),
        };
    }

    [GeneratedRegex(@"\{[^{}]*\}")]
    private static partial Regex Literal();

    /// <summary>Values set on a workspace, by setting name, as <c>--json</c> shows them (see <see cref="Trim"/>).</summary>
    public static IReadOnlyDictionary<string, string>? Trimmed(IReadOnlyDictionary<string, string>? values, Plan? plan)
        => values?.ToDictionary(v => v.Key, v => Trim(v.Value, IsSecret(plan, v.Key))!);
}
