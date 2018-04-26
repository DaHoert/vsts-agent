﻿using Microsoft.TeamFoundation.DistributedTask.WebApi;
using Pipelines = Microsoft.TeamFoundation.DistributedTask.Pipelines;
using Microsoft.VisualStudio.Services.Agent.Util;
using Newtonsoft.Json;
using System;
using System.IO;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Globalization;
using Microsoft.TeamFoundation.Build.WebApi;

namespace Microsoft.VisualStudio.Services.Agent.Worker.Build
{
    [ServiceLocator(Default = typeof(LegacyTrackingManager))]
    public interface ILegacyTrackingManager : IAgentService
    {
        LegacyTrackingConfig Create(
            IExecutionContext executionContext,
            ServiceEndpoint endpoint,
            string hashKey,
            string file,
            bool overrideBuildDirectory);

        TrackingConfigBase LoadIfExists(IExecutionContext executionContext, string file);

        void MarkForGarbageCollection(IExecutionContext executionContext, TrackingConfigBase config);

        void UpdateResourcesTracking(IExecutionContext executionContext, TrackingConfig config);

        void UpdateJobRunProperties(IExecutionContext executionContext, TrackingConfig config, string file);

        void MarkExpiredForGarbageCollection(IExecutionContext executionContext, TimeSpan expiration);

        void DisposeCollectedGarbage(IExecutionContext executionContext);

        void MaintenanceStarted(TrackingConfig config, string file);

        void MaintenanceCompleted(TrackingConfig config, string file);
    }

    public sealed class LegacyTrackingManager : AgentService, ILegacyTrackingManager
    {
        public TrackingConfig Create(
            IExecutionContext executionContext,
            string hashKey,
            string file)
        {
            Trace.Entering();

            // Get or create the top-level tracking config.
            TopLevelTrackingConfig topLevelConfig;
            string topLevelFile = Path.Combine(
                IOUtil.GetWorkPath(HostContext),
                Constants.Build.Path.SourceRootMappingDirectory,
                Constants.Build.Path.TopLevelTrackingConfigFile);
            Trace.Verbose($"Loading top-level tracking config if exists: {topLevelFile}");
            if (!File.Exists(topLevelFile))
            {
                topLevelConfig = new TopLevelTrackingConfig();
            }
            else
            {
                topLevelConfig = JsonConvert.DeserializeObject<TopLevelTrackingConfig>(File.ReadAllText(topLevelFile));
                if (topLevelConfig == null)
                {
                    executionContext.Warning($"Rebuild corruptted top-level tracking configure file {topLevelFile}.");
                    // save the corruptted file in case we need to investigate more.
                    File.Copy(topLevelFile, $"{topLevelFile}.corruptted", true);

                    topLevelConfig = new TopLevelTrackingConfig();
                    DirectoryInfo workDir = new DirectoryInfo(HostContext.GetDirectory(WellKnownDirectory.Work));

                    foreach (var dir in workDir.EnumerateDirectories())
                    {
                        // we scan the entire _work directory and find the directory with the highest integer number.
                        if (int.TryParse(dir.Name, NumberStyles.Integer, CultureInfo.InvariantCulture, out int lastBuildNumber) &&
                            lastBuildNumber > topLevelConfig.LastBuildDirectoryNumber)
                        {
                            topLevelConfig.LastBuildDirectoryNumber = lastBuildNumber;
                        }
                    }
                }
            }

            // Determine the build directory.
            var configurationStore = HostContext.GetService<IConfigurationStore>();
            AgentSettings settings = configurationStore.GetSettings();
            if (settings.IsHosted && (executionContext.Repositories.Any(x => x.Type == "TfsVersionControl")))
            {
                // This should only occur during hosted builds. This was added due to TFVC.
                // TFVC does not allow a local path for a single machine to be mapped in multiple
                // workspaces. The machine name for a hosted images is not unique.
                //
                // So if a customer is running two hosted builds at the same time, they could run
                // into the local mapping conflict.
                //
                // The workaround is to force the build directory to be different across all concurrent
                // hosted builds (for TFVC). The agent ID will be unique across all concurrent hosted
                // builds so that can safely be used as the build directory.
                ArgUtil.Equal(default(int), topLevelConfig.LastBuildDirectoryNumber, nameof(topLevelConfig.LastBuildDirectoryNumber));
                topLevelConfig.LastBuildDirectoryNumber = settings.AgentId;
            }
            else
            {
                topLevelConfig.LastBuildDirectoryNumber++;
            }

            // Update the top-level tracking config.
            topLevelConfig.LastBuildDirectoryCreatedOn = DateTimeOffset.Now;
            WriteToFile(topLevelFile, topLevelConfig);

            // Create the new tracking config.
            TrackingConfig config = new TrackingConfig(
                executionContext,
                topLevelConfig.LastBuildDirectoryNumber,
                hashKey);
            WriteToFile(file, config);

            // Set repository resource variable
            foreach (var repoResource in config.Resources.Repositories)
            {
                var repo = executionContext.Repositories.Single(x => x.Alias == repoResource.Key);
                executionContext.Variables.Set($"system.repository.{repo.Alias}.id", repo.Id);
                executionContext.Variables.Set($"system.repository.{repo.Alias}.name", repo.Properties.Get<string>("name") ?? string.Empty);
                executionContext.Variables.Set($"system.repository.{repo.Alias}.provider", repo.Type);
                executionContext.Variables.Set($"system.repository.{repo.Alias}.uri", repo.Url?.AbsoluteUri);
                executionContext.Variables.Set($"system.repository.{repo.Alias}.clean", repo.Properties.Get<string>("clean") ?? string.Empty);
                executionContext.Variables.Set($"system.repository.{repo.Alias}.localpath", Path.Combine(HostContext.GetDirectory(WellKnownDirectory.Work), repoResource.Value.SourceDirectory));
            }

            // Set build drop resource variable
            foreach (var dropResource in config.Resources.Drops)
            {
                var build = executionContext.Builds.Single(x => x.Alias == dropResource.Key);
                executionContext.Variables.Set($"system.build.{build.Alias}.version", build.Version);
                executionContext.Variables.Set($"system.build.{build.Alias}.type", build.Type);
                executionContext.Variables.Set($"system.build.{build.Alias}.localpath", Path.Combine(HostContext.GetDirectory(WellKnownDirectory.Work), dropResource.Value.DropDirectory));
            }

            return config;
        }

