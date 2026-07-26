using Sprig.Core.Config;
using Sprig.Core.Planning;

namespace Sprig.Core.Shared;

/// <summary>
/// Applies shared-resource overlays to a <see cref="WorkspacePlan"/>.
///
/// <para>This is the whole mechanism: a pure function from plan to plan. Repos and stacks never reference
/// a shared resource, so nothing in a file you share with your team learns that this machine pools
/// anything. Skip the call and you get exactly the plan sprig would have built without the feature —
/// which is what makes <c>--no-shared</c>, <c>enabled: false</c>, and "your teammate never finds out" all
/// true by construction rather than by care.</para>
///
/// <para>Two rules do the load-bearing work:</para>
/// <list type="bullet">
/// <item><b>A target must already resolve.</b> An overlay replaces values; it does not supply missing ones.
/// A target that isn't there is treated as a repo that has moved on — a hard error, not a silent skip,
/// because the alternative is a connection error three layers from the cause.</item>
/// <item><b>Two overlays may not write the same target.</b> Last-writer-wins in a layer people forget
/// exists is not a trade worth making; a conflict names both resources and stops.</item>
/// </list>
/// </summary>
public static class OverlayEngine
{
    /// <summary>
    /// Return <paramref name="plan"/> with every enabled resource's injections applied, recording one
    /// <see cref="PlanNote"/> per override. Resources that don't reach any repo in the plan are no-ops.
    /// </summary>
    /// <exception cref="SharedResourceException">
    /// A target doesn't resolve, or two resources write the same one.
    /// </exception>
    public static WorkspacePlan Apply(WorkspacePlan plan, IReadOnlyList<SharedResourceDefinition> resources)
    {
        var active = resources.Where(r => r.Enabled).ToList();
        if (active.Count == 0) return plan;

        var repos = plan.Repos.ToList();
        var notes = plan.Notes.ToList();
        // (repo, target) → the resource that claimed it, so a second claim can name both.
        var owners = new Dictionary<(string, string), string>();

        foreach (var resource in active)
        {
            foreach (var inject in resource.Injects)
            {
                var index = repos.FindIndex(r => string.Equals(r.Name, inject.Repo, StringComparison.Ordinal));
                if (index < 0) continue;   // this resource simply doesn't reach this plan

                repos[index] = ApplyToRepo(repos[index], resource, inject, owners, notes);
            }
        }

        return plan with { Repos = repos, Notes = notes };
    }

    static PlannedRepo ApplyToRepo(PlannedRepo repo, SharedResourceDefinition resource,
        ResourceInjection inject, Dictionary<(string, string), string> owners, List<PlanNote> notes)
    {
        var updated = repo with { SharedValues = Publish(repo, resource) };
        updated = ApplyInputs(updated, resource, inject, owners, notes);
        updated = ApplyEnv(updated, resource, inject, owners, notes);
        updated = ApplyCompose(updated, resource, inject, owners, notes);
        return ApplySuppress(updated, resource, inject, owners, notes);
    }

    /// <summary>
    /// Merge this resource's values into the repo's scope under both <c>shared.&lt;key&gt;</c> and
    /// <c>shared.&lt;resource&gt;.&lt;key&gt;</c>. The short form is the one you want to type; the long one
    /// is what disambiguates when two resources inject the same repo. Values stay as raw templates so
    /// substitution still happens in exactly one place, at bind time.
    /// </summary>
    static Dictionary<string, string> Publish(PlannedRepo repo, SharedResourceDefinition resource)
    {
        var values = new Dictionary<string, string>(repo.SharedValues, StringComparer.Ordinal)
        {
            ["repo"] = repo.Name,
        };

        foreach (var (key, template) in resource.Values)
        {
            values[$"shared.{resource.Name}.{key}"] = template;

            // A collision here is genuinely ambiguous, so say which one to qualify rather than picking.
            if (values.TryGetValue($"shared.{key}", out var existing) && existing != template)
                throw new SharedResourceException(
                    $"repo '{repo.Name}' is injected by more than one shared resource and they both publish " +
                    $"'{key}' — reference it as ${{sprig.shared.{resource.Name}.{key}}} instead of " +
                    $"${{sprig.shared.{key}}}");

            values[$"shared.{key}"] = template;
        }

        return values;
    }

    static PlannedRepo ApplyInputs(PlannedRepo repo, SharedResourceDefinition resource,
        ResourceInjection inject, Dictionary<(string, string), string> owners, List<PlanNote> notes)
    {
        if (inject.Inputs.Count == 0) return repo;

        var bindings = new Dictionary<string, string>(repo.Bindings, StringComparer.Ordinal);
        foreach (var (input, expression) in inject.Inputs)
        {
            var target = PlanTargets.Input(input);

            // The preferred layer, but only where the repo actually exposes the value. Introducing an
            // input would make the overlay load-bearing: turn it off and the stack no longer resolves.
            if (!repo.EffectiveConfig.Inputs.Any(i => string.Equals(i.Name, input, StringComparison.Ordinal)))
                throw Missing(resource, repo.Name, target,
                    $"repo '{repo.Name}' doesn't declare an input called '{input}'");

            Claim(owners, resource, repo.Name, target);
            notes.Add(new PlanNote(PlanLayer.Shared, target, expression)
            {
                Repo = repo.Name,
                Replaced = bindings[input],
                Source = resource.Name,
            });
            bindings[input] = expression;
        }

        return repo with { Bindings = bindings };
    }

