using ADA.Core.Catalog;
using Semver;

namespace ADA.Core.Resolution;

/// <summary>
/// Chooses one version of each mod so that every requested mod and all its dependencies fit
/// together: each dependency's semver range holds, each version supports the game's build (CL) and
/// has a build for the platform. SML is resolved like any other mod. A mod with no build for the
/// platform that the other side of a multiplayer session doesn't need (a graphics mod on a server)
/// is left out and reported as skipped.
/// </summary>
/// <remarks>
/// Two phases: <see cref="ResolveAsync"/> first fetches the versions of every mod that could be
/// needed, then a synchronous backtracking search picks versions. Mod graphs are small (hundreds of
/// mods, shallow dependencies), so a search that tries the most constrained mod first, and preferred
/// then newest stable versions first, finds an answer quickly; a step limit guards the worst case.
/// </remarks>
public sealed class DependencyResolver(IModVersionSource versions)
{
    internal const int DEFAULT_STEP_LIMIT = 200_000;
    private const int PARALLEL_FETCHES = 8;
    private const int MAX_PROBLEMS_REPORTED = 3;

    /// <summary>Search steps before giving up with a "too many version combinations" problem; tests lower it.</summary>
    internal int StepLimit { get; init; } = DEFAULT_STEP_LIMIT;

    /// <summary>
    /// Picks one version of every requested mod and its dependencies that satisfies all constraints
    /// in the request. Never throws for an unsolvable request: invalid references, invalid ranges,
    /// conflicts and reaching <see cref="StepLimit"/> all come back as problems on the result.
    /// </summary>
    public async Task<ResolveResult> ResolveAsync(ResolveRequest request, CancellationToken ct = default)
    {
        var problems = new List<string>();
        var requested = new List<(ModRequirement Requirement, SemVersionRange Range)>();
        foreach (var requirement in request.Requested)
        {
            if (!ModIdentifiers.IsValidReference(requirement.Reference))
            {
                problems.Add($"\"{requirement.Reference}\" is not a valid mod reference.");
            }
            else if (!TryParseRange(requirement.Range ?? "*", out var range))
            {
                problems.Add($"{requirement.Reference}: \"{requirement.Range}\" is not a valid version range.");
            }
            else
            {
                requested.Add((requirement, range));
            }
        }
        if (problems.Count > 0)
        {
            return new ResolveResult([], problems);
        }

        var catalog = await DiscoverAsync(requested.Select(r => r.Requirement.Reference), request, ct);
        return new Search(catalog, request, StepLimit, ct).Run(requested);
    }

    /// <summary>A version that can be used at all: parsed, on the right platform, supporting the game build.</summary>
    private sealed record Candidate(CatalogVersion Version, SemVersion SemVer, IReadOnlyList<Dependency> Dependencies);

    private sealed record Dependency(string Reference, SemVersionRange Range, string Text, bool Optional);

    /// <param name="Rejection">Why no version is usable at all, when <see cref="Candidates"/> is empty.</param>
    /// <param name="NotNeeded">
    /// No build for this platform, and no version that supports the game build is needed on the
    /// other side of a multiplayer session; resolved by leaving the mod out.
    /// </param>
    private sealed record ModCandidates(IReadOnlyList<Candidate> Candidates, string? Rejection, bool NotNeeded = false);

    /// <summary>Fetches every mod reachable through a usable version's dependencies, a layer at a time.</summary>
    private async Task<Dictionary<string, ModCandidates>> DiscoverAsync(IEnumerable<string> roots, ResolveRequest request,
        CancellationToken ct)
    {
        var catalog = new Dictionary<string, ModCandidates>(StringComparer.OrdinalIgnoreCase);
        var layer = roots.Distinct(StringComparer.OrdinalIgnoreCase).ToList();
        while (layer.Count > 0)
        {
            var fetched = new (string Reference, IReadOnlyList<CatalogVersion> Versions)[layer.Count];
            await Parallel.ForEachAsync(Enumerable.Range(0, layer.Count),
                new ParallelOptions { MaxDegreeOfParallelism = PARALLEL_FETCHES, CancellationToken = ct },
                async (i, token) => fetched[i] = (layer[i], await versions.GetVersionsAsync(layer[i], ct: token)));

            var next = new List<string>();
            foreach (var (reference, versions) in fetched)
            {
                var candidates = Filter(reference, versions, request);
                catalog[reference] = candidates;
                next.AddRange(candidates.Candidates
                    .SelectMany(c => c.Dependencies)
                    .Select(d => d.Reference)
                    .Where(r => !catalog.ContainsKey(r)));
            }
            layer = next.Distinct(StringComparer.OrdinalIgnoreCase).Where(r => !catalog.ContainsKey(r)).ToList();
        }
        return catalog;
    }

