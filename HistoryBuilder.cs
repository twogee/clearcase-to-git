﻿using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Text.RegularExpressions;
using ProtoBuf;

namespace GitImporter
{
    [ProtoContract]
    public class HistoryBuilder
    {
        public static TraceSource Logger = Program.Logger;

        private RawHistoryBuilder _rawHistoryBuilder;
        [ProtoMember(1)]
        private string[] _branchFilters;

        /// <summary>
        /// _roots are directories whose parents have not been requested :
        /// they will therefore never appear in the Content of a DirectoryVersion
        /// </summary>
        [ProtoMember(2)]
        private readonly HashSet<string> _roots = new HashSet<string>();

        /// <summary>
        /// For each branch, its parent branch
        /// </summary>
        [ProtoMember(3)]
        private Dictionary<string, string> _globalBranches;
        private Dictionary<string, LabelInfo> _labels;

        private List<ChangeSet> _flattenChangeSets;
        [ProtoMember(4)]
        private int _lastId = 0;
        private int _currentIndex = 0;
        private readonly Dictionary<string, ChangeSet> _startedBranches = new Dictionary<string, ChangeSet>();
        [ProtoMember(5)]
        private Dictionary<string, int> _rawStartedBranches;

        private readonly Dictionary<Tuple<string, string>, MergeInfo> _merges = new Dictionary<Tuple<string, string>, MergeInfo>();
        
        /// <summary>
        /// an element may appear under different names, especially during a move,
        /// if the destination directory has been checked in before source directory
        /// </summary>
        private readonly Dictionary<string, Dictionary<Element, HashSet<string>>> _elementsNamesByBranch = new Dictionary<string, Dictionary<Element, HashSet<string>>>();
        /// <summary>
        /// some moves (rename) may be in separate ChangeSets, we must be able to know what version to write at the new location
        /// </summary>
        private readonly Dictionary<string, Dictionary<Element, ElementVersion>> _elementsVersionsByBranch = new Dictionary<string, Dictionary<Element, ElementVersion>>();
        [ProtoMember(6)]
        private Dictionary<string, Dictionary<string, List<string>>> _rawElementsNamesByBranch;
        [ProtoMember(7)]
        private Dictionary<string, Dictionary<string, ElementVersion.Reference>> _rawElementsVersionsByBranch;

        private readonly Dictionary<string, ChangeSet> _branchTips = new Dictionary<string, ChangeSet>();
        [ProtoMember(8)]
        private Dictionary<string, int> _rawBranchTips;
        [ProtoMember(9)]
        private string _clearcaseRoot;
        private List<String> _relativeRoots;
        private Dictionary<string, LabelInfo> _allLabels;
        private Dictionary<string, LabelMeta> _labelMetas;


        /// <summary>
        /// For use by protobuf
        /// </summary>
        public HistoryBuilder()
        {
        }

        public HistoryBuilder(VobDB vobDB)
        {
            _rawHistoryBuilder = new RawHistoryBuilder(vobDB);
            _labelMetas = vobDB.LabelMetas;
        }

        public Dictionary<string, LabelInfo> GetLabels()
        {
            return _allLabels;
        }

        public void SetBranchFilters(string[] branches)
        {
            _branchFilters = branches;
            _rawHistoryBuilder.SetBranchFilters(branches);
        }

        public void SetRoots(string clearcaseRoot, IEnumerable<string> roots)
        {
            _roots.Clear();
            // always need "." as root
            _roots.Add(".");
            foreach (var root in roots)
                _roots.Add(root);
            _clearcaseRoot = clearcaseRoot;

            _relativeRoots = roots.Where(r => r != ".").Select(r => {
                if (r.StartsWith(clearcaseRoot)) {
                    r = r.Substring(clearcaseRoot.Length);
                }
                if (r.StartsWith("\\"))
                {
                    r = r.Substring(1);
                }
                return r.Replace("\\", "/");
            }).ToList();
        }

        private string RemoveDotRoot(string path)
        {
            string clearcaseRoot = _clearcaseRoot.Replace("\\", "/");
            if (path.StartsWith(clearcaseRoot))
            {
                path = path.Substring(clearcaseRoot.Length);
            }
            if (path.StartsWith("/")) {
                path = path.Substring(1);
            }
            return path.StartsWith("./") ? path.Substring(2) : path;
        }

