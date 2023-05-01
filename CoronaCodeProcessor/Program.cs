// See https://aka.ms/new-console-template for more information

var logger = LogManager.GetLogger("Main");
DateTimeOffset startTime = DateTimeOffset.Now;
Config config;
Cache cache;
try
{
    config = await Config.Load();
    cache = await Cache.Load(config);

    await SyncSourceMainBranch();
    var rawLogs = await GetLatestGitLog();
    if (config.LimitCommitNumberForDebuggingPurposes is int debugLimit)
    {
        rawLogs = rawLogs.TakeLast(debugLimit).ToArray();
    }
    await UpdateEmailTable(rawLogs);
    var sourceCommits = rawLogs
        .Select(c => new Commit(c.Id, c.Parents.Split(' ', StringSplitOptions.RemoveEmptyEntries), c.Subject, c.Body, c.Author, c.AuthorDate, c.Committer, c.CommitDate))
        .ToArray();
    var sourceCommitById = sourceCommits.ToDictionary(c => c.Id);

    // read and normalize the latest include list
    var includeList = (await File.ReadAllLinesAsync(config.IncludeListFileFullName))
        .Select(x => x.Split("//")[0].Trim())
        .Where(x => !string.IsNullOrWhiteSpace(x))
        .OrderBy(x => x)
        .ToArray();
    // fully reprocess everything if:
    // - filter changed
    // - last commit not in git log (which indicates a possible force push)
    if (!sourceCommitById.ContainsKey(cache.LastSourceCommit))
    {
        logger.Info("Last source commit not found in git log, history diverged, reprocessing everything");
        await RestartTargetGitBranch();
    }
    if (!cache.LastIncludeList.SequenceEqual(includeList))
    {
        logger.Info("Include list has changed, reprocessing everything");
        cache = cache with { LastIncludeList = includeList };
        await RestartTargetGitBranch();
    }

    // process commits
    var sourceCommitsToPortedOnTarget = sourceCommits
        .TakeWhile(c => c.Id != cache.LastSourceCommit)
        .Reverse();
    if (!sourceCommitsToPortedOnTarget.Any())
    {
        logger.Info("No new commits to process");
        return 0;
    }
    foreach (var sourceCommit in sourceCommitsToPortedOnTarget)
    {
        await CreateTargetCommit(sourceCommit);
    }
    var lastTargetCommit = cache.SourceCommitToTargetCommit[cache.LastSourceCommit];
    // we can assume the last source commit is always on the main branch
    // because we only process commits that are on the main branch
    // so we also bring the last target commit to target's main branch
    logger.Info($"Bringing last target commit {lastTargetCommit} to target's main branch");
    await RunTargetGit($"branch -f {config.TargetMainBranch} {lastTargetCommit}");
    await RunTargetGit($"checkout -f {config.TargetMainBranch}");
    var finishTime = DateTimeOffset.Now;
    if (cache.LastFullGenerationEnd == DateTimeOffset.MaxValue)
    {
        cache = cache with { LastFullGenerationEnd = finishTime };
        await cache.Save(config);
    }
    // add additional files
    await AddAdditionalFiles(finishTime);
    // push to remote
    logger.Info("Pushing to remote");
    await RunTargetGit($"push -f -u {config.TargetRemote} {config.TargetMainBranch}");
    logger.Info("Done");
    // clean garbage
    await RunTargetGit("reflog expire --expire-unreachable=now --all");
    await RunTargetGit("gc --prune=now");
    logger.Info("Garbage cleaned");

    return 0;
}
catch (Exception e) when (!Debugger.IsAttached)
{
    logger.Fatal(e, "Unhandled Exception");
}
return 1;


async Task SyncSourceMainBranch()
{
    logger.Info("Syncing source main branch");
    await RunSourceGit($"fetch {config.SourceRemote} {config.SourceMainBranch}");
    logger.Info("Fetched latest changes from source remote");
}