        public void UpdateResourcesTracking(
            IExecutionContext executionContext,
            TrackingConfig config)
        {
            // Use to be single repo and never have multiple repositories, now have multiple repositories
            var extensions = HostContext.GetService<IExtensionManager>();
            if (config.Resources.Repositories.Count == 1 && executionContext.Repositories.Count > 1)
            {
                var selfRepo = executionContext.Repositories.Single(x => string.Equals(x.Alias, "self", StringComparison.OrdinalIgnoreCase));
                ArgUtil.NotNull(selfRepo, nameof(selfRepo));
                config.Resources.Repositories.TryGetValue("self", out RepositoryTrackingConfig selfRepoTracking);
                ArgUtil.NotNull(selfRepoTracking, nameof(selfRepoTracking));

                if (string.Equals(selfRepoTracking.SourceDirectory, config.SourcesDirectory, StringComparison.OrdinalIgnoreCase))
                {
                    var currentSourceDirectory = Path.Combine(HostContext.GetDirectory(WellKnownDirectory.Work), selfRepoTracking.SourceDirectory);
                    var newSourceDirectory = Path.Combine(HostContext.GetDirectory(WellKnownDirectory.Work), config.SourcesDirectory, selfRepo.Alias);
                    if (Directory.Exists(currentSourceDirectory))
                    {
                        var sourceProvider = extensions.GetExtensions<ISourceProvider>().Single(x => x.RepositoryType == selfRepoTracking.RepositoryType);
                        sourceProvider.MigrateSourceDirectory(executionContext, currentSourceDirectory, newSourceDirectory);
                        // if (selfRepoTracking.RepositoryType == RepositoryTypes.TfsVersionControl)
                        // {
                        //     // invoke tf.exe to delete the workspace.
                        //     executionContext.Debug($"Destroy current TFVC source directory under '{currentSourceDirectory}'.");
                        //     // DestroyTFVCSourceDirectory();
                        // }
                        // else
                        // {
                        //     // move current /s to /s/self since we have more repositories need to stored
                        //     var stagingDirectory = Path.Combine(HostContext.GetDirectory(WellKnownDirectory.Work), config.BuildDirectory, Guid.NewGuid().ToString("D"));

                        //     executionContext.Debug($"Move current source directory from '{currentSourceDirectory}' to '{newSourceDirectory}'");
                        //     try
                        //     {
                        //         Directory.Move(currentSourceDirectory, stagingDirectory);
                        //         Directory.Move(stagingDirectory, newSourceDirectory);
                        //     }
                        //     catch (Exception ex)
                        //     {
                        //         Trace.Error(ex);
                        //         // if we can't move the folder and we can't delete the folder, just fail the job.
                        //         IOUtil.DeleteDirectory(currentSourceDirectory, CancellationToken.None);
                        //     }
                        // }
                    }

                    config.Resources.Repositories[selfRepo.Alias].SourceDirectory = Path.Combine(config.SourcesDirectory, selfRepo.Alias);
                }
            }

            // delete local repository if it's no longer need for the definition.
            List<string> staleRepo = new List<string>();
            foreach (var repo in config.Resources.Repositories)
            {
                var existingRepo = executionContext.Repositories.SingleOrDefault(x => string.Equals(x.Alias, repo.Key, StringComparison.OrdinalIgnoreCase));
                if (existingRepo == null || !string.Equals(existingRepo.Url.AbsoluteUri, repo.Value.RepositoryUrl, StringComparison.OrdinalIgnoreCase))
                {
                    staleRepo.Add(repo.Key);
                }
            }
            foreach (var stale in staleRepo)
            {
                executionContext.Debug($"Delete stale local source directory '{config.Resources.Repositories[stale].SourceDirectory}' for repository '{config.Resources.Repositories[stale].RepositoryUrl}' ({stale}).");
                var sourceDir = Path.Combine(HostContext.GetDirectory(WellKnownDirectory.Work), config.Resources.Repositories[stale].SourceDirectory);
                var sourceProvider = extensions.GetExtensions<ISourceProvider>().Single(x => x.RepositoryType == config.Resources.Repositories[stale].RepositoryType);
                sourceProvider.DestroySourceDirectory(executionContext, sourceDir);
                config.Resources.Repositories.Remove(stale);
            }

            // add any new repositories' information
            foreach (var repo in executionContext.Repositories)
            {
                if (!config.Resources.Repositories.ContainsKey(repo.Alias))
                {
                    executionContext.Debug($"Add new repository '{repo.Url.AbsoluteUri}' ({repo.Alias}) at '{Path.Combine(config.SourcesDirectory, repo.Alias)}'.");
                    config.Resources.Repositories[repo.Alias] = new RepositoryTrackingConfig()
                    {
                        RepositoryType = repo.Type,
                        RepositoryUrl = repo.Url.AbsoluteUri,
                        SourceDirectory = Path.Combine(config.SourcesDirectory, repo.Alias)
                    };
                }
            }

            // Set repository resource variable
            foreach (var repoResource in config.Resources.Repositories)
            {
                var repo = executionContext.Repositories.Single(x => x.Alias == repoResource.Key);
                executionContext.Variables.Set($"system.repository.{repo.Alias}.id", repo.Id);
                executionContext.Variables.Set($"system.repository.{repo.Alias}.name", repo.Properties.Get<string>("name") ?? string.Empty);
                executionContext.Variables.Set($"system.repository.{repo.Alias}.provider", repo.Type);
                executionContext.Variables.Set($"system.repository.{repo.Alias}.uri", repo.Url?.AbsoluteUri);
                executionContext.Variables.Set($"system.repository.{repo.Alias}.clean", repo.Properties.Get<string>("clean") ?? string.Empty);
                executionContext.Variables.Set($"system.repository.{repo.Alias}.localpath", Path.Combine(HostContext.GetDirectory(WellKnownDirectory.Work), repoResource.Value.SourceDirectory));
            }

            // Set build drop resource variable
            foreach (var dropResource in config.Resources.Drops)
            {
                var build = executionContext.Builds.Single(x => x.Alias == dropResource.Key);
                executionContext.Variables.Set($"system.build.{build.Alias}.version", build.Version);
                executionContext.Variables.Set($"system.build.{build.Alias}.type", build.Type);
                executionContext.Variables.Set($"system.build.{build.Alias}.localpath", Path.Combine(HostContext.GetDirectory(WellKnownDirectory.Work), dropResource.Value.DropDirectory));
            }
        }

