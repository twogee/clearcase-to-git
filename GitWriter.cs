﻿using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.RegularExpressions;

namespace GitImporter
{
    public class GitWriter : IDisposable
    {
        public class PreWritingHook
        {
            public Regex Target { get; private set; }
            public Action<string> Hook { get; private set; }

            public PreWritingHook(Regex target, Action<string> hook)
            {
                Target = target;
                Hook = hook;
            }
        }

        public class PostWritingHook
        {
            public Regex Target { get; private set; }
            public Action<string, StreamWriter> Hook { get; private set; }

            public PostWritingHook(Regex target, Action<string, StreamWriter> hook)
            {
                Target = target;
                Hook = hook;
            }
        }

        public static TraceSource Logger = Program.Logger;

        private static readonly DateTime Epoch = new DateTime(1970, 1, 1, 0, 0, 0, DateTimeKind.Utc);

        private readonly StreamWriter _writer = new StreamWriter(Console.OpenStandardOutput());
        private readonly Cleartool _cleartool;

        private readonly bool _doNotIncludeFileContent;
        private bool _initialFilesAdded;
        private bool _isIncremental;
        private bool _trimRoots;
        private readonly HashSet<string> _startedBranches = new HashSet<string>();
        private readonly Dictionary<string, string> _branchRename;
        private readonly List<String> _roots;
        private List<String> _relativeRoots;
        private string ClearcaseRoot;

        List<string> _prefixes = new List<String>();
        public List<Tuple<string, string>> InitialFiles { get; private set; }

        public List<PreWritingHook> PreWritingHooks { get; private set; }
        public List<PostWritingHook> PostWritingHooks { get; private set; }

        public GitWriter(string clearcaseRoot, bool trimRoots, bool doNotIncludeFileContent, IEnumerable<string> labels, IEnumerable<string> prefixes, string[] roots,
            Dictionary<string, string> branchRename = null)
        {
            _trimRoots = trimRoots;
            _doNotIncludeFileContent = doNotIncludeFileContent;
            _branchRename = branchRename ?? new Dictionary<string, string>();
            _prefixes.AddRange(prefixes);
            InitialFiles = new List<Tuple<string, string>>();
            PreWritingHooks = new List<PreWritingHook>();
            PostWritingHooks = new List<PostWritingHook>();
            ClearcaseRoot = clearcaseRoot.Replace("\\", "/");
            _roots = new List<string>(roots).Select(r => r.Replace("\\", "/")).ToList();
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

            if (_doNotIncludeFileContent)
                return;
            _cleartool = new Cleartool(clearcaseRoot, new LabelFilter(labels));
        }

        public void WriteChangeSets(IList<ChangeSet> changeSets, Dictionary<string, LabelInfo> labels, Dictionary<string, LabelMeta> labelMetas)
        {
            int total = changeSets.Count;
            int n = 0;
            Logger.TraceData(TraceEventType.Start | TraceEventType.Information, (int)TraceId.ApplyChangeSet, "Start writing " + total + " change sets");

            _isIncremental = false; // changeSets.Count > 0 && changeSets[0].Id > 10;
            int checkpointFrequency = ComputeFrequency(total, 10);
            int reportFrequency = ComputeFrequency(total, 1000);

            _initialFilesAdded = InitialFiles.Count == 0; // already "added" if not specified
            var branchChangeSet = changeSets.GroupBy(c => c.Branch).ToDictionary(x => x.Key, x => x.ToList());
            foreach (var changeSet in changeSets)
            {
                n++;
                if (!_isIncremental && n % checkpointFrequency == 0)
                    _writer.Write("checkpoint\n\n");
                if (n % reportFrequency == 0)
                    _writer.Write("progress Writing change set " + n + " of " + total + "\n\n");

                Logger.TraceData(TraceEventType.Start | TraceEventType.Verbose, (int)TraceId.ApplyChangeSet, "Start writing change set", n);
                WriteChangeSet(changeSet, branchChangeSet, labels, labelMetas);
                Logger.TraceData(TraceEventType.Stop | TraceEventType.Verbose, (int)TraceId.ApplyChangeSet, "Stop writing change set", n);
            }

            Logger.TraceData(TraceEventType.Stop | TraceEventType.Information, (int)TraceId.ApplyChangeSet, "Stop writing " + total + " change sets");
        }

