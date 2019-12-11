// Copyright (c) Microsoft Corporation.
// Licensed under the MIT License.

using Agent.Sdk;
using Microsoft.TeamFoundation.DistributedTask.WebApi;
using Microsoft.VisualStudio.Services.Agent.Util;
using Microsoft.VisualStudio.Services.Agent.Worker.Build;
using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Linq;
using BuildWebApi = Microsoft.TeamFoundation.Build.WebApi;
using Microsoft.TeamFoundation.DistributedTask.Logging;
using Newtonsoft.Json.Linq;

namespace Microsoft.VisualStudio.Services.Agent.Worker
{
    public sealed class VariableScope : IDisposable
    {
        private Variables Data;
        private HashSet<string> Names;

        public VariableScope(Variables data)
        {
            Data = data;
            Names = new HashSet<string>();
        }

        public void Set(string name, string val, bool secret = false)
        {
            Names.Add(name);
            Data.Set(name, val, secret);
        }

        public void Dispose()
        {
            foreach (string name in Names)
            {
                Data.Unset(name);
            }
        }
    }

    public sealed class Variables
    {
        private readonly IHostContext _hostContext;
        private readonly ConcurrentDictionary<string, Variable> _nonexpanded = new ConcurrentDictionary<string, Variable>(StringComparer.OrdinalIgnoreCase);
        private readonly ISecretMasker _secretMasker;
        private readonly object _setLock = new object();
        private readonly Tracing _trace;
        private ConcurrentDictionary<string, Variable> _expanded;

        private List<String> _wellKnownSystemVariables;

        public delegate string TranslationMethod(string val);
        public TranslationMethod StringTranslator = val => val ;


        public IEnumerable<KeyValuePair<string, string>> Public
        {
            get
            {
                return _expanded.Values
                    .Where(x => !x.Secret)
                    .Select(x => new KeyValuePair<string, string>(x.Name, StringTranslator(x.Value)));
            }
        }

        public IEnumerable<KeyValuePair<string, string>> Private
        {
            get
            {
                return _expanded.Values
                    .Where(x => x.Secret)
                    .Select(x => new KeyValuePair<string, string>(x.Name, StringTranslator(x.Value)));
            }
        }

        public Variables(IHostContext hostContext, IDictionary<string, VariableValue> copy, out List<string> warnings)
        {
            // Store/Validate args.
            _hostContext = hostContext;
            _secretMasker = _hostContext.SecretMasker;
            _trace = _hostContext.GetTrace(nameof(Variables));
            ArgUtil.NotNull(hostContext, nameof(hostContext));

            // Validate the dictionary, remove any variable with empty variable name.
            ArgUtil.NotNull(copy, nameof(copy));
            if (copy.Keys.Any(k => string.IsNullOrWhiteSpace(k)))
            {
                _trace.Info($"Remove {copy.Keys.Count(k => string.IsNullOrWhiteSpace(k))} variables with empty variable name.");
            }

            // Initialize the variable dictionary.
            List<Variable> variables = new List<Variable>();
            foreach (var variable in copy)
            {
                if (!string.IsNullOrWhiteSpace(variable.Key))
                {
                    variables.Add(new Variable(variable.Key, variable.Value.Value, variable.Value.IsSecret, variable.Value.IsReadOnly));
                }
            }

            foreach (Variable variable in variables)
            {
                // Store the variable. The initial secret values have already been
                // registered by the Worker class.
                _nonexpanded[variable.Name] = variable;
            }

            // Recursively expand the variables.
            RecalculateExpanded(out warnings);
        }

        // DO NOT add file path variable to here.
        // All file path variables needs to be retrieved and set through ExecutionContext, so it can handle container file path translation.

        public TaskResult? Agent_JobStatus
        {
            get
            {
                return GetEnum<TaskResult>(Constants.Variables.Agent.JobStatus);
            }

            set
            {
                Set(Constants.Variables.Agent.JobStatus, $"{value}");
            }
        }

