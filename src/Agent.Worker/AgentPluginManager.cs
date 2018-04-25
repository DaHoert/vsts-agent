using Microsoft.TeamFoundation.DistributedTask.WebApi;
using Microsoft.VisualStudio.Services.Agent.PluginCore;
using Microsoft.VisualStudio.Services.Agent.Util;
using Microsoft.VisualStudio.Services.WebApi;
using System;
using System.Collections.Generic;
using System.IO;
using System.Reflection;
using System.Runtime.Loader;
using System.Threading;
using System.Threading.Tasks;

namespace Microsoft.VisualStudio.Services.Agent.Worker
{
    [ServiceLocator(Default = typeof(AgentPluginManager))]
    public interface IAgentPluginManager : IAgentService
    {
        Dictionary<Guid, Dictionary<string, Definition>> SupportedTasks { get; }
        Dictionary<string, HashSet<string>> SupportedLoggingCommands { get; }

        Task RunPluginTaskAsync(IExecutionContext context, Guid taskId, Dictionary<string, string> inputs, string stage, EventHandler<ProcessDataReceivedEventArgs> outputHandler);
        void ProcessCommand(IExecutionContext context, Command command);
    }

    public sealed class AgentPluginManager : AgentService, IAgentPluginManager
    {
        private readonly Dictionary<string, HashSet<string>> _supportedLoggingCommands = new Dictionary<string, HashSet<string>>(StringComparer.OrdinalIgnoreCase);
        private readonly Dictionary<Guid, Dictionary<string, Definition>> _supportedTasks = new Dictionary<Guid, Dictionary<string, Definition>>();
        private readonly Dictionary<string, Dictionary<string, AgentPluginInfo>> _loggingCommandAgentPlugins = new Dictionary<string, Dictionary<string, AgentPluginInfo>>(StringComparer.OrdinalIgnoreCase);
        private readonly Dictionary<Guid, AgentPluginInfo> _taskAgentPlugins = new Dictionary<Guid, AgentPluginInfo>();
        public Dictionary<string, HashSet<string>> SupportedLoggingCommands => _supportedLoggingCommands;
        public Dictionary<Guid, Dictionary<string, Definition>> SupportedTasks => _supportedTasks;

        public override void Initialize(IHostContext hostContext)
        {
            base.Initialize(hostContext);
            _supportedLoggingCommands["artifact"] = new HashSet<string>(StringComparer.OrdinalIgnoreCase) { "upload" };
            _loggingCommandAgentPlugins["artifact"] = new Dictionary<string, AgentPluginInfo>(StringComparer.OrdinalIgnoreCase)
            {
                {
                    "upload",
                    new AgentPluginInfo()
                    {
                        AgentPluginAssembly = "Agent.DropPlugin",
                        AgentPluginEntryPointClass = "Agent.DropPlugin.ArtifactUploadCommand"
                    }
                }
            };

            _taskAgentPlugins[WellKnownAgentPluginTasks.CheckoutTaskId] = new AgentPluginInfo()
            {
                AgentPluginAssembly = "Agent.RepositoryPlugin",
                AgentPluginEntryPointClass = "Agent.RepositoryPlugin.CheckoutTask"
            };

            _supportedTasks[WellKnownAgentPluginTasks.CheckoutTaskId] = new Dictionary<string, Definition>(StringComparer.OrdinalIgnoreCase);

            IAgentTaskPlugin taskPlugin = null;
            string typeName = $"{_taskAgentPlugins[WellKnownAgentPluginTasks.CheckoutTaskId].AgentPluginEntryPointClass}, {_taskAgentPlugins[WellKnownAgentPluginTasks.CheckoutTaskId].AgentPluginAssembly}";
            AssemblyLoadContext.Default.Resolving += ResolveAssembly;
            try
            {
                Type type = Type.GetType(typeName, throwOnError: true);
                taskPlugin = Activator.CreateInstance(type) as IAgentTaskPlugin;
            }
            finally
            {
                AssemblyLoadContext.Default.Resolving -= ResolveAssembly;
            }

            Util.ArgUtil.NotNull(taskPlugin, nameof(taskPlugin));
            Util.ArgUtil.NotNullOrEmpty(taskPlugin.Version, nameof(taskPlugin.Version));
            _supportedTasks[WellKnownAgentPluginTasks.CheckoutTaskId][taskPlugin.Version] = new Definition() { Directory = HostContext.GetDirectory(WellKnownDirectory.Work) };
            _supportedTasks[WellKnownAgentPluginTasks.CheckoutTaskId][taskPlugin.Version].Data = new DefinitionData()
            {
                Author = taskPlugin.Author,
                Description = taskPlugin.Description,
                HelpMarkDown = taskPlugin.HelpMarkDown,
                FriendlyName = taskPlugin.FriendlyName,
                Inputs = taskPlugin.Inputs
            };

            if (taskPlugin.Stages.Contains("pre"))
            {
                _supportedTasks[WellKnownAgentPluginTasks.CheckoutTaskId][taskPlugin.Version].Data.PreJobExecution = new ExecutionData()
                {
                    AgentPlugin = new AgentPluginHandlerData()
                    {
                        Target = WellKnownAgentPluginTasks.CheckoutTaskId.ToString("D"),
                        EntryPoint = _taskAgentPlugins[WellKnownAgentPluginTasks.CheckoutTaskId].AgentPluginEntryPointClass,
                        Stage = "pre"
                    }
                };
            }

            if (taskPlugin.Stages.Contains("main"))
            {
                _supportedTasks[WellKnownAgentPluginTasks.CheckoutTaskId][taskPlugin.Version].Data.Execution = new ExecutionData()
                {
                    AgentPlugin = new AgentPluginHandlerData()
                    {
                        Target = WellKnownAgentPluginTasks.CheckoutTaskId.ToString("D"),
                        EntryPoint = _taskAgentPlugins[WellKnownAgentPluginTasks.CheckoutTaskId].AgentPluginEntryPointClass,
                        Stage = "main"
                    }
                };
            }

            if (taskPlugin.Stages.Contains("post"))
            {
                _supportedTasks[WellKnownAgentPluginTasks.CheckoutTaskId][taskPlugin.Version].Data.PostJobExecution = new ExecutionData()
                {
                    AgentPlugin = new AgentPluginHandlerData()
                    {
                        Target = WellKnownAgentPluginTasks.CheckoutTaskId.ToString("D"),
                        EntryPoint = _taskAgentPlugins[WellKnownAgentPluginTasks.CheckoutTaskId].AgentPluginEntryPointClass,
                        Stage = "post"
                    }
                };
            }
        }

