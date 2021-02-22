﻿using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Text.RegularExpressions;

namespace GitImporter
{
    public class RawHistoryBuilder
    {
        public static TraceSource Logger = Program.Logger;

        private const int MaxDelay = 60 * 30;
        private static readonly ChangeSet.TimeComparer TimeComparer = new ChangeSet.TimeComparer();

        private readonly Dictionary<string, Element> _elementsByOid;
        private List<Regex> _branchFilters;

        // ChangeSets, grouped first by branch, then by author
        private Dictionary<string, Dictionary<string, List<ChangeSet>>> _changeSets;

        /// <summary>
        /// For each branch, its parent branch
        /// </summary>
        public Dictionary<string, string> GlobalBranches { get; private set; }

        public Dictionary<string, LabelInfo> Labels { get; private set; }

        public HashSet<string> Roots { get; set; }

        public List<string> RelativeRoots { get; set; }

        public string ClearcaseRoot { get; set; }


        public RawHistoryBuilder(VobDB vobDB)
        {
            if (vobDB != null)
                _elementsByOid = vobDB.ElementsByOid;
            Labels = new Dictionary<string, LabelInfo>();
        }

        public void SetClearcaseRoot(string root)
        {
            ClearcaseRoot = root.Replace("\\", "/");
        }

        public void SetRoots(HashSet<string> roots)
        {
            Roots = roots;
        }

        public void SetRelativeRoots(List<string> roots)
        {
            RelativeRoots = roots;
        }

        public void SetBranchFilters(string[] branches)
        {
            if (branches != null && branches.Length > 0)
                _branchFilters = branches.Select(b => new Regex(b)).ToList();
        }

        public List<ChangeSet> Build(List<ElementVersion> newVersions)
        {
            var allElementBranches = CreateRawChangeSets(newVersions);
            ComputeGlobalBranches(allElementBranches);
            FilterBranches();
            FilterLabels();
            List<ChangeSet> changes = _changeSets.Values.SelectMany(d => d.Values.SelectMany(l => l)).ToList();
            Logger.TraceData(TraceEventType.Start | TraceEventType.Information, (int)TraceId.CreateChangeSet, "Start sorting raw ChangeSets of size", changes.Count);
            changes.Sort(TimeComparer);
            Logger.TraceData(TraceEventType.Stop | TraceEventType.Information, (int)TraceId.CreateChangeSet, "Stop sorting raw ChangeSets");
            return changes;
        }

        private string RemoveDotRoot(string path)
        {
            if (path.StartsWith(ClearcaseRoot))
            {
                path = path.Substring(ClearcaseRoot.Length);
            }
            if (path.StartsWith("/")) {
                path = path.Substring(1);
            }
            return path.StartsWith("./") ? path.Substring(2) : path;
        }

        private IEnumerable<string> CreateRawChangeSets(IEnumerable<ElementVersion> newVersions)
        {
            Logger.TraceData(TraceEventType.Start | TraceEventType.Information, (int)TraceId.CreateChangeSet, "Start creating raw ChangeSets");
            // the list must always be kept sorted, so that BinarySearch works
            // if the size of the list gets too big and (mostly) linear insertion time becomes a problem,
            // we could look at SortedList<> (which is not actually a list, but a dictionary)
            _changeSets = new Dictionary<string, Dictionary<string, List<ChangeSet>>>();
            // keep all FullName's, so that we can try to guess "global" BranchingPoint
            var allElementBranches = new HashSet<string>();
            if (newVersions != null)
            {
                var allNewVersions = new HashSet<ElementVersion>(newVersions);
                foreach (var version in newVersions)
                {
                    if (!InsideRoot(version.Element))
                    {
                        Logger.TraceData(TraceEventType.Verbose, (int)TraceId.CreateChangeSet, "Skipping version not inside root", version);
                        continue;
                    }
                    allElementBranches.Add(version.Branch.FullName);
                    Dictionary<string, List<ChangeSet>> branchChangeSets;
                    if (!_changeSets.TryGetValue(version.Branch.BranchName, out branchChangeSets))
                    {
                        branchChangeSets = new Dictionary<string, List<ChangeSet>>();
                        _changeSets.Add(version.Branch.BranchName, branchChangeSets);
                    }
                    ProcessVersion(version, branchChangeSets, allNewVersions);
                }
            }
            else
            {
                foreach (var element in _elementsByOid.Values)
                {
                    if (!InsideRoot(element))
                    {
                        Logger.TraceData(TraceEventType.Verbose, (int)TraceId.CreateChangeSet, "Skipping element not inside root", element);
                        continue;
                    }
                    foreach (var branch in element.Branches.Values)
                    {
                        allElementBranches.Add(branch.FullName);
                        Dictionary<string, List<ChangeSet>> branchChangeSets;
                        if (!_changeSets.TryGetValue(branch.BranchName, out branchChangeSets))
                        {
                            branchChangeSets = new Dictionary<string, List<ChangeSet>>();
                            _changeSets.Add(branch.BranchName, branchChangeSets);
                        }
                        foreach (var version in branch.Versions)
                            ProcessVersion(version, branchChangeSets, null);
                    }
                }
            }

            Logger.TraceData(TraceEventType.Stop | TraceEventType.Information, (int)TraceId.CreateChangeSet, "Stop creating raw ChangeSets");
            return allElementBranches;
        }

