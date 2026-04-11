namespace DwemerDistro.Launcher.Wpf.Models;

public sealed record CommandResult(int ExitCode, string StandardOutput, string StandardError)
{
    public bool Succeeded => ExitCode == 0;
}

