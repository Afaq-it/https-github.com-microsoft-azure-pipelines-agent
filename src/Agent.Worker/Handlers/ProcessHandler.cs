// Copyright (c) Microsoft Corporation.
// Licensed under the MIT License.

using Agent.Sdk.Knob;
using Agent.Worker.Handlers.Helpers;
using Microsoft.VisualStudio.Services.Agent.Util;
using Newtonsoft.Json;
using System;
using System.IO;
using System.Linq;
using System.Text;
using System.Threading.Tasks;

namespace Microsoft.VisualStudio.Services.Agent.Worker.Handlers
{
    [ServiceLocator(Default = typeof(ProcessHandler))]
    public interface IProcessHandler : IHandler
    {
        ProcessHandlerData Data { get; set; }
    }

    public sealed class ProcessHandler : Handler, IProcessHandler
    {
        private const string OutputDelimiter = "##ENV_DELIMITER_d8c0672b##";
        private readonly object _outputLock = new object();
        private readonly StringBuilder _errorBuffer = new StringBuilder();
        private volatile int _errorCount;
        private bool _foundDelimiter;
        private bool _modifyEnvironment;
        private string _generatedScriptPath;

        public ProcessHandlerData Data { get; set; }

        public async Task RunAsync()
        {
            // Validate args.
            Trace.Entering();
            ArgUtil.NotNull(Data, nameof(Data));
            ArgUtil.NotNull(ExecutionContext, nameof(ExecutionContext));
            ArgUtil.NotNull(Inputs, nameof(Inputs));
            ArgUtil.NotNull(TaskDirectory, nameof(TaskDirectory));

            // Update the env dictionary.
            AddVariablesToEnvironment(excludeNames: true, excludeSecrets: true);
            AddPrependPathToEnvironment();

            // Get the command.
            ArgUtil.NotNullOrEmpty(Data.Target, nameof(Data.Target));
            // TODO: WHICH the command?
            string command = Data.Target;

            // Determine whether the command is rooted.
            // TODO: If command begins and ends with a double-quote, trim quotes before making determination. Likewise when determining whether the file exists.
            bool isCommandRooted = false;
            try
            {
                // Path.IsPathRooted throws if illegal characters are in the path.
                isCommandRooted = Path.IsPathRooted(command);
            }
            catch (Exception ex)
            {
                Trace.Info($"Unable to determine whether the command is rooted: {ex.Message}");
            }

            Trace.Info($"Command is rooted: {isCommandRooted}");

            var disableInlineExecution = StringUtil.ConvertToBoolean(Data.DisableInlineExecution);
            ExecutionContext.Debug($"Disable inline execution: '{disableInlineExecution}'");

            if (disableInlineExecution && !File.Exists(command))
            {
                throw new Exception(StringUtil.Loc("FileNotFound", command));
            }

            // Determine the working directory.
            string workingDirectory;
            if (!string.IsNullOrEmpty(Data.WorkingDirectory))
            {
                workingDirectory = Data.WorkingDirectory;
            }
            else
            {
                if (isCommandRooted && File.Exists(command))
                {
                    workingDirectory = Path.GetDirectoryName(command);
                }
                else
                {
                    workingDirectory = Path.Combine(TaskDirectory, "DefaultTaskWorkingDirectory");
                }
            }

            ExecutionContext.Debug($"Working directory: '{workingDirectory}'");
            Directory.CreateDirectory(workingDirectory);

            // Wrap the command in quotes if required.
            //
            // This is guess-work but is probably mostly accurate. The problem is that the command text
            // box is separate from the args text box. This implies to the user that we take care of quoting
            // for the command.
            //
            // The approach taken here is only to quote if it needs quotes. We should stay out of the way
            // as much as possible. Built-in shell commands will not work if they are quoted, e.g. RMDIR.
            if (command.Contains(" ") || command.Contains("%"))
            {
                if (!command.Contains("\""))
                {
                    command = StringUtil.Format("\"{0}\"", command);
                }
            }

            // Get the arguments.
            string arguments = Data.ArgumentFormat ?? string.Empty;

            // Get the fail on standard error flag.
            bool failOnStandardError = true;
            string failOnStandardErrorString;
            if (Inputs.TryGetValue("failOnStandardError", out failOnStandardErrorString))
            {
                failOnStandardError = StringUtil.ConvertToBoolean(failOnStandardErrorString);
            }

            ExecutionContext.Debug($"Fail on standard error: '{failOnStandardError}'");

            // Get the modify environment flag.
            _modifyEnvironment = StringUtil.ConvertToBoolean(Data.ModifyEnvironment);
            ExecutionContext.Debug($"Modify environment: '{_modifyEnvironment}'");

            // Resolve cmd.exe.
            string cmdExe = System.Environment.GetEnvironmentVariable("ComSpec");
            if (string.IsNullOrEmpty(cmdExe))
            {
                cmdExe = "cmd.exe";
            }

            var enableSecureArguments = AgentKnobs.ProcessHandlerSecureArguments.GetValue(ExecutionContext).AsBoolean();
            ExecutionContext.Debug($"Enable secure arguments: '{enableSecureArguments}'");
            var enableSecureArgumentsAudit = AgentKnobs.ProcessHandlerSecureArgumentsAudit.GetValue(ExecutionContext).AsBoolean();
            ExecutionContext.Debug($"Enable secure arguments audit: '{enableSecureArgumentsAudit}'");
            var enableTelemetry = AgentKnobs.ProcessHandlerTelemetry.GetValue(ExecutionContext).AsBoolean();
            ExecutionContext.Debug($"Enable telemetry: '{enableTelemetry}'");

            var enableFileArgs = disableInlineExecution && enableSecureArguments;

            if ((disableInlineExecution && (enableSecureArgumentsAudit || enableSecureArguments)) || enableTelemetry)
            {
                var (processedArgs, telemetry) = ProcessHandlerHelper.ProcessInputArguments(arguments, Environment);

                if (disableInlineExecution && enableSecureArgumentsAudit)
                {
                    ExecutionContext.Warning($"The following arguments will be executed: '{processedArgs}'");
                }
                if (enableFileArgs)
                {
                    GenerateScriptFile(cmdExe, command, processedArgs);

                    command = DisableDelayedExpansion(command);
                }
                if (enableTelemetry)
                {
                    ExecutionContext.Debug($"Agent PH telemetry: {JsonConvert.SerializeObject(telemetry.ToDictionary(), Formatting.None)}");
                    PublishTelemetry(telemetry.ToDictionary(), "ProcessHandler");
                }
            }

            string cmdExeArgs = PrepareCmdExeArgs(command, arguments, enableFileArgs);

            // Invoke the process.
            ExecutionContext.Debug($"{cmdExe} {cmdExeArgs}");
            ExecutionContext.Command($"{cmdExeArgs}");
            using (var processInvoker = HostContext.CreateService<IProcessInvoker>())
            {
                processInvoker.OutputDataReceived += OnOutputDataReceived;
                if (failOnStandardError)
                {
                    processInvoker.ErrorDataReceived += OnErrorDataReceived;
                }
                else
                {
                    processInvoker.ErrorDataReceived += OnOutputDataReceived;
                }

                int exitCode = await processInvoker.ExecuteAsync(workingDirectory: workingDirectory,
                                                                 fileName: cmdExe,
                                                                 arguments: cmdExeArgs,
                                                                 environment: Environment,
                                                                 requireExitCodeZero: false,
                                                                 outputEncoding: null,
                                                                 killProcessOnCancel: false,
                                                                 redirectStandardIn: null,
                                                                 inheritConsoleHandler: !ExecutionContext.Variables.Retain_Default_Encoding,
                                                                 continueAfterCancelProcessTreeKillAttempt: _continueAfterCancelProcessTreeKillAttempt,
                                                                 cancellationToken: ExecutionContext.CancellationToken);
                FlushErrorData();

                // Fail on error count.
                if (_errorCount > 0)
                {
                    if (ExecutionContext.Result != null)
                    {
                        Trace.Info($"Task result already set. Not failing due to error count ({_errorCount}).");
                    }
                    else
                    {
                        throw new Exception(StringUtil.Loc("ProcessCompletedWithCode0Errors1", exitCode, _errorCount));
                    }
                }

                // Fail on non-zero exit code.
                if (exitCode != 0)
                {
                    throw new Exception(StringUtil.Loc("ProcessCompletedWithExitCode0", exitCode));
                }
            }
        }