        private bool InsideRoot(Element element)
        {
            var name = element.Name.Replace("\\", "/");
            string normalizedName;
            if (name.StartsWith(ClearcaseRoot + "/.@@/main/"))
            {
                // hidden. take special care
                name = name.Substring((ClearcaseRoot + "/.@@/main/").Length);
                var match = Regex.Match(name, "([0-9]+?)/([^0-9/]+)");
                if (!match.Success)
                {
                    throw new Exception("Failed find version for " + element.Name);
                }
                normalizedName = match.Groups[2].Value + "/";
            }
            else
            {
                // visible. nothing special here
                normalizedName = RemoveDotRoot(name);
            }
            return null != RelativeRoots.Find(
                r =>  {
                    Logger.TraceData(TraceEventType.Verbose, (int)TraceId.CreateChangeSet, "Comparing in raw", normalizedName, "vs", r);
                    return normalizedName == "." || normalizedName == r || normalizedName.StartsWith(r + "/") || normalizedName.StartsWith(r + "@@/");
                }
            );
        }

        private void ProcessVersion(ElementVersion version, Dictionary<string, List<ChangeSet>> branchChangeSets, HashSet<ElementVersion> newVersions)
        {
            // we don't really handle versions 0 on branches : always consider BranchingPoint
            ElementVersion versionForLabel = version;
            while (versionForLabel.VersionNumber == 0 && versionForLabel.Branch.BranchingPoint != null)
                versionForLabel = versionForLabel.Branch.BranchingPoint;
            // don't "move" the label on versions that won't be processed (we need to assume these are correct)
            if (newVersions == null || newVersions.Contains(versionForLabel))
            {
                foreach (var label in version.Labels)
                {
                    LabelInfo labelInfo;
                    if (!Labels.TryGetValue(label, out labelInfo))
                    {
                        labelInfo = new LabelInfo(label);
                        Labels.Add(label, labelInfo);
                    }
                    labelInfo.Versions.Add(versionForLabel);
                    // also actually "move" the label
                    if (versionForLabel != version)
                        versionForLabel.Labels.Add(label);
                }
            }
            // end of label move
            if (versionForLabel != version)
                version.Labels.Clear();
            if (version.VersionNumber == 0 && (version.Element.IsDirectory || version.Branch.BranchName != "main"))
                return;
            List<ChangeSet> authorChangeSets;
            if (!branchChangeSets.TryGetValue(version.AuthorLogin, out authorChangeSets))
            {
                authorChangeSets = new List<ChangeSet>();
                branchChangeSets.Add(version.AuthorLogin, authorChangeSets);
            }
            AddVersion(authorChangeSets, version);
        }