    private static ModCandidates Filter(string reference, IReadOnlyList<CatalogVersion> versions, ResolveRequest request)
    {
        if (versions.Count == 0)
        {
            return new ModCandidates([], $"{reference} is not on ficsit.app.");
        }

        var usable = new List<Candidate>();
        var noBuild = 0;
        var noBuildForGame = new List<CatalogVersion>();
        var wrongGame = new List<(SemVersion Version, string Range)>();
        foreach (var version in versions)
        {
            if (!SemVersion.TryParse(version.Version, SemVersionStyles.Any, out var semver))
            {
                continue;
            }

            var supportsGame = GameVersionRange.TryParse(version.GameVersionRange, out var game) && game.Contains(request.GameChangelist);
            if (version.TargetFor(request.Platform) is null)
            {
                noBuild++;
                if (supportsGame)
                {
                    noBuildForGame.Add(version);
                }
                continue;
            }
            if (!supportsGame)
            {
                wrongGame.Add((semver, version.GameVersionRange));
                continue;
            }

            var dependencies = new List<Dependency>();
            var valid = true;
            foreach (var dep in version.Dependencies)
            {
                if (!TryParseRange(dep.Condition, out var range))
                {
                    valid = false; // a dependency we can't interpret can't be guaranteed
                    break;
                }
                dependencies.Add(new Dependency(dep.ModReference, range, dep.Condition, dep.Optional));
            }
            if (valid)
            {
                usable.Add(new Candidate(version, semver, dependencies));
            }
        }

        if (usable.Count > 0)
        {
            return new ModCandidates(usable, null);
        }

        // Only left out when no build for this platform exists for the game at all: a mod that
        // just needs a newer game here is still reported as that.
        if (wrongGame.Count == 0 && noBuildForGame.Count > 0)
        {
            return noBuildForGame.All(v => !v.RequiredOnRemote)
                ? new ModCandidates([], null, NotNeeded: true)
                : new ModCandidates([], $"{reference} has no {request.Platform} build. Players need it on the other side too, so it can't be left out.");
        }

        if (wrongGame.Count > 0)
        {
            var newest = wrongGame.MaxBy(w => w.Version, SemVersion.PrecedenceComparer)!;
            return new ModCandidates([], $"No version of {reference} supports game build {request.GameChangelist} " +
                                         $"(the newest, {newest.Version}, needs {newest.Range}).");
        }
        return new ModCandidates([], noBuild > 0
            ? $"{reference} has no {request.Platform} build."
            : $"{reference} has no usable versions.");
    }

    private static bool TryParseRange(string text, out SemVersionRange range)
    {
        try
        {
            // npm leaves pre-releases (3.13.0-beta.1) out of ranges like ^3.12.0; ficsit.app marks betas
            // by stability instead, which the ordering already handles, so keep them in.
            range = SemVersionRange.ParseNpm(string.IsNullOrWhiteSpace(text) ? "*" : text, includeAllPrerelease: true);
            return true;
        }
        catch (FormatException)
        {
            range = null!;
            return false;
        }
    }

    /// <summary>The backtracking search over one discovered catalogue.</summary>
    private sealed class Search(Dictionary<string, ModCandidates> catalog, ResolveRequest request, int stepLimit, CancellationToken ct)
    {
        private sealed record Constraint(SemVersionRange Range, string Text, string From, bool Optional);

        private readonly Dictionary<string, int> _failures = new(StringComparer.Ordinal);
        private int _steps;

        public ResolveResult Run(IReadOnlyList<(ModRequirement Requirement, SemVersionRange Range)> requested)
        {
            var constraints = new Dictionary<string, List<Constraint>>(StringComparer.OrdinalIgnoreCase);
            foreach (var (requirement, range) in requested)
            {
                Add(constraints, requirement.Reference, new Constraint(range, requirement.Range ?? "any version", "requested", false));
            }

            Dictionary<string, Candidate>? solution;
            try
            {
                solution = Solve(new Dictionary<string, Candidate>(StringComparer.OrdinalIgnoreCase), constraints);
            }
            catch (StepLimitException)
            {
                return new ResolveResult([], ["These mods have too many possible version combinations to check. Pin some versions and try again."]);
            }

            if (solution is null)
            {
                var problems = _failures
                    .OrderByDescending(f => f.Value)
                    .Take(MAX_PROBLEMS_REPORTED)
                    .Select(f => f.Key)
                    .ToList();
                return new ResolveResult([], problems.Count > 0 ? problems : ["No combination of versions works."]);
            }

            var requestedNames = requested.Select(r => r.Requirement.Reference).ToHashSet(StringComparer.OrdinalIgnoreCase);
            return new ResolveResult(Order(solution, requestedNames), [], Skipped(solution, requested));
        }