        public string Agent_ProxyUrl => Get(Constants.Variables.Agent.ProxyUrl);

        public string Agent_ProxyUsername => Get(Constants.Variables.Agent.ProxyUsername);

        public string Agent_ProxyPassword => Get(Constants.Variables.Agent.ProxyPassword);

        public int? Build_BuildId => GetInt(BuildWebApi.BuildVariables.BuildId);

        public string Build_BuildUri => Get(BuildWebApi.BuildVariables.BuildUri);

        public BuildCleanOption? Build_Clean => GetEnum<BuildCleanOption>(Constants.Variables.Features.BuildDirectoryClean) ?? GetEnum<BuildCleanOption>(Constants.Variables.Build.Clean);

        public long? Build_ContainerId => GetLong(BuildWebApi.BuildVariables.ContainerId);

        public string Build_DefinitionName => Get(Constants.Variables.Build.DefinitionName);

        public bool? Build_GatedRunCI => GetBoolean(Constants.Variables.Build.GatedRunCI);

        public string Build_GatedShelvesetName => Get(Constants.Variables.Build.GatedShelvesetName);

        public string Build_Number => Get(Constants.Variables.Build.Number);

        public string Build_RepoTfvcWorkspace => Get(Constants.Variables.Build.RepoTfvcWorkspace);

        public string Build_RequestedFor => Get((BuildWebApi.BuildVariables.RequestedFor));

        public string Build_SourceBranch => Get(Constants.Variables.Build.SourceBranch);

        public string Build_SourceTfvcShelveset => Get(Constants.Variables.Build.SourceTfvcShelveset);

        public string Build_SourceVersion => Get(Constants.Variables.Build.SourceVersion);

        public bool? Build_SyncSources => GetBoolean(Constants.Variables.Build.SyncSources);

        public string Release_ArtifactsDirectory => Get(Constants.Variables.Release.ArtifactsDirectory);

        public string Release_ReleaseEnvironmentUri => Get(Constants.Variables.Release.ReleaseEnvironmentUri);

        public string Release_ReleaseId => Get(Constants.Variables.Release.ReleaseId);

        public string Release_ReleaseName => Get(Constants.Variables.Release.ReleaseName);

        public string Release_ReleaseUri => Get(Constants.Variables.Release.ReleaseUri);

        public int? Release_Download_BufferSize => GetInt(Constants.Variables.Release.ReleaseDownloadBufferSize);

        public int? Release_Parallel_Download_Limit => GetInt(Constants.Variables.Release.ReleaseParallelDownloadLimit);

        public bool Retain_Default_Encoding
        {
            get
            {
                if (!PlatformUtil.RunningOnWindows)
                {
                    return true;
                }
                return GetBoolean(Constants.Variables.Agent.RetainDefaultEncoding) ?? true;
            }
        }

        public bool Read_Only_Variables
        {
            get
            {
                return GetBoolean(Constants.Variables.Agent.ReadOnlyVariables) ?? false;
            }
        }

        public string System_CollectionId => Get(Constants.Variables.System.CollectionId);

        public bool? System_Debug => GetBoolean(Constants.Variables.System.Debug);

        public string System_DefinitionId => Get(Constants.Variables.System.DefinitionId);

        public bool? System_EnableAccessToken => GetBoolean(Constants.Variables.System.EnableAccessToken);

        public HostTypes System_HostType => GetEnum<HostTypes>(Constants.Variables.System.HostType) ?? HostTypes.None;
        public string System_JobId => Get(Constants.Variables.System.JobId);

        public string System_PhaseDisplayName => Get(Constants.Variables.System.PhaseDisplayName);

        public string System_PullRequest_TargetBranch => Get(Constants.Variables.System.PullRequestTargetBranchName);

        public string System_TaskDefinitionsUri => Get(WellKnownDistributedTaskVariables.TaskDefinitionsUrl);

        public string System_TeamProject => Get(BuildWebApi.BuildVariables.TeamProject);

        public Guid? System_TeamProjectId => GetGuid(BuildWebApi.BuildVariables.TeamProjectId);

