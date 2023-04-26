// See https://aka.ms/new-console-template for more information


var excludedFolders = new[]
{
    "aptui",
    "aptui_bak",
    "aptui_bak2",
    "aptui原版修改",
};

var logger = LogManager.GetLogger("Main");
var workingDirectory = Environment.CurrentDirectory;
logger.Debug("Working Directory: {workingDirectory}", workingDirectory);