        // We're writing to user's sript file disable of "Delayed expansion" mode.
        // Reason we're doing this is because we're enable this mode first for args fix with '/V:ON' cmd flag.
        private string DisableDelayedExpansion(string scriptPath)
        {
            ExecutionContext.Debug("Appending disable of Delayed Expansion mode into target script");

            var lines = File.ReadAllLines(scriptPath);
            if (lines.Length > 0)
            {
                string disableDELine = "SetLocal DisableDelayedExpansion".ToLower();
                if (lines[0].ToLower() != disableDELine)
                {
                    lines = new[] { disableDELine }.Concat(lines).ToArray();
                }

                // Creating new temp script to prevent possible write permission issues.
                var agentTemp = ExecutionContext.GetVariableValueOrDefault(Constants.Variables.Agent.TempDirectory);
                var tempScriptName = $"tempScript_{Guid.NewGuid()}{Path.GetExtension(scriptPath)}";
                var tempScriptPath = Path.Combine(agentTemp, tempScriptName);

                File.WriteAllLines(tempScriptPath, lines);

                return tempScriptPath;
            }

            return scriptPath;
        }

        private string PrepareCmdExeArgs(string command, string arguments, bool enableFileArgs)
        {
            string cmdExeArgs;
            if (enableFileArgs)
            {
                cmdExeArgs = $"/c \"{_generatedScriptPath}\"";
            }
            else
            {
                // Format the input to be invoked from cmd.exe to enable built-in shell commands. For example, RMDIR.
                cmdExeArgs = $"/c \"{command} {arguments}";
                cmdExeArgs += _modifyEnvironment
                ? $" && echo {OutputDelimiter} && set \""
                : "\"";
            }

            return cmdExeArgs;
        }