        public string System_TFCollectionUrl => Get(WellKnownDistributedTaskVariables.TFCollectionUrl);

        public string System_StageName => Get(Constants.Variables.System.StageName);

        public int? System_StageAttempt => GetInt(Constants.Variables.System.StageAttempt);

        public string System_PhaseName => Get(Constants.Variables.System.PhaseName);

        public int? System_PhaseAttempt => GetInt(Constants.Variables.System.PhaseAttempt);

        public string System_JobName => Get(Constants.Variables.System.JobName);

        public int? System_JobAttempt => GetInt(Constants.Variables.System.JobAttempt);


        public static readonly HashSet<string> PiiVariables = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
        {
            "Build.AuthorizeAs",
            "Build.QueuedBy",
            "Build.RequestedFor",
            "Build.RequestedForEmail",
            "Build.SourceBranch",
            "Build.SourceBranchName",
            "Build.SourceTfvcShelveset",
            "Build.SourceVersion",
            "Build.SourceVersionAuthor",
            "Job.AuthorizeAs",
            "Release.Deployment.RequestedFor",
            "Release.Deployment.RequestedForEmail",
            "Release.RequestedFor",
            "Release.RequestedForEmail",
        };

        public static readonly string PiiArtifactVariablePrefix = "Release.Artifacts";

        public static readonly List<string> PiiArtifactVariableSuffixes = new List<string>()
        {
            "SourceBranch",
            "SourceBranchName",
            "SourceVersion",
            "RequestedFor"
        };

        public void ExpandValues(IDictionary<string, string> target)
        {
            _trace.Entering();
            var source = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            foreach (Variable variable in _expanded.Values)
            {
                var value = StringTranslator(variable.Value);
                source[variable.Name] = value;
            }

            VarUtil.ExpandValues(_hostContext, source, target);
        }

        public string ExpandValue(string name, string value)
        {
            _trace.Entering();
            var source = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            foreach (Variable variable in _expanded.Values)
            {
                source[variable.Name] = StringTranslator(variable.Value);
            }
            var target = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
            {
                [name] = value
            };
            VarUtil.ExpandValues(_hostContext, source, target);
            return target[name];
        }

        public JToken ExpandValues(JToken target)
        {
            _trace.Entering();
            var source = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            foreach (Variable variable in _expanded.Values)
            {
                source[variable.Name] = StringTranslator(variable.Value);
            }

            return VarUtil.ExpandValues(_hostContext, source, target);
        }

        public string Get(string name)
        {
            Variable variable;
            if (_expanded.TryGetValue(name, out variable))
            {
                var value = StringTranslator(variable.Value);
                _trace.Verbose($"Get '{name}': '{value}'");
                return value;
            }

            _trace.Verbose($"Get '{name}' (not found)");
            return null;
        }

        public bool? GetBoolean(string name)
        {
            bool val;
            if (bool.TryParse(Get(name), out val))
            {
                return val;
            }

            return null;
        }

        public T? GetEnum<T>(string name) where T : struct
        {
            return EnumUtil.TryParse<T>(Get(name));
        }

        public Guid? GetGuid(string name)
        {
            Guid val;
            if (Guid.TryParse(Get(name), out val))
            {
                return val;
            }

            return null;
        }

        public int? GetInt(string name)
        {
            int val;
            if (int.TryParse(Get(name), out val))
            {
                return val;
            }

            return null;
        }

        public long? GetLong(string name)
        {
            long val;
            if (long.TryParse(Get(name), out val))
            {
                return val;
            }

            return null;
        }

        public VariableScope CreateScope()
        {
            return new VariableScope(this);
        }

        public void Unset(string name)
        {
            // Validate the args.
            ArgUtil.NotNullOrEmpty(name, nameof(name));

            // Remove the variable.
            lock (_setLock)
            {
                Variable dummy;
                 _expanded.Remove(name, out dummy);
                _nonexpanded.Remove(name, out dummy);
                _trace.Verbose($"Unset '{name}'");
            }
        }