    static PlannedRepo ApplyEnv(PlannedRepo repo, SharedResourceDefinition resource,
        ResourceInjection inject, Dictionary<(string, string), string> owners, List<PlanNote> notes)
    {
        if (inject.Env.Count == 0) return repo;

        var files = repo.EffectiveConfig.Env.ToList();
        foreach (var injected in inject.Env)
        {
            var index = files.FindIndex(e => SamePath(e.File, injected.File));
            if (index < 0)
            {
                if (!injected.Add)
                    throw Missing(resource, repo.Name, PlanTargets.EnvKey(injected.File, "*"),
                        $"repo '{repo.Name}' doesn't write '{injected.File}' — set \"add\": true to create it");
                files.Add(new EnvOverride { File = injected.File });
                index = files.Count - 1;
            }

            var set = new Dictionary<string, string>(files[index].Set, StringComparer.Ordinal);
            foreach (var (key, template) in injected.Set)
            {
                var target = PlanTargets.EnvKey(injected.File, key);
                if (!set.TryGetValue(key, out var replaced) && !injected.Add)
                    throw Missing(resource, repo.Name, target,
                        $"repo '{repo.Name}' doesn't set '{key}' in '{injected.File}' — it may have been " +
                        "renamed; set \"add\": true if you really mean to introduce it");

                Claim(owners, resource, repo.Name, target);
                notes.Add(new PlanNote(PlanLayer.Shared, target, template)
                {
                    Repo = repo.Name,
                    Replaced = replaced,
                    Source = resource.Name,
                });
                set[key] = template;
            }

            files[index] = files[index] with { Set = set };
        }

        return repo with { EffectiveConfig = repo.EffectiveConfig with { Env = files } };
    }

    static PlannedRepo ApplyCompose(PlannedRepo repo, SharedResourceDefinition resource,
        ResourceInjection inject, Dictionary<(string, string), string> owners, List<PlanNote> notes)
    {
        if (inject.Compose.Count == 0) return repo;

        var files = repo.EffectiveConfig.Compose.ToList();
        foreach (var injected in inject.Compose)
        {
            var index = files.FindIndex(c => SamePath(c.File, injected.File));
            if (index < 0)
                throw Missing(resource, repo.Name, PlanTargets.ComposePath(injected.File, ["*"]),
                    $"repo '{repo.Name}' doesn't declare the compose file '{injected.File}'");

            var overrides = files[index].Overrides.ToList();
            foreach (var over in injected.Overrides)
            {
                var target = PlanTargets.ComposePath(injected.File, over.Path);
                var at = overrides.FindIndex(o => o.Path.SequenceEqual(over.Path, StringComparer.Ordinal));
                if (at < 0 && !injected.Add)
                    throw Missing(resource, repo.Name, target,
                        $"repo '{repo.Name}' doesn't override that path in '{injected.File}' — " +
                        "set \"add\": true to introduce it");

                Claim(owners, resource, repo.Name, target);
                notes.Add(new PlanNote(PlanLayer.Shared, target, over.Template)
                {
                    Repo = repo.Name,
                    Replaced = at < 0 ? null : overrides[at].Template,
                    Source = resource.Name,
                });

                if (at < 0) overrides.Add(over); else overrides[at] = over;
            }

            files[index] = files[index] with { Overrides = overrides };
        }

        return repo with { EffectiveConfig = repo.EffectiveConfig with { Compose = files } };
    }

    static PlannedRepo ApplySuppress(PlannedRepo repo, SharedResourceDefinition resource,
        ResourceInjection inject, Dictionary<(string, string), string> owners, List<PlanNote> notes)
    {
        if (inject.Suppress.Count == 0) return repo;

        var suppressed = repo.Suppress.ToList();
        foreach (var injected in inject.Suppress)
        {
            var declared = repo.EffectiveConfig.Compose.FirstOrDefault(c => SamePath(c.File, injected.File));
            if (declared is null)
                throw Missing(resource, repo.Name, PlanTargets.ComposeService(injected.File, "*"),
                    $"repo '{repo.Name}' doesn't declare the compose file '{injected.File}', so there is " +
                    "nothing to suppress in it");

            foreach (var service in injected.Services)
            {
                var target = PlanTargets.ComposeService(injected.File, service);
                Claim(owners, resource, repo.Name, target);
                notes.Add(new PlanNote(PlanLayer.Shared, target, $"suppressed — provided by {resource.Name}")
                {
                    Repo = repo.Name,
                    Source = resource.Name,
                });
                suppressed.Add(new ComposeSuppression(declared.File, service, resource.Name));
            }
        }

        return repo with { Suppress = suppressed };
    }

    /// <summary>Record who owns a target, refusing a second claim on it.</summary>
    static void Claim(Dictionary<(string, string), string> owners, SharedResourceDefinition resource,
        string repo, string target)
    {
        if (owners.TryGetValue((repo, target), out var first))
            throw new SharedResourceException(
                $"shared resources '{first}' and '{resource.Name}' both override {target} on repo " +
                $"'{repo}'. Two overlays writing one value can't be resolved automatically — disable one, " +
                "or narrow what it injects.");
        owners[(repo, target)] = resource.Name;
    }

    static SharedResourceException Missing(SharedResourceDefinition resource, string repo,
        string target, string detail)
        => new($"shared resource '{resource.Name}' overrides {target} on repo '{repo}', but {detail}. " +
               "An overlay can only replace a value that already resolves, so this is a hard failure " +
               "rather than a silently skipped override.");

    // Repo-relative paths reach the same file whichever slash you wrote, and '.env' vs './.env' is the
    // same file too. Two spellings of one path must not read as two different targets.
    static bool SamePath(string a, string b)
        => string.Equals(Normalize(a), Normalize(b), StringComparison.OrdinalIgnoreCase);

    static string Normalize(string file)
    {
        var path = file.Replace('\\', '/');
        while (path.StartsWith("./", StringComparison.Ordinal)) path = path[2..];
        return path.TrimStart('/');
    }
}
