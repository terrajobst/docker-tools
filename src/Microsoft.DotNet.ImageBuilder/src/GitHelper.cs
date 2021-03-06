﻿// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.
// See the LICENSE file in the project root for more information.

using System;
using System.Collections.Generic;
using System.Linq;
using System.Net.Http;
using System.Threading.Tasks;
using Microsoft.DotNet.ImageBuilder.Commands;
using Microsoft.DotNet.ImageBuilder.Models.Subscription;
using Microsoft.DotNet.VersionTools.Automation;
using Microsoft.DotNet.VersionTools.Automation.GitHubApi;

namespace Microsoft.DotNet.ImageBuilder
{
    public static class GitHelper
    {
        private const int DefaultMaxTries = 10;
        private const int DefaultRetryMillisecondsDelay = 5000;

        public static string GetCommitSha(string filePath, bool useFullHash = false)
        {
            string format = useFullHash ? "H" : "h";
            return ExecuteHelper.Execute(
                "git",
                $"log -1 --format=format:%{format} {filePath}",
                false,
                $"Unable to retrieve the latest commit SHA for {filePath}");
        }

        public static Uri GetArchiveUrl(GitRepo gitRepo)
        {
            return new Uri($"https://github.com/{gitRepo.Owner}/{gitRepo.Name}/archive/{gitRepo.Branch}.zip");
        }

        public static Uri GetBlobUrl(GitOptions gitOptions)
        {
            return new Uri($"https://github.com/{gitOptions.Owner}/{gitOptions.Repo}/blob/{gitOptions.Branch}/{gitOptions.Path}");
        }

        public static Uri GetCommitUrl(GitOptions gitOptions, string sha)
        {
            return new Uri($"https://github.com/{gitOptions.Owner}/{gitOptions.Repo}/commit/{sha}");
        }

        public static async Task<GitReference> PushChangesAsync(IGitHubClient client, IGitOptionsHost options, string commitMessage, Func<GitHubBranch, Task<IEnumerable<GitObject>>> getChanges)
        {
            GitOptions gitOptions = options.GitOptions;
            GitHubProject project = new GitHubProject(gitOptions.Repo, gitOptions.Owner);
            GitHubBranch branch = new GitHubBranch(gitOptions.Branch, project);

            IEnumerable<GitObject> changes = await getChanges(branch);

            if (!changes.Any())
            {
                return null;
            }

            string masterRef = $"heads/{gitOptions.Branch}";
            GitReference currentMaster = await client.GetReferenceAsync(project, masterRef);
            string masterSha = currentMaster.Object.Sha;

            if (!options.IsDryRun)
            {
                GitTree tree = await client.PostTreeAsync(project, masterSha, changes.ToArray());
                GitCommit commit = await client.PostCommitAsync(
                    project, commitMessage, tree.Sha, new[] { masterSha });

                // Only fast-forward. Don't overwrite other changes: throw exception instead.
                return await client.PatchReferenceAsync(project, masterRef, commit.Sha, force: false);
            }
            else
            {
                Logger.WriteMessage($"The following files would have been updated at {gitOptions.Owner}/{gitOptions.Repo}/{gitOptions.Branch}:");
                Logger.WriteMessage();
                foreach (GitObject gitObject in changes)
                {
                    Logger.WriteMessage($"{gitObject.Path}:");
                    Logger.WriteMessage(gitObject.Content);
                    Logger.WriteMessage();
                }

                return null;
            }
        }

        public static async Task ExecuteGitOperationsWithRetryAsync(Func<Task> execute,
            int maxTries = DefaultMaxTries, int retryMillisecondsDelay = DefaultRetryMillisecondsDelay)
        {
            for (int i = 0; i < maxTries; i++)
            {
                try
                {
                    await execute();

                    break;
                }
                catch (HttpRequestException ex) when (i < (maxTries - 1))
                {
                    Logger.WriteMessage($"Encountered exception interacting with GitHub: {ex.Message}");
                    Logger.WriteMessage($"Trying again in {retryMillisecondsDelay}ms. {maxTries - i - 1} tries left.");
                    await Task.Delay(retryMillisecondsDelay);
                }
            }
        }
    }
}