        public void Set(string name, string val, bool secret = false, bool readOnly = false)
        {
            // Validate the args.
            ArgUtil.NotNullOrEmpty(name, nameof(name));

            // Add or update the variable.
            lock (_setLock)
            {
                // Determine whether the value should be a secret. The approach taken here is somewhat
                // conservative. If the previous expanded variable is a secret, then assume the new
                // value should be a secret as well.
                //
                // Keep in mind, the two goals of flagging variables as secret:
                // 1) Mask secrets from the logs.
                // 2) Keep secrets out of environment variables for tasks. Secrets must be passed into
                //    tasks via inputs. It's better to take a conservative approach when determining
                //    whether a variable should be marked secret. Otherwise nested secret values may
                //    inadvertantly end up in public environment variables.
                secret = secret || (_expanded.ContainsKey(name) && _expanded[name].Secret);

                // Register the secret. Secret masker handles duplicates gracefully.
                if (secret && !string.IsNullOrEmpty(val))
                {
                    _secretMasker.AddValue(val);
                }

                // Store the value as-is to the expanded dictionary and the non-expanded dictionary.
                // It is not expected that the caller needs to store an non-expanded value and then
                // retrieve the expanded value in the same context.
                var variable = new Variable(name, val, secret, readOnly);
                _expanded[name] = variable;
                _nonexpanded[name] = variable;
                _trace.Verbose($"Set '{name}' = '{val}'");
            }
        }

        public bool IsReadOnly(string name)
        {
            if (!Read_Only_Variables)
            {
                return false;
            }
            Variable existingVariable = null;
            if (!_expanded.TryGetValue(name, out existingVariable)) {
                _nonexpanded.TryGetValue(name, out existingVariable);
            }
            return (existingVariable != null && IsReadOnly(existingVariable));
        }

        public void Transform(Func<string, string> function)
        {
            lock (_setLock)
            {
                Dictionary<string, Variable> modified = new Dictionary<string, Variable>();
                foreach (var entry in _expanded)
                {
                    var variable = entry.Value;
                    var newVal = function.Invoke(variable.Value);
                    if (string.CompareOrdinal(newVal, variable.Value) != 0)
                    {
                        modified.Add(entry.Key, new Variable(entry.Key, newVal, variable.Secret, variable.ReadOnly));
                    }
                }
                foreach (var entry in modified)
                {
                    var name = entry.Key;
                    var variable = entry.Value;
                    _expanded[name] = variable;
                    _nonexpanded[name] = variable;
                }
            }
        }

        public bool TryGetValue(string name, out string val)
        {
            Variable variable;
            if (_expanded.TryGetValue(name, out variable))
            {
                val = StringTranslator(variable.Value);
                _trace.Verbose($"Get '{name}': '{val}'");
                return true;
            }

            val = null;
            _trace.Verbose($"Get '{name}' (not found)");
            return false;
        }

