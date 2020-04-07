// Copyright (c) Microsoft Corporation.
// Licensed under the MIT License.

namespace Agent.Sdk.Knob
{

    public class AgentKnobs
    {
        public static readonly Knob UseNode10 = new Knob(
            nameof(UseNode10),
            "Forces the agent to use Node 10 handler for all Node-based tasks",
            new RuntimeKnobSource("AGENT_USE_NODE10"),
            new EnvironmentKnobSource("AGENT_USE_NODE10"),
            new BuiltInDefaultKnobSource("false"));

        public static readonly Knob DisableAgentDowngrade = new Knob(
            nameof(DisableAgentDowngrade),
            "Disable agent downgrades. Upgrades will still be allowed.",
            new EnvironmentKnobSource("AZP_AGENT_DOWNGRADE_DISABLED"),
            new BuiltInDefaultKnobSource("false"));
        
        public const string QuietCheckoutRuntimeVarName = "agent.source.checkout.quiet";
        public const string QuietCheckoutEnvVarName = "AGENT_SOURCE_CHECKOUT_QUIET";
        
        public static readonly Knob QuietCheckout = new Knob(
            nameof(QuietCheckout),
            "Aggressively reduce what gets logged to the console when checking out source.",
            new RuntimeKnobSource(QuietCheckoutRuntimeVarName),
            new EnvironmentKnobSource(QuietCheckoutEnvVarName),
            new BuiltInDefaultKnobSource("false"));

        public static readonly Knob ProxyAddress = new Knob(
            nameof(ProxyAddress),
            "Proxy server address if one exists",
            new EnvironmentKnobSource("VSTS_HTTP_PROXY"),
            new EnvironmentKnobSource("http_proxy"),
            new BuiltInDefaultKnobSource(string.Empty));

        public static readonly Knob ProxyUsername = new Knob(
            nameof(ProxyUsername),
            "Proxy username if one exists",
            new EnvironmentKnobSource("VSTS_HTTP_PROXY_USERNAME"),
            new BuiltInDefaultKnobSource(string.Empty));

        public static readonly Knob ProxyPassword = new Knob(
            nameof(ProxyPassword),
            "Proxy password if one exists",
            new EnvironmentKnobSource("VSTS_HTTP_PROXY_PASSWORD"),
            new BuiltInDefaultKnobSource(string.Empty));

        public static readonly Knob NoProxy = new Knob(
            nameof(NoProxy),
            "Proxy bypass list if one exists. Should be comma seperated",
            new EnvironmentKnobSource("no_proxy"),
            new BuiltInDefaultKnobSource(string.Empty));

        public static readonly Knob HttpRetryCount = new Knob(
            nameof(HttpRetryCount),
            "Number of times to retry Http requests",
            new EnvironmentKnobSource("VSTS_HTTP_RETRY"),
            new BuiltInDefaultKnobSource("3"));

        public static readonly Knob HttpTimeout = new Knob(
            nameof(HttpTimeout),
            "Timeout for Http requests",
            new EnvironmentKnobSource("VSTS_HTTP_TIMEOUT"),
            new BuiltInDefaultKnobSource("100"));

        public static readonly Knob HttpTrace = new Knob(
            nameof(HttpTrace),
            "Enable http trace if true",
            new EnvironmentKnobSource("VSTS_AGENT_HTTPTRACE"),
            new BuiltInDefaultKnobSource("false"));

        public static readonly Knob AgentPerflog = new Knob(
            nameof(AgentPerflog),
            "If set, writes a perf counter trace for the agent. Writes to the location set in this variable.",
            new EnvironmentKnobSource("VSTS_AGENT_PERFLOG"),
            new BuiltInDefaultKnobSource(string.Empty));

        public static readonly Knob AgentToolsDirectory = new Knob(
            nameof(AgentToolsDirectory),
            "The location to look for/create the agents tool cache",
            new EnvironmentKnobSource("AGENT_TOOLSDIRECTORY"),
            new EnvironmentKnobSource("agent.ToolsDirectory"),
            new BuiltInDefaultKnobSource(string.Empty));

        public static readonly Knob PermissionsCheckFailsafe = new Knob(
            nameof(PermissionsCheckFailsafe),
            "Maximum depth of file permitted in directory hierarchy when checking permissions. Check to avoid accidentally entering infinite loops.",
            new EnvironmentKnobSource("AGENT_TEST_VALIDATE_EXECUTE_PERMISSIONS_FAILSAFE"),
            new BuiltInDefaultKnobSource("100"));

        public static readonly Knob PreferGitFromPath = new Knob(
            nameof(PreferGitFromPath),
            "Determines which Git we will use on Windows. By default, we prefer the built-in portable git in the agent's externals folder, setting this to true makes the agent find git.exe from %PATH% if possible.",
            new RuntimeKnobSource("system.prefergitfrompath"),
            new EnvironmentKnobSource("system.prefergitfrompath"),
            new BuiltInDefaultKnobSource("true"));

        public static readonly Knob AllowUnsafeMultilineSecret = new Knob(
            nameof(AllowUnsafeMultilineSecret),
            "WARNING: enabling this may allow secrets to leak. Allows multi-line secrets to be set. Unsafe because it is possible for log lines to get dropped in agent failure cases, causing the secret to not get correctly masked. We recommend leaving this option off.",
            new RuntimeKnobSource("SYSTEM_UNSAFEALLOWMULTILINESECRET"),
            new EnvironmentKnobSource("SYSTEM_UNSAFEALLOWMULTILINESECRET"),
            new BuiltInDefaultKnobSource("false"));

        public static readonly Knob OverwriteTemp = new Knob(
            nameof(OverwriteTemp),
            "If true, the system temp variable will be overriden to point to the agent's temp directory.",
            new RuntimeKnobSource("VSTS_OVERWRITE_TEMP"),
            new EnvironmentKnobSource("VSTS_OVERWRITE_TEMP"),
            new BuiltInDefaultKnobSource("false"));

        public static readonly Knob PreferPowershellHandlerOnContainers = new Knob(
            nameof(PreferPowershellHandlerOnContainers),
            "If true, prefer using the PowerShell handler on containers for tasks that provide both a Node and PowerShell handler version.",
            new RuntimeKnobSource("agent.preferPowerShellOnContainers"),
            new EnvironmentKnobSource("AGENT_PREFER_POWERSHELL_ON_CONTAINERS"),
            new BuiltInDefaultKnobSource("false"));

        public static readonly Knob TraceVerbose = new Knob(
            nameof(TraceVerbose),
            "If set to anything, trace level will be verbose",
            new EnvironmentKnobSource("VSTSAGENT_TRACE"),
            new BuiltInDefaultKnobSource(string.Empty));
    }

}
