using Microsoft.TeamFoundation.DistributedTask.WebApi;
using Pipelines = Microsoft.TeamFoundation.DistributedTask.Pipelines;
using Microsoft.VisualStudio.Services.Agent.Util;
using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using Expressions = Microsoft.TeamFoundation.DistributedTask.Orchestration.Server.Expressions;
using Microsoft.VisualStudio.Services.Agent.Worker.Container;

namespace Microsoft.VisualStudio.Services.Agent.Worker
{
    public interface IStep
    {
        Expressions.INode Condition { get; set; }
        ContainerInfo Container { get; }
        bool ContinueOnError { get; }
        string DisplayName { get; }
        bool Enabled { get; }
        IExecutionContext ExecutionContext { get; set; }
        TimeSpan? Timeout { get; }
        Task RunAsync();
        void InitializeStep(IExecutionContext executionContext);
    }

    [ServiceLocator(Default = typeof(StepsRunner))]
    public interface IStepsRunner : IAgentService
    {
        event EventHandler OnCancellation;
        Task RunAsync(IExecutionContext Context, IList<IStep> steps);
    }

    public sealed class StepsRunner : AgentService, IStepsRunner
    {
        public event EventHandler OnCancellation;

        // StepsRunner should never throw exception to caller
        public async Task RunAsync(IExecutionContext context, IList<IStep> steps)
        {
            ArgUtil.NotNull(context, nameof(context));
            ArgUtil.NotNull(steps, nameof(steps));

            // TaskResult:
            //  Abandoned (Server set this.)
            //  Canceled
            //  Failed
            //  Skipped
            //  Succeeded
            //  SucceededWithIssues
            CancellationTokenRegistration? cancelRegister = null;
            foreach (IStep step in steps)
            {
                Trace.Info($"Processing step: DisplayName='{step.DisplayName}', ContinueOnError={step.ContinueOnError}, Enabled={step.Enabled}");
                ArgUtil.Equal(true, step.Enabled, nameof(step.Enabled));
                ArgUtil.NotNull(step.ExecutionContext, nameof(step.ExecutionContext));
                ArgUtil.NotNull(step.ExecutionContext.Variables, nameof(step.ExecutionContext.Variables));

                // Start.
                step.ExecutionContext.Start();

                // Variable expansion.
                List<string> expansionWarnings;
                step.ExecutionContext.Variables.RecalculateExpanded(out expansionWarnings);
                expansionWarnings?.ForEach(x => step.ExecutionContext.Warning(x));

                var expressionManager = HostContext.GetService<IExpressionManager>();
                try
                {
                    // Register job cancellation call back only if job cancellation token not been fire before each step run
                    if (!context.CancellationToken.IsCancellationRequested)
                    {
                        // Test the condition again. The job was canceled after the condition was originally evaluated.
                        cancelRegister = context.CancellationToken.Register(() =>
                        {
                            // mark job as cancelled
                            context.Result = TaskResult.Canceled;
                            context.Variables.Agent_JobStatus = context.Result;
                            if (OnCancellation != null)
                            {
                                OnCancellation(this, EventArgs.Empty);
                            }

                            step.ExecutionContext.Debug($"Re-evaluate condition on job cancellation for step: '{step.DisplayName}'.");
                            bool conditionReTestResult;
                            if (HostContext.AgentShutdownToken.IsCancellationRequested)
                            {
                                step.ExecutionContext.Debug($"Skip Re-evaluate condition on agent shutdown.");
                                conditionReTestResult = false;
                            }
                            else
                            {
                                try
                                {
                                    conditionReTestResult = expressionManager.Evaluate(step.ExecutionContext, step.Condition, hostTracingOnly: true);
                                }
                                catch (Exception ex)
                                {
                                    // Cancel the step since we get exception while re-evaluate step condition.
                                    Trace.Info("Caught exception from expression when re-test condition on job cancellation.");
                                    step.ExecutionContext.Error(ex);
                                    conditionReTestResult = false;
                                }
                            }

                            if (!conditionReTestResult)
                            {
                                // Cancel the step.
                                Trace.Info("Cancel current running step.");
                                step.ExecutionContext.CancelToken();
                            }
                        });
                    }
                    else
                    {
                        if (context.Result != TaskResult.Canceled)
                        {
                            // mark job as cancelled
                            context.Result = TaskResult.Canceled;
                            context.Variables.Agent_JobStatus = context.Result;
                        }
                    }

                    // Evaluate condition.
                    step.ExecutionContext.Debug($"Evaluating condition for step: '{step.DisplayName}'");
                    Exception conditionEvaluateError = null;
                    bool conditionResult;
                    if (HostContext.AgentShutdownToken.IsCancellationRequested)
                    {
                        step.ExecutionContext.Debug($"Skip evaluate condition on agent shutdown.");
                        conditionResult = false;
                    }
                    else
                    {
                        try
                        {
                            conditionResult = expressionManager.Evaluate(step.ExecutionContext, step.Condition);
                        }
                        catch (Exception ex)
                        {
                            Trace.Info("Caught exception from expression.");
                            Trace.Error(ex);
                            conditionResult = false;
                            conditionEvaluateError = ex;
                        }
                    }

                    // no evaluate error but condition is false
                    if (!conditionResult && conditionEvaluateError == null)
                    {
                        // Condition == false
                        Trace.Info("Skipping step due to condition evaluation.");
                        step.ExecutionContext.Complete(TaskResult.Skipped);
                        continue;
                    }

                    if (conditionEvaluateError != null)
                    {
                        // fail the step since there is an evaluate error.
                        step.ExecutionContext.Error(conditionEvaluateError);
                        step.ExecutionContext.Complete(TaskResult.Failed);
                    }
                    else
                    {
                        // Run the step.
                        await RunStepAsync(step, context.CancellationToken);
                    }
                }
                finally
                {
                    if (cancelRegister != null)
                    {
                        cancelRegister?.Dispose();
                        cancelRegister = null;
                    }
                }

                // Update the job result.
                if (step.ExecutionContext.Result == TaskResult.SucceededWithIssues ||
                    step.ExecutionContext.Result == TaskResult.Failed)
                {
                    Trace.Info($"Update job result with current step result '{step.ExecutionContext.Result}'.");
                    context.Result = TaskResultUtil.MergeTaskResults(context.Result, step.ExecutionContext.Result.Value);
                    context.Variables.Agent_JobStatus = context.Result;
                }
                else
                {
                    Trace.Info($"No need for updating job result with current step result '{step.ExecutionContext.Result}'.");
                }

                Trace.Info($"Current state: job state = '{context.Result}'");
            }
        }

