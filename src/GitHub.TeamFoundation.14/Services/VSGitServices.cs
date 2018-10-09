﻿#if !TEAMEXPLORER14
// Microsoft.VisualStudio.Shell.Framework has an alias to avoid conflict with IAsyncServiceProvider
extern alias SF15;
using ServiceProgressData = SF15::Microsoft.VisualStudio.Shell.ServiceProgressData;
#endif

using System;
using System.Threading;
using System.Collections.Generic;
using System.Diagnostics.CodeAnalysis;
using System.ComponentModel.Composition;
using System.Linq;
using System.Reactive.Linq;
using System.Threading.Tasks;
using GitHub.Extensions;
using GitHub.Logging;
using GitHub.Models;
using GitHub.TeamFoundation;
using GitHub.VisualStudio;
using Microsoft.TeamFoundation.Controls;
using Microsoft.TeamFoundation.Git.Controls.Extensibility;
using Microsoft.VisualStudio.TeamFoundation.Git.Extensibility;
using ReactiveUI;
using Serilog;
using Microsoft;

namespace GitHub.Services
{
    [Export(typeof(IVSGitServices))]
    [PartCreationPolicy(CreationPolicy.Shared)]
    public class VSGitServices : IVSGitServices
    {
        static readonly ILogger log = LogManager.ForContext<VSGitServices>();

        readonly IGitHubServiceProvider serviceProvider;

        [SuppressMessage("Microsoft.Performance", "CA1823:AvoidUnusedPrivateFields", Justification = "Used in VS2017")]
        readonly Lazy<IStatusBarNotificationService> statusBar;
        readonly Lazy<IVSServices> vsServices;

        /// <summary>
        /// This MEF export requires specific versions of TeamFoundation. IGitExt is declared here so
        /// that instances of this type cannot be created if the TeamFoundation dlls are not available
        /// (otherwise we'll have multiple instances of IVSServices exports, and that would be Bad(tm))
        /// </summary>
        [SuppressMessage("Microsoft.Performance", "CA1823:AvoidUnusedPrivateFields")]
        IGitExt gitExtService;

        [ImportingConstructor]
        public VSGitServices(IGitHubServiceProvider serviceProvider,
            Lazy<IStatusBarNotificationService> statusBar,
            Lazy<IVSServices> vsServices)
        {
            this.serviceProvider = serviceProvider;
            this.statusBar = statusBar;
            this.vsServices = vsServices;
        }

        // The Default Repository Path that VS uses is hidden in an internal
        // service 'ISccSettingsService' registered in an internal service
        // 'ISccServiceHost' in an assembly with no public types that's
        // always loaded with VS if the git service provider is loaded
        public string GetLocalClonePathFromGitProvider()
        {
            string ret = string.Empty;

            try
            {
                ret = RegistryHelper.PokeTheRegistryForLocalClonePath();
            }
            catch (Exception ex)
            {
                log.Error(ex, "Error loading the default cloning path from the registry");
            }
            return ret;
        }

        /// <inheritdoc/>
        public async Task Clone(
            string cloneUrl,
            string clonePath,
            bool recurseSubmodules,
            object progress = null)
        {
            var teamExplorer = serviceProvider.TryGetService<ITeamExplorer>();
            Assumes.Present(teamExplorer);

#if TEAMEXPLORER14
            var gitExt = await GetGitRepositoriesExtAsync(teamExplorer);
            gitExt.Clone(cloneUrl, clonePath, recurseSubmodules ? CloneOptions.RecurseSubmodule : CloneOptions.None);

            // The operation will have completed when CanClone goes false and then true again.
            // It looks like the CanClone property is only live as long as the Connect page is visible.
            await gitExt.WhenAnyValue(x => x.CanClone).Where(x => !x).Take(1); // Wait until started
            await gitExt.WhenAnyValue(x => x.CanClone).Where(x => x).Take(1); // Wait until completed

            // Show progress on Team Explorer - Home
            NavigateToHomePage(teamExplorer);

            // Open cloned repository in Team Explorer
            vsServices.Value.TryOpenRepository(clonePath);
#else
            var gitExt = serviceProvider.GetService<IGitActionsExt>();
            var typedProgress = ((Progress<ServiceProgressData>)progress) ?? new Progress<ServiceProgressData>();

            // Show progress on Team Explorer - Home
            NavigateToHomePage(teamExplorer);

            await Microsoft.VisualStudio.Shell.ThreadHelper.JoinableTaskFactory.RunAsync(async () =>
            {
                typedProgress.ProgressChanged += (s, e) => statusBar.Value.ShowMessage(e.ProgressText);
                await gitExt.CloneAsync(cloneUrl, clonePath, recurseSubmodules, default(CancellationToken), typedProgress);
            });
#endif
        }

        static void NavigateToHomePage(ITeamExplorer teamExplorer)
        {
            teamExplorer.NavigateToPage(new Guid(TeamExplorerPageIds.Home), null);
        }

        static async Task<IGitRepositoriesExt> GetGitRepositoriesExtAsync(ITeamExplorer teamExplorer)
        {
            var connectPage = await NavigateToPageAsync(teamExplorer, new Guid(TeamExplorerPageIds.Connect));
            Assumes.Present(connectPage);
            var gitExt = connectPage.GetService<IGitRepositoriesExt>();
            Assumes.Present(gitExt);
            return gitExt;
        }

        static async Task<ITeamExplorerPage> NavigateToPageAsync(ITeamExplorer teamExplorer, Guid pageId)
        {
            teamExplorer.NavigateToPage(pageId, null);
            var page = await teamExplorer
                .WhenAnyValue(x => x.CurrentPage)
                .Where(x => x?.GetId() == pageId)
                .Take(1);
            return page;
        }

        IGitRepositoryInfo GetRepoFromVS()
        {
            gitExtService = serviceProvider.GetService<IGitExt>();
            return gitExtService.ActiveRepositories.FirstOrDefault();
        }

        public LibGit2Sharp.IRepository GetActiveRepo()
        {
            var repo = GetRepoFromVS();
            return repo != null
                ? serviceProvider.GetService<IGitService>().GetRepository(repo.RepositoryPath)
                : serviceProvider.GetSolution().GetRepositoryFromSolution();
        }

        public string GetActiveRepoPath()
        {
            string ret = null;
            var repo = GetRepoFromVS();
            if (repo != null)
                ret = repo.RepositoryPath;
            if (ret == null)
            {
                using (var repository = serviceProvider.GetSolution().GetRepositoryFromSolution())
                {
                    ret = repository?.Info?.Path;
                }
            }
            return ret ?? String.Empty;
        }

        public IEnumerable<ILocalRepositoryModel> GetKnownRepositories()
        {
            try
            {
                return RegistryHelper.PokeTheRegistryForRepositoryList();
            }
            catch (Exception ex)
            {
                log.Error(ex, "Error loading the repository list from the registry");
                return Enumerable.Empty<ILocalRepositoryModel>();
            }
        }

        public string SetDefaultProjectPath(string path)
        {
            return RegistryHelper.SetDefaultProjectPath(path);
        }
    }
}
