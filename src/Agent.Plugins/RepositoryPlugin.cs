using System;
using System.Collections;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.TeamFoundation.Build.WebApi;
using Microsoft.TeamFoundation.DistributedTask.WebApi;
using Agent.Sdk;
using Pipelines = Microsoft.TeamFoundation.DistributedTask.Pipelines;
using Microsoft.VisualStudio.Services.Agent.Util;

namespace Agent.Plugins.Repository
{
    public interface ISourceProvider
    {
        Task GetSourceAsync(AgentTaskPluginExecutionContext executionContext, Pipelines.RepositoryResource repository, CancellationToken cancellationToken);

        Task PostJobCleanupAsync(AgentTaskPluginExecutionContext executionContext, Pipelines.RepositoryResource repository);
    }

    public abstract class RepositoryTask : IAgentTaskPlugin
    {
        public Guid Id => Pipelines.PipelineConstants.CheckoutTask.Id;
        public string Version => Pipelines.PipelineConstants.CheckoutTask.Version;

        public abstract string Stage { get; }

        public abstract Task RunAsync(AgentTaskPluginExecutionContext executionContext, CancellationToken token);

        protected ISourceProvider GetSourceProvider(string repositoryType)
        {
            ISourceProvider sourceProvider = null;

            if (string.Equals(repositoryType, Pipelines.RepositoryTypes.Bitbucket, StringComparison.OrdinalIgnoreCase) ||
                string.Equals(repositoryType, Pipelines.RepositoryTypes.GitHub, StringComparison.OrdinalIgnoreCase) ||
                string.Equals(repositoryType, Pipelines.RepositoryTypes.GitHubEnterprise, StringComparison.OrdinalIgnoreCase))
            {
                sourceProvider = new AuthenticatedGitSourceProvider();
            }
            else if (string.Equals(repositoryType, Pipelines.RepositoryTypes.ExternalGit, StringComparison.OrdinalIgnoreCase))
            {
                sourceProvider = new ExternalGitSourceProvider();
            }
            else if (string.Equals(repositoryType, Pipelines.RepositoryTypes.Git, StringComparison.OrdinalIgnoreCase))
            {
                sourceProvider = new TfsGitSourceProvider();
            }
            else if (string.Equals(repositoryType, Pipelines.RepositoryTypes.Tfvc, StringComparison.OrdinalIgnoreCase))
            {
                sourceProvider = new TfsVCSourceProvider();
            }
            else if (string.Equals(repositoryType, Pipelines.RepositoryTypes.Svn, StringComparison.OrdinalIgnoreCase))
            {
                sourceProvider = new SvnSourceProvider();
            }
            else
            {
                throw new NotSupportedException(repositoryType);
            }

            return sourceProvider;
        }
        // protected void MergeInputs(AgentTaskPluginExecutionContext executionContext, Pipelines.RepositoryResource repository)
        // {
        //     string clean = executionContext.GetInput("clean");
        //     if (!string.IsNullOrEmpty(clean))
        //     {
        //         repository.Properties.Set<bool>("clean", StringUtil.ConvertToBoolean(clean));
        //     }

        //     // there is no addition inputs for TFVC and SVN
        //     if (repository.Type == RepositoryTypes.Bitbucket ||
        //         repository.Type == RepositoryTypes.GitHub ||
        //         repository.Type == RepositoryTypes.GitHubEnterprise ||
        //         repository.Type == RepositoryTypes.Git ||
        //         repository.Type == RepositoryTypes.TfsGit)
        //     {
        //         string checkoutSubmodules = executionContext.GetInput("checkoutSubmodules");
        //         if (!string.IsNullOrEmpty(checkoutSubmodules))
        //         {
        //             repository.Properties.Set<bool>("checkoutSubmodules", StringUtil.ConvertToBoolean(checkoutSubmodules));
        //         }

        //         string checkoutNestedSubmodules = executionContext.GetInput("checkoutNestedSubmodules");
        //         if (!string.IsNullOrEmpty(checkoutNestedSubmodules))
        //         {
        //             repository.Properties.Set<bool>("checkoutNestedSubmodules", StringUtil.ConvertToBoolean(checkoutNestedSubmodules));
        //         }

        //         string preserveCredential = executionContext.GetInput("preserveCredential");
        //         if (!string.IsNullOrEmpty(preserveCredential))
        //         {
        //             repository.Properties.Set<bool>("preserveCredential", StringUtil.ConvertToBoolean(preserveCredential));
        //         }

        //         string gitLfsSupport = executionContext.GetInput("gitLfsSupport");
        //         if (!string.IsNullOrEmpty(gitLfsSupport))
        //         {
        //             repository.Properties.Set<bool>("gitLfsSupport", StringUtil.ConvertToBoolean(gitLfsSupport));
        //         }

        //         string acceptUntrustedCerts = executionContext.GetInput("acceptUntrustedCerts");
        //         if (!string.IsNullOrEmpty(acceptUntrustedCerts))
        //         {
        //             repository.Properties.Set<bool>("acceptUntrustedCerts", StringUtil.ConvertToBoolean(acceptUntrustedCerts));
        //         }

        //         string fetchDepth = executionContext.GetInput("fetchDepth");
        //         if (!string.IsNullOrEmpty(fetchDepth))
        //         {
        //             repository.Properties.Set<string>("fetchDepth", fetchDepth);
        //         }
        //     }
        // }
    }

    public class CheckoutTask : RepositoryTask
    {
        public override string Stage => "main";

        public override async Task RunAsync(AgentTaskPluginExecutionContext executionContext, CancellationToken token)
        {
            var repoAlias = executionContext.GetInput(Pipelines.PipelineConstants.CheckoutTaskInputs.Repository, true);
            var repo = executionContext.Repositories.Single(x => string.Equals(x.Alias, repoAlias, StringComparison.OrdinalIgnoreCase));
            //MergeInputs(executionContext, repo);

            ISourceProvider sourceProvider = GetSourceProvider(repo.Type);
            await sourceProvider.GetSourceAsync(executionContext, repo, token);
        }
    }

    public class CleanupTask : RepositoryTask
    {
        public override string Stage => "post";

        public override async Task RunAsync(AgentTaskPluginExecutionContext executionContext, CancellationToken token)
        {
            var repoAlias = executionContext.GetInput(Pipelines.PipelineConstants.CheckoutTaskInputs.Repository, true);
            var repo = executionContext.Repositories.Single(x => string.Equals(x.Alias, repoAlias, StringComparison.OrdinalIgnoreCase));
            //MergeInputs(executionContext, repo);

            ISourceProvider sourceProvider = GetSourceProvider(repo.Type);
            await sourceProvider.PostJobCleanupAsync(executionContext, repo);
        }
    }
}
