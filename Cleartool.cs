﻿using System;
using System.Collections.Generic;
using System.Linq;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Threading;
using System.Text;
using System.Text.RegularExpressions;

namespace GitImporter
{
    public class Cleartool : IDisposable
    {
        private const string _cleartool = "cleartool_tty.exe";

        public static TraceSource Logger = Program.Logger;

        private readonly Process _process;
        private readonly Thread _outputThread;
        private readonly Thread _errorThread;
        private readonly StreamWriter _inputWriter;
        private readonly ManualResetEventSlim _cleartoolAvailable = new ManualResetEventSlim();
        private readonly string _clearcaseRoot;
        private readonly LabelFilter _labelFilter;

        private readonly Regex _directoryEntryRegex = new Regex("^===> name: \"([^\"]+)\"");
        private readonly Regex _oidRegex = new Regex(@"cataloged oid: (\S+) \(mtype \d+\)");
        private readonly Regex _symlinkRegex = new Regex("^.+ --> (.+)$");
        private readonly Regex _mergeRegex = new Regex(@"^(""Merge@\d+@[^""]+"" (<-|->) ""[^""]+\\([^\\]+)\\((CHECKEDOUT\.)?\d+)"" )+$");

        private readonly Regex _separator = new Regex("~#~");

        private List<string> _currentOutput = new List<string>();
        private string _lastError;
        private const int _nbRetry = 15;

        public Cleartool(string clearcaseRoot, LabelFilter labelFilter)
        {
            _labelFilter = labelFilter;

            var startInfo = new ProcessStartInfo(_cleartool)
                            { UseShellExecute = false, RedirectStandardInput = true, RedirectStandardOutput = true, RedirectStandardError = true };
            _process = new Process { StartInfo = startInfo };
            _process.Start();
            _inputWriter = new StreamWriter(_process.StandardInput.BaseStream, Encoding.Default) { AutoFlush = true };
            _outputThread = new Thread(ReadOutput) { IsBackground = true };
            _outputThread.Start();
            _errorThread = new Thread(ReadError) { IsBackground = true };
            _errorThread.Start();
            _cleartoolAvailable.Wait();
            _clearcaseRoot = clearcaseRoot;
            ExecuteCommand("cd \"" + _clearcaseRoot + "\"");
        }

        void ReadOutput()
        {
            int c;
            string currentString = "";
            const string prompt = "cleartool> ";
            int promptLength = prompt.Length;
            int currentIndexInPrompt = 0;
            while ((c = _process.StandardOutput.Read()) != -1)
            {
                switch ((char)c)
                {
                    case '\r':
                    case '\n':
                        if (!string.IsNullOrWhiteSpace(currentString))
                            _currentOutput.Add(currentString);
                        currentString = "";
                        break;
                    default:
                        currentString += (char)c;
                        if (prompt[currentIndexInPrompt] == (char)c)
                        {
                            currentIndexInPrompt++;
                            if (currentIndexInPrompt == promptLength)
                            {
                                string last = currentString.Substring(0, currentString.Length - promptLength);
                                if (last.Length > 0)
                                    _currentOutput.Add(last);
                                currentString = "";
                                currentIndexInPrompt = 0;
                                _cleartoolAvailable.Set();
                            }
                        }
                        else
                            // fortunately, there is only one 'c' in the prompt
                            currentIndexInPrompt = (char)c == prompt[0] ? 1 : 0;
                        break;
                }
            }
        }

        void ReadError()
        {
            string error;
            while ((error = _process.StandardError.ReadLine()) != null)
            {
                _lastError = error;
                Logger.TraceData(TraceEventType.Warning, (int)TraceId.Cleartool, error);
            }
        }

        private List<string> ExecuteCommand(string cmd)
        {
            for (int i = 0; i < _nbRetry; i++)
            {
                Logger.TraceData(TraceEventType.Start | TraceEventType.Verbose, (int)TraceId.Cleartool, "Start executing cleartool command", cmd);
                _cleartoolAvailable.Reset();
                _lastError = null;
                _currentOutput = new List<string>();
                _inputWriter.WriteLine(cmd);
                _cleartoolAvailable.Wait();
                Logger.TraceData(TraceEventType.Stop | TraceEventType.Verbose, (int)TraceId.Cleartool, "Stop executing cleartool command", cmd);
                if (_lastError != null)
                {
                    bool lastTry = i == _nbRetry - 1;
                    Logger.TraceData(TraceEventType.Warning, (int)TraceId.Cleartool, "Cleartool command failed" + (!lastTry ? ", retrying" : ""), cmd);
                    if (!lastTry)
                        Thread.Sleep(2000);
                }
                else
                {
                    if (i > 0)
                        Logger.TraceData(TraceEventType.Information, (int)TraceId.Cleartool, "Cleartool command succeeded on retry #" + i, cmd);
                    var result = _currentOutput;
                    return result;
                }
            }
            Logger.TraceData(TraceEventType.Error, (int)TraceId.Cleartool, "Cleartool command failed " + _nbRetry + " times, aborting", cmd);
            return new List<string>();
        }

        public List<string> Lsvtree(string element)
        {
            return ExecuteCommand("lsvtree -short -all -obsolete \"" + element + "\"").Select(v => v.Substring(v.LastIndexOf("@@") + 2)).ToList();
        }