        public TrackingConfig Create(
            IExecutionContext executionContext,
            ServiceEndpoint endpoint,
            string hashKey,
            string file,
            bool overrideBuildDirectory)
        {
            Trace.Entering();

            // Get or create the top-level tracking config.
            TopLevelTrackingConfig topLevelConfig;
            string topLevelFile = Path.Combine(
                IOUtil.GetWorkPath(HostContext),
                Constants.Build.Path.SourceRootMappingDirectory,
                Constants.Build.Path.TopLevelTrackingConfigFile);
            Trace.Verbose($"Loading top-level tracking config if exists: {topLevelFile}");
            if (!File.Exists(topLevelFile))
            {
                topLevelConfig = new TopLevelTrackingConfig();
            }
            else
            {
                topLevelConfig = JsonConvert.DeserializeObject<TopLevelTrackingConfig>(File.ReadAllText(topLevelFile));
                if (topLevelConfig == null)
                {
                    executionContext.Warning($"Rebuild corruptted top-level tracking configure file {topLevelFile}.");
                    // save the corruptted file in case we need to investigate more.
                    File.Copy(topLevelFile, $"{topLevelFile}.corruptted", true);

                    topLevelConfig = new TopLevelTrackingConfig();
                    DirectoryInfo workDir = new DirectoryInfo(HostContext.GetDirectory(WellKnownDirectory.Work));

                    foreach (var dir in workDir.EnumerateDirectories())
                    {
                        // we scan the entire _work directory and find the directory with the highest integer number.
                        if (int.TryParse(dir.Name, NumberStyles.Integer, CultureInfo.InvariantCulture, out int lastBuildNumber) &&
                            lastBuildNumber > topLevelConfig.LastBuildDirectoryNumber)
                        {
                            topLevelConfig.LastBuildDirectoryNumber = lastBuildNumber;
                        }
                    }
                }
            }

            // Determine the build directory.
            if (overrideBuildDirectory)
            {
                // This should only occur during hosted builds. This was added due to TFVC.
                // TFVC does not allow a local path for a single machine to be mapped in multiple
                // workspaces. The machine name for a hosted images is not unique.
                //
                // So if a customer is running two hosted builds at the same time, they could run
                // into the local mapping conflict.
                //
                // The workaround is to force the build directory to be different across all concurrent
                // hosted builds (for TFVC). The agent ID will be unique across all concurrent hosted
                // builds so that can safely be used as the build directory.
                ArgUtil.Equal(default(int), topLevelConfig.LastBuildDirectoryNumber, nameof(topLevelConfig.LastBuildDirectoryNumber));
                var configurationStore = HostContext.GetService<IConfigurationStore>();
                AgentSettings settings = configurationStore.GetSettings();
                topLevelConfig.LastBuildDirectoryNumber = settings.AgentId;
            }
            else
            {
                topLevelConfig.LastBuildDirectoryNumber++;
            }

            // Update the top-level tracking config.
            topLevelConfig.LastBuildDirectoryCreatedOn = DateTimeOffset.Now;
            WriteToFile(topLevelFile, topLevelConfig);

            // Create the new tracking config.
            TrackingConfig config = new TrackingConfig(
                executionContext,
                endpoint,
                topLevelConfig.LastBuildDirectoryNumber,
                hashKey);
            WriteToFile(file, config);
            return config;
        }