        private void GenerateScriptFile(string cmdExe, string command, string arguments)
        {
            var scriptId = Guid.NewGuid().ToString();
            string inputArgsEnvVarName = VarUtil.ConvertToEnvVariableFormat("AGENT_PH_ARGS_" + scriptId[..8]);

            System.Environment.SetEnvironmentVariable(inputArgsEnvVarName, arguments);

            var agentTemp = ExecutionContext.GetVariableValueOrDefault(Constants.Variables.Agent.TempDirectory);
            _generatedScriptPath = Path.Combine(agentTemp, $"processHandlerScript_{scriptId}.cmd");

            var scriptArgs = $"/v:ON /c \"{command} !{inputArgsEnvVarName}!";

            scriptArgs += _modifyEnvironment
            ? $" && echo {OutputDelimiter} && set \""
            : "\"";

            using (var writer = new StreamWriter(_generatedScriptPath))
            {
                writer.WriteLine($"{cmdExe} {scriptArgs}");
            }

            ExecutionContext.Debug($"Generated script file: {_generatedScriptPath}");
        }

        private void FlushErrorData()
        {
            if (_errorBuffer.Length > 0)
            {
                ExecutionContext.Error(_errorBuffer.ToString());
                _errorCount++;
                _errorBuffer.Clear();
            }
        }

        private void OnErrorDataReceived(object sender, ProcessDataReceivedEventArgs e)
        {
            lock (_outputLock)
            {
                if (!string.IsNullOrEmpty(e.Data))
                {
                    _errorBuffer.AppendLine(e.Data);
                }
            }
        }

        private void OnOutputDataReceived(object sender, ProcessDataReceivedEventArgs e)
        {
            lock (_outputLock)
            {
                FlushErrorData();
                string line = e.Data ?? string.Empty;
                if (_modifyEnvironment)
                {
                    if (_foundDelimiter)
                    {
                        // The line is output from the SET command. Update the environment.
                        int index = line.IndexOf('=');
                        if (index > 0)
                        {
                            string key = line.Substring(0, index);
                            string value = line.Substring(index + 1);

                            // Omit special environment variables:
                            //   "TF_BUILD" is set by ProcessInvoker.
                            //   "agent.jobstatus" is set by ???.
                            if (string.Equals(key, Constants.TFBuild, StringComparison.Ordinal)
                                || string.Equals(key, Constants.Variables.Agent.JobStatus, StringComparison.Ordinal))
                            {
                                return;
                            }

                            ExecutionContext.Debug($"Setting env '{key}' = '{value}'");
                            System.Environment.SetEnvironmentVariable(key, value);
                        }

                        return;
                    } // if (_foundDelimiter)

                    // Use StartsWith() instead of Equals() to allow for trailing spaces from the ECHO command.
                    if (line.StartsWith(OutputDelimiter, StringComparison.Ordinal))
                    {
                        // The line is the output delimiter.
                        // Set the flag and clear the environment variable dictionary.
                        _foundDelimiter = true;
                        return;
                    }
                } // if (_modifyEnvironment)

                // The line is output from the process that was invoked.
                if (!CommandManager.TryProcessCommand(ExecutionContext, line))
                {
                    ExecutionContext.Output(line);
                }
            }
        }
    }
}
