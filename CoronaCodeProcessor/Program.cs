﻿// See https://aka.ms/new-console-template for more information


var logger = LogManager.GetLogger("Main");
var config = await Config.Load();

await SyncSourceMasterBranch();
var rawLogs = await GetLatestGitLog();
await UpdateEmailTable();
var sourceCommits = rawLogs
    .Select(c => new Commit(c.Id, c.Parents.Split(' '), c.Subject, c.Body, c.Author, c.AuthorDate, c.Committer, c.CommitDate))
    .ToArray();
var sourceCommitById = sourceCommits.ToDictionary(c => c.Id);

// read and normalize the latest include list
var includeList = (await File.ReadAllLinesAsync(config.IncludeListFileFullName))
    .Select(x => x.Trim())
    .Where(x => !string.IsNullOrWhiteSpace(x))
    .OrderBy(x => x);
var includeListString = string.Join('\n', includeList);
// fully reprocess everything if:
// - filter changed
// - last commit not in git log (which indicates a possible force push)
if (!sourceCommitById.ContainsKey(config.LastSourceCommit))
{
    logger.Info("Last source commit not found in git log, history diverged, reprocessing everything");
    await RestartTargetGitBranch();
}
if (config.LastIncludeList != includeListString)
{
    logger.Info("Include list has changed, reprocessing everything");
    await RestartTargetGitBranch();
}

// process commits
var sourceCommitsToPortedOnTarget = sourceCommits
    .TakeWhile(c => c.Id != config.LastSourceCommit)
    .Reverse();
foreach (var sourceCommit in sourceCommitsToPortedOnTarget)
{
    await CreateTargetCommit(sourceCommit);
}

async Task SyncSourceMasterBranch()
{
    logger.Info("Syncing source master branch");
    await RunSourceGit("checkout master");
    await RunSourceGit("fetch");
    await RunSourceGit("reset --hard origin/master");
    logger.Info("Obtained latest changes from master");
}

async Task<RawCommit[]> GetLatestGitLog()
{
    const string format = "{ 'id': '%H', 'parents': '%P', 'subject': '%s', 'body': '%b', 'author': '%ae', 'authorDate': '%aI', 'committer': '%ce', 'committerDate': '%cI', 'committerName': '%an' }";
    const char startOfText = '\x02';
    const char endOfText = '\x03';
    const char unitSeparator = '\x1F';
    var formatString = format
    // we want to produce JSON, however if we are using quotes like normal JSON,
    // and the commit message contains quotes, it will break the JSON format.
    // So we use a different character to represent quotes.
    // We replace it to 0x1F (Unit Separator), which is a control character that is unlikely to appear in commit messages.
    // We also replace { and } to 0x02 (Start of Text) and 0x03 (End of Text) respectively.
        .Replace('{', startOfText)
        .Replace('}', endOfText)
        .Replace('\'', unitSeparator);
    var log = await RunSourceGit($"--no-pager log --format=\"{formatString}\"");
    // preprocess the log to replace the special characters back
    bool inBrackets = false;
    var logBuilder = new StringBuilder(log.Length);
    logBuilder.Append('[');
    foreach (var character in log)
    {
        switch (character)
        {
            case startOfText:
                if (logBuilder.Length > 0)
                {
                    // separate brackets with comma
                    logBuilder.Append(',');
                }
                logBuilder.Append('{');
                inBrackets = true;
                continue;
            case endOfText:
                logBuilder.Append('}');
                inBrackets = false;
                continue;
            case unitSeparator:
                logBuilder.Append('"');
                continue;
        }
        if (!inBrackets)
        {
            continue;
        }
        if (character < ' ' || character is '"' or '\\' or '/')
        {
            logBuilder.Append($"\\u{(int)character:X4}");
            continue;
        }
        logBuilder.Append(character);
    }
    logBuilder.Append(']');
    return JsonSerializer.Deserialize<RawCommit[]>(logBuilder.ToString())!;
}

async Task UpdateEmailTable()
{
    foreach (var c in rawLogs)
    {
        // populate email to author dict
        if (!config.NameByEmail.ContainsKey(c.Author))
        {
            config.NameByEmail[c.Author] = c.AuthorName;
            logger.Info($"Added {c.AuthorName} to config");
        }
        if (!config.NameByEmail.ContainsKey(c.Committer))
        {
            config.NameByEmail[c.Committer] = c.CommitterName;
            logger.Info($"Added {c.CommitterName} to config");
        }
    }
    // save email to author dict
    await config.Save();
}