        public TrackingConfigBase LoadIfExists(IExecutionContext executionContext, string file)
        {
            Trace.Entering();

            // The tracking config will not exist for a new definition.
            if (!File.Exists(file))
            {
                return null;
            }

            // Load the content and distinguish between tracking config file
            // version 1 and file version 2.
            string content = File.ReadAllText(file);
            string fileFormatVersionJsonProperty = StringUtil.Format(
                @"""{0}""",
                TrackingConfig.FileFormatVersionJsonProperty);
            if (content.Contains(fileFormatVersionJsonProperty))
            {
                // The config is the new format.
                Trace.Verbose("Parsing new tracking config format.");
                return JsonConvert.DeserializeObject<TrackingConfig>(content);
            }

            // Attempt to parse the legacy format.
            Trace.Verbose("Parsing legacy tracking config format.");
            LegacyTrackingConfig config = LegacyTrackingConfig.TryParse(content);
            if (config == null)
            {
                executionContext.Warning(StringUtil.Loc("UnableToParseBuildTrackingConfig0", content));
            }

            return config;
        }

        public void MarkForGarbageCollection(IExecutionContext executionContext, TrackingConfigBase config)
        {
            Trace.Entering();

            // Convert legacy format to the new format.
            LegacyTrackingConfig legacyConfig = config as LegacyTrackingConfig;
            if (legacyConfig != null)
            {
                // Convert legacy format to the new format.
                config = new TrackingConfig(
                    executionContext,
                    legacyConfig,
                    // The repository type and sources folder wasn't stored in the legacy format - only the
                    // build folder was stored. Since the hash key has changed, it is
                    // unknown what the source folder was named. Just set the folder name
                    // to "s" so the property isn't left blank. 
                    repositoryType: string.Empty,
                    sourcesDirectoryNameOnly: Constants.Build.Path.SourcesDirectory);
            }

            // Write a copy of the tracking config to the GC folder.
            string gcDirectory = Path.Combine(
                IOUtil.GetWorkPath(HostContext),
                Constants.Build.Path.SourceRootMappingDirectory,
                Constants.Build.Path.GarbageCollectionDirectory);
            string file = Path.Combine(
                gcDirectory,
                StringUtil.Format("{0}.json", Guid.NewGuid()));
            WriteToFile(file, config);
        }