async Task<RawCommit[]> GetLatestGitLog()
{
    const string format = @"{
        'id': '%H', 'parents': '%P', 'subject': '%s', 'body': '%b',
        'author': '%ae', 'authorDate': '%aI', 'authorName': '%an',
        'committer': '%ce', 'commitDate': '%cI', 'committerName': '%cn' 
    }";
    const char startOfText = '\x02';
    const char endOfText = '\x03';
    const char unitSeparator = '\x1F';
    var formatString = System.Text.RegularExpressions.Regex.Replace(format, "\\s+", " ")
    // we want to produce JSON, however if we are using quotes like normal JSON,
    // and the commit message contains quotes, it will break the JSON format.
    // So we use a different character to represent quotes.
    // We replace it to 0x1F (Unit Separator), which is a control character that is unlikely to appear in commit messages.
    // We also replace { and } to 0x02 (Start of Text) and 0x03 (End of Text) respectively.
        .Replace('{', startOfText)
        .Replace('}', endOfText)
        .Replace('\'', unitSeparator);
    var log = await RunSourceGit($"--no-pager log --format=\"{formatString}\" {config.SourceRemote}/{config.SourceMainBranch}");
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
    logger.Debug($"git log:\n{logBuilder}");
    return JsonSerializer.Deserialize<RawCommit[]>(logBuilder.ToString(), Config.JsonOptions)!;
}

async Task UpdateEmailTable(RawCommit[] rawLogs)
{
    foreach (var c in rawLogs)
    {
        // populate email to author dict
        if (!cache.NameByEmail.ContainsKey(c.Author))
        {
            cache.NameByEmail[c.Author] = c.AuthorName;
            logger.Info($"Added {c.AuthorName} to config");
        }
        if (!cache.NameByEmail.ContainsKey(c.Committer))
        {
            cache.NameByEmail[c.Committer] = c.CommitterName;
            logger.Info($"Added {c.CommitterName} to config");
        }
    }
    // save email to author dict
    await cache.Save(config);
}

async Task CreateTargetCommit(Commit sourceCommit)
{
    logger.Info($"Switching to source {sourceCommit.Id}");

    // switch to the source commit, and clean
    await RunSourceGit($"-c advice.detachedHead=false checkout {sourceCommit.Id} --force");
    await RunSourceGit("reset --hard");
    await RunSourceGit("clean -fdx");

    // switch to the target commit's parent
    var targetParents = sourceCommit.Parents
        .Select(p => cache.SourceCommitToTargetCommit[p])
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
        await RunTargetGit($"-c advice.detachedHead=false checkout {targetParents[0]} --force");
        // clean
        await RunSourceGit("reset --hard");
        await RunSourceGit("clean -fdx");
    }

    // copy files from source to target according to the include list
    await Task.Run(async () =>
    {
        var deleteTask = Task.Run(() =>
        {
            logger.Trace("Starting deleting target repository content");
            Array.ForEach(Directory.GetFiles(config.TargetDirectory), File.Delete);
            foreach (var directory in Directory.GetDirectories(config.TargetDirectory))
            {
                var directoryInfo = new DirectoryInfo(directory);
                if (directoryInfo.Name != ".git")
                {
                    directoryInfo.Delete(true);
                    logger.Trace($"Deleted folder {directory}");
                }
            }
        });
        var files = cache.LastIncludeList.SelectMany(pattern => FindFiles(config.SourceDirectory, pattern)).ToArray();
        // wait until files are deleted
        await deleteTask;
        foreach (var sourceFullName in files)
        {
            var relativeFileName = Path.GetRelativePath(config.SourceDirectory, sourceFullName);
            var destinationFullName = Path.Combine(config.TargetDirectory, relativeFileName);
            new FileInfo(destinationFullName).Directory?.Create();
            File.Copy(sourceFullName, destinationFullName, true);
            logger.Trace($"Copied {sourceFullName} to {destinationFullName}");
        }
        File.WriteAllText(config.TargetRepositoryLastSourceCommitFileFullName, sourceCommit.Id);
    });

    var commitMessage = $"{sourceCommit.Subject.Trim()}\n\n{sourceCommit.Body.Trim()}";
    // commit
    var environment = new Dictionary<string, string>
    {
        ["GIT_AUTHOR_NAME"] = config.NameByEmail[sourceCommit.Author],
        ["GIT_AUTHOR_EMAIL"] = config.EmailMap.GetValueOrDefault(sourceCommit.Author, "<>"),
        ["GIT_AUTHOR_DATE"] = sourceCommit.AuthorDate,
        ["GIT_COMMITTER_NAME"] = config.NameByEmail[sourceCommit.Committer],
        ["GIT_COMMITTER_EMAIL"] = config.EmailMap.GetValueOrDefault(sourceCommit.Committer, "<>"),
        ["GIT_COMMITTER_DATE"] = sourceCommit.CommitDate,
    };
    await RunTargetGit($"add -A");
    await RunTargetGit($"commit -F - --allow-empty-message", commitMessage, environment);
    // last commit id
    var targetCommit = await RunTargetGit("rev-parse HEAD");
    targetCommit = targetCommit.Trim(); // rev-parse HEAD will output with newline
    if (targetParents.Length > 1)
    {
        // merge
        var mergeParentsOptions = string.Join(' ', targetParents.Select(p => $"-p {p}"));
        logger.Info($"Merging with {mergeParentsOptions}, original new target commit id: {targetCommit}");
        targetCommit = await RunTargetGit($"commit-tree {mergeParentsOptions} -F - {targetCommit}^{{tree}}", commitMessage, environment);
        targetCommit = targetCommit.Trim();
    }
    
    // save last commit id
    logger.Info($"Committed on target repository: {targetCommit}...");
    cache.SourceCommitToTargetCommit[sourceCommit.Id] = targetCommit;
    cache = cache with { LastSourceCommit = sourceCommit.Id };
    await cache.Save(config);
}

