using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Threading;

namespace JenkinsConsoleUtility.Commands
{
    public class KillTaskCommand : ICommand
    {
        private const int TASK_KILL_DELAY_MS = 15000;
        private const int TASK_KILL_SLEEP_MS = 500;

        private static readonly string[] MyCommandKeys = { "kill" };
        public string[] CommandKeys { get { return MyCommandKeys; } }
        private static readonly string[] MyMandatoryArgKeys = { "taskName" };
        public string[] MandatoryArgKeys { get { return MyMandatoryArgKeys; } }

        public int Execute(Dictionary<string, string> argsLc, Dictionary<string, string> argsCased)
        {
            var taskName = JenkinsConsoleUtility.GetArgVar(argsLc, "taskName");

            List<Process> hitList = new List<Process>();
            hitList.AddRange(Process.GetProcessesByName(taskName));
            foreach (var eachProcess in hitList)
            {
                JenkinsConsoleUtility.FancyWriteToConsole("Closing task: " + eachProcess.ProcessName, null, ConsoleColor.Yellow);
                eachProcess.CloseMainWindow(); // Gently close the target so they don't throw error codes 
            }

            // Sleep for a while and wait for the programs to close safely
            var openCount = hitList.Count;
            for (var i = 0; i < TASK_KILL_DELAY_MS; i += TASK_KILL_SLEEP_MS)
            {
                Thread.Sleep(TASK_KILL_SLEEP_MS);
                openCount = 0;
                foreach (var eachProcess in hitList)
                {
                    if (!eachProcess.HasExited)
                        openCount += 1;
                }
                if (openCount == 0)
                    break;
            }

            // Time's up, close everything
            foreach (var eachProcess in hitList)
            {
                if (eachProcess.HasExited)
                    continue; // Finished skip it
                JenkinsConsoleUtility.FancyWriteToConsole("Killing task: " + eachProcess.ProcessName, null, ConsoleColor.Red);
                eachProcess.Kill(); // If it didn't close gently, then close it better.
            }

            if (hitList.Count == 0)
                JenkinsConsoleUtility.FancyWriteToConsole("No tasks to kill: " + taskName, null, ConsoleColor.Red);
            return hitList.Count > 0 ? 0 : 1;
        }
    }
}