        public void RecalculateExpanded(out List<string> warnings)
        {
            // TODO: A performance improvement could be made by short-circuiting if the non-expanded values are not dirty. It's unclear whether it would make a significant difference.

            // Take a lock to prevent the variables from changing while expansion is being processed.
            lock (_setLock)
            {
                const int MaxDepth = 50;
                // TODO: Validate max size? No limit on *nix. Max of 32k per env var on Windows https://msdn.microsoft.com/en-us/library/windows/desktop/ms682653%28v=vs.85%29.aspx
                _trace.Entering();
                warnings = new List<string>();

                // Create a new expanded instance.
                var expanded = new ConcurrentDictionary<string, Variable>(_nonexpanded, StringComparer.OrdinalIgnoreCase);

                // Process each variable in the dictionary.
                foreach (string name in _nonexpanded.Keys)
                {
                    bool secret = _nonexpanded[name].Secret;
                    bool readOnly = _nonexpanded[name].ReadOnly;
                    _trace.Verbose($"Processing expansion for variable: '{name}'");

                    // This algorithm handles recursive replacement using a stack.
                    // 1) Max depth is enforced by leveraging the stack count.
                    // 2) Cyclical references are detected by walking the stack.
                    // 3) Additional call frames are avoided.
                    bool exceedsMaxDepth = false;
                    bool hasCycle = false;
                    var stack = new Stack<RecursionState>();
                    RecursionState state = new RecursionState(name: name, value: _nonexpanded[name].Value ?? string.Empty);

                    // The outer while loop is used to manage popping items from the stack (of state objects).
                    while (true)
                    {
                        // The inner while loop is used to manage replacement within the current state object.

                        // Find the next macro within the current value.
                        while (state.StartIndex < state.Value.Length &&
                            (state.PrefixIndex = state.Value.IndexOf(Constants.Variables.MacroPrefix, state.StartIndex, StringComparison.Ordinal)) >= 0 &&
                            (state.SuffixIndex = state.Value.IndexOf(Constants.Variables.MacroSuffix, state.PrefixIndex + Constants.Variables.MacroPrefix.Length, StringComparison.Ordinal)) >= 0)
                        {
                            // A candidate was found.
                            string nestedName = state.Value.Substring(
                                startIndex: state.PrefixIndex + Constants.Variables.MacroPrefix.Length,
                                length: state.SuffixIndex - state.PrefixIndex - Constants.Variables.MacroPrefix.Length);
                            if (!secret)
                            {
                                _trace.Verbose($"Found macro candidate: '{nestedName}'");
                            }

                            Variable nestedVariable;
                            if (!string.IsNullOrEmpty(nestedName) &&
                                _nonexpanded.TryGetValue(nestedName, out nestedVariable))
                            {
                                // A matching variable was found.

                                // Check for max depth.
                                int currentDepth = stack.Count + 1; // Add 1 since the current state isn't on the stack.
                                if (currentDepth == MaxDepth)
                                {
                                    // Warn and break out of the while loops.
                                    _trace.Warning("Exceeds max depth.");
                                    exceedsMaxDepth = true;
                                    warnings.Add(StringUtil.Loc("Variable0ExceedsMaxDepth1", name, MaxDepth));
                                    break;
                                }
                                // Check for a cyclical reference.
                                else if (string.Equals(state.Name, nestedName, StringComparison.OrdinalIgnoreCase) ||
                                    stack.Any(x => string.Equals(x.Name, nestedName, StringComparison.OrdinalIgnoreCase)))
                                {
                                    // Warn and break out of the while loops.
                                    _trace.Warning("Cyclical reference detected.");
                                    hasCycle = true;
                                    warnings.Add(StringUtil.Loc("Variable0ContainsCyclicalReference", name));
                                    break;
                                }
                                else
                                {
                                    // Push the current state and start a new state. There is no need to break out
                                    // of the inner while loop. It will continue processing the new current state.
                                    secret = secret || nestedVariable.Secret;
                                    if (!secret)
                                    {
                                        _trace.Verbose($"Processing expansion for nested variable: '{nestedName}'");
                                    }

                                    stack.Push(state);
                                    state = new RecursionState(name: nestedName, value: nestedVariable.Value ?? string.Empty);
                                }
                            }
                            else
                            {
                                // A matching variable was not found.
                                if (!secret)
                                {
                                    _trace.Verbose("Macro not found.");
                                }

                                state.StartIndex = state.PrefixIndex + 1;
                            }
                        } // End of inner while loop for processing the variable.

                        // No replacement is performed if something went wrong.
                        if (exceedsMaxDepth || hasCycle)
                        {
                            break;
                        }

                        // Check if finished processing the stack.
                        if (stack.Count == 0)
                        {
                            // Store the final value and break out of the outer while loop.
                            if (!string.Equals(state.Value, _nonexpanded[name].Value, StringComparison.Ordinal))
                            {
                                // Register the secret.
                                if (secret && !string.IsNullOrEmpty(state.Value))
                                {
                                    _secretMasker.AddValue(state.Value);
                                }

                                // Set the expanded value.
                                expanded[state.Name] = new Variable(state.Name, state.Value, secret, readOnly);
                                _trace.Verbose($"Set '{state.Name}' = '{state.Value}'");
                            }

                            break;
                        }

                        // Adjust and pop the parent state.
                        if (!secret)
                        {
                            _trace.Verbose("Popping recursion state.");
                        }

                        RecursionState parent = stack.Pop();
                        parent.Value = string.Concat(
                            parent.Value.Substring(0, parent.PrefixIndex),
                            state.Value,
                            parent.Value.Substring(parent.SuffixIndex + Constants.Variables.MacroSuffix.Length));
                        parent.StartIndex = parent.PrefixIndex + (state.Value).Length;
                        state = parent;
                        if (!secret)
                        {
                            _trace.Verbose($"Intermediate state '{state.Name}': '{state.Value}'");
                        }
                    } // End of outer while loop for recursively processing the variable.
                } // End of foreach loop over each key in the dictionary.

                _expanded = expanded;
            } // End of critical section.

        }