        private Assembly ResolveAssembly(AssemblyLoadContext context, AssemblyName assembly)
        {
            string assemblyFilename = assembly.Name + ".dll";
            return context.LoadFromAssemblyPath(Path.Combine(HostContext.GetDirectory(WellKnownDirectory.Bin), assemblyFilename));
        }

        public async Task RunPluginTaskAsync(IExecutionContext context, Guid taskId, Dictionary<string, string> inputs, string stage, EventHandler<ProcessDataReceivedEventArgs> outputHandler)
        {
            _taskAgentPlugins.TryGetValue(taskId, out AgentPluginInfo plugin);
            Util.ArgUtil.NotNull(plugin, nameof(plugin));

            // Resolve the working directory.
            string workingDirectory = HostContext.GetDirectory(WellKnownDirectory.Bin);
            Util.ArgUtil.Directory(workingDirectory, nameof(workingDirectory));

            // Agent.PluginHost
            string file = Path.Combine(HostContext.GetDirectory(WellKnownDirectory.Bin), $"Agent.PluginHost{Util.IOUtil.ExeExtension}");

            // Agent.PluginHost's arguments
            string arguments = $"task \"{plugin.AgentPluginEntryPointClass}, {plugin.AgentPluginAssembly}\"";

            // construct plugin context
            AgentTaskPluginExecutionContext pluginContext = new AgentTaskPluginExecutionContext
            {
                Inputs = inputs,
                Stage = stage,
                Repositories = context.Repositories,
                Endpoints = context.Endpoints,
                PrependPath = context.PrependPath
            };
            // variables
            foreach (var publicVar in context.Variables.Public)
            {
                pluginContext.Variables[publicVar.Key] = publicVar.Value;
            }
            foreach (var publicVar in context.Variables.Private)
            {
                pluginContext.Variables[publicVar.Key] = new VariableValue(publicVar.Value, true);
            }
            // task variables (used by wrapper task)
            foreach (var publicVar in context.TaskVariables.Public)
            {
                pluginContext.TaskVariables[publicVar.Key] = publicVar.Value;
            }
            foreach (var publicVar in context.TaskVariables.Private)
            {
                pluginContext.TaskVariables[publicVar.Key] = new VariableValue(publicVar.Value, true);
            }

            using (var processInvoker = HostContext.CreateService<IProcessInvoker>())
            {
                processInvoker.OutputDataReceived += outputHandler;
                processInvoker.ErrorDataReceived += outputHandler;

                // Execute the process. Exit code 0 should always be returned.
                // A non-zero exit code indicates infrastructural failure.
                // Task failure should be communicated over STDOUT using ## commands.
                await processInvoker.ExecuteAsync(workingDirectory: workingDirectory,
                                                  fileName: file,
                                                  arguments: arguments,
                                                  environment: null,
                                                  requireExitCodeZero: true,
                                                  outputEncoding: null,
                                                  killProcessOnCancel: false,
                                                  contentsToStandardIn: new List<string>() { JsonUtility.ToString(pluginContext) },
                                                  cancellationToken: context.CancellationToken);
            }
        }

