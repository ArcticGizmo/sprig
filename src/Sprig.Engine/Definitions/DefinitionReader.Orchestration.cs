using Sprig.Model;
using YamlDotNet.RepresentationModel;

namespace Sprig.Engine.Definitions;

public static partial class DefinitionReader
{
    sealed class OrchestrationReader(FileReader file)
    {
        public OrchestrationModel? Read(YamlMappingNode root)
        {
            var top = file.Fields(root, "an orchestration file", DefinitionKeys.Names(DefinitionKeys.Orchestration))!;
            var name = file.Text(top["orchestration"], "the orchestration's name");
            if (name is null) return file.Fail<OrchestrationModel>(root, "the orchestration needs a name: 'orchestration: <name>'");

            var repos = new List<RepoRefModel>();
            var paths = new Dictionary<string, string>(StringComparer.Ordinal);
            foreach (var (repo, key, value) in file.Entries(top["repos"], "repos"))
            {
                if (!file.CheckName(repo, key, "a repo")) continue;
                if (file.Fields(value, $"repo '{repo}'", DefinitionKeys.Names(DefinitionKeys.RepoRef)) is not { } fields) continue;
                var use = file.Text(fields["use"], $"repo '{repo}''s use");
                var path = fields["path"] is { } p ? file.Text(p, $"repo '{repo}''s path")?.Replace('\\', '/') : null;
                if ((use is null) == (path is null))
                {
                    file.Error(key, $"repo '{repo}' needs one of path: (its folder, relative to this file) or use: (its ID on the Repos list)");
                    continue;
                }
                var keyedBy = use ?? path!;
                if (paths.ContainsValue(keyedBy))
                {
                    file.Error(key, $"repo '{repo}' is the same repo as '{paths.First(x => x.Value == keyedBy).Key}'");
                    continue;
                }
                paths[repo] = keyedBy;
                repos.Add(new RepoRefModel(keyedBy, file.At(key), use));
            }
            if (top["repos"] is null) file.Error(root, "an orchestration needs repos: the repos it runs together");

            var scope = new Scope(file) { Repos = paths };
            var (containers, finishContainers) = ReadContainers(file, scope, top["containers"]);
            scope.CheckValues();
            finishContainers();

            var bindings = new List<BindingModel>();
            foreach (var (input, key, value) in file.Entries(top["inputs"], "inputs"))
                if (file.Value(value, $"input '{input}'") is { } raw && scope.Template(raw, value) is { } template)
                    bindings.Add(new BindingModel(input, template, file.At(key)));

            var groups = new List<GroupModel>();
            foreach (var (group, key, value) in file.Entries(top["groups"], "groups"))
            {
                if (!file.CheckName(group, key, "a group")) continue;
                var members = new List<(string Repo, List<string>? Apps)>();
                foreach (var (member, node) in file.List(value, $"group '{group}'"))
                {
                    var slash = member.IndexOf('/');
                    var repo = slash < 0 ? member : member[..slash];
                    if (!paths.TryGetValue(repo, out var path))
                    {
                        file.Error(node, $"'{repo}' isn't one of the orchestration's repos");
                        continue;
                    }
                    var existing = members.FindIndex(m => m.Repo == path);
                    if (slash < 0)
                    {
                        if (existing >= 0) members[existing] = (path, null);
                        else members.Add((path, null));
                    }
                    else if (existing < 0) members.Add((path, [member[(slash + 1)..]]));
                    else members[existing].Apps?.Add(member[(slash + 1)..]);
                }
                groups.Add(new GroupModel(group, members.Select(m => new GroupMemberModel(m.Repo, m.Apps)).ToArray(), file.At(key)));
            }

            var links = new List<LinkModel>();
            foreach (var (label, key, value) in file.Entries(top["links"], "links"))
                if (file.Text(value, $"link '{label}'") is { } raw && scope.Template(raw, value) is { } template)
                    links.Add(new LinkModel(label, template, file.At(key)));

            return new OrchestrationModel(CurrentSchema, name, file.At(root), containers, repos, bindings, groups,
                links.Count > 0 ? links : null);
        }
    }
}