        /// <summary>
        /// List content of a directory (possibly with a version-extended path),
        /// as a dictionary &lt;name as it appears in this version, oid of the element&gt;
        /// Symbolic links are stored as a string with the SYMLINK prefix
        /// </summary>
        public Dictionary<string, string> Ls(string element)
        {
            var result = new Dictionary<string, string>();
            string name = null, oid = null;
            foreach (var line in ExecuteCommand("ls -dump \"" + element + "\""))
            {
                Match match;
                if ((match = _directoryEntryRegex.Match(line)).Success)
                {
                    if (name != null && oid != null)
                        result[name] = oid;
                    name = match.Groups[1].Value;
                    oid = null;
                }
                else if ((match = _oidRegex.Match(line)).Success)
                    oid = match.Groups[1].Value;
                else if ((match = _symlinkRegex.Match(line)).Success)
                    oid = SymLinkElement.SYMLINK + match.Groups[1].Value;
            }
            if (name != null && oid != null)
                result[name] = oid;
            return result;
        }

        public string GetOid(string element)
        {
            bool isDir;
            return GetOid(element, out isDir);
        }

        public string GetOid(string element, out bool isDir)
        {
            isDir = false;
            if (!element.EndsWith("@@"))
                element += "@@";
            var result = ExecuteCommand("desc -fmt \"%On" + _separator + "%m\" \"" + element + "\"");
            if (result.Count == 0)
                return null;
            string[] parts = _separator.Split(result[0]);
            isDir = parts[1] == "directory element";
            return parts[0];
        }

        public string GetPredecessor(string version)
        {
            return ExecuteCommand("desc -pred -s \"" + version + "\"").FirstOrDefault();
        }

        public void GetVersionDetails(ElementVersion version, out List<Tuple<string, int>> mergesTo, out List<Tuple<string, int>> mergesFrom)
        {
            bool isDir = version.Element.IsDirectory;
            // not interested in directory merges
            string format = "%Fu" + _separator +
                "%u" + _separator +
                "%d" + _separator +
                "%Nc" + _separator +
                "%[activity]CXp" + _separator +
                "%Nl" +
                (isDir ? "" : _separator + "%[hlink:Merge]p");
            // string.Join to handle multi-line comments
            string raw = string.Join("\r\n", ExecuteCommand("desc -fmt \"" + format + "\" \"" + version + "\""));
            string[] parts = _separator.Split(raw);
            version.AuthorName = string.Intern(parts[0]);
            version.AuthorLogin = string.Intern(parts[1]);
            version.Date = DateTime.Parse(parts[2], null, DateTimeStyles.RoundtripKind).ToUniversalTime();
            version.Comment = string.Intern(parts[3]);
            version.Activity = string.Intern(parts[4]);
            foreach (string label in parts[5].Split(' '))
                if (!string.IsNullOrWhiteSpace(label) && _labelFilter.ShouldKeep(label))
                    version.Labels.Add(string.Intern(label));
            mergesTo = mergesFrom = null;
            if (isDir && parts.Count() >= 6 && !string.IsNullOrEmpty(parts[5]))
            {
                Logger.TraceData(TraceEventType.Verbose, (int)TraceId.Cleartool, "Ignoring directory merge info for", version);
            }
            if (isDir || string.IsNullOrEmpty(parts[6]))
                return;

            Match match = _mergeRegex.Match(parts[6]);
            if (!match.Success)
            {
                Logger.TraceData(TraceEventType.Warning, (int)TraceId.Cleartool, "Failed to parse merge data '" + parts[6] + "'");
                return;
            }
            mergesTo = new List<Tuple<string, int>>();
            mergesFrom = new List<Tuple<string, int>>();
            int count = match.Groups[1].Captures.Count;
            for (int i = 0; i < count; i++)
            {
                var addTo = match.Groups[2].Captures[i].Value == "->" ? mergesTo : mergesFrom;
                var branch = match.Groups[3].Captures[i].Value;
                var versionNumber = match.Groups[4].Captures[i].Value;
                if (versionNumber.StartsWith("CHECKEDOUT"))
                    continue;
                addTo.Add(new Tuple<string, int>(branch, int.Parse(versionNumber)));
            }
        }

        public List<string> ListLabels()
        {
            return ExecuteCommand("lstype -kind lbtype -obsolete -fmt \"%n\\r\\n\"").Select(s => s.Trim()).ToList();
        }

        public void GetLabelDetails(string label, out string authorName, out string login, out DateTime date)
        {
            string format = "%Fu" + _separator + "%u" + _separator + "%d";
            // string.Join to handle multi-line comments
            string raw = string.Join("\r\n", ExecuteCommand("desc -fmt \"" + format + "\" \"lbtype:" + label + "\""));
            string[] parts = _separator.Split(raw);
            authorName = string.Intern(parts[0]);
            login = string.Intern(parts[1]);
            //date = DateTime.ParseExact(parts[2], "yyyyMMdd.HHmmss", null).ToUniversalTime();
            date = DateTime.Parse(parts[2], null, DateTimeStyles.RoundtripKind).ToUniversalTime();
        }

        public string Get(string element)
        {
            string tmp = Path.Combine(Path.GetTempPath(), Path.GetRandomFileName());
            ExecuteCommand("get -to \"" + tmp + "\" \"" + element + "\"");
            return tmp;
        }

        public string GetElement(string oid)
        {
            string fullPath = ExecuteCommand("desc -s oid:" + oid).FirstOrDefault();
            if (string.IsNullOrEmpty(fullPath))
                return null;
            // try to normalize to _clearcaseRoot, it depends if we are using a dynamic or snapshot view
            string toRemove = _clearcaseRoot + "\\";
            while (!fullPath.StartsWith(toRemove))
                toRemove = toRemove.Substring(toRemove.IndexOf('\\') + 1);
            return fullPath.Substring(toRemove.Length);
        }

        public void Dispose()
        {
            _inputWriter.WriteLine("quit");
            _outputThread.Join();
            _errorThread.Join();
            _process.Close();
        }
    }
}