        private bool InsideRoot(Element element)
        {
            string clearcaseRoot = _clearcaseRoot.Replace("\\", "/");
            var name = element.Name.Replace("\\", "/");
            string normalizedName;
            if (name.StartsWith(clearcaseRoot + "/.@@/main/"))
            {
                // hidden. take special care
                name = name.Substring((clearcaseRoot + "/.@@/main/").Length);
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
            return null != _relativeRoots.Find(
                r =>  {
                    Logger.TraceData(TraceEventType.Verbose, (int)TraceId.CreateChangeSet, "Comparing in history", normalizedName, "vs", r);
                    return normalizedName == "." || normalizedName == r || normalizedName.StartsWith(r + "/") || normalizedName.StartsWith(r + "@@/");
                }
            );
        }

        public IList<ChangeSet> Build(List<ElementVersion> newVersions)
        {
            _rawHistoryBuilder.SetClearcaseRoot(_clearcaseRoot);
            _rawHistoryBuilder.SetRoots(_roots);
            _rawHistoryBuilder.SetRelativeRoots(_relativeRoots);
            _flattenChangeSets = _rawHistoryBuilder.Build(newVersions);
            if (_globalBranches != null)
            {
                string existingParent;
                foreach (var branch in _rawHistoryBuilder.GlobalBranches)
                    if (_globalBranches.TryGetValue(branch.Key, out existingParent))
                    {
                        if (branch.Value != existingParent)
                            throw new Exception("Inconsistent branch " + branch.Key + " parent : " + branch.Value + " != " + existingParent);
                    }
                    else
                        _globalBranches.Add(branch.Key, branch.Value);
            }
            else
                _globalBranches = _rawHistoryBuilder.GlobalBranches;
            _labels = _rawHistoryBuilder.Labels;
            _allLabels = new Dictionary<string, LabelInfo>(_labels);
            List<ChangeSet> result = ProcessElementNames();
            foreach (var cs in result.Where(c => c.Branch == null))
            {
                Logger.TraceData(TraceEventType.Verbose, (int)TraceId.ApplyChangeSet, "ChangeSet", cs.Id, "has no branch");
            }
            result.RemoveAll(c => c.Branch == null);
            return CleanupHistory(result);
        }

        private List<ChangeSet> CleanupHistory(List<ChangeSet> history)
        {
            Logger.TraceData(TraceEventType.Information, (int)TraceId.ApplyChangeSet, "Before cleaning history we got", history.Count, "entries");
            var branchChangeSet = history.GroupBy(c => c.Branch).ToDictionary(x => x.Key, x => x.ToList());
            List<ChangeSet> toRemove = new List<ChangeSet>();
            foreach (ChangeSet changeSet in history) {
                if (changeSet.IsUselessGitCommit)
                {
                    Logger.TraceData(TraceEventType.Verbose, (int)TraceId.ApplyChangeSet, "Will drop changeset because it's useless", changeSet);
                    toRemove.Add(changeSet);
                    ReassignBranchingPoint(changeSet, branchChangeSet);
                    continue;
                }
                if (changeSet.IsEmptyGitCommit)
                {
                    // move the tags to a non-empty commit.
                    ChangeSet labelPoint = FindNonEmptyChangeSet(changeSet, branchChangeSet, toRemove);
                    if (labelPoint == null)
                    {
                        // drop any labels because we can't attach them anywhere...
                        if (changeSet.Labels.Count > 0)
                        {
                            Logger.TraceData(TraceEventType.Warning, (int)TraceId.ApplyChangeSet, "Dropping labels because we couldn't move them to a non-empty commit", string.Join(" ", changeSet.Labels.ToArray()));
                        }
                    }
                    else
                    {
                        labelPoint.Labels.AddRange(changeSet.Labels);
                    }
                    ReassignBranchingPoint(changeSet, branchChangeSet);
                    Logger.TraceData(TraceEventType.Information, (int)TraceId.ApplyChangeSet, "Will drop changeset because it's empty (tags got moved)", changeSet);
                    toRemove.Add(changeSet);
                    continue;
                }
                if (changeSet.BranchingPoint != null)
                {
                    // might have been removed so search for a new one
                    changeSet.BranchingPoint = FindNonEmptyChangeSet(changeSet.BranchingPoint, branchChangeSet, toRemove);
                }
            }
            foreach (ChangeSet changeSet in toRemove) {
                history.Remove(changeSet);
            }
            Logger.TraceData(TraceEventType.Information, (int)TraceId.ApplyChangeSet, "After cleaning history we got", history.Count, "entries");
            return history;
        }

        private void ReassignBranchingPoint(ChangeSet newBranch, Dictionary<string, List<ChangeSet>> branches)
        {
            if (newBranch.BranchingPoint == null || newBranch.Branch == "master")
            {
                return;
            }
            int index = branches[newBranch.Branch].IndexOf(newBranch);
            if (index < 0)
            {
                throw new Exception("Didn't find " + newBranch);
            }
            index++;
            if (index < branches[newBranch.Branch].Count)
            {
                branches[newBranch.Branch][index].BranchingPoint = newBranch.BranchingPoint;
            }
        }

        private ChangeSet PreviousInBranch(ChangeSet changeSet, Dictionary<string, List<ChangeSet>> branches)
        {
            int index = branches[changeSet.Branch].IndexOf(changeSet);
            if (index < 0)
            {
                throw new Exception("Didn't find " + changeSet);
            }
            index--;
            return index < 0 ? null : branches[changeSet.Branch][index];
        }

        private ChangeSet FindNonEmptyChangeSet(ChangeSet labelPoint, Dictionary<string, List<ChangeSet>> branches, List<ChangeSet> toRemove)
        {
            while (labelPoint.IsEmptyGitCommit || toRemove.Contains(labelPoint))
            {
                ChangeSet previous = PreviousInBranch(labelPoint, branches);
                if (previous == null)
                {
                    if (labelPoint.BranchingPoint == null)
                            return null;
                    // we started a new branch with a tag.
                    // We still want this tag as non-interesting have been filtered out.
                    // Move it up to the branching point and keep on going...
                    Logger.TraceData(TraceEventType.Information, (int)TraceId.ApplyChangeSet, "Going to parent branch", labelPoint.BranchingPoint.Branch, "for", labelPoint, "with tags", string.Join(" ", labelPoint.Labels.ToArray()));
                    labelPoint = labelPoint.BranchingPoint;
                    continue;
                }
                labelPoint = previous;
            }
            return labelPoint;
        }

        private List<ChangeSet> ProcessElementNames()
        {
            Logger.TraceData(TraceEventType.Start | TraceEventType.Information, (int)TraceId.CreateChangeSet, "Start process element names");

            // same content than _flattenChangeSets, but not necessarily same order
            var orderedChangeSets = new List<ChangeSet>(_flattenChangeSets.Count);
            // in case of incremental import, we no longer have the Id corresponding to the index in orderedChangeSets
            // add 1 because of 1-based ids for git marks
            int startingId = _lastId + 1;

            // branch and version for which the elementName could not be found
            var orphanedVersionsByElement = new Dictionary<Element, List<Tuple<string, ChangeSet.NamedVersion>>>();
            // orphan versions that completed a label : should be orphans till the end
            var labeledOrphans = new HashSet<ElementVersion>();

            var applyNowChangeSets = new Queue<ChangeSet>();

            while (true)
            {
                ChangeSet changeSet;
                if (applyNowChangeSets.Count > 0)
                    changeSet = applyNowChangeSets.Dequeue();
                else
                {
                    applyNowChangeSets = new Queue<ChangeSet>(FindNextChangeSets());
                    if (applyNowChangeSets.Count > 0)
                        changeSet = applyNowChangeSets.Dequeue();
                    else
                        // done !
                        break;
                }

                _lastId++;
                // Id is 1-based index because it is used as a mark id for git fast-import, that reserves id 0
                changeSet.Id = _lastId;
                orderedChangeSets.Add(changeSet);
                Logger.TraceData(TraceEventType.Start | TraceEventType.Verbose, (int)TraceId.CreateChangeSet, "Start process element names in ChangeSet", changeSet);
                if (_lastId % 1000 == 0)
                    Logger.TraceData(TraceEventType.Information, (int)TraceId.CreateChangeSet, "Processing element names in ChangeSet", changeSet);

                _branchTips[changeSet.Branch] = changeSet;
                Dictionary<Element, HashSet<string>> elementsNames;
                Dictionary<Element, ElementVersion> elementsVersions;
                bool isNewBranch = !_startedBranches.ContainsKey(changeSet.Branch);
                if (isNewBranch)
                {
                    // branch could start without branching from main
                    if (changeSet.Branch != "main")
                    {
                        string parentBranch = _globalBranches[changeSet.Branch];
                        // if parentBranch not started yet, assume it starts now from its own parent
                        string toStart;
                        do
                        {
                            toStart = null;
                            string currentParent = parentBranch;
                            // branch did not start has no parent so we must abort if we reach it
                            while (!_startedBranches.ContainsKey(currentParent))
                            {
                                if (currentParent == "main")
                                    // main was not started! this branch spawned from somewhere else
                                    break;
                                toStart = currentParent;
                                currentParent = _globalBranches[currentParent];
                            }
                            if (currentParent == "main" && !_startedBranches.ContainsKey(currentParent))
                            {
                                Logger.TraceData(TraceEventType.Warning, (int)TraceId.CreateChangeSet,
                                     "main has not been started. Creating dummy commit.");
                                // main branch has not been started
                                // that means this branch did not start from the main branch, but rather on it's own branch
                                // insert an empty main commit and continue
                                var mainBranch = new ChangeSet(changeSet.AuthorName, changeSet.AuthorLogin, toStart, changeSet.FinishTime);
                                mainBranch.BranchingPoint = null;
                                _branchTips["main"] = mainBranch;
                                mainBranch.Id = changeSet.Id;
                                _lastId++;
                                changeSet.Id = _lastId;
                                orderedChangeSets.Insert(orderedChangeSets.Count - 1, mainBranch);
                                _elementsNamesByBranch.Add("main", new Dictionary<Element, HashSet<string>>());
                                _elementsVersionsByBranch.Add("main", new Dictionary<Element, ElementVersion>());
                                _startedBranches.Add("main", null);
                            }
                            if (toStart != null)
                            {
                                // create an empty ChangeSet to start
                                var missingBranchStartingPoint = _branchTips[currentParent];
                                missingBranchStartingPoint.IsBranchingPoint = true;
                                var missingBranch = new ChangeSet(missingBranchStartingPoint.AuthorName,
                                    missingBranchStartingPoint.AuthorLogin, toStart, missingBranchStartingPoint.FinishTime);
                                missingBranch.BranchingPoint = missingBranchStartingPoint;
                                _elementsNamesByBranch.Add(toStart, _elementsNamesByBranch[currentParent].ToDictionary(elementNames => elementNames.Key, elementNames => new HashSet<string>(elementNames.Value)));
                                _elementsVersionsByBranch.Add(toStart, new Dictionary<Element, ElementVersion>(_elementsVersionsByBranch[currentParent]));
                                _startedBranches.Add(toStart, missingBranchStartingPoint);
                                _branchTips[toStart] = missingBranchStartingPoint;
                                missingBranch.Id = changeSet.Id;
                                _lastId++;
                                changeSet.Id = _lastId;
                                orderedChangeSets.Insert(orderedChangeSets.Count - 1, missingBranch);
                            }
                        } while (toStart != null);

                        changeSet.BranchingPoint = _branchTips[parentBranch];
                        _branchTips[parentBranch].IsBranchingPoint = true;
                        // we need a deep copy here
                        elementsNames = _elementsNamesByBranch[parentBranch].ToDictionary(elementNames => elementNames.Key, elementNames => new HashSet<string>(elementNames.Value));
                        elementsVersions = new Dictionary<Element, ElementVersion>(_elementsVersionsByBranch[parentBranch]);
                    }
                    else
                    {
                        elementsNames = new Dictionary<Element, HashSet<string>>();
                        elementsVersions = new Dictionary<Element, ElementVersion>();
                    }
                    _elementsNamesByBranch.Add(changeSet.Branch, elementsNames);
                    _elementsVersionsByBranch.Add(changeSet.Branch, elementsVersions);
                    _startedBranches.Add(changeSet.Branch, changeSet.BranchingPoint);
                }
                else
                {
                    elementsNames = _elementsNamesByBranch[changeSet.Branch];
                    elementsVersions = _elementsVersionsByBranch[changeSet.Branch];
                }

                var changeSetBuilder = new ChangeSetBuilder(changeSet, elementsNames, elementsVersions, orphanedVersionsByElement, _roots);
                var orphanedVersions = changeSetBuilder.Build();

                ProcessLabels(changeSet, elementsVersions, orphanedVersions, labeledOrphans);

                Logger.TraceData(TraceEventType.Start | TraceEventType.Verbose, (int)TraceId.CreateChangeSet, "Stop process element names in ChangeSet", changeSet.Id);
            }

            // really lost versions
            var lostVersions = new HashSet<ElementVersion>();
            foreach (var orphanedVersions in orphanedVersionsByElement.Values)
                foreach (var orphanedVersion in orphanedVersions)
                {
                    var lostVersion = orphanedVersion.Item2.Version;
                    lostVersions.Add(lostVersion);
                    labeledOrphans.Remove(lostVersion);
                    if (InsideRoot(lostVersion.Element))
                    {
                        Logger.TraceData(TraceEventType.Warning, (int)TraceId.CreateChangeSet,
                                     "Version " + lostVersion + " has not been visible in any imported directory version");
                    }
                }

            // now that we now what versions to ignore :
            foreach (var changeSet in orderedChangeSets)
                ProcessMerges(changeSet, lostVersions);

            ComputeAllMerges();

            // labeled orphans that unexpectedly found a parent
            foreach (var orphan in labeledOrphans)
                Logger.TraceData(TraceEventType.Warning, (int)TraceId.CreateChangeSet,
                                 "Version " + orphan + " has been labeled while an orphan");

            // uncompleted labels
            foreach (var label in _labels.Values)
            {
                foreach (var missingVersion in label.MissingVersions)
                    Logger.TraceData(TraceEventType.Verbose, (int)TraceId.CreateChangeSet,
                                     "Version " + missingVersion + " with label " + label.Name + " not seen");
                Logger.TraceData(TraceEventType.Warning, (int)TraceId.CreateChangeSet, "Label " + label.Name + " has " + label.MissingVersions.Count + " missing versions : not applied");
            }

            // we may need to reorder a little bit in case of merges where the "To" is before the "From"
            var result = new List<ChangeSet>(orderedChangeSets.Count);
            for (int i = 0; i < orderedChangeSets.Count; i++)
                AddChangeSet(orderedChangeSets, result, i, startingId, i);

            Logger.TraceData(TraceEventType.Stop | TraceEventType.Information, (int)TraceId.CreateChangeSet, "Stop process element names");

            return result;
        }

        private IEnumerable<ChangeSet> FindNextChangeSets()
        {
            ChangeSet changeSet = null;
            while (changeSet == null)
                if (_currentIndex < _flattenChangeSets.Count)
                    changeSet = _flattenChangeSets[_currentIndex++];
                else
                    return new ChangeSet[0];
            
            // stay on current : maybe we won't return changeSet, so we will need to process it next time
            _currentIndex--;

            var wouldBreakLabels = FindWouldBreakLabels(changeSet);
            if (wouldBreakLabels.Count == 0)
            {
                _flattenChangeSets[_currentIndex++] = null;
                return new[] { changeSet };
            }
            // we look for a label that could be completed with changeSets within the next hours
            foreach (var pair in wouldBreakLabels)
            {
                int index = _currentIndex;
                string label = pair.Item1;
                string branch = pair.Item2;
                LabelInfo labelInfo;
                if (!_labels.TryGetValue(label, out labelInfo))
                    // a label could be a broken candidate on several branches, and therefore already removed
                    continue;
                HashSet<ElementVersion> missingVersions;
                if (!labelInfo.MissingVersions.TryGetValue(branch, out missingVersions))
                {
                    // this means we would break a spawning branch : we simply look for the first ChangeSet on this branch
                    ChangeSet spawningPoint = FindBranchToSpawn(labelInfo, branch);
                    Logger.TraceData(TraceEventType.Information, (int)TraceId.CreateChangeSet,
                        string.Format("Applying {0} before {1} to start branch {2}", spawningPoint, changeSet, spawningPoint.Branch));
                    return new[] { spawningPoint };
                }
                // we are only interested in missing versions on branch associated with the label as broken
                // versions on parent branch are OK (else we would already have failed),
                // versions on children branches will be handled later (when the branch spawns)
                missingVersions = new HashSet<ElementVersion>(missingVersions);
                var missing = new HashSet<ElementVersion>(missingVersions);
                while (true)
                {
                    ElementVersion lastVersion = missing.OrderBy(v => v.Date.Ticks).Last();
                    if (changeSet.FinishTime.AddHours(8.0) < lastVersion.Date && _labelMetas[label].Created.AddHours(8.0) < lastVersion.Date)
                    {
                        Logger.TraceData(TraceEventType.Warning, (int)TraceId.CreateChangeSet,
                            string.Format("Label {0} broken at {1}, {4} versions including {2} ({3}) is too late : will drop that version and try to continue", label, changeSet, lastVersion, lastVersion.Date, missing.Count));
                        _labels[label].Versions.Remove(lastVersion);
                        _labels[label].MissingVersions.RemoveFromCollection(lastVersion.Branch.BranchName, lastVersion);
                        _allLabels[label].PossiblyBroken.Add(Tuple.Create(lastVersion, (ElementVersion)null));
                        missing.Remove(lastVersion);
                        if (!missing.Any())
                        {
                            break;
                        }
                    } else {
                        break;
                    }
                }
                var neededChangeSets = new HashSet<int>();
                while (missingVersions.Count > 0 && index < _flattenChangeSets.Count)
                {
                    var toCheck = _flattenChangeSets[index++];
                    if (toCheck == null || toCheck.Branch != branch)
                        continue;
                    foreach (var version in toCheck.Versions)
                        if (missingVersions.Contains(version.Version))
                        {
                            neededChangeSets.Add(index - 1);
                            missingVersions.Remove(version.Version);
                        }
                }
                if (missingVersions.Count > 0)
                {
                   string msg = "Label " + label + " broken because it could not be completed : versions " +
                        string.Join(", ", missingVersions.Select(v => v.ToString())) + " not in any further ChangeSet on branch " + changeSet.Branch +
                        ". Will try to force the versions anyway!";
                    Logger.TraceData(TraceEventType.Warning, (int)TraceId.CreateChangeSet, msg);
                    _labels.Remove(label);
                    continue;
                }

                bool isLabelConsistent = ProcessDependenciesAndCheckLabelConsistency(neededChangeSets, label, index);
                if (!isLabelConsistent)
                {
                    Logger.TraceData(TraceEventType.Information, (int)TraceId.CreateChangeSet, "Label " + label + " inconsistent at " + changeSet + " : force applying anyway");
                }
                // everything is OK : applying all neededChangeSets will complete label for this branch
                var result = new List<ChangeSet>();
                foreach (int i in neededChangeSets.OrderBy(i => i))
                {
                    result.Add(_flattenChangeSets[i]);
                    _flattenChangeSets[i] = null;
                }
                Logger.TraceData(TraceEventType.Information, (int)TraceId.CreateChangeSet,
                    string.Format("Applying {0} ChangeSets before {1} to complete label {2}", result.Count, changeSet, label));
                return result;
            }
            // too bad, no WouldBeBroken label can be completed, simply go on
            _flattenChangeSets[_currentIndex++] = null;
            return new[] { changeSet };
        }

        private ChangeSet FindBranchToSpawn(LabelInfo labelInfo, string branch)
        {
            string missingBranch = null;
            foreach (var key in labelInfo.MissingVersions.Keys)
            {
                missingBranch = key;
                while (missingBranch != null && !_startedBranches.ContainsKey(missingBranch) &&
                       _globalBranches[missingBranch] != branch)
                    missingBranch = _globalBranches[missingBranch];
                if (missingBranch != null && !_startedBranches.ContainsKey(missingBranch) && _globalBranches[missingBranch] == branch)
                    // found it !
                    break;
                missingBranch = null;
            }
            if (missingBranch == null)
                throw new Exception("Could not find missing branch of " + labelInfo.Name + " leading to " + branch);
            for (int i = _currentIndex; i < _flattenChangeSets.Count; i++)
                if (_flattenChangeSets[i] != null && _flattenChangeSets[i].Branch == missingBranch)
                {
                    var result = _flattenChangeSets[i];
                    _flattenChangeSets[i] = null;
                    return result;
                }
            throw new Exception("Could not find spawning ChangeSet of " + missingBranch);
        }

        private bool ProcessDependenciesAndCheckLabelConsistency(HashSet<int> neededChangeSets, string label, int index)
        {
            var allNewVersions = neededChangeSets.Select(i => _flattenChangeSets[i])
                .SelectMany(c => c.Versions).Select(v => v.Version)
                .GroupBy(v => v.Element).ToDictionary(g => g.Key, g => new List<ElementVersion>(g));
            // process backwards to get dependencies of dependencies
            // this may include ChangeSets on a parent branch
            bool isLabelConsistent = true;
            for (int i = index - 1; i >= _currentIndex; i--)
            {
                var toCheck = _flattenChangeSets[i];
                if (toCheck == null)
                    continue;
                if (neededChangeSets.Contains(i))
                {
                    if (FindWouldBreakLabels(toCheck).Any(l => l.Item1 == label))
                    {
                        isLabelConsistent = false;
                        break;
                    }
                    continue;
                }
                if (!toCheck.Versions.Select(v => v.Version)
                         .Any(v =>
                         {
                             List<ElementVersion> l;
                             return allNewVersions.TryGetValue(v.Element, out l) && l.Any(v.IsAncestorOf);
                         }))
                    continue;
                if (FindWouldBreakLabels(toCheck).Any(l => l.Item1 == label))
                {
                    isLabelConsistent = false;
                    break;
                }
                neededChangeSets.Add(i);
                foreach (var version in toCheck.Versions.Select(v => v.Version))
                    allNewVersions.AddToCollection(version.Element, version);
            }
            return isLabelConsistent;
        }

        private HashSet<Tuple<string, string>> FindWouldBreakLabels(ChangeSet changeSet)
        {
            var result = new HashSet<Tuple<string, string>>();
            if (!_startedBranches.ContainsKey(changeSet.Branch))
            {
                // then we would break any label on this new branch that is not complete on all parent branches
                string parentBranch = _globalBranches[changeSet.Branch];
                while (parentBranch != null)
                {
                    foreach (var labelInfo in _labels.Values
                        .Where(l => l.MissingVersions.ContainsKey(changeSet.Branch) && l.MissingVersions.ContainsKey(parentBranch)))
                        result.Add(new Tuple<string, string>(labelInfo.Name, parentBranch));
                    parentBranch = _globalBranches[parentBranch];
                }
            }
            foreach (var label in changeSet.Versions
                .Select(v =>
                    {
                        var prev = v.Version.GetPreviousVersion();
                        while (prev != null && prev.VersionNumber == 0)
                            prev = prev.GetPreviousVersion();
                        return prev;
                    })
                .Where(v => v != null)
                .SelectMany(v => v.Labels
                                .Where(label =>
                                           {
                                               LabelInfo labelInfo;
                                               if (!_labels.TryGetValue(label, out labelInfo))
                                                   return false;
                                               // if changeSet.Branch is not "finished" for the label -> broken
                                               if (labelInfo.MissingVersions.ContainsKey(changeSet.Branch))
                                                   return true;
                                               // ok if all branches starting from changeSet.Branch on which the label exists are started
                                               foreach (var branch in labelInfo.MissingVersions.Keys)
                                               {
                                                   var missingBranch = branch;
                                                   while (missingBranch != null && !_startedBranches.ContainsKey(missingBranch))
                                                   {
                                                       var parent = _globalBranches[missingBranch];
                                                       if (parent == changeSet.Branch)
                                                           // we need to spawn missingBranch before applying changeSet
                                                           return true;
                                                       missingBranch = parent;
                                                   }
                                               }
                                               return false;
                                           })))
                result.Add(new Tuple<string, string>(label, changeSet.Branch));

            return result;
        }

        private void ProcessLabels(ChangeSet changeSet, Dictionary<Element, ElementVersion> elementsVersions, List<ElementVersion> orphanedVersions, HashSet<ElementVersion> labeledOrphans)
        {
            var finishedLabels = new HashSet<string>();
            foreach (var version in orphanedVersions)
            {
                // we should usually not complete a label here : the version of the parent directory with the label should not have been seen
                // this can still happen if the version only appears in directories not imported (meaning they will be reported in "really lost versions")
                changeSet.Labels.AddRange(ProcessVersionLabels(changeSet, version, elementsVersions, finishedLabels));
                if (finishedLabels.Count > 0)
                {
                    labeledOrphans.Add(version);
                    Logger.TraceData(TraceEventType.Warning, (int)TraceId.CreateChangeSet,
                                     "Label(s) " + string.Join(", ", finishedLabels) + " has been completed with orphan version " + version);
                }
            }
            foreach (var namedVersion in changeSet.Versions.ToList())
                changeSet.Labels.AddRange(ProcessVersionLabels(changeSet, namedVersion.Version, elementsVersions, finishedLabels));
        }

        private IEnumerable<string> ProcessVersionLabels(ChangeSet changeSet, ElementVersion version, Dictionary<Element, ElementVersion> elementsVersions, HashSet<string> finishedLabels)
        {
            var result = new List<string>();
            foreach (var label in version.Labels)
            {
                LabelInfo labelInfo;
                if (!_labels.TryGetValue(label, out labelInfo))
                    continue;
                labelInfo.MissingVersions.RemoveFromCollection(version.Branch.BranchName, version);
                if (labelInfo.MissingVersions.Count > 0)
                    continue;
                Logger.TraceData(TraceEventType.Verbose, (int)TraceId.CreateChangeSet, "Label " + label + " completed with version " + version);
                // so we removed the last missing version, check that everything is still OK
                _labels.Remove(label);
                finishedLabels.Add(label);
                bool ok = true;
                foreach (var toCheck in labelInfo.Versions)
                {
                    ElementVersion inCurrentVersions;
                    elementsVersions.TryGetValue(toCheck.Element, out inCurrentVersions);
                    if ((inCurrentVersions == null && toCheck.VersionNumber != 0) ||
                        (inCurrentVersions != null && inCurrentVersions != toCheck))
                    {
                        if (inCurrentVersions != null && inCurrentVersions.VersionNumber == 0)
                        {
                            string msg = "Label " + label + " is inconsistent : removing element that should be on " + toCheck +
                                ", not on " + inCurrentVersions;
                            Logger.TraceData(TraceEventType.Warning, (int)TraceId.CreateChangeSet, msg);
                            _allLabels[label].Versions.Remove(inCurrentVersions);
                            changeSet.Versions.RemoveAll(named => named.Version.VersionPath == inCurrentVersions.VersionPath);
                        }
                        else
                        {
                            string msg = "Label " + label + " is inconsistent : should be on " + toCheck +
                                (inCurrentVersions == null ? ", but element has no current version" : ", not on " + inCurrentVersions);
                            Logger.TraceData(TraceEventType.Verbose, (int)TraceId.CreateChangeSet, msg);
                        }
                        _allLabels[label].PossiblyBroken.Add(Tuple.Create(toCheck, inCurrentVersions));
                        ok = false;
                    }
                }
                if (!ok)
                {
                    Logger.TraceData(TraceEventType.Information, (int)TraceId.CreateChangeSet, "Label " + label + " was inconsistent, but will force apply anyway");
                    result.Add(label);
                }
                else
                {
                    Logger.TraceData(TraceEventType.Verbose, (int)TraceId.CreateChangeSet, "Label " + label + " was applied");
                    result.Add(label);
                }
            }
            return result;
        }

        private void ProcessMerges(ChangeSet changeSet, HashSet<ElementVersion> lostVersions)
        {
            foreach (var version in changeSet.Versions.Select(v => v.Version))
            {
                foreach (var mergeTo in version.MergesTo)
                    ProcessMerge(changeSet, version, mergeTo, false, lostVersions);

                foreach (var mergeFrom in version.MergesFrom)
                    ProcessMerge(changeSet, mergeFrom, version, true, lostVersions);
            }

            // merge to skipped versions are OK
            foreach (var version in changeSet.SkippedVersions)
                foreach (var mergeFrom in version.MergesFrom)
                    ProcessMerge(changeSet, mergeFrom, version, true, lostVersions);
        }

        private void ProcessMerge(ChangeSet changeSet, ElementVersion fromVersion, ElementVersion toVersion, bool isToVersionInChangeSet, HashSet<ElementVersion> lostVersions)
        {
            if (lostVersions.Contains(fromVersion) || lostVersions.Contains(toVersion))
                return;

            var from = fromVersion.Branch.BranchName;
            var to = toVersion.Branch.BranchName;
            // for now we only merge back to parent branch, assuming other merges are cherry-picking
            string fromParent;
            if (!_globalBranches.TryGetValue(from, out fromParent) || fromParent != to)
                return;
            // since version 0 is identical to branching point : not interesting
            if (fromVersion.VersionNumber == 0)
                return;
            // handle several merges with a common end : we consider only the latest
            if (!IsLatestMerge(fromVersion, toVersion))
                return;

            var key = new Tuple<string, string>(from, to);
            MergeInfo mergeInfo;
            if (!_merges.TryGetValue(key, out mergeInfo))
            {
                mergeInfo = new MergeInfo(from, to);
                _merges.Add(key, mergeInfo);
            }
            // a version may be seen several times if it appears under a new name,
            // or if a move of files happened in several steps, needing to re-create them (instead of simply moving them)
            if (isToVersionInChangeSet)
            {
                if (mergeInfo.SeenToVersions.Contains(toVersion))
                    return;
                mergeInfo.SeenToVersions.Add(toVersion);
                // either the fromVersion has already been seen, and toVersion is in MissingToVersions
                // or we add fromVersion to MissingFromVersions
                ChangeSet fromChangeSet;
                if (mergeInfo.MissingToVersions.TryGetValue(toVersion, out fromChangeSet))
                {
                    mergeInfo.MissingToVersions.Remove(toVersion);
                    var missingInChangeSet = mergeInfo.MissingToVersionsByChangeSet[fromChangeSet];
                    missingInChangeSet.Remove(toVersion);
                    if (missingInChangeSet.Count == 0)
                    {
                        mergeInfo.MissingToVersionsByChangeSet.Remove(fromChangeSet);
                        mergeInfo.Merges[fromChangeSet] = changeSet;
                    }
                }
                else
                    mergeInfo.MissingFromVersions[fromVersion] = changeSet;
            }
            else
            {
                if (mergeInfo.SeenFromVersions.Contains(fromVersion))
                    return;
                mergeInfo.SeenFromVersions.Add(fromVersion);
                // either toVersion has already been seen, and fromVersion is in MissingFromVersions
                // or we add toVersion to MissingToVersions
                ChangeSet toChangeSet;
                if (mergeInfo.MissingFromVersions.TryGetValue(fromVersion, out toChangeSet))
                {
                    mergeInfo.MissingFromVersions.Remove(fromVersion);
                    ChangeSet existingTo;
                    if (!mergeInfo.Merges.TryGetValue(changeSet, out existingTo) || existingTo.Id < toChangeSet.Id)
                        mergeInfo.Merges[changeSet] = toChangeSet;
                }
                else
                {
                    mergeInfo.MissingToVersions[toVersion] = changeSet;
                    mergeInfo.MissingToVersionsByChangeSet.AddToCollection(changeSet, toVersion);
                }
            }
        }

        private static bool IsLatestMerge(ElementVersion fromVersion, ElementVersion toVersion)
        {
            return !fromVersion.MergesTo.Any(v => v.Branch == toVersion.Branch && v.VersionNumber > toVersion.VersionNumber) &&
                !toVersion.MergesFrom.Any(v => v.Branch == fromVersion.Branch && v.VersionNumber > fromVersion.VersionNumber);
        }

        private static void AddChangeSet(List<ChangeSet> source, List<ChangeSet> destination, int sourceIndex, int startingId, int firstNonNullIndex)
        {
            var changeSet = source[sourceIndex];
            if (changeSet == null)
                return;

            // add missing changeSets on other branches needed to have all merges available
            foreach (var fromChangeSet in changeSet.Merges.Where(c => source[c.Id - startingId] != null))
            {
                Logger.TraceData(TraceEventType.Information, (int)TraceId.CreateChangeSet,
                    "Reordering : ChangeSet " + fromChangeSet + " must be imported before because of merge into " + changeSet);
                if (fromChangeSet != source[fromChangeSet.Id - startingId])
                    throw new Exception("Inconsistent Id for " + fromChangeSet +
                        " : changeSet at corresponding index was " + (source[fromChangeSet.Id - startingId] == null ? "null" : source[fromChangeSet.Id - startingId].ToString()));
                for (int i = firstNonNullIndex; i <= fromChangeSet.Id - startingId; i++)
                {
                    if (source[i] != null && source[i].Branch == fromChangeSet.Branch)
                    {
                        AddChangeSet(source, destination, i, startingId, firstNonNullIndex);
                    }
                }
            }

            destination.Add(changeSet);
            source[sourceIndex] = null;
        }

        private void ComputeAllMerges()
        {
            foreach (var mergeInfo in _merges.Values)
            {
                int currentTo = int.MaxValue;
                var fromChangeSets = mergeInfo.Merges.Keys.OrderByDescending(c => c.Id);
                foreach (var from in fromChangeSets)
                {
                    ChangeSet to = null;
                    foreach (var candidate in fromChangeSets.Where(c => c.Id <= from.Id).Select(c => mergeInfo.Merges[c]))
                        if (to == null || candidate.Id > to.Id)
                            to = candidate;

                    // we cannot merge before branching point !!
                    if (to != null && to.Id < currentTo)
                    {
                        if (to.Id <= _startedBranches[from.Branch].Id)
                        {
                            Logger.TraceData(TraceEventType.Warning, (int)TraceId.CreateChangeSet,
                                "Invalid merge from " + from + " to " + to + " : branch " + mergeInfo.From + " branched from later changeSet " + _startedBranches[mergeInfo.From]);
                            // no hope to have another meaningful merge
                            break;
                        }
                        currentTo = to.Id;
                        to.Merges.Add(from);
                        from.IsMerged = true;
                    }
                }
                // incomplete merges
                if (mergeInfo.MissingFromVersions.Count > 0 || mergeInfo.MissingToVersions.Count > 0)
                    Logger.TraceData(TraceEventType.Warning, (int)TraceId.CreateChangeSet,
                                     "Merge " + mergeInfo + " could not be completed : " +
                                     (mergeInfo.MissingFromVersions.Count > 0 ? "missing fromVersions : " + string.Join(", ", mergeInfo.MissingFromVersions.Select(v => v.Key.ToString())) : "") +
                                     (mergeInfo.MissingToVersions.Count > 0 ? "missing toVersions : " + string.Join(", ", mergeInfo.MissingToVersions.Select(v => v.Key.ToString())) : ""));
            }
        }

        [ProtoBeforeSerialization]
        private void BeforeProtobufSerialization()
        {
            _rawElementsNamesByBranch = new Dictionary<string, Dictionary<string, List<string>>>();
            foreach (var pair in _elementsNamesByBranch)
            {
                var elements = new Dictionary<string, List<string>>();
                _rawElementsNamesByBranch.Add(pair.Key, elements);
                foreach (var element in pair.Value)
                {
                    var names = new List<string>(element.Value);
                    elements.Add(element.Key.Oid, names);
                }
            }
            _rawElementsVersionsByBranch = new Dictionary<string, Dictionary<string, ElementVersion.Reference>>();
            foreach (var pair in _elementsVersionsByBranch)
            {
                var elements = new Dictionary<string, ElementVersion.Reference>();
                _rawElementsVersionsByBranch.Add(pair.Key, elements);
                foreach (var element in pair.Value)
                    elements.Add(element.Key.Oid, new ElementVersion.Reference(element.Value));
            }
            _rawBranchTips = _branchTips.ToDictionary(p => p.Key, p => p.Value.Id);
            // "main" branch has a null BranchingPoint
            _rawStartedBranches = _startedBranches.ToDictionary(p => p.Key, p => p.Value == null ? 0 : p.Value.Id);
        }

        public void Fixup(VobDB vobDB)
        {
            _rawHistoryBuilder = new RawHistoryBuilder(null);
            _rawHistoryBuilder.SetBranchFilters(_branchFilters);
            if (_rawElementsNamesByBranch != null)
            {
                foreach (var pair in _rawElementsNamesByBranch)
                {
                    var elements = new Dictionary<Element, HashSet<string>>();
                    _elementsNamesByBranch.Add(pair.Key, elements);
                    foreach (var nameByOid in pair.Value)
                    {
                        Element element;
                        if (!vobDB.ElementsByOid.TryGetValue(nameByOid.Key, out element))
                        {
                            Logger.TraceData(TraceEventType.Warning, (int)TraceId.CreateChangeSet, "Could not find element with oid " + nameByOid.Key + " in loaded vobDB");
                            continue;
                        }
                        var names = new HashSet<string>(nameByOid.Value);
                        elements.Add(element, names);
                    }
                }
                _rawElementsNamesByBranch = null;
            }
            if (_rawElementsVersionsByBranch != null)
            {
                foreach (var pair in _rawElementsVersionsByBranch)
                {
                    var elements = new Dictionary<Element, ElementVersion>();
                    _elementsVersionsByBranch.Add(pair.Key, elements);
                    foreach (var versionByOid in pair.Value)
                    {
                        Element element;
                        if (!vobDB.ElementsByOid.TryGetValue(versionByOid.Key, out element))
                        {
                            Logger.TraceData(TraceEventType.Warning, (int)TraceId.CreateChangeSet, "Could not find element with oid " + versionByOid.Key + " in loaded vobDB");
                            continue;
                        }
                        elements.Add(element, element.GetVersion(versionByOid.Value.BranchName, versionByOid.Value.VersionNumber));
                    }
                }
                _rawElementsVersionsByBranch = null;
            }
            if (_rawBranchTips != null)
            {
                foreach (var branchTip in _rawBranchTips)
                    // creating dummy changeSets : they will never be used except for their Id
                    _branchTips.Add(branchTip.Key, new ChangeSet("dummy", "dummy", branchTip.Key, DateTime.Now) { Id = branchTip.Value });
                _rawBranchTips = null;
            }
            if (_rawStartedBranches != null)
            {
                foreach (var startedBranch in _rawStartedBranches)
                    // creating dummy changeSets : they will never be used except for their Id
                    _startedBranches.Add(startedBranch.Key, startedBranch.Value == 0 ? null : new ChangeSet("dummy", "dummy", startedBranch.Key, DateTime.Now) { Id = startedBranch.Value });
                _rawStartedBranches = null;
            }
        }

        /// <summary>
        /// Handles cases of branches differing only on case : git branches are implemented as
        /// files named after the branches, so they collide on case-insensitive file systems such as windows
        /// </summary>
        /// <returns>A dictionary of branches needing renaming to their new name</returns>
        public Dictionary<string, string> GetBranchRename()
        {
            // not perfectly clean : we use the order of the keys of a Dictionary, which happens
            // to be reliable here (insertion order, since no remove), but is implementation-dependent
            var caseInsensitiveNames = new HashSet<string>();
            var result = new Dictionary<string, string>();
            foreach (var branch in _globalBranches.Keys)
            {
                var caseInsensitiveName = branch.ToLowerInvariant();
                if (!caseInsensitiveNames.Contains(caseInsensitiveName))
                {
                    caseInsensitiveNames.Add(caseInsensitiveName);
                    continue;
                }
                int n = 0;
                string newName;
                do
                {
                    n++;
                    newName = string.Format("{0}_{1}", branch, n);
                } while (caseInsensitiveNames.Contains(newName.ToLowerInvariant()));
                result.Add(branch, newName);
                caseInsensitiveNames.Add(newName.ToLowerInvariant());
            }
            return result;
        }
    }
}
