﻿using System;
using System.Collections.Generic;
using System.Collections.Specialized;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.RegularExpressions;
using Uhuru.Openshift.Runtime;
using Uhuru.Openshift.Runtime.Config;
using Uhuru.Openshift.Runtime.Utils;
using Uhuru.Openshift.Utilities;


namespace Uhuru.OpenShift.TrapUser
{
    public class UserShellTrap
    {
        private static void LoadEnv(string directory, Dictionary<string, string> targetList)
        {
            if (targetList == null)
            {
                throw new ArgumentNullException("targetList");
            }

            if (!Directory.Exists(directory))
            {
                return;
            }

            string[] envFiles = Directory.GetFiles(directory);

            foreach (string envFile in envFiles)
            {
                string varValue = File.ReadAllText(envFile);
                string varKey = Path.GetFileName(envFile);
                targetList[varKey] = varValue;
            }
        }

        private static void SetupGearEnv(Dictionary<string, string>  targetList)
        {
            if (targetList == null)
            {
                throw new ArgumentNullException("targetList");
            }
  
            string globalEnv = Path.Combine(NodeConfig.ConfigDir, "env");

            string homeDir = Environment.GetEnvironmentVariable("HOME");

            UserShellTrap.LoadEnv(globalEnv, targetList);

            UserShellTrap.LoadEnv(Path.Combine(homeDir, ".env"), targetList);

            foreach (string dir in Directory.GetDirectories(Path.Combine(homeDir, ".env"), "*"))
            {
                LoadEnv(dir, targetList);
            }

            string[] userHomeDirs = Directory.GetDirectories(homeDir, "*", SearchOption.TopDirectoryOnly);

            foreach (string userHomeDir in userHomeDirs)
            {
                LoadEnv(Path.Combine(userHomeDir, "env"), targetList);
            }
        }

        public static int StartShell(string args)
        {
            string assemblyLocation = Path.GetDirectoryName(typeof(UserShellTrap).Assembly.Location);

            Dictionary<string, string> envVars = new Dictionary<string, string>();

            foreach (string key in Environment.GetEnvironmentVariables().Keys)
            {
                envVars[key] = Environment.GetEnvironmentVariable(key);
            }

            envVars["CYGWIN"] = "nodosfilewarning winsymlinks:native";

            SetupGearEnv(envVars);

            envVars["TEMP"] = Path.Combine(envVars["OPENSHIFT_HOMEDIR"], ".tmp");
            envVars["TMP"] = envVars["TEMP"];

            string arguments = string.Empty;
            if (args.StartsWith("\""))
            {
                arguments = Regex.Replace(args, @"\A""[^""]+""\s", "");
            }
            else
            {
                arguments = Regex.Replace(args, @"\A[^\s]+", "");
            }

            string bashBin = Path.Combine(NodeConfig.Values["SSHD_BASE_DIR"], @"bin\bash.exe");
            string gearUuid = envVars.ContainsKey("OPENSHIFT_GEAR_UUID") ? envVars["OPENSHIFT_GEAR_UUID"] : string.Empty;

            int exitCode = 0;

            if (Environment.UserName.StartsWith(Prison.PrisonUser.GlobalPrefix))
            {
                ProcessStartInfo shellStartInfo = new ProcessStartInfo();
                foreach (string key in envVars.Keys)
                {
                    shellStartInfo.EnvironmentVariables[key] = envVars[key];
                }

                shellStartInfo.FileName = bashBin;
                shellStartInfo.Arguments = string.Format(@"--norc --login --noprofile {0}", arguments);
                shellStartInfo.UseShellExecute = false;
                Logger.Debug("Started trapped bash for gear {0}", gearUuid);
                Process shell = Process.Start(shellStartInfo);
                shell.WaitForExit();
                exitCode = shell.ExitCode;
            }
            else
            {
                var prison = Prison.Prison.LoadPrisonAndAttach(Guid.Parse(gearUuid.PadLeft(32, '0')));
                Logger.Debug("Starting trapped bash for gear {0}", gearUuid);
                var process = prison.Execute(bashBin, string.Format("--norc --login --noprofile {0}", arguments), false, envVars);
                process.WaitForExit();
                exitCode = process.ExitCode;

                string userHomeDir = envVars.ContainsKey("OPENSHIFT_HOMEDIR") && Directory.Exists(envVars["OPENSHIFT_HOMEDIR"]) ? envVars["OPENSHIFT_HOMEDIR"] : string.Empty;

                if (!string.IsNullOrEmpty(userHomeDir))
                {
                    LinuxFiles.TakeOwnershipOfGearHome(userHomeDir, prison.User.Username);

                    Logger.Debug("Fixing symlinks for gear {0}", gearUuid);
                    try
                    {
                        LinuxFiles.FixSymlinks(userHomeDir);
                    }
                    catch (Exception ex)
                    {
                        Logger.Error("There was an error while trying to fix symlinks for gear {0}: {1} - {2}", gearUuid, ex.Message, ex.StackTrace);
                    }
                }
                else
                {
                    Logger.Warning("Not taking ownership or fixing symlinks for gear {0}. Could not locate its home directory.", gearUuid);
                }
            }

            return exitCode;
        }
    }
}