async Task AddAdditionalFiles(DateTimeOffset finishTime)
{
    if (string.IsNullOrWhiteSpace(config.AdditionalFilesDirectory))
    {
        logger.Info("No additional files directory specified, skipping");
    }
    else
    {
        logger.Info($"Adding additional files from {config.AdditionalFilesDirectory}");
        await Task.Run(() =>
        {
            var directories = new Queue<string>();
            directories.Enqueue(config.AdditionalFilesDirectory);
            while (directories.Count > 0)
            {
                var directory = directories.Dequeue();
                foreach (var file in Directory.GetFiles(directory))
                {
                    var relativeFileName = Path.GetRelativePath(config.AdditionalFilesDirectory, file);
                    var destinationFullName = Path.Combine(config.TargetDirectory, relativeFileName);
                    new FileInfo(destinationFullName).Directory?.Create();
                    File.Copy(file, destinationFullName, true);
                    logger.Trace($"Copied {file} to {destinationFullName}");
                }
                foreach (var subDirectory in Directory.GetDirectories(directory))
                {
                    directories.Enqueue(subDirectory);
                }
            }
        });
    }
    if (config.AutoGeneratedReadMeFileFormat is { } format)
    {
        string formatted;
        var fullGenerationDuration = cache.LastFullGenerationEnd - cache.LastFullGenerationStart;
        if (cache.LastFullGenerationStart == startTime)
        {
            formatted = string.Format(format, "/", "/", startTime, cache.LastFullGenerationStart, fullGenerationDuration);
        }
        else
        {
            formatted = string.Format(format, startTime, finishTime - startTime, cache.LastFullGenerationStart, fullGenerationDuration);
        }
        logger.Info($"Prepared autogenerated content {formatted}");
        // find readme file
        foreach (var readme in await Task.Run(() => FindFiles(config.TargetDirectory, "readme*")))
        {
            string content;
            try
            {
                content = await File.ReadAllTextAsync(readme, new UTF8Encoding(false, true));
            }
            catch (Exception ex)
            {
                logger.Warn($"Failed read {readme}, probably it's not a text file: {ex}");
                continue;
            }
            // ensure content is text file
            if (content.Any(x => x is < ' ' and not '\r' and not '\n' and not '\t' || x is '\x7F'))
            {
                logger.Warn($"Probably {readme} is not a text file");
                continue;
            }
            content += formatted;
            await File.WriteAllTextAsync(readme, content);
            logger.Info($"Processed readme file {readme}");
        }
    }

    // check if anything changed
    if (!(await RunTargetGit("status -s")).Trim().Any())
    {
        logger.Info("No changes detected, skipping commit for additional files");
        return;
    }

    await RunTargetGit("add -A");
    var authorEmail = config.AdditionalFilesAuthor ?? "<>";
    var author = config.NameByEmail.GetValueOrDefault(authorEmail, "<>");
    authorEmail = config.EmailMap.GetValueOrDefault(authorEmail, authorEmail);
    var nowString = finishTime.ToString("o");
    await RunTargetGit($"commit -F - --allow-empty-message", config.AdditionalFilesCommitMessage, new()
    {
        ["GIT_AUTHOR_NAME"] = author,
        ["GIT_AUTHOR_EMAIL"] = authorEmail,
        ["GIT_AUTHOR_DATE"] = nowString,
        ["GIT_COMMITTER_NAME"] = author,
        ["GIT_COMMITTER_EMAIL"] = nowString,
        ["GIT_COMMITTER_DATE"] = nowString,
    });
    logger.Info("Additional files added");
}

