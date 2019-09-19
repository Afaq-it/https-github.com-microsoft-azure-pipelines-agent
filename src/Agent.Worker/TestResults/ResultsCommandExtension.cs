﻿using Microsoft.TeamFoundation.TestManagement.WebApi;
using Microsoft.VisualStudio.Services.Agent.Util;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using Microsoft.VisualStudio.Services.WebApi;
using Microsoft.TeamFoundation.DistributedTask.WebApi;
using Microsoft.TeamFoundation.TestClient.PublishTestResults;
using Microsoft.VisualStudio.Services.Agent.Worker.Telemetry;
using Microsoft.VisualStudio.Services.WebPlatform;
using Microsoft.VisualStudio.Services.Agent.Worker.LegacyTestResults;

namespace Microsoft.VisualStudio.Services.Agent.Worker.TestResults
{
    public sealed class ResultsCommandExtension : AgentService, IWorkerCommandExtension
    {
        private IExecutionContext _executionContext;
        private const string _publishTestResultsLibFeatureFlag = "";
        //publish test results inputs
        private List<string> _testResultFiles;
        private string _testRunner;
        private bool _mergeResults;
        private string _platform;
        private string _configuration;
        private string _runTitle;
        private bool _publishRunLevelAttachments;

        private bool _failTaskOnFailedTests;

        private string _testRunSystem;

        //telemetry parameter
        private const string _telemetryFeature = "PublishTestResultsCommand";
        private const string _telemetryArea = "TestResults";
        private Dictionary<string, object> _telemetryProperties;

        public Type ExtensionType => typeof(IWorkerCommandExtension);

        public string CommandArea => "results";

        public HostTypes SupportedHostTypes => HostTypes.All;
        
        public void ProcessCommand(IExecutionContext context, Command command)
        {
            if (string.Equals(command.Event, WellKnownResultsCommand.PublishTestResults, StringComparison.OrdinalIgnoreCase))
            {
                ProcessPublishTestResultsCommand(context, command.Properties, command.Data);
            }
            else
            {
                throw new Exception(StringUtil.Loc("ResultsCommandNotFound", command.Event));
            }
        }

        private void ProcessPublishTestResultsCommand(IExecutionContext context, Dictionary<string, string> eventProperties, string data)
        {
            ArgUtil.NotNull(context, nameof(context));
            _executionContext = context;

            _telemetryProperties = new Dictionary<string, object>();
            PopulateTelemetryData();

            LoadPublishTestResultsInputs(context, eventProperties, data);

            string teamProject = context.Variables.System_TeamProject;
            TestRunContext runContext = CreateTestRunContext();

            VssConnection connection = WorkerUtilities.GetVssConnection(_executionContext);

            var commandContext = HostContext.CreateService<IAsyncCommandContext>();
            commandContext.InitializeCommandContext(context, StringUtil.Loc("PublishTestResults"));
            commandContext.Task = PublishTestRunDataAsync(connection, teamProject, runContext);
            _executionContext.AsyncCommands.Add(commandContext);
            
        }