        public void UpdateJobRunProperties(IExecutionContext executionContext, TrackingConfig config, string file)
        {
            Trace.Entering();

            // Update the info properties and save the file.
            config.UpdateJobRunProperties(executionContext);
            WriteToFile(file, config);
        }

        public void MaintenanceStarted(TrackingConfig config, string file)
        {
            Trace.Entering();
            config.LastMaintenanceAttemptedOn = DateTimeOffset.Now;
            config.LastMaintenanceCompletedOn = null;
            WriteToFile(file, config);
        }

        public void MaintenanceCompleted(TrackingConfig config, string file)
        {
            Trace.Entering();
            config.LastMaintenanceCompletedOn = DateTimeOffset.Now;
            WriteToFile(file, config);
        }

        public void MarkExpiredForGarbageCollection(IExecutionContext executionContext, TimeSpan expiration)
        {
            Trace.Entering();
            Trace.Info("Scan all SourceFolder tracking files.");
            string searchRoot = Path.Combine(HostContext.GetDirectory(WellKnownDirectory.Work), Constants.Build.Path.SourceRootMappingDirectory);
            if (!Directory.Exists(searchRoot))
            {
                executionContext.Output(StringUtil.Loc("GCDirNotExist", searchRoot));
                return;
            }

            var allTrackingFiles = Directory.EnumerateFiles(searchRoot, Constants.Build.Path.TrackingConfigFile, SearchOption.AllDirectories);
            Trace.Verbose($"Find {allTrackingFiles.Count()} tracking files.");

            executionContext.Output(StringUtil.Loc("DirExpireLimit", expiration.TotalDays));
            executionContext.Output(StringUtil.Loc("CurrentUTC", DateTime.UtcNow.ToString("o")));

            // scan all sourcefolder tracking file, find which folder has never been used since UTC-expiration
            // the scan and garbage discovery should be best effort.
            // if the tracking file is in old format, just delete the folder since the first time the folder been use we will convert the tracking file to new format.
            foreach (var trackingFile in allTrackingFiles)
            {
                try
                {
                    executionContext.Output(StringUtil.Loc("EvaluateTrackingFile", trackingFile));
                    TrackingConfigBase tracking = LoadIfExists(executionContext, trackingFile);

                    // detect whether the tracking file is in new format.
                    TrackingConfig newTracking = tracking as TrackingConfig;
                    if (newTracking == null)
                    {
                        LegacyTrackingConfig legacyConfig = tracking as LegacyTrackingConfig;
                        ArgUtil.NotNull(legacyConfig, nameof(LegacyTrackingConfig));

                        Trace.Verbose($"{trackingFile} is a old format tracking file.");

                        executionContext.Output(StringUtil.Loc("GCOldFormatTrackingFile", trackingFile));
                        MarkForGarbageCollection(executionContext, legacyConfig);
                        IOUtil.DeleteFile(trackingFile);
                    }
                    else
                    {
                        Trace.Verbose($"{trackingFile} is a new format tracking file.");
                        ArgUtil.NotNull(newTracking.LastRunOn, nameof(newTracking.LastRunOn));
                        executionContext.Output(StringUtil.Loc("BuildDirLastUseTIme", Path.Combine(HostContext.GetDirectory(WellKnownDirectory.Work), newTracking.BuildDirectory), newTracking.LastRunOnString));
                        if (DateTime.UtcNow - expiration > newTracking.LastRunOn)
                        {
                            executionContext.Output(StringUtil.Loc("GCUnusedTrackingFile", trackingFile, expiration.TotalDays));
                            MarkForGarbageCollection(executionContext, newTracking);
                            IOUtil.DeleteFile(trackingFile);
                        }
                    }
                }
                catch (Exception ex)
                {
                    executionContext.Error(StringUtil.Loc("ErrorDuringBuildGC", trackingFile));
                    executionContext.Error(ex);
                }
            }
        }

