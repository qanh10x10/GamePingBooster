using System.IO;
using GamePingBooster.Core.Ipc;
using GamePingBooster.Service;
using GamePingBooster.Service.Discovery;
using GamePingBooster.Service.Dns;
using GamePingBooster.Service.Native;

namespace GamePingBooster.TunnelCheck;

internal static partial class Program
{
    public static Task RunSideBySideIdentityScenarios()
    {
        Console.WriteLine("Running SideBySideIdentityScenarios (offline regression, zero OS/service mutations)...");

        TestIpcNamedPipeIdentity();
        TestSingleInstanceMutexIdentity();
        TestServiceConfigAndDataDirectoryIdentity();
        TestWintunAdapterAndDriverSetupIdentity();
        TestEtwSessionIdentity();
        TestNrptPrefixIsolationAndVendorPreservation();
        TestScmServiceIdentity();
        TestInstallerScriptIdentityAndSafeKill();

        return Task.CompletedTask;
    }

    private static void TestIpcNamedPipeIdentity()
    {
        Check("Named pipe identity is HollowPingBooster",
            IpcConstants.PipeName == "HollowPingBooster",
            $"Expected 'HollowPingBooster', got '{IpcConstants.PipeName}'");

        Check("Named pipe identity does not match vendor GamePingBooster",
            !string.Equals(IpcConstants.PipeName, "GamePingBooster", StringComparison.OrdinalIgnoreCase),
            $"PipeName must not match vendor: '{IpcConstants.PipeName}'");

        var pipePath = @"\\.\pipe\" + IpcConstants.PipeName;
        Check("Full pipe path is isolated from vendor pipe path",
            !string.Equals(pipePath, @"\\.\pipe\GamePingBooster", StringComparison.OrdinalIgnoreCase),
            $"Pipe path collides with vendor: '{pipePath}'");
    }

    private static void TestSingleInstanceMutexIdentity()
    {
        Check("UI SingleInstance mutex is Local\\HollowPingBooster.SingleInstance",
            IpcConstants.MutexName == @"Local\HollowPingBooster.SingleInstance",
            $"Expected 'Local\\HollowPingBooster.SingleInstance', got '{IpcConstants.MutexName}'");

        Check("UI SingleInstance mutex does not match vendor mutex",
            !string.Equals(IpcConstants.MutexName, @"Local\GamePingBooster.SingleInstance", StringComparison.OrdinalIgnoreCase),
            $"Mutex collides with vendor: '{IpcConstants.MutexName}'");
    }

