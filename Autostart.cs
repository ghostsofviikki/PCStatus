using System.Diagnostics;
using System.Security;

namespace PCStatus;

/// <summary>
/// "Start with Windows" via a Task Scheduler logon task with highest privileges. The Run registry key
/// would trigger a UAC prompt on every logon for an elevated app.
/// </summary>
public static class Autostart
{
    private const string TaskName = "PCStatus";

    public static bool IsEnabled() => RunSchtasks($"/Query /TN \"{TaskName}\"") == 0;

    public static bool Enable()
    {
        string exe = Environment.ProcessPath ?? throw new InvalidOperationException("Unknown exe path");
        // Defaults for schtasks-created tasks would stop the app on battery and after 72 h, so use XML.
        // Trigger on any user's logon and run as that user (BUILTIN\Users), elevated where they're an admin.
        // This also works when the installer ran under a different admin account than the one logging in.
        string xml = $"""
            <?xml version="1.0" encoding="UTF-16"?>
            <Task version="1.2" xmlns="http://schemas.microsoft.com/windows/2004/02/mit/task">
              <RegistrationInfo><Description>PCStatus tray monitor</Description></RegistrationInfo>
              <Triggers>
                <LogonTrigger><Enabled>true</Enabled><Delay>PT5S</Delay></LogonTrigger>
              </Triggers>
              <Principals>
                <Principal id="Author">
                  <GroupId>S-1-5-32-545</GroupId>
                  <RunLevel>HighestAvailable</RunLevel>
                </Principal>
              </Principals>
              <Settings>
                <MultipleInstancesPolicy>IgnoreNew</MultipleInstancesPolicy>
                <DisallowStartIfOnBatteries>false</DisallowStartIfOnBatteries>
                <StopIfGoingOnBatteries>false</StopIfGoingOnBatteries>
                <ExecutionTimeLimit>PT0S</ExecutionTimeLimit>
                <AllowHardTerminate>true</AllowHardTerminate>
                <StartWhenAvailable>false</StartWhenAvailable>
                <IdleSettings><StopOnIdleEnd>false</StopOnIdleEnd><RestartOnIdle>false</RestartOnIdle></IdleSettings>
                <Enabled>true</Enabled>
                <Priority>7</Priority>
              </Settings>
              <Actions Context="Author">
                <Exec><Command>{SecurityElement.Escape(exe)}</Command></Exec>
              </Actions>
            </Task>
            """;

        string tmp = Path.Combine(Path.GetTempPath(), $"pcstatus-task-{Guid.NewGuid():N}.xml");
        try
        {
            File.WriteAllText(tmp, xml, System.Text.Encoding.Unicode);
            return RunSchtasks($"/Create /TN \"{TaskName}\" /XML \"{tmp}\" /F") == 0;
        }
        finally
        {
            try { File.Delete(tmp); } catch { }
        }
    }

    public static bool Disable() => RunSchtasks($"/Delete /TN \"{TaskName}\" /F") == 0;

    private static int RunSchtasks(string args)
    {
        var psi = new ProcessStartInfo("schtasks.exe", args)
        {
            CreateNoWindow = true,
            UseShellExecute = false,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
        };
        using var p = Process.Start(psi)!;
        p.StandardOutput.ReadToEnd();
        p.StandardError.ReadToEnd();
        p.WaitForExit();
        return p.ExitCode;
    }
}