        public void DisposeCollectedGarbage(IExecutionContext executionContext)
        {
            Trace.Entering();
            PrintOutDiskUsage(executionContext);

            string gcDirectory = Path.Combine(
                HostContext.GetDirectory(WellKnownDirectory.Work),
                Constants.Build.Path.SourceRootMappingDirectory,
                Constants.Build.Path.GarbageCollectionDirectory);

            if (!Directory.Exists(gcDirectory))
            {
                executionContext.Output(StringUtil.Loc("GCDirNotExist", gcDirectory));
                return;
            }

            IEnumerable<string> gcTrackingFiles = Directory.EnumerateFiles(gcDirectory, "*.json");
            if (gcTrackingFiles == null || gcTrackingFiles.Count() == 0)
            {
                executionContext.Output(StringUtil.Loc("GCDirIsEmpty", gcDirectory));
                return;
            }

            Trace.Info($"Find {gcTrackingFiles.Count()} GC tracking files.");

            if (gcTrackingFiles.Count() > 0)
            {
                foreach (string gcFile in gcTrackingFiles)
                {
                    // maintenance has been cancelled.
                    executionContext.CancellationToken.ThrowIfCancellationRequested();

                    try
                    {
                        var gcConfig = LoadIfExists(executionContext, gcFile) as TrackingConfig;
                        ArgUtil.NotNull(gcConfig, nameof(TrackingConfig));

                        string fullPath = Path.Combine(HostContext.GetDirectory(WellKnownDirectory.Work), gcConfig.BuildDirectory);
                        executionContext.Output(StringUtil.Loc("Deleting", fullPath));
                        IOUtil.DeleteDirectory(fullPath, executionContext.CancellationToken);

                        executionContext.Output(StringUtil.Loc("DeleteGCTrackingFile", fullPath));
                        IOUtil.DeleteFile(gcFile);
                    }
                    catch (Exception ex)
                    {
                        executionContext.Error(StringUtil.Loc("ErrorDuringBuildGCDelete", gcFile));
                        executionContext.Error(ex);
                    }
                }

                PrintOutDiskUsage(executionContext);
            }
        }

        private void PrintOutDiskUsage(IExecutionContext context)
        {
            // Print disk usage should be best effort, since DriveInfo can't detect usage of UNC share.
            try
            {
                context.Output($"Disk usage for working directory: {HostContext.GetDirectory(WellKnownDirectory.Work)}");
                var workDirectoryDrive = new DriveInfo(HostContext.GetDirectory(WellKnownDirectory.Work));
                long freeSpace = workDirectoryDrive.AvailableFreeSpace;
                long totalSpace = workDirectoryDrive.TotalSize;
#if OS_WINDOWS
                context.Output($"Working directory belongs to drive: '{workDirectoryDrive.Name}'");
#else
                context.Output($"Information about file system on which working directory resides.");
#endif
                context.Output($"Total size: '{totalSpace / 1024.0 / 1024.0} MB'");
                context.Output($"Available space: '{freeSpace / 1024.0 / 1024.0} MB'");
            }
            catch (Exception ex)
            {
                context.Warning($"Unable inspect disk usage for working directory {HostContext.GetDirectory(WellKnownDirectory.Work)}.");
                Trace.Error(ex);
                context.Debug(ex.ToString());
            }
        }

        private void WriteToFile(string file, object value)
        {
            Trace.Entering();
            Trace.Verbose($"Writing config to file: {file}");

            // Create the directory if it does not exist.
            Directory.CreateDirectory(Path.GetDirectoryName(file));
            IOUtil.SaveObject(value, file);
        }
    }
}