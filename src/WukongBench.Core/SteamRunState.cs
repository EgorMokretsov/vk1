using System.Text.RegularExpressions;

namespace WukongBench.Core;

public static class SteamRunState
{
    // Use only running-list events for this app. Other apps, SSGL messages,
    // and removals of individual child PIDs do not mean it is ready to relaunch.
    public static bool? IsRunning(string log)
    {
        bool? running = null;
        foreach (string line in log.Split('\n'))
        {
            if (Regex.IsMatch(line, @"^\[[^\]\r\n]+\] AppID 3132990 adding PID \d+ as a tracked process\b")) running = true;
            else if (Regex.IsMatch(line, @"^\[[^\]\r\n]+\] Remove 3132990 from running list\s*$")) running = false;
        }
        return running;
    }
}
