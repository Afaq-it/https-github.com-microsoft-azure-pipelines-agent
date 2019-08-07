using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Threading.Tasks;
using System.Threading;
using System.IO;
using System.Runtime.InteropServices;
using Agent.Plugins.PipelineArtifact;
using Agent.Plugins.PipelineCache.Telemetry;
using Agent.Sdk;
using Microsoft.VisualStudio.Services.BlobStore.Common;
using Microsoft.VisualStudio.Services.BlobStore.Common.Telemetry;
using Microsoft.VisualStudio.Services.BlobStore.WebApi;
using Microsoft.VisualStudio.Services.Content.Common;
using Microsoft.VisualStudio.Services.Content.Common.Tracing;
using Microsoft.VisualStudio.Services.PipelineCache.WebApi;
using Microsoft.VisualStudio.Services.PipelineCache.Common;
using Microsoft.VisualStudio.Services.WebApi;
using System.Runtime.ExceptionServices;

namespace Agent.Plugins.PipelineCache
{
    public class PipelineCacheServer
    {
        private readonly bool isWindows = RuntimeInformation.IsOSPlatform(OSPlatform.Windows);

        internal async Task UploadAsync(
            AgentTaskPluginExecutionContext context,
            Fingerprint fingerprint,
            string path,
            bool isTar,
            CancellationToken cancellationToken)
        {
            VssConnection connection = context.VssConnection;
            BlobStoreClientTelemetry clientTelemetry;
            DedupManifestArtifactClient dedupManifestClient = DedupManifestArtifactClientFactory.CreateDedupManifestClient(context, connection, cancellationToken, out clientTelemetry);
            PipelineCacheClient pipelineCacheClient = this.CreateClient(clientTelemetry, context, connection);

            using (clientTelemetry)
            {
                // Check if the key exists.
                PipelineCacheActionRecord cacheRecordGet = clientTelemetry.CreateRecord<PipelineCacheActionRecord>((level, uri, type) =>
                        new PipelineCacheActionRecord(level, uri, type, PipelineArtifactConstants.RestoreCache, context));
                PipelineCacheArtifact getResult = await pipelineCacheClient.GetPipelineCacheArtifactAsync(new [] {fingerprint}, cancellationToken, cacheRecordGet);
                // Send results to CustomerIntelligence
                context.PublishTelemetry(area: PipelineArtifactConstants.AzurePipelinesAgent, feature: PipelineArtifactConstants.PipelineCache, record: cacheRecordGet);
                //If cache exists, return.
                if (getResult != null)
                {
                    context.Output($"Cache with fingerprint `{getResult.Fingerprint}` already exists.");
                    return;
                }

                // Tar the contents of the directory and create a tar, and then upload the tar.
                /* string tempPath = Path.GetTempPath();
                IEnumerable<string> absoluteFilesPaths = Directory.GetFiles(path, "*.*", SearchOption.AllDirectories);
                IEnumerable<string> absolutedirectoriesPaths = Directory.GetDirectories(path, "*.*", SearchOption.AllDirectories);

                Func<IEnumerable<string>, IEnumerable<string>> getRelativePaths = (inputPaths) => {
                    List<string> outputPaths = new List<string>();
                    foreach(var x in inputPaths)
                    {
                        outputPaths.Add(Path.GetRelativePath(path, x));
                    }
                    return outputPaths;
                };

                IEnumerable<string> relativeFilesPath = getRelativePaths(absoluteFilesPaths);
                IEnumerable<string> relativeDirectoryPath = getRelativePaths(absolutedirectoriesPaths);
                var outputFile = Path.Combine(path, Guid.NewGuid().ToString()+"files.txt");
                var outputFileStream = File.Create(outputFile);
                var archieveFile = Path.Combine(path, Guid.NewGuid().ToString() + "archieve.tar");
                using(StreamWriter stream = new StreamWriter(outputFileStream))
                {
                    foreach(var file in relativeFilesPath)
                    {
                        stream.WriteLine(file);
                    }
                    foreach(var directory in relativeDirectoryPath)
                    {
                        stream.WriteLine(directory);
                    }
                }*/

                if (!isTar)
                {
                    //Upload the pipeline artifact.
                    PipelineCacheActionRecord uploadRecord = clientTelemetry.CreateRecord<PipelineCacheActionRecord>((level, uri, type) =>
                        new PipelineCacheActionRecord(level, uri, type, nameof(dedupManifestClient.PublishAsync), context));
                    PublishResult result = await clientTelemetry.MeasureActionAsync(
                        record: uploadRecord,
                        actionAsync: async () =>
                        {
                            return await dedupManifestClient.PublishAsync(path, cancellationToken);
                        });

                    CreatePipelineCacheArtifactOptions options = new CreatePipelineCacheArtifactOptions
                    {
                        Fingerprint = fingerprint,
                        RootId = result.RootId,
                        ManifestId = result.ManifestId,
                        ProofNodes = result.ProofNodes.ToArray(),
                    };

                    // Cache the artifact
                    PipelineCacheActionRecord cacheRecord = clientTelemetry.CreateRecord<PipelineCacheActionRecord>((level, uri, type) =>
                        new PipelineCacheActionRecord(level, uri, type, PipelineArtifactConstants.SaveCache, context));
                    CreateStatus status = await pipelineCacheClient.CreatePipelineCacheArtifactAsync(options, cancellationToken, cacheRecord);

                    // Send results to CustomerIntelligence
                    context.PublishTelemetry(area: PipelineArtifactConstants.AzurePipelinesAgent, feature: PipelineArtifactConstants.PipelineCache, record: uploadRecord);
                    context.PublishTelemetry(area: PipelineArtifactConstants.AzurePipelinesAgent, feature: PipelineArtifactConstants.PipelineCache, record: cacheRecord);
                }
                else
                {
                    var archieveFile = Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString() + "archieve.tar");
                    var processTcs = new TaskCompletionSource<int>();
                    using (var cancelSource = new CancellationTokenSource())
                    using (var linkedSource = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, cancelSource.Token))
                    using (var process = new Process())
                    {
                        process.StartInfo.FileName = "tar"; // tar // @"C:\Program Files\7-Zip\7z.exe"
                        process.StartInfo.Arguments = $"-cf {archieveFile} {path}"; // tar -cf archieve.tar -T "C:/tEMP_pATHSfiles.txt"
                        process.StartInfo.UseShellExecute = false;
                        process.StartInfo.RedirectStandardInput = true;
                        process.StartInfo.RedirectStandardOutput = true;
                        process.StartInfo.RedirectStandardError = true;
                        process.EnableRaisingEvents = true;
                        process.Exited += (sender, args) =>
                        {
                            cancelSource.Cancel();
                            processTcs.SetResult(process.ExitCode);
                        };

                        try
                        {
                            context.Info($"Starting '{process.StartInfo.FileName}' with arguments '{process.StartInfo.Arguments}'...");
                            process.Start();
                        }
                        catch (Exception e)
                        {
                            process.Kill();
                            // delete files.
                            ExceptionDispatchInfo.Capture(e).Throw();
                        }

                        var output = new List<string>();
                        Func<string, StreamReader, Task> readLines = (prefix, reader) => Task.Run(async () =>
                        {
                            string line;
                            while (null != (line = await reader.ReadLineAsync()))
                            {
                                lock (output)
                                {
                                    output.Add($"{prefix}{line}");
                                }
                            }
                        });
                        Task readStdOut = readLines("stdout: ", process.StandardOutput);
                        Task readStdError = readLines("stderr: ", process.StandardError);

                        // Our goal is to always have the process ended or killed by the time we exit the function.
                        try
                        {
                            using (cancellationToken.Register(() => process.Kill()))
                            {
                                // readStdOut and readStdError should only fail if the process dies
                                // processTcs.Task cannot fail as we only call SetResult on processTcs
                                await Task.WhenAll(readStdOut, readStdError, processTcs.Task);
                            }

                            int exitCode = await processTcs.Task;

                            if (exitCode == 0)
                            {
                                context.Verbose($"Process exit code: {exitCode}");
                                // delete archieve file.
                                foreach (string line in output)
                                {
                                    context.Verbose(line);
                                }
                            }
                            else
                            {
                                throw new Exception($"Process returned non-zero exit code: {exitCode}");
                            }
                        }
                        catch (Exception e)
                        {
                            // Delete archieve file.
                            foreach (string line in output)
                            {
                                context.Info(line);
                            }
                            ExceptionDispatchInfo.Capture(e).Throw();
                        }
                    } // end of tarring process.

                    //Upload the pipeline artifact.
                    PipelineCacheActionRecord uploadRecord = clientTelemetry.CreateRecord<PipelineCacheActionRecord>((level, uri, type) =>
                        new PipelineCacheActionRecord(level, uri, type, nameof(dedupManifestClient.PublishAsync), context));
                    PublishResult result = await clientTelemetry.MeasureActionAsync(
                        record: uploadRecord,
                        actionAsync: async () =>
                        {
                            return await dedupManifestClient.PublishAsync(archieveFile, cancellationToken);
                        });

                    CreatePipelineCacheArtifactOptions options = new CreatePipelineCacheArtifactOptions
                    {
                        Fingerprint = fingerprint,
                        RootId = result.RootId,
                        ManifestId = result.ManifestId,
                        ProofNodes = result.ProofNodes.ToArray(),
                        ContentFormat = ContentFormatConstants.SingleTar
                    };

                    // Cache the artifact
                    PipelineCacheActionRecord cacheRecord = clientTelemetry.CreateRecord<PipelineCacheActionRecord>((level, uri, type) =>
                        new PipelineCacheActionRecord(level, uri, type, PipelineArtifactConstants.SaveCache, context));
                    CreateStatus status = await pipelineCacheClient.CreatePipelineCacheArtifactAsync(options, cancellationToken, cacheRecord);
                }
                context.Output("Saved item.");
            }
        }

        internal async Task DownloadAsync(
            AgentTaskPluginExecutionContext context,
            Fingerprint[] fingerprints,
            string path,
            string cacheHitVariable,
            bool isTar,
            CancellationToken cancellationToken)
        {
            VssConnection connection = context.VssConnection;
            BlobStoreClientTelemetry clientTelemetry;
            DedupManifestArtifactClient dedupManifestClient = DedupManifestArtifactClientFactory.CreateDedupManifestClient(context, connection, cancellationToken, out clientTelemetry);
            PipelineCacheClient pipelineCacheClient = this.CreateClient(clientTelemetry, context, connection);

            using (clientTelemetry)
            {
                PipelineCacheActionRecord cacheRecord = clientTelemetry.CreateRecord<PipelineCacheActionRecord>((level, uri, type) =>
                        new PipelineCacheActionRecord(level, uri, type, PipelineArtifactConstants.RestoreCache, context));
                PipelineCacheArtifact result = await pipelineCacheClient.GetPipelineCacheArtifactAsync(fingerprints, cancellationToken, cacheRecord);

                // Send results to CustomerIntelligence
                context.PublishTelemetry(area: PipelineArtifactConstants.AzurePipelinesAgent, feature: PipelineArtifactConstants.PipelineCache, record: cacheRecord);

                if (result != null)
                {
                    context.Output($"Entry found at fingerprint: `{result.Fingerprint.ToString()}`");
                    context.Verbose($"Manifest ID is: {result.ManifestId.ValueString}");
                    PipelineCacheActionRecord downloadRecord = clientTelemetry.CreateRecord<PipelineCacheActionRecord>((level, uri, type) =>
                        new PipelineCacheActionRecord(level, uri, type, nameof(DownloadAsync), context));
                    await clientTelemetry.MeasureActionAsync(
                        record: downloadRecord,
                        actionAsync: async () =>
                        {
                            await this.DownloadPipelineCacheAsync(context, dedupManifestClient, result.ManifestId, path, isTar, result.ContentFormat, cancellationToken);
                        });

                    // Send results to CustomerIntelligence
                    context.PublishTelemetry(area: PipelineArtifactConstants.AzurePipelinesAgent, feature: PipelineArtifactConstants.PipelineCache, record: downloadRecord);
                    
                    context.Output("Cache restored.");
                }

                if (!string.IsNullOrEmpty(cacheHitVariable))
                {
                    if (result == null) {
                        context.SetVariable(cacheHitVariable, "false");
                    }  else  {
                        context.Verbose($"Exact fingerprint: `{result.Fingerprint.ToString()}`");

                        bool foundExact = false;
                        foreach(var fingerprint in fingerprints)
                        {
                            context.Verbose($"This fingerprint: `{fingerprint.ToString()}`");

                            if(fingerprint == result.Fingerprint)
                            {
                                foundExact = true;
                                break;
                            }
                        }

                        context.SetVariable(cacheHitVariable, foundExact ? "true" : "inexact");
                    }
                }
            }
        }

        private PipelineCacheClient CreateClient(
            BlobStoreClientTelemetry blobStoreClientTelemetry,
            AgentTaskPluginExecutionContext context,
            VssConnection connection)
        {
            var tracer = new CallbackAppTraceSource(str => context.Output(str), System.Diagnostics.SourceLevels.Information);
            IClock clock = UtcClock.Instance;
            var pipelineCacheHttpClient = connection.GetClient<PipelineCacheHttpClient>();
            var pipelineCacheClient = new PipelineCacheClient(blobStoreClientTelemetry, pipelineCacheHttpClient, clock, tracer);

            return pipelineCacheClient;
        }

        private async Task DownloadPipelineCacheAsync(
            AgentTaskPluginExecutionContext context,
            DedupManifestArtifactClient dedupManifestClient,
            DedupIdentifier manifestId,
            string targetDirectory,
            bool isTar,
            string contentFormat,
            CancellationToken cancellationToken)
        {

            // Throw for bunch of invalid combinations of istar and contentFormat.
            if (!isTar)
            {
                DownloadDedupManifestArtifactOptions options = DownloadDedupManifestArtifactOptions.CreateWithManifestId(
                    manifestId,
                    targetDirectory,
                    proxyUri: null,
                    minimatchPatterns: null);

                await dedupManifestClient.DownloadAsync(options, cancellationToken);
            }
            else
            {
                DownloadDedupManifestArtifactOptions options = DownloadDedupManifestArtifactOptions.CreateWithManifestId(
                    manifestId,
                    targetDirectory,
                    proxyUri: null,
                    minimatchPatterns: null);

                options.IsTar = true;

                var processTcs = new TaskCompletionSource<int>();

                using (var cancelSource = new CancellationTokenSource())
                using (var linkedSource = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, cancelSource.Token))
                using (var process = new Process())
                {
                    process.StartInfo.FileName = !isWindows ? @"C:\Program Files\7-Zip\7z.exe" : "tar"; // tar // @"C:\Program Files\7-Zip\7z.exe"
                    process.StartInfo.Arguments = !isWindows ? $"x -si -aoa -o{targetDirectory} -ttar" : $"-xf - -C {targetDirectory}"; // -xf - -P -C {targetDirectory} // tarring.StartInfo.Arguments = $"x -si -aoa -o{targetDirectory} -ttar";
                    process.StartInfo.UseShellExecute = false;
                    process.StartInfo.RedirectStandardInput = true;
                    process.StartInfo.RedirectStandardOutput = true;
                    process.StartInfo.RedirectStandardError = true;
                    process.EnableRaisingEvents = true;
                    process.Exited += (sender, args) =>
                    {
                        cancelSource.Cancel();
                        processTcs.SetResult(process.ExitCode);
                    };

                    context.Info($"Starting '{process.StartInfo.FileName}' with arguments '{process.StartInfo.Arguments}'...");
                    process.Start();

                    var output = new List<string>();

                    Func<string, StreamReader, Task> readLines = (prefix, reader) => Task.Run(async () =>
                    {
                        string line;
                        while (null != (line = await reader.ReadLineAsync()))
                        {
                            lock (output)
                            {
                                output.Add($"{prefix}{line}");
                            }
                        }
                    });

                    Task readStdOut = readLines("stdout: ", process.StandardOutput);
                    Task readStdError = readLines("stderr: ", process.StandardError);
                    Task downloadTask = Task.Run(async () =>
                    {
                        try
                        {
                            options.Stream = process.StandardInput.BaseStream;
                            await dedupManifestClient.DownloadAsync(options, linkedSource.Token);
                            //process.StandardInput.BaseStream.Close();
                            options.Stream.Close();
                        }
                        catch (Exception e)
                        {
                            process.Kill();
                            ExceptionDispatchInfo.Capture(e).Throw();
                        }
                    });

                    // Our goal is to always have the process ended or killed by the time we exit the function.

                    try
                    {
                        using (cancellationToken.Register(() => process.Kill()))
                        {
                            // readStdOut and readStdError should only fail if the process dies
                            // processTcs.Task cannot fail as we only call SetResult on processTcs
                            // downloadTask *can* fail, but when it does, it will also kill the process
                            await Task.WhenAll(readStdOut, readStdError, processTcs.Task, downloadTask);
                        }

                        int exitCode = await processTcs.Task;

                        if (exitCode == 0)
                        {
                            context.Verbose($"Process exit code: {exitCode}");
                            foreach (string line in output)
                            {
                                context.Verbose(line);
                            }
                        }
                        else
                        {
                            throw new Exception($"Process returned non-zero exit code: {exitCode}");
                        }
                    }
                    catch (Exception e)
                    {
                        foreach (string line in output)
                        {
                            context.Info(line);
                        }
                        ExceptionDispatchInfo.Capture(e).Throw();
                    }
                }
            }

        }
    }
}