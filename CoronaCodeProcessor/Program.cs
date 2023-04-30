// See https://aka.ms/new-console-template for more information


var logger = LogManager.GetLogger("Main");
AppDomain.CurrentDomain.UnhandledException += (object sender, UnhandledExceptionEventArgs e) =>
{
    logger.Fatal((Exception)e.ExceptionObject, "Unhandled exception");
};
var config = await Config.Load();

await SyncSourceMasterBranch();
var rawLogs = await GetLatestGitLog();
if (config.LimitCommitNumberForDebuggingPurposes is int debugLimit)
{
    rawLogs = rawLogs.TakeLast(debugLimit).ToArray();
}
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
var lastTargetCommit = config.SourceCommitToTargetCommit[config.LastSourceCommit];
// we can assume the last source commit is always on the main branch
// because we only process commits that are on the main branch
// so we also bring the last target commit to target's main branch
logger.Info($"Bringing last target commit {lastTargetCommit} to target's main branch");
await RunTargetGit($"branch -f main {lastTargetCommit}");
// push to remote
logger.Info("Pushing to remote");
await RunTargetGit($"push -f -u origin main");
logger.Info("Done");

return 0;


async Task SyncSourceMasterBranch()
{
    logger.Info("Syncing source master branch");
    await RunSourceGit("checkout master --force");
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
                logBuilder.Append('{');
                inBrackets = true;
                continue;
            case endOfText:
                logBuilder.Append("},");
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
    return JsonSerializer.Deserialize<RawCommit[]>(logBuilder.ToString(), Config.JsonOptions)!;
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

    // switch to the source commit, and clean
    await RunSourceGit($"checkout {sourceCommit.Id} --force");
    await RunSourceGit("reset --hard");
    await RunSourceGit("clean -fdx");

    // switch to the target commit's parent
    var targetParents = sourceCommit.Parents
        .Select(p => config.SourceCommitToTargetCommit[p])
        .ToArray();
    if (targetParents.Length == 0)
    {
        // this commit is the root commit, so we don't have a parent
        logger.Info($"Commit {sourceCommit.Id} is the root commit, so we don't have a parent");
        await RestartTargetGitBranch();
    }
    else
    {
        if (targetParents.Length == 1)
        {
            logger.Info($"Commit {sourceCommit.Id} has a single parent {sourceCommit.Parents[0]}, switching to the corresponding target commit {targetParents[0]}");
        }
        else
        {
            logger.Info($"Commit {sourceCommit.Id} has multiple parents {string.Join(", ", sourceCommit.Parents)}, switching to first parent {targetParents[0]}");
        }
        await RunTargetGit($"checkout {targetParents[0]} --force");
        // clean
        await RunSourceGit("reset --hard");
        await RunSourceGit("clean -fdx");
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
        File.WriteAllText(config.TargetRepositoryLastSourceCommitFileFullName, sourceCommit.Id);
    });

    var commitMessage = $"{sourceCommit.Subject}\n\n{sourceCommit.Body}";
    // commit
    var environment = new Dictionary<string, string>
    {
        ["GIT_AUTHOR_NAME"] = config.NameByEmail[sourceCommit.Author],
        ["GIT_AUTHOR_EMAIL"] = "<>",
        ["GIT_AUTHOR_DATE"] = sourceCommit.AuthorDate,
        ["GIT_COMMITTER_NAME"] = config.NameByEmail[sourceCommit.Committer],
        ["GIT_COMMITTER_EMAIL"] = "<>",
        ["GIT_COMMITTER_DATE"] = sourceCommit.CommitDate,
    };
    await RunTargetGit($"commit -a -F -", commitMessage, environment);
    // last commit id
    var targetCommit = await RunTargetGit("rev-parse HEAD");
    if (targetParents.Length > 1)
    {
        // merge
        var mergeParentsOptions = string.Join(' ', targetParents.Select(p => $"-p {p}"));
        logger.Info($"Merging with {mergeParentsOptions}, original new target commit id: {targetCommit}");
        targetCommit = await RunTargetGit($"commit-tree {mergeParentsOptions} -F - {targetCommit}^{{tree}}", commitMessage, environment);
    }

    // save last commit id
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
    await RunTargetGit("reset --hard");
    await RunTargetGit("clean -fdx");
    // create the main branch
    await RunTargetGit($"branch -m {mainBranch}");
    logger.Info($"Reset target repository main branch done");
}

Task<string> RunSourceGit(string arguments)
{
    logger.Debug($"source git {arguments}");
    return RunGit(arguments, config.SourceDirectory, default, default);
}
Task<string> RunTargetGit(string arguments, string? stdin = default, Dictionary<string, string>? enviroment = default)
{
    logger.Debug($"target git {arguments}");
    if (stdin is not null)
    {
        logger.Debug($"stdin: {stdin}");
    }
    if (enviroment is not null)
    {
        logger.Debug($"enviroment: {string.Join(", ", enviroment.Select(x => $"{x.Key}={x.Value}"))}");
    }
    return RunGit(arguments, config.DestinationDirectory, stdin, enviroment);
}

async Task<string> RunGit(string arguments, string directory, string? stdin, Dictionary<string, string>? enviroment)
{
    var info = new ProcessStartInfo
    {
        FileName = "git",
        Arguments = arguments,
        WorkingDirectory = directory,
        UseShellExecute = false,
        RedirectStandardOutput = true,
        RedirectStandardInput = stdin is not null
    };
    if (enviroment is not null)
    {
        foreach (var (key, value) in enviroment)
        {
            info.EnvironmentVariables[key] = value;
        }
    }
    using var process = Process.Start(info) ?? throw new Exception("Failed to start process");
    if (stdin is not null)
    {
        await process.StandardInput.WriteAsync(stdin);
        process.StandardInput.Flush();
        process.StandardInput.Close();
    }
    var result = await process.StandardOutput.ReadToEndAsync();
    if (process.ExitCode != 0)
    {
        throw new Exception($"git {arguments} on {info.WorkingDirectory} failed with exit code {process.ExitCode}");
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
    string LastIncludeList,
    int? LimitCommitNumberForDebuggingPurposes = null,
    string? DebugIncludeListFullName = null)
{
    public const string FileName = "config.json";
    public static readonly JsonSerializerOptions JsonOptions = new()
    {
        AllowTrailingCommas = true,
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase
    };
    public string IncludeListFileFullName => DebugIncludeListFullName ?? Path.Combine(SourceDirectory, IncludeListFileName);
    public string TargetRepositoryLastSourceCommitFileFullName => Path.Combine(DestinationDirectory, LastCommitFileName);

    public static async Task<Config> Load()
    {
        return JsonSerializer.Deserialize<Config>(await File.ReadAllTextAsync(FileName), JsonOptions)!;
    }

    public Task Save()
    {
        return File.WriteAllTextAsync(FileName, JsonSerializer.Serialize(this, JsonOptions));
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