async Task CreateTargetCommit(Commit sourceCommit)
{
    logger.Info($"Switching to source {sourceCommit.Id}");

    // clean, then switch to the target commit
    await RunSourceGit("reset --hard");
    await RunSourceGit("clean -fdx");
    await RunSourceGit($"checkout {sourceCommit.Id}");
    
    // clean, then switch to the commit's parent
    await RunTargetGit("reset --hard");
    await RunTargetGit("clean -fdx");
    switch (sourceCommit.Parents.Length)
    {
        case 0:
            // this commit is the root commit, so we don't have a parent
            logger.Info($"Commit {sourceCommit.Id} is the root commit, so we don't have a parent");
            await RestartTargetGitBranch();
            break;
        case 1:
            // this commit has a single parent, so we can just switch to it
            var targetParent = config.SourceCommitToTargetCommit[sourceCommit.Parents[0]];
            logger.Info($"Commit {sourceCommit.Id} has a single parent {sourceCommit.Parents[0]}, switched to the corresponding target commit {targetParent}");
            await RunTargetGit($"checkout {targetParent}");
            break;
        default:
            throw new NotImplementedException("Merge commits are not supported yet");
    }
    
    // copy files from source to target according to the include list
    await Task.Run(() =>
    {
        var files = includeList.SelectMany(pattern => Directory.EnumerateFiles(config.SourceDirectory, pattern)).ToArray();
        foreach (var sourceFullName in files)
        {
            var relativeFileName = Path.GetRelativePath(config.SourceDirectory, sourceFullName);
            var destinationFullName = Path.Combine(config.DestinationDirectory, relativeFileName);
            File.Copy(sourceFullName, destinationFullName, true);
            logger.Debug($"Copied {sourceFullName} to {destinationFullName}");
        }
    });

    // commit
    await RunTargetGit($"commit -a -m \"{sourceCommit.Subject}\" -m \"{sourceCommit.Body}\"", new()
    {
        ["GIT_AUTHOR_NAME"] = config.NameByEmail[sourceCommit.Author],
        ["GIT_AUTHOR_EMAIL"] = "<>",
        ["GIT_AUTHOR_DATE"] = sourceCommit.AuthorDate,
        ["GIT_COMMITTER_NAME"] = config.NameByEmail[sourceCommit.Committer],
        ["GIT_COMMITTER_EMAIL"] = "<>",
        ["GIT_COMMITTER_DATE"] = sourceCommit.CommitDate,
    });

    // save last commit id
    var targetCommit = await RunTargetGit("rev-parse HEAD");
    logger.Info($"Committed on target repository: {targetCommit}...");
    config.SourceCommitToTargetCommit[sourceCommit.Id] = targetCommit;
    config = config with { LastSourceCommit = sourceCommit.Id };
    await config.Save();
}

async Task RestartTargetGitBranch()
{
    config = config with
    {
        LastSourceCommit = string.Empty,
        LastIncludeList = includeListString,
        SourceCommitToTargetCommit = new()
    };
    await config.Save();

    logger.Info($"Reset target repository main branch");
    const string mainBranch = "main";
    var temporaryId = $"branch-{Guid.NewGuid():N}";
    await RunTargetGit($"checkout --orphan {temporaryId}");
    var existingBranches = (await RunTargetGit("branch --list"))
        .Split('\n')
        .Select(x => x.Trim())
        .ToHashSet();
    if (existingBranches.Contains(mainBranch))
    {
        // delete the main branch
        await RunTargetGit($"branch -D {mainBranch}");
    }
    // delete everything
    await RunTargetGit("reset");
    await RunTargetGit("clean -fdx");
    // create the main branch
    await RunTargetGit($"branch -m {mainBranch}");
    logger.Info($"Reset target repository main branch done");
}

Task<string> RunSourceGit(string arguments, Dictionary<string, string>? enviroment = default)
{
    logger.Debug($"source git {arguments}");
    return RunGit(arguments, config.SourceDirectory, enviroment);
}
Task<string> RunTargetGit(string arguments, Dictionary<string, string>? enviroment = default)
{
    logger.Debug($"target git {arguments}");
    return RunGit(arguments, config.DestinationDirectory, enviroment);
}

async Task<string> RunGit(string arguments, string directory, Dictionary<string, string>? enviroment)
{
    var info = new ProcessStartInfo
    {
        FileName = "git",
        Arguments = arguments,
        WorkingDirectory = directory,
        UseShellExecute = false,
        RedirectStandardOutput = true,
    };
    if (enviroment is not null)
    {
        foreach (var (key, value) in enviroment)
        {
            info.EnvironmentVariables[key] = value;
        }
    }
    using var process = Process.Start(info) ?? throw new Exception("Failed to start process");
    var result = await process.StandardOutput.ReadToEndAsync();
    if (process.ExitCode != 0)
    {
        throw new Exception($"git {arguments} failed with exit code {process.ExitCode}");
    }
    return result;
}

record Config(
    string SourceDirectory,
    string DestinationDirectory,
    string IncludeListFileName,
    string LastCommitFileName,
    Dictionary<string, string> SourceCommitToTargetCommit,
    Dictionary<string, string> NameByEmail,
    string LastSourceCommit,
    string LastIncludeList)
{
    public const string FileName = "config.json";
    public string IncludeListFileFullName => Path.Combine(SourceDirectory, IncludeListFileName);
    public string LastCommitFileFullName => Path.Combine(DestinationDirectory, LastCommitFileName);

    public static async Task<Config> Load()
    {
        return JsonSerializer.Deserialize<Config>(await File.ReadAllTextAsync(FileName))!;
    }

    public Task Save()
    {
        return File.WriteAllTextAsync(FileName, JsonSerializer.Serialize(this));
    }
}

record Commit(
    string Id,
    string[] Parents,
    string Subject,
    string Body,
    string Author,
    string AuthorDate,
    string Committer,
    string CommitDate);

record RawCommit(
    string Id,
    string Parents,
    string Subject,
    string Body,
    string Author,
    string AuthorDate,
    string AuthorName,
    string Committer,
    string CommitDate,
    string CommitterName);