        private static int ComputeFrequency(int total, int target)
        {
            int frequency;
            var queue = new Queue<int>(new[] { 1, 2, 5 });
            while (total / (frequency = queue.Dequeue()) > target)
                queue.Enqueue(frequency * 10);
            return frequency;
        }

        private void WriteChangeSet(ChangeSet changeSet, Dictionary<string, List<ChangeSet>> branches, Dictionary<string, LabelInfo> labels, Dictionary<string, LabelMeta> labelMetas)
        {
            if (changeSet.IsEmpty)
            {
                Logger.TraceData(TraceEventType.Information, (int)TraceId.ApplyChangeSet, "Skipped empty ChangeSet " + changeSet);
                return;
            }

            bool writeCommit = true;
            if (changeSet.IsEmptyGitCommit)
            {
                Logger.TraceData(TraceEventType.Information, (int)TraceId.ApplyChangeSet, "Only writing the tags (if any) for " + changeSet);
                writeCommit = false;
            }

            string branchName = changeSet.Branch == "main" ? "master" : MaybeRenamed(changeSet.Branch);
            if (writeCommit)
            {
                _writer.Write("commit refs/heads/" + branchName + "\n");
                _writer.Write("mark :" + changeSet.Id + "\n");
                _writer.Write("committer " + changeSet.AuthorName + " <" + changeSet.AuthorLogin + "> " + (changeSet.StartTime - Epoch).TotalSeconds + " +0200\n");
                _writer.Write("# " + changeSet.StartTime + "\n");
                InlineString(changeSet.GetComment());
                if (changeSet.BranchingPoint != null)
                {
                    _writer.Write("from :" + changeSet.BranchingPoint.Id + "\n");
                    _startedBranches.Add(branchName);
                }
                else if (_isIncremental && !_startedBranches.Contains(branchName))
                {
                    _writer.Write("from refs/heads/" + branchName + "^0\n");
                    _startedBranches.Add(branchName);
                }
                foreach (var merge in changeSet.Merges)
                    _writer.Write("merge :" + merge.Id + "\n");

                if (!_initialFilesAdded && branchName == "master")
                {
                    _initialFilesAdded = true;
                    foreach (var initialFile in InitialFiles)
                    {
                        var fileInfo = new FileInfo(initialFile.Item2);
                        if (fileInfo.Exists)
                        {
                            _writer.Write("M 644 inline " + initialFile.Item1 + "\n");
                            InlineString(File.ReadAllText(initialFile.Item2));
                        }
                    }
                }

                // order is significant : we must Rename and Copy files before (maybe) deleting their directory
                foreach (var pair in changeSet.Renamed)
                    _writer.Write("R \"" + RemoveDotRoot(pair.Item1) + "\" \"" + RemoveDotRoot(pair.Item2) + "\"\n");
                foreach (var pair in changeSet.Copied)
                    _writer.Write("C \"" + RemoveDotRoot(pair.Item1) + "\" \"" + RemoveDotRoot(pair.Item2) + "\"\n");
                foreach (var removed in changeSet.Removed)
                    _writer.Write("D " + RemoveDotRoot(removed) + "\n");

                foreach (var symLink in changeSet.SymLinks)
                {
                    _writer.Write("M 120000 inline " + RemoveDotRoot(symLink.Item1) + "\n");
                    InlineString(RemoveDotRoot(symLink.Item2));
                }

                foreach (var namedVersion in changeSet.Versions)
                {
                    if (namedVersion.Version is DirectoryVersion || namedVersion.Names.Count == 0)
                        continue;

                    bool isEmptyFile = namedVersion.Version.VersionNumber == 0 && namedVersion.Version.Branch.BranchName == "main";

                    if (_doNotIncludeFileContent || isEmptyFile)
                    {
                        foreach (string name in namedVersion.Names.Select(RemoveDotRoot))
                            if (isEmptyFile)
                                _writer.Write("M 644 inline " + name + "\ndata 0\n\n");
                            else
                            {
                                // don't use InlineString here, so that /FetchFileContent is easy to implement
                                _writer.Write("M 644 inline " + name + "\ndata <<EOF\n" + namedVersion.Version + "#" + namedVersion.Version.Element.Oid + "\nEOF\n\n");
                                // also include name in a comment for hooks in /FetchFileContent
                                _writer.Write("#" + name + "\n");
                            }
                        continue;
                    }

                    InlineClearcaseFileVersion(namedVersion.Version.Element.Name, namedVersion.Version.Element.Oid, namedVersion.Version.VersionPath, namedVersion.Names.Select(RemoveDotRoot), true);
                }
            }

            foreach (var label in changeSet.Labels)
            {
                _writer.Write("tag " + label + "\n");
                _writer.Write("from :" + changeSet.Id + "\n");
                LabelMeta meta = labelMetas[label];

                var epoch = new DateTime(1970, 1, 1, 0, 0, 0, DateTimeKind.Utc);
                _writer.Write("tagger " + meta.AuthorName + " <" + meta.AuthorLogin + "> " + (meta.Created - epoch).TotalSeconds + " +0000\n"); // var +0200

                List<Tuple<ElementVersion, ElementVersion>> possibleBroken = labels[label].PossiblyBroken.Where(
                    t => null != _relativeRoots.Find(
                        r => RemoveDotRoot(t.Item1.ToString().Replace("\\", "/") + "/").StartsWith(r + "/")
                    )).ToList();
                if (possibleBroken.Count > 0)
                {
                    Logger.TraceData(TraceEventType.Warning, (int)TraceId.ApplyChangeSet, "Label " + label + " was inconsistent when writing it. Count", possibleBroken.Count);
                    string msg = "Warning! This tag could be incorrect.\nGot an unexpected result, though it could still be correct.\n\n";
                    foreach (Tuple<ElementVersion, ElementVersion> items in possibleBroken)
                    {
                        msg += "Expected \"" + RemoveDotRoot(items.Item1.ToString()) + "\", but got " + (items.Item2 == null ? "nothing" : "\"" + RemoveDotRoot(items.Item2.ToString()) + "\"") + ".\n";
                    }
                    InlineString(msg);
                }
                else
                {
                    _writer.Write("data 0\n\n");
                }
            }
        }

