using Microsoft.TeamFoundation.Build.WebApi;
using Pipelines = Microsoft.TeamFoundation.DistributedTask.Pipelines;
using Microsoft.TeamFoundation.DistributedTask.WebApi;
using Microsoft.VisualStudio.Services.Agent.Util;
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading.Tasks;

namespace Microsoft.VisualStudio.Services.Agent.Worker.Build
{
    public sealed class BuildJobExtension : JobExtension
    {
        public override Type ExtensionType => typeof(IJobExtension);
        public override HostTypes HostType => HostTypes.Build;

        private Pipelines.RepositoryResource Repository { set; get; }
        private ISourceProvider SourceProvider { set; get; }

        public override IStep GetExtensionPreJobStep(IExecutionContext jobContext)
        {
            return null;
        }

        public override IStep GetExtensionPostJobStep(IExecutionContext jobContext)
        {
            return null;
        }

        // 1. use source provide to solve path, if solved result is rooted, return full path.
        // 2. prefix default path root (build.sourcesDirectory), if result is rooted, return full path.
        public override string GetRootedPath(IExecutionContext context, string path)
        {
            string rootedPath = null;

            if (SourceProvider != null && Repository != null)
            {
                path = SourceProvider.GetLocalPath(context, Repository, path) ?? string.Empty;
                Trace.Info($"Build JobExtension resolving path use source provide: {path}");

                if (!string.IsNullOrEmpty(path) &&
                    path.IndexOfAny(Path.GetInvalidPathChars()) < 0 &&
                    Path.IsPathRooted(path))
                {
                    try
                    {
                        rootedPath = Path.GetFullPath(path);
                        Trace.Info($"Path resolved by source provider is a rooted path, return absolute path: {rootedPath}");
                        return rootedPath;
                    }
                    catch (Exception ex)
                    {
                        Trace.Info($"Path resolved by source provider is a rooted path, but it is not a full qualified path: {path}");
                        Trace.Error(ex);
                    }
                }
            }

            string defaultPathRoot = context.Variables.Get(Constants.Variables.Build.SourcesDirectory) ?? string.Empty;
            Trace.Info($"The Default Path Root of Build JobExtension is build.sourcesDirectory: {defaultPathRoot}");

            if (defaultPathRoot != null && defaultPathRoot.IndexOfAny(Path.GetInvalidPathChars()) < 0 &&
                path != null && path.IndexOfAny(Path.GetInvalidPathChars()) < 0)
            {
                path = Path.Combine(defaultPathRoot, path);
                Trace.Info($"After prefix Default Path Root provide by JobExtension: {path}");
                if (Path.IsPathRooted(path))
                {
                    try
                    {
                        rootedPath = Path.GetFullPath(path);
                        Trace.Info($"Return absolute path after prefix DefaultPathRoot: {rootedPath}");
                        return rootedPath;
                    }
                    catch (Exception ex)
                    {
                        Trace.Error(ex);
                        Trace.Info($"After prefix Default Path Root provide by JobExtension, the Path is a rooted path, but it is not full qualified, return the path: {path}.");
                        return path;
                    }
                }
            }

            return rootedPath;
        }

        public override void ConvertLocalPath(IExecutionContext context, string localPath, out string repoName, out string sourcePath)
        {
            repoName = "";

            // If no repo was found, send back an empty repo with original path.
            sourcePath = localPath;

            if (!string.IsNullOrEmpty(localPath) &&
                File.Exists(localPath) &&
                Repository != null &&
                SourceProvider != null)
            {
                // If we found a repo, calculate the relative path to the file
                repoName = Repository.Properties.Get<string>(Pipelines.RepositoryPropertyNames.Name);
                sourcePath = IOUtil.MakeRelative(localPath, context.Variables.Get(Constants.Variables.Build.SourcesDirectory));
            }
        }