        public void CopyInto(Dictionary<string, VariableValue> target)
        {
            foreach (var var in this.Public)
            {
                target[var.Key] = var.Value;
            }
            foreach (var var in this.Private)
            {
                target[var.Key] = new VariableValue(var.Value, true);
            }
        }

        private Boolean IsReadOnly(Variable variable)
        {
            if (variable.ReadOnly)
            {
                return true;
            }

            if (_wellKnownSystemVariables == null)
            {
                _wellKnownSystemVariables = new List<String>();

                // If variable is not marked as readOnly, return whether its in the set of reserved system variables (which are always readOnly)
                List<String> readOnlyPrefixes = new List<String>()
                {
                    "system.",
                    "build.",
                    "agent."
                };
                // These are all classes which have system variables defined in them. See https://github.com/microsoft/azure-pipelines-agent/blob/master/src/Microsoft.VisualStudio.Services.Agent/Constants.cs
                List<System.Type> wellKnownSystemVariableClasses = new List<System.Type>()
                {
                    typeof(Constants.Variables.Agent),
                    typeof(Constants.Variables.Build),
                    typeof(Constants.Variables.Features),
                    typeof(Constants.Variables.Pipeline),
                    typeof(Constants.Variables.Release),
                    typeof(Constants.Variables.System),
                    typeof(Constants.Variables.Task),
                    typeof(WellKnownDistributedTaskVariables)
                };

                // Iterate through members of each class and add any system variables (aka prefixed with our readOnlyPrefixes)
                foreach (System.Type systemVariableClass in wellKnownSystemVariableClasses)
                {
                    var wellKnownDistributedTaskFields = systemVariableClass.GetFields();
                    foreach(var field in wellKnownDistributedTaskFields)
                    {
                        String value = field.GetValue(systemVariableClass).ToString().ToLower();
                        foreach(String prefix in readOnlyPrefixes)
                        {
                            if (value.StartsWith(prefix))
                            {
                                _wellKnownSystemVariables.Add(value);
                            }
                        }
                    }
                }
            }

            return _wellKnownSystemVariables.Contains(variable.Name.ToLower());
        }

        private sealed class RecursionState
        {
            public RecursionState(string name, string value)
            {
                Name = name;
                Value = value;
            }

            public string Name { get; private set; }
            public string Value { get; set; }
            public int StartIndex { get; set; }
            public int PrefixIndex { get; set; }
            public int SuffixIndex { get; set; }
        }
    }

    public sealed class Variable
    {
        public string Name { get; private set; }
        public bool Secret { get; private set; }
        public string Value { get; private set; }
        public bool ReadOnly { get; private set; }

        public Variable(string name, string value, bool secret, bool readOnly)
        {
            ArgUtil.NotNullOrEmpty(name, nameof(name));
            Name = name;
            Value = value ?? string.Empty;
            Secret = secret;
            ReadOnly = readOnly;
        }
    }
}