        private Dictionary<string, Candidate>? Solve(Dictionary<string, Candidate> chosen, Dictionary<string, List<Constraint>> constraints)
        {
            ct.ThrowIfCancellationRequested();

            // Next: the still-needed mod with the fewest versions left, so dead ends show up early.
            string? next = null;
            List<Candidate>? options = null;
            foreach (var (reference, list) in constraints)
            {
                if (chosen.ContainsKey(reference) || list.All(c => c.Optional) || IsNotNeeded(reference))
                {
                    continue;
                }

                var viable = Viable(reference, list);
                if (options is null || viable.Count < options.Count)
                {
                    next = reference;
                    options = viable;
                }
                if (viable.Count == 0)
                {
                    break;
                }
            }

            if (next is null)
            {
                return chosen;
            }

            if (options!.Count == 0)
            {
                Fail(Explain(next, constraints[next]));
                return null;
            }

            foreach (var option in options)
            {
                if (++_steps > stepLimit)
                {
                    throw new StepLimitException();
                }

                var from = $"{next} {option.Version.Version}";
                var nextConstraints = Copy(constraints);
                string? conflict = null;
                foreach (var dep in option.Dependencies)
                {
                    Add(nextConstraints, dep.Reference, new Constraint(dep.Range, dep.Text, from, dep.Optional));
                    if (chosen.TryGetValue(dep.Reference, out var picked) && !dep.Range.Contains(picked.SemVer))
                    {
                        conflict = $"{from} needs {dep.Reference} {dep.Text}, but {dep.Reference} {picked.Version.Version} " +
                                   $"is needed by {Describe(constraints[dep.Reference])}.";
                        break;
                    }
                }
                if (conflict is not null)
                {
                    Fail(conflict);
                    continue;
                }

                var nextChosen = new Dictionary<string, Candidate>(chosen, StringComparer.OrdinalIgnoreCase) { [next] = option };
                if (Solve(nextChosen, nextConstraints) is { } solution)
                {
                    return solution;
                }
            }
            return null;
        }

        /// <summary>Usable versions of a mod that satisfy every constraint on it, best first.</summary>
        private List<Candidate> Viable(string reference, List<Constraint> constraints)
        {
            if (!catalog.TryGetValue(reference, out var mod))
            {
                return [];
            }

            var preferred = request.Preferred?.GetValueOrDefault(reference);
            return mod.Candidates
                .Where(c => constraints.All(k => k.Range.Contains(c.SemVer)))
                .OrderByDescending(c => c.Version.Version == preferred)
                .ThenBy(c => c.Version.Stability) // Release, then Beta, then Alpha
                .ThenByDescending(c => c.SemVer, SemVersion.PrecedenceComparer)
                .ToList();
        }

        private bool IsNotNeeded(string reference) => catalog.TryGetValue(reference, out var mod) && mod.NotNeeded;

        /// <summary>
        /// Not-needed mods that the request or a chosen version requires, with who asked. Constraints
        /// from versions the search tried and dropped don't count, so the list matches the plan.
        /// </summary>
        private List<SkippedMod> Skipped(Dictionary<string, Candidate> chosen,
            IReadOnlyList<(ModRequirement Requirement, SemVersionRange Range)> requested)
        {
            var askedBy = new Dictionary<string, List<string>>(StringComparer.OrdinalIgnoreCase);
            foreach (var (requirement, _) in requested.Where(r => IsNotNeeded(r.Requirement.Reference)))
            {
                AskedBy(requirement.Reference).Add("requested");
            }
            foreach (var (reference, candidate) in chosen)
            {
                foreach (var dep in candidate.Dependencies.Where(d => !d.Optional && IsNotNeeded(d.Reference)))
                {
                    AskedBy(dep.Reference).Add($"{reference} {candidate.Version.Version}");
                }
            }
            return askedBy
                .OrderBy(a => a.Key, StringComparer.OrdinalIgnoreCase)
                .Select(a => new SkippedMod(a.Key, a.Value.OrderByDescending(r => r == "requested").ThenBy(r => r, StringComparer.Ordinal).ToList()))
                .ToList();

            List<string> AskedBy(string reference)
            {
                if (!askedBy.TryGetValue(reference, out var list))
                {
                    askedBy[reference] = list = [];
                }

                return list;
            }
        }