        // Prepare build directory
        // Set all build related variables
        public override void InitializeJobExtension(IExecutionContext executionContext, IList<Pipelines.JobStep> steps, Pipelines.WorkspaceOptions workspace)
        {
            // Validate args.
            Trace.Entering();
            ArgUtil.NotNull(executionContext, nameof(executionContext));

            // This flag can be false for jobs like cleanup artifacts.
            // If syncSources = false, we will not set source related build variable, not create build folder, not sync source.
            bool syncSources = executionContext.Variables.Build_SyncSources ?? true;
            if (!syncSources)
            {
                Trace.Info($"{Constants.Variables.Build.SyncSources} = false, we will not set source related build variable, not create build folder and not sync source");
                return;
            }

            // We only support checkout one repository at this time.
            if (!TrySetPrimaryEndpointAndProviderInfo(executionContext))
            {
                throw new Exception(StringUtil.Loc("SupportedRepositoryEndpointNotFound"));
            }

            executionContext.Debug($"Primary repository: {Repository.Properties.Get<string>(Pipelines.RepositoryPropertyNames.Name)}. repository type: {Repository.Type}");

            // Set the repo variables.
            if (!string.IsNullOrEmpty(Repository.Id)) // TODO: Move to const after source artifacts PR is merged.
            {
                executionContext.Variables.Set(Constants.Variables.Build.RepoId, Repository.Id);
            }

            executionContext.Variables.Set(Constants.Variables.Build.RepoName, Repository.Properties.Get<string>(Pipelines.RepositoryPropertyNames.Name));
            executionContext.Variables.Set(Constants.Variables.Build.RepoProvider, Repository.Type);
            executionContext.Variables.Set(Constants.Variables.Build.RepoUri, Repository.Url?.AbsoluteUri);

            var checkoutTask = steps.SingleOrDefault(x => x is Pipelines.TaskStep taskStep && taskStep.Reference.Id == Pipelines.PipelineConstants.CheckoutTask.Id && taskStep.Reference.Version == Pipelines.PipelineConstants.CheckoutTask.Version) as Pipelines.TaskStep;
            if (checkoutTask != null)
            {
                if (checkoutTask.Inputs.ContainsKey(Pipelines.PipelineConstants.CheckoutTaskInputs.Submodules))
                {
                    executionContext.Variables.Set(Constants.Variables.Build.RepoGitSubmoduleCheckout, checkoutTask.Inputs[Pipelines.PipelineConstants.CheckoutTaskInputs.Submodules]);
                }

                // overwrite primary repository's clean value if build.repository.clean is sent from server. this is used by tfvc gated check-in
                bool? repoClean = executionContext.Variables.GetBoolean(Constants.Variables.Build.RepoClean);
                if (repoClean != null)
                {
                    checkoutTask.Inputs[Pipelines.PipelineConstants.CheckoutTaskInputs.Clean] = repoClean.Value.ToString();
                }
                else
                {
                    if (checkoutTask.Inputs.ContainsKey(Pipelines.PipelineConstants.CheckoutTaskInputs.Clean))
                    {
                        executionContext.Variables.Set(Constants.Variables.Build.RepoClean, checkoutTask.Inputs[Pipelines.PipelineConstants.CheckoutTaskInputs.Clean]);
                    }
                }
            }

            // Prepare the build directory.
            executionContext.Output(StringUtil.Loc("PrepareBuildDir"));
            var directoryManager = HostContext.GetService<IBuildDirectoryManager>();
            TrackingConfig trackingConfig = directoryManager.PrepareDirectory(
                executionContext,
                Repository,
                workspace);

            // Set the directory variables.
            executionContext.Output(StringUtil.Loc("SetBuildVars"));
            string _workDirectory = HostContext.GetDirectory(WellKnownDirectory.Work);
            executionContext.Variables.Set(Constants.Variables.Agent.BuildDirectory, Path.Combine(_workDirectory, trackingConfig.BuildDirectory));
            executionContext.Variables.Set(Constants.Variables.System.ArtifactsDirectory, Path.Combine(_workDirectory, trackingConfig.ArtifactsDirectory));
            executionContext.Variables.Set(Constants.Variables.System.DefaultWorkingDirectory, Path.Combine(_workDirectory, trackingConfig.SourcesDirectory));
            executionContext.Variables.Set(Constants.Variables.Common.TestResultsDirectory, Path.Combine(_workDirectory, trackingConfig.TestResultsDirectory));
            executionContext.Variables.Set(Constants.Variables.Build.BinariesDirectory, Path.Combine(_workDirectory, trackingConfig.BuildDirectory, Constants.Build.Path.BinariesDirectory));
            executionContext.Variables.Set(Constants.Variables.Build.SourcesDirectory, Path.Combine(_workDirectory, trackingConfig.SourcesDirectory));
            executionContext.Variables.Set(Constants.Variables.Build.StagingDirectory, Path.Combine(_workDirectory, trackingConfig.ArtifactsDirectory));
            executionContext.Variables.Set(Constants.Variables.Build.ArtifactStagingDirectory, Path.Combine(_workDirectory, trackingConfig.ArtifactsDirectory));
            executionContext.Variables.Set(Constants.Variables.Build.RepoLocalPath, Path.Combine(_workDirectory, trackingConfig.SourcesDirectory));

            Repository.Properties.Set<string>("sourcedirectory", Path.Combine(_workDirectory, trackingConfig.SourcesDirectory));
            //SourceProvider.SetVariablesInEndpoint(executionContext, SourceEndpoint);
        }