        private async Task RunStepAsync(IStep step, CancellationToken jobCancellationToken)
        {
            // Start the step.
            Trace.Info("Starting the step.");
            step.ExecutionContext.Section(StringUtil.Loc("StepStarting", step.DisplayName));
            step.ExecutionContext.SetTimeout(timeout: step.Timeout);
            try
            {
                await step.RunAsync();
            }
            catch (OperationCanceledException ex)
            {
                if (step.ExecutionContext.CancellationToken.IsCancellationRequested &&
                    !jobCancellationToken.IsCancellationRequested)
                {
                    Trace.Error($"Caught timeout exception from step: {ex.Message}");
                    step.ExecutionContext.Error(StringUtil.Loc("StepTimedOut"));
                    step.ExecutionContext.Result = TaskResult.Failed;
                }
                else
                {
                    // Log the exception and cancel the step.
                    Trace.Error($"Caught cancellation exception from step: {ex}");
                    step.ExecutionContext.Error(ex);
                    step.ExecutionContext.Result = TaskResult.Canceled;
                }
            }
            catch (Exception ex)
            {
                // Log the error and fail the step.
                Trace.Error($"Caught exception from step: {ex}");
                step.ExecutionContext.Error(ex);
                step.ExecutionContext.Result = TaskResult.Failed;
            }

            // Wait till all async commands finish.
            foreach (var command in step.ExecutionContext.AsyncCommands ?? new List<IAsyncCommandContext>())
            {
                try
                {
                    // wait async command to finish.
                    await command.WaitAsync();
                }
                catch (OperationCanceledException ex)
                {
                    if (step.ExecutionContext.CancellationToken.IsCancellationRequested &&
                        !jobCancellationToken.IsCancellationRequested)
                    {
                        // Log the timeout error, set step result to falied if the current result is not canceled.
                        Trace.Error($"Caught timeout exception from async command {command.Name}: {ex}");
                        step.ExecutionContext.Error(StringUtil.Loc("StepTimedOut"));

                        // if the step already canceled, don't set it to failed.
                        step.ExecutionContext.CommandResult = TaskResultUtil.MergeTaskResults(step.ExecutionContext.CommandResult, TaskResult.Failed);
                    }
                    else
                    {
                        // log and save the OperationCanceledException, set step result to canceled if the current result is not failed.
                        Trace.Error($"Caught cancellation exception from async command {command.Name}: {ex}");
                        step.ExecutionContext.Error(ex);

                        // if the step already failed, don't set it to canceled.
                        step.ExecutionContext.CommandResult = TaskResultUtil.MergeTaskResults(step.ExecutionContext.CommandResult, TaskResult.Canceled);
                    }
                }
                catch (Exception ex)
                {
                    // Log the error, set step result to falied if the current result is not canceled.
                    Trace.Error($"Caught exception from async command {command.Name}: {ex}");
                    step.ExecutionContext.Error(ex);

                    // if the step already canceled, don't set it to failed.
                    step.ExecutionContext.CommandResult = TaskResultUtil.MergeTaskResults(step.ExecutionContext.CommandResult, TaskResult.Failed);
                }
            }

            // Merge executioncontext result with command result
            if (step.ExecutionContext.CommandResult != null)
            {
                step.ExecutionContext.Result = TaskResultUtil.MergeTaskResults(step.ExecutionContext.Result, step.ExecutionContext.CommandResult.Value);
            }

            // Fixup the step result if ContinueOnError.
            if (step.ExecutionContext.Result == TaskResult.Failed && step.ContinueOnError)
            {
                step.ExecutionContext.Result = TaskResult.SucceededWithIssues;
                Trace.Info($"Updated step result: {step.ExecutionContext.Result}");
            }
            else
            {
                Trace.Info($"Step result: {step.ExecutionContext.Result}");
            }

            // Complete the step context.
            step.ExecutionContext.Section(StringUtil.Loc("StepFinishing", step.DisplayName));
            step.ExecutionContext.Complete();
        }
    }
}