        private string MaybeRenamed(string branch)
        {
            string newName;
            _branchRename.TryGetValue(branch, out newName);
            return newName ?? branch;
        }

        private void InlineString(string data)
        {
            byte[] encoded = Encoding.UTF8.GetBytes(data);
            _writer.Write("data " + encoded.Length + "\n");
            _writer.Flush();
            _writer.BaseStream.Write(encoded, 0, encoded.Length);
            _writer.Write("\n");
        }

        private string RemoveDotRoot(string path)
        {
            if (_trimRoots)
            {
                foreach (string root in _roots)
                {
                    if (path.StartsWith(root))
                    {
                        path = path.Substring(root.Length);
                        break;
                    }
                }
            }
            else
            {
                if (path.StartsWith(ClearcaseRoot))
                {
                    path = path.Substring(ClearcaseRoot.Length);
                }
            }
            foreach (var prefix in _prefixes)
            {
                if (path.StartsWith(prefix))
                {
                    path = path.Substring(prefix.Length);
                }
            }
            if (path.StartsWith("/")) {
                path = path.Substring(1);
            }
            return path.StartsWith("./") ? path.Substring(2) : path;
        }

        private void InlineClearcaseFileVersion(string elementPath, string elementOid, string version, IEnumerable<string> names, bool writeNames)
        {
            string fullName = elementPath + "@@" + version;
            string fileName = _cleartool.Get(fullName);
            var fileInfo = new FileInfo(fileName);
            if (!fileInfo.Exists)
            {
                // in incremental import, elements may have been moved, without a new version, we try to use the oid
                // (we don't always do that to avoid unnecessary calls to cleartool)
                string newElementName = _cleartool.GetElement(elementOid);
                if (!string.IsNullOrEmpty(newElementName))
                {
                    // GetElement returns a "real" element name, ie ending with "@@"
                    fullName = newElementName + version;
                    fileName = _cleartool.Get(fullName);
                    fileInfo = new FileInfo(fileName);
                }
                else
                    Logger.TraceData(TraceEventType.Warning, (int)TraceId.ApplyChangeSet, "Element with oid " + elementOid + " could not be found in clearcase");
            }
            if (!fileInfo.Exists)
            {
                Logger.TraceData(TraceEventType.Warning, (int)TraceId.ApplyChangeSet, "Version " + fullName + " could not be read from clearcase");
                // still create a file for later delete or rename
                foreach (string name in names)
                {
                    if (writeNames)
                        _writer.Write("M 644 inline " + name + "\n");
                    InlineString("// clearcase error while retrieving " + fullName);
                }
                return;
            }
            // clearcase always creates as ReadOnly
            fileInfo.IsReadOnly = false;
            foreach (string name in names)
            {
                foreach (var hook in PreWritingHooks)
                    if (hook.Target.IsMatch(name))
                        hook.Hook(fileName);

                if (writeNames)
                    _writer.Write("M 644 inline " + name + "\n");
                _writer.Write("data " + fileInfo.Length + "\n");
                // Flush() before using BaseStream directly
                _writer.Flush();
                using (var s = new FileStream(fileName, FileMode.Open, FileAccess.Read))
                    s.CopyTo(_writer.BaseStream);
                _writer.Write("\n");
                foreach (var hook in PostWritingHooks)
                    if (hook.Target.IsMatch(name))
                        hook.Hook(fileName, _writer);
            }
            fileInfo.Delete();
        }