        public void ProcessCommand(IExecutionContext context, Command command)
        {
            // queue async command task to run agent plugin.
            context.Debug($"Process {command.Area}.{command.Event} command at backend.");
            var commandContext = HostContext.CreateService<IAsyncCommandContext>();
            commandContext.InitializeCommandContext(context, "Process Command");
            commandContext.Task = ProcessPluginCommandAsync(commandContext, command, context.CancellationToken);
            context.AsyncCommands.Add(commandContext);
        }

        private async Task ProcessPluginCommandAsync(IAsyncCommandContext context, Command command, CancellationToken token)
        {
            if (_loggingCommandAgentPlugins.ContainsKey(command.Area) && _loggingCommandAgentPlugins[command.Area].ContainsKey(command.Event))
            {
                var plugin = _loggingCommandAgentPlugins[command.Area][command.Event];
                Util.ArgUtil.NotNull(plugin, nameof(plugin));

                // Resolve the working directory.
                string workingDirectory = HostContext.GetDirectory(WellKnownDirectory.Bin);
                Util.ArgUtil.Directory(workingDirectory, nameof(workingDirectory));

                // Agent.PluginHost
                string file = Path.Combine(HostContext.GetDirectory(WellKnownDirectory.Bin), $"Agent.PluginHost{Util.IOUtil.ExeExtension}");

                // Agent.PluginHost's arguments
                string arguments = $"command \"{plugin.AgentPluginEntryPointClass}, {plugin.AgentPluginAssembly}\"";

                // construct plugin context
                AgentCommandPluginExecutionContext pluginContext = new AgentCommandPluginExecutionContext
                {
                    Data = command.Data,
                    Properties = command.Properties,
                    Repositories = context.RawContext.Repositories,
                    Endpoints = context.RawContext.Endpoints,
                };
                // variables
                foreach (var publicVar in context.RawContext.Variables.Public)
                {
                    pluginContext.Variables[publicVar.Key] = publicVar.Value;
                }
                foreach (var publicVar in context.RawContext.Variables.Private)
                {
                    pluginContext.Variables[publicVar.Key] = new VariableValue(publicVar.Value, true);
                }

                using (var processInvoker = HostContext.CreateService<IProcessInvoker>())
                {
                    object stderrLock = new object();
                    List<string> stderr = new List<string>();
                    processInvoker.OutputDataReceived += (object sender, ProcessDataReceivedEventArgs e) =>
                    {
                        context.Output(e.Data);
                    };
                    processInvoker.ErrorDataReceived += (object sender, ProcessDataReceivedEventArgs e) =>
                    {
                        lock (stderrLock)
                        {
                            stderr.Add(e.Data);
                        };
                    }; ;

                    // Execute the process. Exit code 0 should always be returned.
                    // A non-zero exit code indicates infrastructural failure.
                    // Task failure should be communicated over STDOUT using ## commands.
                    int returnCode = await processInvoker.ExecuteAsync(workingDirectory: workingDirectory,
                                                      fileName: file,
                                                      arguments: arguments,
                                                      environment: null,
                                                      requireExitCodeZero: false,
                                                      outputEncoding: null,
                                                      killProcessOnCancel: false,
                                                      contentsToStandardIn: new List<string>() { JsonUtility.ToString(pluginContext) },
                                                      cancellationToken: token);

                    if (returnCode != 0)
                    {
                        context.Output(string.Join(Environment.NewLine, stderr));
                        throw new ProcessExitCodeException(returnCode, file, arguments);
                    }
                    else if (stderr.Count > 0)
                    {
                        throw new InvalidOperationException(string.Join(Environment.NewLine, stderr));
                    }
                }
            }
            else
            {
                throw new NotSupportedException(command.ToString());
            }
        }
    }

    public class AgentPluginInfo
    {
        public string AgentPluginAssembly { get; set; }
        public string AgentPluginEntryPointClass { get; set; }
    }

    public static class WellKnownAgentPluginTasks
    {
        public static Guid CheckoutTaskId = new Guid("c61807ba-5e20-4b70-bd8c-3683c9f74003");
    }
}