    private static void TestServiceConfigAndDataDirectoryIdentity()
    {
        var dirName = Path.GetFileName(ServiceConfig.DefaultDirectory);
        Check("ServiceConfig.DefaultDirectory folder name is HollowPingBooster",
            string.Equals(dirName, "HollowPingBooster", StringComparison.OrdinalIgnoreCase),
            $"Expected folder 'HollowPingBooster', got '{dirName}' in '{ServiceConfig.DefaultDirectory}'");

        Check("ServiceConfig.DefaultDirectory does not target vendor GamePingBooster",
            !ServiceConfig.DefaultDirectory.Contains("GamePingBooster", StringComparison.OrdinalIgnoreCase),
            $"DefaultDirectory targets vendor folder: '{ServiceConfig.DefaultDirectory}'");

        var expectedBase = Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData);
        var expectedDir = Path.Combine(expectedBase, "HollowPingBooster");
        Check("ServiceConfig.DefaultDirectory is %ProgramData%\\HollowPingBooster",
            string.Equals(ServiceConfig.DefaultDirectory, expectedDir, StringComparison.OrdinalIgnoreCase),
            $"Expected '{expectedDir}', got '{ServiceConfig.DefaultDirectory}'");
    }

    private static void TestWintunAdapterAndDriverSetupIdentity()
    {
        var config = new ServiceConfig();
        Check("Default adapter name is 'Hollow Ping Booster'",
            config.AdapterName == "Hollow Ping Booster",
            $"Expected 'Hollow Ping Booster', got '{config.AdapterName}'");

        Check("Default adapter name does not match vendor 'Game Ping Booster'",
            !string.Equals(config.AdapterName, "Game Ping Booster", StringComparison.OrdinalIgnoreCase),
            $"Adapter name collides with vendor: '{config.AdapterName}'");

        Check("DriverSetup temp adapter name is 'HollowPingBooster Setup'",
            DriverSetup.TempAdapterName == "HollowPingBooster Setup",
            $"Expected 'HollowPingBooster Setup', got '{DriverSetup.TempAdapterName}'");

        Check("WintunAdapter default tunnel type is 'HollowPingBooster'",
            WintunAdapter.DefaultTunnelType == "HollowPingBooster",
            $"Expected 'HollowPingBooster', got '{WintunAdapter.DefaultTunnelType}'");
    }

    private static void TestEtwSessionIdentity()
    {
        Check("GameDestinationRecorder ETW session is 'HollowPingBooster-Discovery'",
            GameDestinationRecorder.SessionName == "HollowPingBooster-Discovery",
            $"Expected 'HollowPingBooster-Discovery', got '{GameDestinationRecorder.SessionName}'");

        Check("GameDestinationRecorder ETW session does not match vendor",
            !string.Equals(GameDestinationRecorder.SessionName, "GamePingBooster-Discovery", StringComparison.OrdinalIgnoreCase),
            $"ETW session collides with vendor: '{GameDestinationRecorder.SessionName}'");
    }

    private static void TestNrptPrefixIsolationAndVendorPreservation()
    {
        Check("NrptPolicy unblock RulePrefix is 'HollowPingBooster-Unblock-'",
            NrptPolicy.RulePrefix == "HollowPingBooster-Unblock-",
            $"Expected 'HollowPingBooster-Unblock-', got '{NrptPolicy.RulePrefix}'");

        Check("NrptPolicy full-tunnel prefix is 'HollowPingBooster-FullTunnel-'",
            NrptPolicy.FullTunnelPrefix == "HollowPingBooster-FullTunnel-",
            $"Expected 'HollowPingBooster-FullTunnel-', got '{NrptPolicy.FullTunnelPrefix}'");

        Check("NrptPolicy.AllPrefixes does not contain vendor unblock prefix",
            !NrptPolicy.AllPrefixes.Any(p => p.StartsWith("GamePingBooster-Unblock-", StringComparison.OrdinalIgnoreCase)),
            "AllPrefixes contains vendor unblock prefix");

        Check("NrptPolicy.AllPrefixes does not contain vendor steam prefix",
            !NrptPolicy.AllPrefixes.Any(p => p.StartsWith("GamePingBooster-Steam-", StringComparison.OrdinalIgnoreCase)),
            "AllPrefixes contains vendor steam prefix");

        Check("NrptPolicy.AllPrefixes contains only Hollow unblock prefix",
            NrptPolicy.AllPrefixes.Length == 1 && NrptPolicy.AllPrefixes[0] == "HollowPingBooster-Unblock-",
            $"Expected ['HollowPingBooster-Unblock-'], got [{string.Join(", ", NrptPolicy.AllPrefixes)}]");

        // Regression: Prove that cleaning Hollow rules NEVER matches vendor rules
        Check("NrptPolicy rule matcher preserves vendor unblock rules",
            !NrptPolicy.IsOwnedRule("GamePingBooster-Unblock-steam-00"),
            "Matcher incorrectly flagged vendor unblock rule as owned!");

        Check("NrptPolicy rule matcher preserves vendor steam rules",
            !NrptPolicy.IsOwnedRule("GamePingBooster-Steam-00"),
            "Matcher incorrectly flagged vendor steam rule as owned!");

        Check("NrptPolicy rule matcher matches own unblock rule",
            NrptPolicy.IsOwnedRule("HollowPingBooster-Unblock-steam-00"),
            "Matcher failed to flag own unblock rule!");

        Check("NrptPolicy rule matcher does not conflate unblock with full tunnel",
            !NrptPolicy.IsOwnedRule("HollowPingBooster-FullTunnel-Default"),
            "Unblock matcher must not match full-tunnel rule!");
    }

    private static void TestScmServiceIdentity()
    {
        Check("BoosterService SCM ServiceName is 'HollowPingBooster'",
            BoosterService.ExpectedServiceName == "HollowPingBooster",
            $"Expected 'HollowPingBooster', got '{BoosterService.ExpectedServiceName}'");

        Check("BoosterService SCM ServiceName does not match vendor 'GamePingBooster'",
            !string.Equals(BoosterService.ExpectedServiceName, "GamePingBooster", StringComparison.OrdinalIgnoreCase),
            $"SCM ServiceName collides with vendor: '{BoosterService.ExpectedServiceName}'");
    }

    private static void TestInstallerScriptIdentityAndSafeKill()
    {
        var issPath = FindInstallerScript();
        Check("Installer script GamePingBooster.iss exists", File.Exists(issPath), $"Path: {issPath}");
        if (!File.Exists(issPath)) return;

        var text = File.ReadAllText(issPath);

        Check("Installer AppId is distinct from vendor {6E7A4C21-8D3F-4B62-9E15-2C4A7F9D0B83}",
            !text.Contains("{6E7A4C21-8D3F-4B62-9E15-2C4A7F9D0B83}", StringComparison.OrdinalIgnoreCase),
            "Installer still uses vendor AppId {6E7A4C21-8D3F-4B62-9E15-2C4A7F9D0B83}");

        Check("Installer DefaultDirName installs into 'Hollow Ping Booster'",
            text.Contains(@"{autopf}\Hollow Ping Booster", StringComparison.OrdinalIgnoreCase),
            "Installer DefaultDirName does not target {autopf}\\Hollow Ping Booster");

        Check("Installer DefaultDirName does not install into vendor 'Game Ping Booster'",
            !text.Contains(@"{autopf}\Game Ping Booster", StringComparison.OrdinalIgnoreCase),
            "Installer DefaultDirName still targets vendor {autopf}\\Game Ping Booster");

        Check("Installer ServiceName is HollowPingBooster",
            text.Contains("#define ServiceName    \"HollowPingBooster\"") || text.Contains("#define ServiceName \"HollowPingBooster\""),
            "Installer ServiceName is not HollowPingBooster");

        Check("Installer does not perform blind taskkill /IM GamePingBooster.exe or /IM {#UiExe}",
            !text.Contains("taskkill.exe'), '/F /IM {#UiExe}'") && !text.Contains("taskkill.exe'), '/F /IM GamePingBooster.exe'"),
            "Installer still contains blind taskkill by executable image name!");

        Check("Installer scopes UI process termination to its exact executable path",
            text.Contains("$_.Path.Equals($appPath, [System.StringComparison]::OrdinalIgnoreCase)"),
            "Installer must compare the exact executable path, not an unbounded folder prefix.");
        Check("Uninstall leaves the shared Wintun driver installed",
            !text.Contains("Parameters: \"--remove-driver\""),
            "Removing the machine-wide Wintun driver could break the preserved vendor app.");

        var devScript = File.ReadAllText(Path.Combine(Path.GetDirectoryName(Path.GetDirectoryName(issPath)!)!, "gpb.ps1"));
        Check("Dev stop helper skips unknown process paths and only stops owned roots",
            devScript.Contains("if (-not $p.Path) { continue }") && devScript.Contains("$ownedRoots"),
            "A missing process path must never permit killing a vendor service.");
        Check("NRPT removal uses the tested ownership matcher",
            File.ReadAllText(Path.Combine(Path.GetDirectoryName(Path.GetDirectoryName(issPath)!)!,
                "client/src/GamePingBooster.Service/Dns/NrptPolicy.cs")).Contains("RemoveWhere(log, IsOwnedRule)"),
            "The ownership regression must exercise the matcher used by removal.");
    }

    private static string FindInstallerScript()
    {
        // Search upwards from BaseDirectory to find installer/GamePingBooster.iss
        var dir = AppContext.BaseDirectory;
        for (var i = 0; i < 10 && dir is not null; i++)
        {
            var candidate = Path.Combine(dir, "installer", "GamePingBooster.iss");
            if (File.Exists(candidate)) return candidate;
            dir = Path.GetDirectoryName(dir);
        }
        return Path.Combine(AppContext.BaseDirectory, "installer", "GamePingBooster.iss");
    }
}