        public void WriteFile(string fileName)
        {
            // we can't simply ReadLine() because of end of line discrepancies within comments
            int c;
            int lineNb = 0;
            var currentLine = new List<char>(1024);
            const string inlineFile = "data <<EOF\n";
            int index = 0;
            using (var s = new StreamReader(fileName))
                while ((c = s.Read()) != -1)
                {
                    if (index == -1)
                    {
                        _writer.Write((char)c);
                        if (c == '\n')
                        {
                            index = 0;
                            lineNb++;
                        }
                        continue;
                    }
                    if (c != inlineFile[index])
                    {
                        foreach (char c1 in currentLine)
                            _writer.Write(c1);
                        _writer.Write((char)c);
                        currentLine.Clear();
                        index = c == '\n' ? 0 : -1;
                        continue;
                    }
                    index++;
                    currentLine.Add((char)c);
                    if (index < inlineFile.Length)
                        continue;
                    // we just matched the whole "data <<EOF\n" line : next line is the version we should fetch
                    string versionToFetch = s.ReadLine();
                    if (string.IsNullOrEmpty(versionToFetch))
                        throw new Exception("Error line " + lineNb + " : expecting version path, reading empty line");
                    string eof = s.ReadLine();
                    if (eof != "EOF")
                        throw new Exception("Error line " + lineNb + " : expecting 'EOF', reading '" + eof + "'");
                    eof = s.ReadLine();
                    if (eof != "")
                        throw new Exception("Error line " + lineNb + " : expecting blank line, reading '" + eof + "'");
                    var name = s.ReadLine();
                    if (name == null || !name.StartsWith("#"))
                        throw new Exception("Error line " + lineNb + " : expecting comment with file name, reading '" + name + "'");
                    lineNb += 5;
                    string elementOid = null;
                    var parts = versionToFetch.Split('#');
                    // backward compatibility : do not require oid
                    if (parts.Length == 2)
                    {
                        versionToFetch = parts[0];
                        elementOid = parts[1];
                    }
                    int pos = versionToFetch.LastIndexOf("@@");
                    string elementPath = versionToFetch.Substring(0, pos);
                    string versionPath = versionToFetch.Substring(pos + 2);
                    InlineClearcaseFileVersion(elementPath, elementOid, versionPath, new[] { name.Substring(1) }, false);
                    currentLine.Clear();
                    index = 0;
                }
        }

        public void Dispose()
        {
            _writer.Dispose();
            if (_cleartool != null)
                _cleartool.Dispose();
        }
    }
}