string[] FindFiles(string directory, string pattern)
{
    logger.Trace($"Searching files in {directory} with pattern {pattern}");
    var enumerateOptions = new EnumerationOptions { MatchCasing = MatchCasing.CaseInsensitive };
    var wildcards = new[] { '*', '?' };
    var slashes = new[] { '/', '\\' };
    var wildcardIndex = pattern.IndexOfAny(wildcards);
    if (wildcardIndex != -1)
    {
        // handle directories in the pattern before the wildcard.
        var slashBeforeWildcard = pattern.LastIndexOfAny(slashes, wildcardIndex);
        if (slashBeforeWildcard != -1)
        {
            // split pattern into two parts,
            // one for searching directories without wildcard
            // and another for searching childrens with wildcard in found directory.
            var childPattern = pattern[(slashBeforeWildcard + 1)..];
            var directoryPattern = pattern[..slashBeforeWildcard];
            return Directory.EnumerateDirectories(directory, directoryPattern, enumerateOptions)
                .SelectMany(d => FindFiles(d, childPattern))
                .ToArray();
        }
        // handle directories in the pattern after the wildcard.
        var slashAfterWildcard = pattern.IndexOfAny(slashes, wildcardIndex);
        if (slashAfterWildcard != -1)
        {
            // split pattern into two parts,
            // one for searching directories with wildcard,
            // and another for searching childrens in found directory.
            var childPattern = pattern[(slashAfterWildcard + 1)..];
            var directoryPattern = pattern[..slashAfterWildcard];
            return Directory.EnumerateDirectories(directory, directoryPattern, enumerateOptions)
                .SelectMany(d => FindFiles(d, childPattern))
                .ToArray();
        }
    }
    return Directory.GetFiles(directory, pattern, enumerateOptions).ToArray();
}

async Task RestartTargetGitBranch()
{
    cache = cache with
    {
        LastSourceCommit = string.Empty,
        SourceCommitToTargetCommit = new(),
        LastFullGenerationStart = startTime,
        LastFullGenerationEnd = DateTimeOffset.MaxValue,
    };
    await cache.Save(config);

    logger.Info($"Reset target repository main branch");
    var temporaryId = $"branch-{Guid.NewGuid():N}";
    await RunTargetGit($"checkout --orphan {temporaryId}");
    var existingBranches = (await RunTargetGit("branch --list"))
        .Split('\n')
        .Select(x => x.Trim())
        .ToHashSet();
    if (existingBranches.Contains(config.TargetMainBranch))
    {
        // delete the main branch
        await RunTargetGit($"branch -D {config.TargetMainBranch}");
    }
    // delete everything
    await RunTargetGit("reset --hard");
    await RunTargetGit("clean -fdx");
    // create the main branch
    await RunTargetGit($"branch -m {config.TargetMainBranch}");
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
    return RunGit(arguments, config.TargetDirectory, stdin, enviroment);
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
        RedirectStandardInput = stdin is not null,
        StandardOutputEncoding = Encoding.UTF8,
    };
    if (enviroment is not null)
    {
        foreach (var (key, value) in enviroment)
        {
            info.EnvironmentVariables[key] = value;
        }
    }
    if (info.RedirectStandardInput)
    {
        info.StandardInputEncoding = Encoding.UTF8;
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
        throw new Exception($"git {arguments} on {info.WorkingDirectory} failed with exit code {process.ExitCode}, output: {result}");
    }
    return result;
}