        private void LoadPublishTestResultsInputs(IExecutionContext context, Dictionary<string, string> eventProperties, string data)
        {
            // Validate input test results files
            string resultFilesInput;
            eventProperties.TryGetValue(PublishTestResultsEventProperties.ResultFiles, out resultFilesInput);
            // To support compat we parse data first. If data is empty parse 'TestResults' parameter
            if (!string.IsNullOrWhiteSpace(data) && data.Split(',').Count() != 0)
            {
                if (context.Container != null)
                {
                    _testResultFiles = data.Split(',').Select(x => context.Container.TranslateToHostPath(x)).ToList();
                }
                else
                {
                    _testResultFiles = data.Split(',').ToList();
                }
            }
            else
            {
                if (string.IsNullOrEmpty(resultFilesInput) || resultFilesInput.Split(',').Count() == 0)
                {
                    throw new ArgumentException(StringUtil.Loc("ArgumentNeeded", "TestResults"));
                }

                if (context.Container != null)
                {
                    _testResultFiles = resultFilesInput.Split(',').Select(x => context.Container.TranslateToHostPath(x)).ToList();
                }
                else
                {
                    _testResultFiles = resultFilesInput.Split(',').ToList();
                }
            }

            //validate testrunner input
            eventProperties.TryGetValue(PublishTestResultsEventProperties.Type, out _testRunner);
            if (string.IsNullOrEmpty(_testRunner))
            {
                throw new ArgumentException(StringUtil.Loc("ArgumentNeeded", "Testrunner"));
            }

            string mergeResultsInput;
            eventProperties.TryGetValue(PublishTestResultsEventProperties.MergeResults, out mergeResultsInput);
            if (string.IsNullOrEmpty(mergeResultsInput) || !bool.TryParse(mergeResultsInput, out _mergeResults))
            {
                // if no proper input is provided by default we merge test results
                _mergeResults = true;
            }

            eventProperties.TryGetValue(PublishTestResultsEventProperties.Platform, out _platform);
            if (_platform == null)
            {
                _platform = string.Empty;
            }

            eventProperties.TryGetValue(PublishTestResultsEventProperties.Configuration, out _configuration);
            if (_configuration == null)
            {
                _configuration = string.Empty;
            }

            eventProperties.TryGetValue(PublishTestResultsEventProperties.RunTitle, out _runTitle);
            if (_runTitle == null)
            {
                _runTitle = string.Empty;
            }

            eventProperties.TryGetValue(PublishTestResultsEventProperties.TestRunSystem, out _testRunSystem);
            if (_testRunSystem == null)
            {
                _testRunSystem = string.Empty;
            }

            string failTaskInput;
            eventProperties.TryGetValue(PublishTestResultsEventProperties.FailTaskOnFailedTests, out failTaskInput);
            if (string.IsNullOrEmpty(failTaskInput) || !bool.TryParse(failTaskInput, out _failTaskOnFailedTests))
            {
                // if no proper input is provided by default fail task is false
                _failTaskOnFailedTests = false;
            }

            string publishRunAttachmentsInput;
            eventProperties.TryGetValue(PublishTestResultsEventProperties.PublishRunAttachments, out publishRunAttachmentsInput);
            if (string.IsNullOrEmpty(publishRunAttachmentsInput) || !bool.TryParse(publishRunAttachmentsInput, out _publishRunLevelAttachments))
            {
                // if no proper input is provided by default we publish attachments.
                _publishRunLevelAttachments = true;
            }
        }

        private void LogPublishTestResultsFailureWarning(Exception ex)
        {
            string message = ex.Message;
            if (ex.InnerException != null)
            {
                message += Environment.NewLine;
                message += ex.InnerException.Message;
            }
            _executionContext.Warning(StringUtil.Loc("FailedToPublishTestResults", message));
        }

        // Adds Target Branch Name info to run create model
        private void AddTargetBranchInfoToRunCreateModel(RunCreateModel runCreateModel, string pullRequestTargetBranchName)
        {
            if (string.IsNullOrEmpty(pullRequestTargetBranchName) ||
                !string.IsNullOrEmpty(runCreateModel.BuildReference?.TargetBranchName))
            {
                return;
            }

            if (runCreateModel.BuildReference == null)
            {
                runCreateModel.BuildReference = new BuildConfiguration() { TargetBranchName = pullRequestTargetBranchName };
            }
            else
            {
                runCreateModel.BuildReference.TargetBranchName = pullRequestTargetBranchName;
            }
        }