        // private async Task GetSourceAsync(IExecutionContext executionContext, object data)
        // {
        //     // Validate args.
        //     Trace.Entering();

        //     // This flag can be false for jobs like cleanup artifacts.
        //     // If syncSources = false, we will not set source related build variable, not create build folder, not sync source.
        //     bool syncSources = executionContext.Variables.Build_SyncSources ?? true;
        //     if (!syncSources)
        //     {
        //         Trace.Info($"{Constants.Variables.Build.SyncSources} = false, we will not set source related build variable, not create build folder and not sync source");
        //         return;
        //     }

        //     ArgUtil.NotNull(SourceEndpoint, nameof(SourceEndpoint));
        //     ArgUtil.NotNull(SourceProvider, nameof(SourceProvider));

        //     // Read skipSyncSource property fron endpoint data
        //     string skipSyncSourceText;
        //     bool skipSyncSource = false;
        //     if (SourceEndpoint.Data.TryGetValue("skipSyncSource", out skipSyncSourceText))
        //     {
        //         skipSyncSource = StringUtil.ConvertToBoolean(skipSyncSourceText, false);
        //     }

        //     // Prefer feature variable over endpoint data
        //     skipSyncSource = executionContext.Variables.GetBoolean(Constants.Variables.Features.SkipSyncSource) ?? skipSyncSource;

        //     if (skipSyncSource)
        //     {
        //         executionContext.Output($"Skip sync source for endpoint: {SourceEndpoint.Name}");
        //     }
        //     else
        //     {
        //         executionContext.Debug($"Sync source for endpoint: {SourceEndpoint.Name}");
        //         await SourceProvider.GetSourceAsync(executionContext, SourceEndpoint, executionContext.CancellationToken);
        //     }
        // }

        // private async Task PostJobCleanupAsync(IExecutionContext executionContext, object data)
        // {
        //     // Validate args.
        //     Trace.Entering();

        //     // If syncSources = false, we will not reset repository.
        //     bool syncSources = executionContext.Variables.Build_SyncSources ?? true;
        //     if (!syncSources)
        //     {
        //         Trace.Verbose($"{Constants.Variables.Build.SyncSources} = false, we will not run post job cleanup for this repository");
        //         return;
        //     }

        //     ArgUtil.NotNull(SourceEndpoint, nameof(SourceEndpoint));
        //     ArgUtil.NotNull(SourceProvider, nameof(SourceProvider));

        //     // Read skipSyncSource property fron endpoint data
        //     string skipSyncSourceText;
        //     bool skipSyncSource = false;
        //     if (SourceEndpoint != null && SourceEndpoint.Data.TryGetValue("skipSyncSource", out skipSyncSourceText))
        //     {
        //         skipSyncSource = StringUtil.ConvertToBoolean(skipSyncSourceText, false);
        //     }

        //     // Prefer feature variable over endpoint data
        //     skipSyncSource = executionContext.Variables.GetBoolean(Constants.Variables.Features.SkipSyncSource) ?? skipSyncSource;

        //     if (skipSyncSource)
        //     {
        //         Trace.Verbose($"{Constants.Variables.Build.SyncSources} = false, we will not run post job cleanup for this repository");
        //         return;
        //     }

        //     await SourceProvider.PostJobCleanupAsync(executionContext, SourceEndpoint);
        // }

        private bool TrySetPrimaryEndpointAndProviderInfo(IExecutionContext executionContext)
        {
            // Return the first repository resource and its source provider.
            Trace.Entering();
            var extensionManager = HostContext.GetService<IExtensionManager>();
            List<ISourceProvider> sourceProviders = extensionManager.GetExtensions<ISourceProvider>();
            Repository = executionContext.Repositories.SingleOrDefault();
            if (Repository != null)
            {
                SourceProvider = sourceProviders.FirstOrDefault(x => string.Equals(x.RepositoryType, Repository.Type, StringComparison.OrdinalIgnoreCase));

                if (SourceProvider != null)
                {
                    return true;
                }
            }

            return false;
        }
    }
}