record Config(
    string CacheFileName,
    string SourceDirectory,
    string TargetDirectory,
    string IncludeListFileName,
    string LastCommitFileName,
    Dictionary<string, string> NameByEmail,
    Dictionary<string, string> EmailMap,
    string SourceRemote,
    string SourceMainBranch,
    string TargetRemote,
    string TargetMainBranch,
    string? AutoGeneratedReadMeFileFormat,
    string? AdditionalFilesDirectory,
    string? AdditionalFilesCommitMessage,
    string? AdditionalFilesAuthor,
    int? LimitCommitNumberForDebuggingPurposes = null,
    string? DebugIncludeListFullName = null)
{
    [System.Text.Json.Serialization.JsonIgnore]
    public const string FileName = "config.json";
    [System.Text.Json.Serialization.JsonIgnore]
    public static readonly JsonSerializerOptions JsonOptions = new()
    {
        AllowTrailingCommas = true,
        ReadCommentHandling = JsonCommentHandling.Skip,
        WriteIndented = true,
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        PropertyNameCaseInsensitive = true,
    };
    [System.Text.Json.Serialization.JsonIgnore]
    public string IncludeListFileFullName => DebugIncludeListFullName ?? Path.Combine(SourceDirectory, IncludeListFileName);
    [System.Text.Json.Serialization.JsonIgnore]
    public string TargetRepositoryLastSourceCommitFileFullName => Path.Combine(TargetDirectory, LastCommitFileName);

    public static async Task<Config> Load()
    {
        var loaded = JsonSerializer.Deserialize<Config>(await File.ReadAllTextAsync(FileName), JsonOptions)!;
        static void CheckRequiredField(string? field, [System.Runtime.CompilerServices.CallerArgumentExpression(nameof(field))] string name = null!)
        {
            if (string.IsNullOrWhiteSpace(field))
            {
                throw new ArgumentException($"config field {name.Split('.').Last()} is required");
            }
        }
        CheckRequiredField(loaded.CacheFileName);
        CheckRequiredField(loaded.SourceDirectory);
        CheckRequiredField(loaded.TargetDirectory);
        CheckRequiredField(loaded.IncludeListFileName);
        CheckRequiredField(loaded.LastCommitFileName);
        // fix null fields
        loaded = loaded with
        {
            SourceRemote = loaded.SourceRemote ?? "origin",
            SourceMainBranch = loaded.SourceMainBranch ?? "master",
            TargetRemote = loaded.TargetRemote ?? "origin",
            TargetMainBranch = loaded.TargetMainBranch ?? "main",
            NameByEmail = loaded.NameByEmail ?? new(),
            EmailMap = loaded.EmailMap ?? new(),
        };
        return loaded;
    }

    public Task Save()
    {
        return File.WriteAllTextAsync(FileName, JsonSerializer.Serialize(this, JsonOptions));
    }
}

record Cache(
    Dictionary<string, string> SourceCommitToTargetCommit,
    Dictionary<string, string> NameByEmail,
    string LastSourceCommit,
    string[] LastIncludeList,
    DateTimeOffset LastFullGenerationStart,
    DateTimeOffset LastFullGenerationEnd)
{
    public static async Task<Cache> Load(Config config)
    {
        var content = await File.ReadAllTextAsync(config.CacheFileName);
        var loaded = JsonSerializer.Deserialize<Cache>(content, Config.JsonOptions)!;
        // fix null fields
        loaded = loaded with
        {
            SourceCommitToTargetCommit = loaded.SourceCommitToTargetCommit ?? new(),
            NameByEmail = loaded.NameByEmail ?? new(),
            LastSourceCommit = loaded.LastSourceCommit ?? string.Empty,
            LastIncludeList = loaded.LastIncludeList ?? Array.Empty<string>(),
        };
        // override cache from config
        foreach (var (key, value) in config.NameByEmail)
        {
            loaded.NameByEmail[key] = value;
        }
        return loaded;
    }

    public Task Save(Config config)
    {
        return File.WriteAllTextAsync(config.CacheFileName, JsonSerializer.Serialize(this, Config.JsonOptions));
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