        private TestRunContext CreateTestRunContext()
        {
            string releaseUri = null;
            string releaseEnvironmentUri = null;

            string teamProject = _executionContext.Variables.System_TeamProject;
            string owner = _executionContext.Variables.Build_RequestedFor;
            string buildUri = _executionContext.Variables.Build_BuildUri;
            int buildId = _executionContext.Variables.Build_BuildId ?? 0;
            string pullRequestTargetBranchName = _executionContext.Variables.System_PullRequest_TargetBranch;
            string stageName = _executionContext.Variables.System_StageName;
            string phaseName = _executionContext.Variables.System_PhaseName;
            string jobName = _executionContext.Variables.System_JobName;
            int stageAttempt = _executionContext.Variables.System_StageAttempt ?? 0;
            int phaseAttempt = _executionContext.Variables.System_PhaseAttempt ?? 0;
            int jobAttempt = _executionContext.Variables.System_JobAttempt ?? 0;

            //Temporary fix to support publish in RM scenarios where there might not be a valid Build ID associated.
            //TODO: Make a cleaner fix after TCM User Story 401703 is completed.
            if (buildId == 0)
            {
                _platform = _configuration = null;
            }

            if (!string.IsNullOrWhiteSpace(_executionContext.Variables.Release_ReleaseUri))
            {
                releaseUri = _executionContext.Variables.Release_ReleaseUri;
                releaseEnvironmentUri = _executionContext.Variables.Release_ReleaseEnvironmentUri;
            }

            // If runName is not provided by the task, then create runName from testRunner name and buildId.
            string runName = String.IsNullOrWhiteSpace(_runTitle)
                ? String.Format("{0}_TestResults_{1}", _testRunner, buildId)
                : _runTitle;

            StageReference stageReference = new StageReference() { StageName = stageName, Attempt = Convert.ToInt32(stageAttempt) };
            PhaseReference phaseReference = new PhaseReference() { PhaseName = phaseName, Attempt = Convert.ToInt32(phaseAttempt) };
            JobReference jobReference = new JobReference() { JobName = jobName, Attempt = Convert.ToInt32(jobAttempt) };
            PipelineReference pipelineReference = new PipelineReference()
            {
                PipelineId = buildId,
                StageReference = stageReference,
                PhaseReference = phaseReference,
                JobReference = jobReference
            };

            TestRunContext testRunContext = new TestRunContext(
                owner: owner,
                platform: _platform,
                configuration: _configuration,
                buildId: buildId,
                buildUri: buildUri,
                releaseUri: releaseUri,
                releaseEnvironmentUri: releaseEnvironmentUri,
                runName: runName,
                testRunSystem: _testRunSystem,
                buildAttachmentProcessor: new CodeCoverageBuildAttachmentProcessor(),
                targetBranchName: pullRequestTargetBranchName,
                pipelineReference: pipelineReference
            );
            return testRunContext;

        }

        private PublishOptions GetPublishOptions()
        {
            var publishOptions = new PublishOptions()
            {
                IsMergeTestResultsToSingleRun = _mergeResults,
                IsAddTestRunAttachments = _publishRunLevelAttachments
            };

            return publishOptions;
        }

        private async Task PublishTestRunDataAsync(VssConnection connection, String teamProject, TestRunContext testRunContext)
        {
            bool isTestRunOutcomeFailed = false;
            try
            {
                var featureFlagService = HostContext.GetService<IFeatureFlagService>();
                featureFlagService.InitializeFeatureService(_executionContext, connection);

                if (featureFlagService.GetFeatureFlagState(_publishTestResultsLibFeatureFlag, Service.TFS)){
                    TestDataProvider testDataProvider = ParseTestResultsFile(testRunContext);

                    var publisher = HostContext.GetService<ITestRunDataPublisher>();
                    publisher.InitializePublisher(_executionContext, teamProject, connection);

                    var testRunData = testDataProvider.GetTestRunData();
                    await publisher.PublishAsync(testRunContext, testRunData, GetPublishOptions(), _executionContext.CancellationToken);

                    isTestRunOutcomeFailed = GetTestRunOutcome(testRunData);
                    
                }
                else {
                    var publisher = HostContext.GetService<ILegacyTestRunDataPublisher>();
                    publisher.InitializePublisher(_executionContext, teamProject, connection, _testRunner, _publishRunLevelAttachments);

                    isTestRunOutcomeFailed = await publisher.PublishAsync(testRunContext, _testResultFiles, _runTitle, _executionContext.Variables.Build_BuildId, _mergeResults);
                }

                if (isTestRunOutcomeFailed)
                {
                    _executionContext.Result = TaskResult.Failed;
                    _executionContext.Error(StringUtil.Loc("FailedTestsInResults"));
                }

                await PublishEventsAsync(connection);
            }
            catch (Exception ex)
            {
                _executionContext.Error("Could not publish test run level data."+ ex);
            }
        }