        private static void AddVersion(List<ChangeSet> changeSets, ElementVersion version)
        {
            // used either for search or for new ChangeSet
            var changeSet = new ChangeSet(version.AuthorName, version.AuthorLogin, version.Branch.BranchName, version.Date);
            if (changeSets.Count == 0)
            {
                changeSet.Add(version);
                changeSets.Add(changeSet);
                return;
            }

            int index = changeSets.BinarySearch(changeSet, TimeComparer);
            if (index >= 0)
            {
                changeSets[index].Add(version);
                return;
            }

            index = ~index; // index of first element bigger
            if (index == changeSets.Count)
            {
                // so even the last one is not bigger
                ChangeSet candidate = changeSets[index - 1];
                if (version.Date <= candidate.FinishTime.AddSeconds(MaxDelay))
                    candidate.Add(version);
                else
                {
                    changeSet.Add(version);
                    changeSets.Add(changeSet);
                }
                return;
            }
            if (index == 0)
            {
                ChangeSet candidate = changeSets[0];
                if (version.Date >= candidate.StartTime.AddSeconds(-MaxDelay))
                    candidate.Add(version);
                else
                {
                    changeSet.Add(version);
                    changeSets.Insert(0, changeSet);
                }
                return;
            }
            DateTime lowerBound = changeSets[index - 1].FinishTime;
            DateTime upperBound = changeSets[index].StartTime;
            if (version.Date <= lowerBound.AddSeconds(MaxDelay) && version.Date < upperBound.AddSeconds(-MaxDelay))
            {
                changeSets[index - 1].Add(version);
                return;
            }
            if (version.Date > lowerBound.AddSeconds(MaxDelay) && version.Date >= upperBound.AddSeconds(-MaxDelay))
            {
                changeSets[index].Add(version);
                return;
            }
            if (version.Date > lowerBound.AddSeconds(MaxDelay) && version.Date < upperBound.AddSeconds(-MaxDelay))
            {
                changeSet.Add(version);
                changeSets.Insert(index, changeSet);
                return;
            }
            // last case : we should merge the two ChangeSets (that are now "linked" by the version we are adding)
            changeSets[index - 1].Add(version);
            foreach (var v in changeSets[index].Versions)
                changeSets[index - 1].Add(v.Version);
            changeSets.RemoveAt(index);
        }

        private void ComputeGlobalBranches(IEnumerable<string> allElementBranches)
        {
            var allPotentialParents = new Dictionary<string, HashSet<string>>();
            foreach (var branch in allElementBranches)
            {
                var path = branch.Split('\\');
                // add all hierarchy to account for incremental import, where we may have a version on a branch, but none on its parent
                for (int i = 1; i < path.Length; i++)
                    allPotentialParents.AddToCollection(path[i], path[i - 1]);
            }
            RemoveCycles(allPotentialParents);
            var depths = allPotentialParents.Keys.ToDictionary(s => s, unused => 0);
            depths["main"] = 1;
            bool finished = false;
            while (!finished)
            {
                finished = true;
                foreach (var pair in allPotentialParents)
                {
                    int depth = pair.Value.Max(p => depths[p]) + 1;
                    if (depth > depths[pair.Key])
                    {
                        finished = false;
                        depths[pair.Key] = depth;
                    }
                }
            }
            GlobalBranches = new Dictionary<string, string>();
            GlobalBranches["main"] = null;
            foreach (var pair in allPotentialParents)
            {
                var maxDepth = pair.Value.Max(p => depths[p]);
                var candidates = pair.Value.Where(p => depths[p] == maxDepth).ToList();
                if (candidates.Count > 1)
                    Logger.TraceData(TraceEventType.Warning, (int)TraceId.CreateChangeSet,
                                     "Branch " + pair.Key + " parent is ambiguous between " + string.Join(" and ", candidates) +
                                     ", choosing " + candidates[0]);
                GlobalBranches[pair.Key] = candidates[0];
            }
        }

        private class CycleComparer : IEqualityComparer<List<string>>
        {
            public bool Equals(List<string> x, List<string> y)
            {
                if (x == null)
                    return y == null;
                for (int i = 0; i < x.Count; i++)
                    if (x.Skip(i).Concat(x.Take(i)).SequenceEqual(y))
                        return true;
                return false;
            }

            public int GetHashCode(List<string> obj)
            {
                return obj.Aggregate(0, (i, s) => i ^ s.GetHashCode());
            }
        }

        private void RemoveCycles(Dictionary<string, HashSet<string>> allPotentialParents)
        {
            var allCycles = FindAllCycles(allPotentialParents);
            // not efficient, but having cycles is a bad (and hopefully rare) situation to begin with
            while (allCycles.Count > 0)
            {
                // first find the link that would break as many cycles as possible
                var allPairs = allCycles
                    .SelectMany(cycle => cycle.Zip(cycle.Skip(1).Concat(new[] { cycle[0] }), Tuple.Create))
                    .GroupBy(p => p)
                    .Select(g => Tuple.Create(g.Key, g.Count()))
                    .OrderByDescending(g => g.Item2).ToList();
                var candidates = allPairs.TakeWhile(t => t.Item2 == allPairs[0].Item2).Select(p => p.Item1);

                // then break at the branch with the most potential parents
                var toBreak = candidates.OrderByDescending(p => allPotentialParents[p.Item1].Count).First();
                Logger.TraceData(TraceEventType.Warning, (int)TraceId.CreateChangeSet,
                    "Branch cycle(s) : " +
                    string.Join(" ; ", allCycles.Select(cycle => string.Join(" -> ", cycle.Concat(new[] { cycle[0] })))) +
                    ", breaking by removing " + toBreak.Item2 + " as a potential parent of " + toBreak.Item1);
                allPotentialParents[toBreak.Item1].Remove(toBreak.Item2);

                allCycles = FindAllCycles(allPotentialParents);
            }
        }