        private string Explain(string reference, List<Constraint> constraints)
        {
            if (!catalog.TryGetValue(reference, out var mod) || mod.Candidates.Count == 0)
            {
                var needed = constraints.Where(c => c.From != "requested").Select(c => c.From).Distinct().ToList();
                var reason = mod?.Rejection ?? $"{reference} is not on ficsit.app.";
                return needed.Count > 0 ? $"{reason} It is needed by {string.Join(", ", needed)}." : reason;
            }

            var newest = mod.Candidates.MaxBy(c => c.SemVer, SemVersion.PrecedenceComparer)!.Version.Version;

            // A requirement no usable version meets on its own is the whole story; name it.
            var impossible = constraints.Where(c => !mod.Candidates.Any(v => c.Range.Contains(v.SemVer))).ToList();
            if (impossible.Count > 0)
            {
                var first = impossible[0];
                var who = first.From == "requested" ? "Your selection" : first.From;
                return $"{who} needs {reference} {first.Text}, but no {reference} that supports game build " +
                       $"{request.GameChangelist} matches (the newest that does is {newest}).";
            }

            // Otherwise the requirements only clash in combination.
            return $"No version of {reference} satisfies all of: " +
                   $"{string.Join("; ", constraints.Select(c => $"{c.Text} (for {(c.From == "requested" ? "your selection" : c.From)})"))}.";
        }

        private static string Describe(IEnumerable<Constraint> constraints)
        {
            var sources = constraints.Select(c => c.From == "requested" ? "your selection" : c.From).Distinct().ToList();
            return sources.Count == 1 ? sources[0] : string.Join(" and ", sources);
        }

        private void Fail(string message) => _failures[message] = _failures.GetValueOrDefault(message) + 1;

        /// <summary>Dependencies before the mods that need them; otherwise by name, so the order is stable.</summary>
        private static List<ResolvedMod> Order(Dictionary<string, Candidate> chosen, HashSet<string> requested)
        {
            var requiredBy = chosen.Keys.ToDictionary(k => k, _ => new List<string>(), StringComparer.OrdinalIgnoreCase);
            foreach (var (reference, candidate) in chosen)
            {
                foreach (var dep in candidate.Dependencies.Where(d => chosen.ContainsKey(d.Reference)))
                {
                    requiredBy[dep.Reference].Add($"{reference} {candidate.Version.Version}");
                }
            }

            var ordered = new List<ResolvedMod>();
            var done = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            var visiting = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (var reference in chosen.Keys.Order(StringComparer.OrdinalIgnoreCase))
            {
                Visit(reference);
            }

            return ordered;

            void Visit(string reference)
            {
                // A dependency cycle cannot be ordered strictly; break it where it is found.
                if (done.Contains(reference) || !visiting.Add(reference))
                {
                    return;
                }

                foreach (var dep in chosen[reference].Dependencies
                             .Select(d => d.Reference)
                             .Where(chosen.ContainsKey)
                             .Order(StringComparer.OrdinalIgnoreCase))
                {
                    Visit(dep);
                }

                visiting.Remove(reference);
                done.Add(reference);
                var reasons = requiredBy[reference].Order(StringComparer.Ordinal).ToList();
                if (requested.Contains(reference))
                {
                    reasons.Insert(0, "requested");
                }

                ordered.Add(new ResolvedMod(chosen[reference].Version, reasons));
            }
        }

        private static void Add(Dictionary<string, List<Constraint>> constraints, string reference, Constraint constraint)
        {
            if (!constraints.TryGetValue(reference, out var list))
            {
                constraints[reference] = list = [];
            }

            list.Add(constraint);
        }

        private static Dictionary<string, List<Constraint>> Copy(Dictionary<string, List<Constraint>> constraints) =>
            constraints.ToDictionary(kv => kv.Key, kv => new List<Constraint>(kv.Value), StringComparer.OrdinalIgnoreCase);
    }

    private sealed class StepLimitException : Exception;
}
