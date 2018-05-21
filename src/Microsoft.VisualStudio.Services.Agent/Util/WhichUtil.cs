using System;
using System.IO;
using System.Linq;

namespace Microsoft.VisualStudio.Services.Agent.Util
{
    [ServiceLocator(Default = typeof(WhichUtil))]
    public interface IWhichUtil : IAgentService
    {
        string Which(string command, bool require = false);
    }

    // TODO: Should also search for a file with the exact file name match regardless of extension. For example, should be able to resolve a PowerShell script from the PATH even though ps1 is not included in PATHEXT.
    public sealed class WhichUtil : AgentService, IWhichUtil
    {
        public string Which(string command, bool require = false)
        {
            ArgUtil.NotNullOrEmpty(command, nameof(command));
            Trace.Info($"Which: '{command}'");
#if OS_WINDOWS
            string path = Environment.GetEnvironmentVariable("Path");
#else
            string path = Environment.GetEnvironmentVariable("PATH");
#endif
            if (string.IsNullOrEmpty(path))
            {
                Trace.Info("PATH environment variable not defined.");
                path = path ?? string.Empty;
            }

            string[] pathSegments = path.Split(new Char[] { Path.PathSeparator }, StringSplitOptions.RemoveEmptyEntries);
            for (int i = 0; i < pathSegments.Length; i++)
            {
                pathSegments[i] = Environment.ExpandEnvironmentVariables(pathSegments[i]);
            }

            foreach (string pathSegment in pathSegments)
            {
                if (!string.IsNullOrEmpty(pathSegment) && Directory.Exists(pathSegment))
                {
                    string[] matches = null;
#if OS_WINDOWS
                    string pathExt = Environment.GetEnvironmentVariable("PATHEXT");
                    if (string.IsNullOrEmpty(pathExt))
                    {
                        // XP's system default value for PATHEXT system variable
                        pathExt = ".com;.exe;.bat;.cmd;.vbs;.vbe;.js;.jse;.wsf;.wsh";
                    }

                    string[] pathExtSegments = pathExt.Split(new string[] { ";" }, StringSplitOptions.RemoveEmptyEntries);

                    // if command already has an extension.
                    if (pathExtSegments.Any(ext => command.EndsWith(ext, StringComparison.OrdinalIgnoreCase)))
                    {
                        try
                        {
                            matches = Directory.GetFiles(pathSegment, command);
                        }
                        catch (UnauthorizedAccessException ex)
                        {
                            Trace.Error("Ignore UnauthorizedAccess exception during Which.");
                            Trace.Error(ex);
                        }

                        if (matches != null && matches.Length > 0)
                        {
                            Trace.Info("Location: '{0}'", matches.First());
                            return matches.First();
                        }
                    }
                    else
                    {
                        string searchPattern;
                        searchPattern = StringUtil.Format($"{command}.*");
                        try
                        {
                            matches = Directory.GetFiles(pathSegment, searchPattern);
                        }
                        catch (UnauthorizedAccessException ex)
                        {
                            Trace.Error("Ignore UnauthorizedAccess exception during Which.");
                            Trace.Error(ex);
                        }

                        if (matches != null && matches.Length > 0)
                        {
                            // add extension.
                            for (int i = 0; i < pathExtSegments.Length; i++)
                            {
                                string fullPath = Path.Combine(pathSegment, $"{command}{pathExtSegments[i]}");
                                if (matches.Any(p => p.Equals(fullPath, StringComparison.OrdinalIgnoreCase)))
                                {
                                    Trace.Info($"Location: '{fullPath}'");
                                    return fullPath;
                                }
                            }
                        }
                    }
#else
                    try
                    {
                        matches = Directory.GetFiles(pathSegment, command);
                    }
                    catch (UnauthorizedAccessException ex)
                    {
                        Trace.Error("Ignore UnauthorizedAccess exception during Which.");
                        Trace.Error(ex);
                    }

                    if (matches != null && matches.Length > 0)
                    {
                        Trace.Info("Location: '{0}'", matches.First());
                        return matches.First();
                    }
#endif
                }
            }

            Trace.Info("Not found.");
            if (require)
            {
                throw new FileNotFoundException(
                    message: StringUtil.Loc("FileNotFound", command),
                    fileName: command);
            }

            return null;
        }
    }
}