        private bool GetTestRunOutcome(IList<Microsoft.TeamFoundation.TestClient.PublishTestResults.TestRunData> testRunDataList)
        {
            if (_failTaskOnFailedTests)
            {
                // Reads through each testCaseResult in testRunDataList 
                foreach (var testRunData in testRunDataList)
                {
                    foreach (var testCaseResult in testRunData.TestResults)
                    {
                        // Return true if outcome is failed or aborted
                        if (testCaseResult.Outcome == TestOutcome.Failed.ToString() || testCaseResult.Outcome == TestOutcome.Aborted.ToString())
                        {
                            return true;
                        }
                    }
                }
            }

            return false;
        }

        private TestDataProvider ParseTestResultsFile(TestRunContext runContext)
        {
            var extensionManager = HostContext.GetService<IExtensionManager>();
            IParser parser = (extensionManager.GetExtensions<IParser>()).FirstOrDefault(x => _testRunner.Equals(x.Name, StringComparison.OrdinalIgnoreCase));
            
            if (parser == null)
            {
                throw new ArgumentException("Unknown test runner");
            }
            return parser.ParseTestResultFiles(_executionContext, runContext, _testResultFiles);
        }

        private async Task PublishEventsAsync(VssConnection connection)
        {
            try
            {
                CustomerIntelligenceEvent ciEvent = new CustomerIntelligenceEvent()
                {
                    Area = _telemetryArea,
                    Feature = _telemetryFeature,
                    Properties = _telemetryProperties
                };

                var ciService = HostContext.GetService<ICustomerIntelligenceServer>();
                ciService.Initialize(connection);
                await ciService.PublishEventsAsync(new CustomerIntelligenceEvent[] { ciEvent });
            }
            catch(Exception ex)
            {
                _executionContext.Debug(StringUtil.Loc("TelemetryCommandFailed", ex.Message));
            }
        }

        private void PopulateTelemetryData()
        {
            _telemetryProperties.Add("ExecutionId", _executionContext.Id);
            _telemetryProperties.Add("BuildId", _executionContext.Variables.Build_BuildId);
            _telemetryProperties.Add("BuildUri", _executionContext.Variables.Build_BuildUri);
            _telemetryProperties.Add("Attempt", _executionContext.Variables.System_JobAttempt);
            _telemetryProperties.Add("ProjectId", _executionContext.Variables.System_TeamProjectId);
            _telemetryProperties.Add("ProjectName", _executionContext.Variables.System_TeamProject);

            if (!string.IsNullOrWhiteSpace(_executionContext.Variables.Release_ReleaseUri))
            {
                _telemetryProperties.Add("ReleaseUri", _executionContext.Variables.Release_ReleaseUri);
                _telemetryProperties.Add("ReleaseId", _executionContext.Variables.Release_ReleaseId);
            }
        }
    }

    internal static class WellKnownResultsCommand
    {
        public static readonly string PublishTestResults = "publish";
    }

    internal static class PublishTestResultsEventProperties
    {
        public static readonly string Type = "type";
        public static readonly string MergeResults = "mergeResults";
        public static readonly string Platform = "platform";
        public static readonly string Configuration = "config";
        public static readonly string RunTitle = "runTitle";
        public static readonly string PublishRunAttachments = "publishRunAttachments";
        public static readonly string ResultFiles = "resultFiles";
        public static readonly string TestRunSystem = "testRunSystem";
        public static readonly string FailTaskOnFailedTests = "failTaskOnFailedTests";
    }
}