        private HashSet<List<string>> FindAllCycles(Dictionary<string, HashSet<string>> allPotentialParents)
        {
            return new HashSet<List<string>>(allPotentialParents.Keys
                .SelectMany(k => FindCycles(k, new List<string>(), allPotentialParents)),
                new CycleComparer());
        }

        private List<List<string>> FindCycles(string toCheck, List<string> currentChain, Dictionary<string, HashSet<string>> allPotentialParents)
        {
            if (toCheck == "main")
                return new List<List<string>>();
            if (currentChain.Contains(toCheck))
                return new List<List<string>> {new List<string>(currentChain.SkipWhile(s => s != toCheck)) };

            var newChain = new List<string>(currentChain) { toCheck };
            return allPotentialParents[toCheck].SelectMany(p => FindCycles(p, newChain, allPotentialParents)).ToList();
        }

        private void FilterBranches()
        {
            var relativeRoots = ComputeRelativeRoots();
            var branchesToRemove = new HashSet<string>();

            if (_branchFilters != null && _branchFilters.Count != 0)
            {
                branchesToRemove.UnionWith(GlobalBranches.Keys.Where(b => b != "main" && !_branchFilters.Exists(r => r.IsMatch(b))));
            }
            bool finished = false;
            while (!finished)
            {
                finished = true;
                foreach (var toRemove in branchesToRemove)
                {
                    // only branches from which no non-filtered branches spawn can be removed
                    if (GlobalBranches.ContainsKey(toRemove) && !GlobalBranches.Values.Contains(toRemove))
                    {
                        Logger.TraceData(TraceEventType.Information, (int)TraceId.CreateChangeSet, "Branch " + toRemove + " filtered out");
                        finished = false;
                        GlobalBranches.Remove(toRemove);
                        _changeSets.Remove(toRemove);
                    }
                }
            }
        }

        private void FilterLabels()
        {
            var labelsToRemove = Labels.Values
                .Where(l => l.Versions.Exists(v => !GlobalBranches.ContainsKey(v.Branch.BranchName)))
                .Select(l => l.Name).ToList();
            foreach (var toRemove in labelsToRemove)
            {
                Logger.TraceData(TraceEventType.Information, (int)TraceId.CreateChangeSet, "Label " + toRemove + " filtered : was on a filtered out branch");
                Labels.Remove(toRemove);
            }

            var relativeRoots = ComputeRelativeRoots();
            labelsToRemove = Labels.Values
                .Where(l => {
                    // only filter this label if all the versions are not in a root
                    return null == l.Versions.Find(v => {
                        int count = relativeRoots.Where(r => {
                            string name = v.Element.Name;
                            if (name.StartsWith(ClearcaseRoot))
                            {
                                name = name.Substring(ClearcaseRoot.Length);
                            }
                            if (name.StartsWith("\\"))
                            {
                                name = name.Substring(1);
                            }
                            return name.StartsWith(r);
                        }).Count();
                        if (count > 1) {
                            // sanity check
                            throw new Exception("ambiguous roots for " + v);
                        }
                        return count == 1;
                    });
                })
                .Select(l => l.Name).ToList();
            foreach (var toRemove in labelsToRemove)
            {
                Logger.TraceData(TraceEventType.Information, (int)TraceId.CreateChangeSet, "Label " + toRemove + " filtered : not used in any root");
                Labels.Remove(toRemove);
            }

            foreach (var label in Labels.Values)
                label.Reset();
        }

        private List<string> ComputeRelativeRoots()
        {
            return Roots.Where(r => r != ".").Select(r => {
                if (r.StartsWith(ClearcaseRoot)) {
                    r = r.Substring(ClearcaseRoot.Length);
                }
                if (r.StartsWith("\\"))
                {
                    r = r.Substring(1);
                }
                return r;
            }).ToList();
        }
    }
}
