using Grpc.Net.Client;
using Grpc.Core;
using KyberApi;
using KyberCommon;
using KyberInterface;
using System;
using System.Collections.Generic;
using System.Data;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Net.Sockets;
using System.Text.Json;
using System.Text.RegularExpressions;
using System.Threading.Tasks;
using System.Runtime.InteropServices;

class Program
{
    static readonly string[] SupportedModExtensions =
    {
        ".fbmod",
        ".fbcollection"
    };

    private sealed class BattleData
    {
        public string BattleType { get; set; } = "";
        public string Planet { get; set; } = "";
        public string Mod { get; set; } = "";

        public string Team1Faction { get; set; } = "Team1";
        public string Team2Faction { get; set; } = "Team2";

        public List<BattlePlayer> Players { get; set; } = new List<BattlePlayer>();

        public int PlayerWaitSeconds { get; set; } = 90;

    }
    private sealed class BattlePlayer
    {
        public string KyberId { get; set; } = "";
        public string KyberName { get; set; } = "";
        public string Faction { get; set; } = "";

        // Optional override.
        // Use "Team1", "Team2", "1", or "2" if you want direct team control.
        public string Team { get; set; } = "";
    }

    private sealed class BattleResult
    {
        public string Planet { get; set; } = "";
        public string BattleType { get; set; } = "";

        public string WinnerTeam { get; set; } = "";
        public string LoserTeam { get; set; } = "";
        public string WinnerFaction { get; set; } = "";
        public string LoserFaction { get; set; } = "";

        public string OutroDetected { get; set; } = "";
        public bool EorDetected { get; set; }
        public bool GameEndedDetected { get; set; }

        public string KyberLogPath { get; set; } = "";
        public string TimestampUtc { get; set; } = "";
    }

    [DllImport("kernel32.dll")]
    private static extern IntPtr GetConsoleWindow();

    [DllImport("user32.dll")]
    private static extern bool ShowWindow(IntPtr hWnd, int nCmdShow);

    private const int SW_HIDE = 0;
    private const int SW_SHOW = 5;

    static void HideOwnConsoleWindow()
    {
        try
        {
            IntPtr handle = GetConsoleWindow();

            if (handle != IntPtr.Zero)
            {
                ShowWindow(handle, SW_HIDE);
            }
        }
        catch
        {
            // Do not block startup if hiding fails.
        }
    }

    static async Task Main(string[] args)
    {

        bool silentMode = args.Any(a => a.Equals("--silent", StringComparison.OrdinalIgnoreCase));

        if (silentMode && !Debugger.IsAttached)
        {
            HideOwnConsoleWindow();
        }

        Process cliProcess = null;

        try
        {
            Console.WriteLine("Starting KyberBridge...");

            string host = "127.0.0.1";

            string baseDir = AppContext.BaseDirectory;
            Console.WriteLine($"Base Directory: {baseDir}");

            BattleData battleData = LoadBattleData(baseDir);
            DeleteOldBattleResult(baseDir);

            string destinationPlanet = GetArg(args, "--planet", battleData.Planet);
            string battleType = GetArg(args, "--battle", battleData.BattleType);
            string selectedMod = GetArg(args, "--mod", battleData.Mod);

            if (string.IsNullOrWhiteSpace(destinationPlanet))
            {
                throw new Exception("Planet is missing from BattleData.json.");
            }

            if (string.IsNullOrWhiteSpace(battleType))
            {
                throw new Exception("BattleType is missing from BattleData.json.");
            }

            if (string.IsNullOrWhiteSpace(selectedMod))
            {
                selectedMod = "None";
            }

            Console.WriteLine($"Planet: {destinationPlanet}");
            Console.WriteLine($"Battle Type: {battleType}");
            Console.WriteLine($"Mod: {selectedMod}");
            Console.WriteLine($"Team1 Faction: {battleData.Team1Faction}");
            Console.WriteLine($"Team2 Faction: {battleData.Team2Faction}");

            string rawModsPath = PrepareGalacticConquestMods(baseDir, selectedMod);

            string runtimeDir = Path.Combine(baseDir, "Runtime");
            Console.WriteLine($"Runtime Directory: {runtimeDir}");

            if (!Directory.Exists(runtimeDir))
            {
                throw new Exception($"Runtime directory not found: {runtimeDir}");
            }

            string launcherDir = @"C:\Program Files (x86)\KYBER Launcher";
            Console.WriteLine($"Launcher Directory: {launcherDir}");

            if (!Directory.Exists(launcherDir))
            {
                throw new Exception("KYBER Launcher installation not found.");
            }

            string token = GetKyberToken();
            Console.WriteLine("Kyber token loaded.");

            try
            {
                Console.WriteLine("Launching Kyber launcher...");

                string launcherPath = Path.Combine(launcherDir, "kyber_launcher.exe");

                if (File.Exists(launcherPath))
                {
                    Process.Start(new ProcessStartInfo
                    {
                        FileName = launcherPath,
                        WorkingDirectory = launcherDir,
                        UseShellExecute = true
                    });
                }
                else
                {
                    Console.WriteLine("kyber_launcher.exe not found.");
                }
            }
            catch (Exception ex)
            {
                Console.WriteLine($"Failed to launch Kyber launcher: {ex.Message}");
            }

            await Task.Delay(2000);

            Console.WriteLine("Launching Battlefront 2 through Kyber...");

            string cliPath = Path.Combine(runtimeDir, "kyber_cli.exe");
            if (!File.Exists(cliPath))
            {
                throw new Exception($"kyber_cli.exe not found: {cliPath}");
            }

            string rustLibPath = Path.Combine(runtimeDir, "rust_lib.dll");
            if (!File.Exists(rustLibPath))
            {
                throw new Exception($"rust_lib.dll not found: {rustLibPath}");
            }

            int kyberPort = -1;
            bool frontendReady = false;

            var startInfo = new ProcessStartInfo
            {
                FileName = cliPath,
                Arguments = BuildStartGameArguments(token, rawModsPath),
                UseShellExecute = false,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                CreateNoWindow = true,
                WindowStyle = ProcessWindowStyle.Hidden,
                WorkingDirectory = runtimeDir
            };

            cliProcess = new Process();
            cliProcess.StartInfo = startInfo;

            cliProcess.OutputDataReceived += (sender, e) =>
            {
                if (string.IsNullOrWhiteSpace(e.Data))
                {
                    return;
                }

                Console.WriteLine(e.Data);

                if (e.Data.Contains("Kyber will listen on port"))
                {
                    var match = Regex.Match(e.Data, @"port (\d+)");
                    if (match.Success)
                    {
                        kyberPort = int.Parse(match.Groups[1].Value);
                        Console.WriteLine($"Detected Kyber RPC port: {kyberPort}");
                    }
                }

                if (e.Data.Contains("Setting Presence to Ingame: In the menus"))
                {
                    frontendReady = true;
                    Console.WriteLine("Frontend ready detected.");
                }
            };

            cliProcess.ErrorDataReceived += (sender, e) =>
            {
                if (!string.IsNullOrWhiteSpace(e.Data))
                {
                    Console.WriteLine("[ERROR] " + e.Data);
                }
            };

            cliProcess.Start();
            cliProcess.BeginOutputReadLine();
            cliProcess.BeginErrorReadLine();

            Console.WriteLine("Waiting for Kyber RPC port...");

            DateTime portWaitStart = DateTime.Now;

            while (kyberPort == -1)
            {
                await Task.Delay(500);

                if ((DateTime.Now - portWaitStart).TotalSeconds > 60)
                {
                    throw new Exception("Timed out waiting for Kyber port.");
                }
            }

            Console.WriteLine($"Waiting for port {kyberPort} to become available...");

            bool available = await WaitForPort(host, kyberPort);
            if (!available)
            {
                throw new Exception("Kyber RPC server never became available.");
            }

            Console.WriteLine("Kyber gRPC server detected.");

            /*
            Console.WriteLine("Waiting for frontend initialization...");

            DateTime frontendStart = DateTime.Now;

            while (!frontendReady)
            {
                await Task.Delay(500);

                if ((DateTime.Now - frontendStart).TotalSeconds > 120)
                {
                    throw new Exception("Timed out waiting for frontend.");
                }
            }

            Console.WriteLine("Waiting additional time for network initialization...");
            */

            await Task.Delay(5000);

            string address = $"http://{host}:{kyberPort}";
            Console.WriteLine($"Connecting to {address}");

            var channel = GrpcChannel.ForAddress(address);

            var client = new KyberInterface.Server.ServerClient(channel);
            var commonClient = new Common.CommonClient(channel);

            var launchData = ResolveBattleLaunch(destinationPlanet, battleType);

            Console.WriteLine($"Resolved Map: {launchData.Map}");
            Console.WriteLine($"Resolved Mode: {launchData.Mode}");

            Console.WriteLine("Sending StartServer request...");

            var request = new StartServerRequest
            {
                Name = $"Galactic Conquest: {destinationPlanet} - {battleType}",
                Description = $"Battling on {destinationPlanet} for total control of the galaxy",
                MaxPlayers = (uint)ResolveMaxPlayers(battleType),
                Password = "",
                ProximityChat = true,
            };

            request.MapRotation.Add(new LevelSetup
            {
                Map = launchData.Map,
                Mode = launchData.Mode
            });

            var response = await client.StartServerAsync(request);

            string serverId = response.Id;

            Console.WriteLine($"Kyber Server ID: {serverId}");

            var apiChannel = GrpcChannel.ForAddress("https://api.prod.kyber.gg");

            var serverManagementClient =
                new KyberApi.ServerManagement.ServerManagementClient(apiChannel);

            var apiHeaders = new Metadata
{
    { "authorization", token }
};

            Console.WriteLine();
            Console.WriteLine("====================================");
            Console.WriteLine("SERVER STARTED SUCCESSFULLY");
            Console.WriteLine("====================================");
            Console.WriteLine();

            Console.WriteLine(response);

            Console.WriteLine();
            Console.WriteLine("Kyber server is running.");
            Console.WriteLine("Watching Kyber log for battle result...");

            await Task.Delay(3000);

            await AssignConfiguredPlayersToTeamsAsync(
    commonClient,
    battleData
);

            if (IsSpaceBattle(battleType))
            {
                Console.WriteLine("Space battle detected. Skipping ground bot-fill commands.");

                // If space still crashes, comment this line out too.
                await RunServerCommandAsync(
                    commonClient,
                    "Kyber.startgame"
                );
            }
            else
            {
                await RunServerCommandAsync(
                    commonClient,
                    "AutoPlayers.ForceFillGameplayBotsTeam1 32"
                );

                await RunServerCommandAsync(
                    commonClient,
                    "AutoPlayers.ForceFillGameplayBotsTeam2 32"
                );

                await RunServerCommandAsync(
                    commonClient,
                    "Kyber.startgame"
                );
            }

            string kyberLogPath = FindLatestKyberLogFile();
            long kyberLogStartOffset = GetFileLengthSafe(kyberLogPath);

            Console.WriteLine($"Watching log: {kyberLogPath}");
            Console.WriteLine($"Starting from byte offset: {kyberLogStartOffset}");

            BattleResult battleResult = await WaitForBattleResultFromKyberLogAsync(
                kyberLogPath,
                kyberLogStartOffset,
                destinationPlanet,
                battleType,
                battleData.Team1Faction,
                battleData.Team2Faction
            );

            WriteBattleResultJson(baseDir, battleResult);

            Console.WriteLine();
            Console.WriteLine("====================================");
            Console.WriteLine("BATTLE RESULT DETECTED");
            Console.WriteLine("====================================");
            Console.WriteLine($"Winner Team: {battleResult.WinnerTeam}");
            Console.WriteLine($"Winner Faction: {battleResult.WinnerFaction}");
            Console.WriteLine($"Loser Team: {battleResult.LoserTeam}");
            Console.WriteLine($"Loser Faction: {battleResult.LoserFaction}");
            Console.WriteLine();

            Console.WriteLine("Battle complete. Closing Battlefront II and Kyber server...");
            await Task.Delay(3000);
            CloseBattlefrontAndKyber(cliProcess);
        }
        catch (Exception ex)
        {
            Console.WriteLine();
            Console.WriteLine("Failed to start server:");
            Console.WriteLine(ex);

            try
            {
                if (cliProcess != null && !cliProcess.HasExited)
                {
                    Console.WriteLine("Stopping kyber_cli.exe after failure...");
                    cliProcess.Kill(entireProcessTree: true);
                }
            }
            catch
            {
            }

            Console.WriteLine();
            Console.WriteLine("Press ENTER to exit...");
            Console.ReadLine();
        }
    }

    static async Task AssignConfiguredPlayersToTeamsAsync(
    Common.CommonClient commonClient,
    BattleData battleData)
    {
        if (battleData.Players == null || battleData.Players.Count == 0)
        {
            Console.WriteLine("No configured Galactic Conquest players to assign.");
            return;
        }

        int waitSeconds = battleData.PlayerWaitSeconds;

        if (waitSeconds <= 0)
        {
            waitSeconds = 90;
        }

        Console.WriteLine();
        Console.WriteLine("====================================");
        Console.WriteLine("ASSIGNING GALACTIC CONQUEST PLAYERS");
        Console.WriteLine("====================================");
        Console.WriteLine($"Waiting up to {waitSeconds} seconds for configured players to appear...");

        DateTime waitUntil = DateTime.Now.AddSeconds(waitSeconds);

        HashSet<string> assignedPlayers = new HashSet<string>(
            StringComparer.OrdinalIgnoreCase
        );

        while (DateTime.Now < waitUntil)
        {
            try
            {
                var info = await commonClient.GetInfoAsync(new Empty());

                if (info == null || info.Server == null || info.Server.PlayerList == null)
                {
                    await Task.Delay(1000);
                    continue;
                }

                foreach (var serverPlayer in info.Server.PlayerList)
                {
                    string serverPlayerId = TryReadStringProperty(
                        serverPlayer,
                        "Id",
                        "UserId",
                        "PersonaId",
                        "PlayerId"
                    );

                    string serverPlayerName = TryReadStringProperty(
                        serverPlayer,
                        "Name",
                        "Username",
                        "DisplayName",
                        "PlayerName"
                    );

                    if (string.IsNullOrWhiteSpace(serverPlayerId))
                    {
                        continue;
                    }

                    foreach (BattlePlayer configuredPlayer in battleData.Players)
                    {
                        string configuredKey = GetConfiguredPlayerKey(configuredPlayer);

                        if (string.IsNullOrWhiteSpace(configuredKey))
                        {
                            continue;
                        }

                        if (assignedPlayers.Contains(configuredKey))
                        {
                            continue;
                        }

                        bool matched = DoesConfiguredPlayerMatchServerPlayer(
                            configuredPlayer,
                            serverPlayerId,
                            serverPlayerName
                        );

                        if (!matched)
                        {
                            continue;
                        }

                        int targetTeam = ResolveTeamForConfiguredPlayer(
                            battleData,
                            configuredPlayer
                        );

                        Console.WriteLine(
                            $"Matched GC player '{configuredKey}' to Kyber player id '{serverPlayerId}'. Assigning to Team {targetTeam}."
                        );

                        await RunServerCommandAsync(
                            commonClient,
                            $"Kyber.SetTeamById {serverPlayerId} {targetTeam}",
                            500
                        );

                        assignedPlayers.Add(configuredKey);
                    }
                }

                if (assignedPlayers.Count >= battleData.Players.Count)
                {
                    Console.WriteLine("All configured Galactic Conquest players assigned.");
                    return;
                }
            }
            catch (Exception ex)
            {
                Console.WriteLine($"Player assignment check failed, retrying: {ex.Message}");
            }

            await Task.Delay(1000);
        }

        Console.WriteLine();
        Console.WriteLine("Finished waiting for configured players.");
        Console.WriteLine($"Assigned {assignedPlayers.Count} of {battleData.Players.Count} configured players.");
        Console.WriteLine();
    }

    static string GetConfiguredPlayerKey(BattlePlayer player)
    {
        if (player == null)
        {
            return "";
        }

        if (!string.IsNullOrWhiteSpace(player.KyberId))
        {
            return player.KyberId.Trim();
        }

        if (!string.IsNullOrWhiteSpace(player.KyberName))
        {
            return player.KyberName.Trim();
        }

        return "";
    }

    static bool DoesConfiguredPlayerMatchServerPlayer(
        BattlePlayer configuredPlayer,
        string serverPlayerId,
        string serverPlayerName)
    {
        if (configuredPlayer == null)
        {
            return false;
        }

        if (!string.IsNullOrWhiteSpace(configuredPlayer.KyberId) &&
            !string.IsNullOrWhiteSpace(serverPlayerId) &&
            configuredPlayer.KyberId.Equals(serverPlayerId, StringComparison.OrdinalIgnoreCase))
        {
            return true;
        }

        if (!string.IsNullOrWhiteSpace(configuredPlayer.KyberName) &&
            !string.IsNullOrWhiteSpace(serverPlayerName) &&
            configuredPlayer.KyberName.Equals(serverPlayerName, StringComparison.OrdinalIgnoreCase))
        {
            return true;
        }

        return false;
    }

    static int ResolveTeamForConfiguredPlayer(
        BattleData battleData,
        BattlePlayer player)
    {
        if (player == null)
        {
            throw new Exception("Cannot resolve team for empty player.");
        }

        if (!string.IsNullOrWhiteSpace(player.Team))
        {
            string team = player.Team.Trim();

            if (team.Equals("1", StringComparison.OrdinalIgnoreCase) ||
                team.Equals("Team1", StringComparison.OrdinalIgnoreCase))
            {
                return 1;
            }

            if (team.Equals("2", StringComparison.OrdinalIgnoreCase) ||
                team.Equals("Team2", StringComparison.OrdinalIgnoreCase))
            {
                return 2;
            }

            throw new Exception($"Invalid player team value: {player.Team}");
        }

        if (string.IsNullOrWhiteSpace(player.Faction))
        {
            throw new Exception($"Player '{GetConfiguredPlayerKey(player)}' has no faction or team set.");
        }

        if (player.Faction.Equals(battleData.Team1Faction, StringComparison.OrdinalIgnoreCase))
        {
            return 1;
        }

        if (player.Faction.Equals(battleData.Team2Faction, StringComparison.OrdinalIgnoreCase))
        {
            return 2;
        }

        throw new Exception(
            $"Player faction '{player.Faction}' does not match Team1Faction '{battleData.Team1Faction}' or Team2Faction '{battleData.Team2Faction}'."
        );
    }

    static string TryReadStringProperty(object source, params string[] propertyNames)
    {
        if (source == null || propertyNames == null)
        {
            return "";
        }

        Type type = source.GetType();

        foreach (string propertyName in propertyNames)
        {
            var property = type.GetProperty(propertyName);

            if (property == null)
            {
                continue;
            }

            object value = property.GetValue(source, null);

            if (value == null)
            {
                continue;
            }

            string text = value.ToString();

            if (!string.IsNullOrWhiteSpace(text))
            {
                return text;
            }
        }

        return "";
    }

    static async Task RunServerCommandAsync(
    Common.CommonClient commonClient,
    string command,
    int delayAfterMs = 750)
    {
        if (string.IsNullOrWhiteSpace(command))
        {
            return;
        }

        if (command.StartsWith("/"))
        {
            command = command.Substring(1);
        }

        Console.WriteLine($"Running server command: {command}");

        await commonClient.RunCommandAsync(new RunCommandRequest
        {
            Command = command
        });

        await Task.Delay(delayAfterMs);
    }

    static bool IsSpaceBattle(string battleType)
    {
        return !string.IsNullOrWhiteSpace(battleType) &&
               battleType.Trim().Equals("space", StringComparison.OrdinalIgnoreCase);
    }

    static int ResolveMaxPlayers(string battleType)
    {
        if (IsSpaceBattle(battleType))
        {
            // Starfighter Assault is much smaller than ground Supremacy/GA.
            // Keep this conservative for stability.
            return 24;
        }

        return 64;
    }

    static BattleData LoadBattleData(string baseDir)
    {
        string battleDataPath = Path.Combine(baseDir, "BattleData.json");

        Console.WriteLine("Reading BattleData.json...");
        Console.WriteLine($"Expected path: {battleDataPath}");

        if (!File.Exists(battleDataPath))
        {
            throw new Exception($"BattleData.json not found: {battleDataPath}");
        }

        string json = File.ReadAllText(battleDataPath);

        BattleData data = JsonSerializer.Deserialize<BattleData>(
            json,
            new JsonSerializerOptions
            {
                PropertyNameCaseInsensitive = true
            }
        );

        if (data == null)
        {
            throw new Exception("BattleData.json could not be parsed.");
        }

        if (string.IsNullOrWhiteSpace(data.Mod))
        {
            data.Mod = "None";
        }

        if (string.IsNullOrWhiteSpace(data.Team1Faction))
        {
            data.Team1Faction = "Team1";
        }

        if (string.IsNullOrWhiteSpace(data.Team2Faction))
        {
            data.Team2Faction = "Team2";
        }

        Console.WriteLine("BattleData.json loaded successfully.");
        Console.WriteLine($"Loaded Planet: {data.Planet}");
        Console.WriteLine($"Loaded BattleType: {data.BattleType}");
        Console.WriteLine($"Loaded Mod: {data.Mod}");
        Console.WriteLine($"Loaded Team1Faction: {data.Team1Faction}");
        Console.WriteLine($"Loaded Team2Faction: {data.Team2Faction}");

        return data;
    }

    static void WriteBattleResultJson(string baseDir, BattleResult result)
    {
        string resultPath = Path.Combine(baseDir, "BattleResult.json");

        string json = JsonSerializer.Serialize(
            result,
            new JsonSerializerOptions
            {
                WriteIndented = true
            }
        );

        File.WriteAllText(resultPath, json);

        Console.WriteLine($"BattleResult.json written to: {resultPath}");
    }

    static void DeleteOldBattleResult(string baseDir)
    {
        string resultPath = Path.Combine(baseDir, "BattleResult.json");

        if (File.Exists(resultPath))
        {
            File.Delete(resultPath);
            Console.WriteLine("Old BattleResult.json deleted.");
        }
    }

    static string PrepareGalacticConquestMods(string baseDir, string selectedMod)
    {
        Console.WriteLine();
        Console.WriteLine("====================================");
        Console.WriteLine("PREPARING GALACTIC CONQUEST MODS");
        Console.WriteLine("====================================");

        PrepareActiveMods(baseDir, selectedMod);

        string rawModsPath = CreateRawModsJsonForActiveMods(baseDir);

        if (string.IsNullOrWhiteSpace(rawModsPath))
        {
            Console.WriteLine("No active mods. Kyber will launch vanilla.");
        }
        else
        {
            Console.WriteLine($"Raw mods file created: {rawModsPath}");
        }

        Console.WriteLine("Galactic Conquest mod setup complete.");
        Console.WriteLine();

        return rawModsPath;
    }

    static void PrepareActiveMods(string baseDir, string selectedMod)
    {
        string modsRoot = Path.Combine(baseDir, "Mods");
        string activeModsDir = Path.Combine(baseDir, "ActiveMods");

        Directory.CreateDirectory(modsRoot);
        Directory.CreateDirectory(activeModsDir);

        Console.WriteLine($"Mods Root: {modsRoot}");
        Console.WriteLine($"ActiveMods: {activeModsDir}");

        ClearDirectory(activeModsDir);

        if (string.IsNullOrWhiteSpace(selectedMod) ||
            selectedMod.Equals("None", StringComparison.OrdinalIgnoreCase) ||
            selectedMod.Equals("Vanilla", StringComparison.OrdinalIgnoreCase))
        {
            Console.WriteLine("No mod selected. ActiveMods cleared for vanilla launch.");
            return;
        }

        string safeModName = ValidateModSelection(selectedMod);

        string sourceRoot;
        List<string> selectedFiles = ResolveSelectedModFiles(modsRoot, safeModName, out sourceRoot);

        if (selectedFiles.Count == 0)
        {
            throw new Exception($"No supported mod files found for selected mod: {safeModName}");
        }

        foreach (string sourceFile in selectedFiles)
        {
            string relativePath = Path.GetRelativePath(sourceRoot, sourceFile);
            string destinationFile = Path.Combine(activeModsDir, relativePath);

            string destinationDirectory = Path.GetDirectoryName(destinationFile);

            if (!string.IsNullOrWhiteSpace(destinationDirectory))
            {
                Directory.CreateDirectory(destinationDirectory);
            }

            File.Copy(sourceFile, destinationFile, overwrite: true);

            Console.WriteLine($"Added to ActiveMods: {destinationFile}");
        }

        Console.WriteLine($"Prepared {selectedFiles.Count} mod file(s) for: {safeModName}");
    }

    static List<string> ResolveSelectedModFiles(string modsRoot, string selectedMod, out string sourceRoot)
    {
        sourceRoot = "";

        string selectedModFolder = Path.Combine(modsRoot, selectedMod);

        if (Directory.Exists(selectedModFolder))
        {
            sourceRoot = selectedModFolder;

            return Directory
                .GetFiles(selectedModFolder, "*.*", SearchOption.AllDirectories)
                .Where(IsSupportedModFile)
                .OrderBy(path => path)
                .ToList();
        }

        List<string> directMatches = Directory
            .GetFiles(modsRoot, "*.*", SearchOption.TopDirectoryOnly)
            .Where(IsSupportedModFile)
            .Where(path =>
            {
                string fileName = Path.GetFileName(path);
                string fileNameWithoutExtension = Path.GetFileNameWithoutExtension(path);

                return fileName.Equals(selectedMod, StringComparison.OrdinalIgnoreCase) ||
                       fileNameWithoutExtension.Equals(selectedMod, StringComparison.OrdinalIgnoreCase);
            })
            .OrderBy(path => path)
            .ToList();

        if (directMatches.Count > 0)
        {
            sourceRoot = modsRoot;
            return directMatches;
        }

        throw new Exception(
            $"Selected mod '{selectedMod}' was not found. Expected either a folder or file inside: {modsRoot}"
        );
    }

    static string BuildStartGameArguments(string token, string rawModsPath)
    {
        string arguments = $"start_game --token {token}";

        if (!string.IsNullOrWhiteSpace(rawModsPath))
        {
            arguments += $" --raw-mods \"{rawModsPath}\"";
        }

        Console.WriteLine($"Kyber CLI Arguments: {arguments}");

        return arguments;
    }

    static string CreateRawModsJsonForActiveMods(string baseDir)
    {
        string activeModsDir = Path.Combine(baseDir, "ActiveMods");
        string rawModsPath = Path.Combine(baseDir, "KyberRawMods.json");

        Directory.CreateDirectory(activeModsDir);

        List<string> modPaths = Directory
            .GetFiles(activeModsDir, "*.*", SearchOption.AllDirectories)
            .Where(IsSupportedModFile)
            .OrderBy(path => path)
            .Select(path =>
            {
                string relativePath = Path.GetRelativePath(activeModsDir, path);

                // Use forward slashes because this is JSON passed into Dart/Kyber.
                return relativePath.Replace("\\", "/");
            })
            .ToList();

        if (modPaths.Count == 0)
        {
            if (File.Exists(rawModsPath))
            {
                File.Delete(rawModsPath);
                Console.WriteLine("Old KyberRawMods.json deleted because no mods are active.");
            }

            return "";
        }

        var rawMods = new
        {
            basePath = activeModsDir,
            modPaths = modPaths
        };

        string json = JsonSerializer.Serialize(
            rawMods,
            new JsonSerializerOptions
            {
                WriteIndented = true
            }
        );

        File.WriteAllText(rawModsPath, json);

        Console.WriteLine("KyberRawMods.json contents:");
        Console.WriteLine(json);

        return rawModsPath;
    }

    /* UNUSED
    static void SyncActiveModsToKyberModsFolder(string baseDir)
    {
        string activeModsDir = Path.Combine(baseDir, "ActiveMods");
        string kyberModsDir = ResolveKyberModsDirectory();
        string managedKyberModsDir = Path.Combine(kyberModsDir, "GalacticConquestActive");

        Directory.CreateDirectory(activeModsDir);
        Directory.CreateDirectory(kyberModsDir);
        Directory.CreateDirectory(managedKyberModsDir);

        Console.WriteLine($"Kyber Mods Directory: {kyberModsDir}");
        Console.WriteLine($"Managed GC Mods Directory: {managedKyberModsDir}");

        ClearDirectory(managedKyberModsDir);

        List<string> activeFiles = Directory
            .GetFiles(activeModsDir, "*.*", SearchOption.AllDirectories)
            .Where(IsSupportedModFile)
            .OrderBy(path => path)
            .ToList();

        if (activeFiles.Count == 0)
        {
            Console.WriteLine("No active Galactic Conquest mods to sync. Managed Kyber mod folder cleared.");
            return;
        }

        foreach (string sourceFile in activeFiles)
        {
            string relativePath = Path.GetRelativePath(activeModsDir, sourceFile);
            string destinationFile = Path.Combine(managedKyberModsDir, relativePath);

            string destinationDirectory = Path.GetDirectoryName(destinationFile);

            if (!string.IsNullOrWhiteSpace(destinationDirectory))
            {
                Directory.CreateDirectory(destinationDirectory);
            }

            File.Copy(sourceFile, destinationFile, overwrite: true);

            Console.WriteLine($"Synced to Kyber: {destinationFile}");
        }

        Console.WriteLine($"Synced {activeFiles.Count} Galactic Conquest mod file(s) to Kyber.");
    }
    */

    static string ResolveKyberModsDirectory()
    {
        string appData = Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData);

        string appDataKyberRoot = Path.Combine(
            appData,
            "ArmchairDevelopers",
            "Kyber"
        );

        string appDataKyberMods = Path.Combine(
            appDataKyberRoot,
            "Mods"
        );

        if (Directory.Exists(appDataKyberRoot))
        {
            Directory.CreateDirectory(appDataKyberMods);
            return appDataKyberMods;
        }

        string documentsPath = Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments);

        if (string.IsNullOrWhiteSpace(documentsPath))
        {
            string userProfile = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
            documentsPath = Path.Combine(userProfile, "Documents");
        }

        string documentsKyberMods = Path.Combine(documentsPath, "Kyber", "Mods");

        Directory.CreateDirectory(documentsKyberMods);

        return documentsKyberMods;
    }

    static string ValidateModSelection(string selectedMod)
    {
        string value = selectedMod.Trim();

        if (string.IsNullOrWhiteSpace(value))
        {
            throw new Exception("Selected mod name is empty.");
        }

        if (value.Contains("..") ||
            value.Contains("\\") ||
            value.Contains("/"))
        {
            throw new Exception($"Invalid mod name in BattleData.json: {selectedMod}");
        }

        return value;
    }

    static bool IsSupportedModFile(string path)
    {
        string extension = Path.GetExtension(path);

        return SupportedModExtensions.Any(
            supported => supported.Equals(extension, StringComparison.OrdinalIgnoreCase)
        );
    }

    static void ClearDirectory(string directoryPath)
    {
        if (!Directory.Exists(directoryPath))
        {
            return;
        }

        foreach (string file in Directory.GetFiles(directoryPath))
        {
            File.Delete(file);
        }

        foreach (string directory in Directory.GetDirectories(directoryPath))
        {
            Directory.Delete(directory, recursive: true);
        }
    }

    static async Task<BattleResult> WaitForBattleResultFromKyberLogAsync(
        string logPath,
        long startOffset,
        string planet,
        string battleType,
        string team1Faction,
        string team2Faction)
    {
        string winnerTeam = "";
        string loserTeam = "";
        string winnerFaction = "";
        string loserFaction = "";
        string outroDetected = "";

        bool eorDetected = false;
        bool gameEndedDetected = false;

        long currentOffset = startOffset;

        Console.WriteLine("Waiting for Outro_Team1 / Outro_Team2 and game end...");

        DateTime waitStarted = DateTime.Now;

        while (true)
        {
            if ((DateTime.Now - waitStarted).TotalMinutes > 120)
            {
                throw new Exception("Timed out waiting for battle result from Kyber log.");
            }

            List<string> newLines = ReadNewLogLines(logPath, ref currentOffset);

            foreach (string line in newLines)
            {
                string detectedWinnerTeam = DetectWinnerTeamFromOutroLine(line);

                if (detectedWinnerTeam.Equals("Team1", StringComparison.OrdinalIgnoreCase))
                {
                    winnerTeam = "Team1";
                    loserTeam = "Team2";
                    winnerFaction = string.IsNullOrWhiteSpace(team1Faction) ? "Team1" : team1Faction;
                    loserFaction = string.IsNullOrWhiteSpace(team2Faction) ? "Team2" : team2Faction;
                    outroDetected = ExtractOutroLabel(line);

                    Console.WriteLine($"Detected {outroDetected}. Team1 won.");
                }
                else if (detectedWinnerTeam.Equals("Team2", StringComparison.OrdinalIgnoreCase))
                {
                    winnerTeam = "Team2";
                    loserTeam = "Team1";
                    winnerFaction = string.IsNullOrWhiteSpace(team2Faction) ? "Team2" : team2Faction;
                    loserFaction = string.IsNullOrWhiteSpace(team1Faction) ? "Team1" : team1Faction;
                    outroDetected = ExtractOutroLabel(line);

                    Console.WriteLine($"Detected {outroDetected}. Team2 won.");
                }

                if (line.Contains("/EOR", StringComparison.OrdinalIgnoreCase) ||
                    line.Contains("\\EOR", StringComparison.OrdinalIgnoreCase) ||
                    line.Contains(" EOR", StringComparison.OrdinalIgnoreCase))
                {
                    eorDetected = true;
                    Console.WriteLine("Detected EOR bundle.");
                }

                if (line.Contains("Game ended, moving to next level", StringComparison.OrdinalIgnoreCase))
                {
                    gameEndedDetected = true;
                    Console.WriteLine("Detected game end.");
                }

                if (!string.IsNullOrWhiteSpace(winnerTeam) && gameEndedDetected)
                {
                    return new BattleResult
                    {
                        Planet = planet,
                        BattleType = battleType,

                        WinnerTeam = winnerTeam,
                        LoserTeam = loserTeam,
                        WinnerFaction = winnerFaction,
                        LoserFaction = loserFaction,

                        OutroDetected = outroDetected,
                        EorDetected = eorDetected,
                        GameEndedDetected = gameEndedDetected,

                        KyberLogPath = logPath,
                        TimestampUtc = DateTime.UtcNow.ToString("o")
                    };
                }
            }

            await Task.Delay(1000);
        }
    }

    static List<string> ReadNewLogLines(string logPath, ref long offset)
    {
        List<string> lines = new List<string>();

        if (!File.Exists(logPath))
        {
            return lines;
        }

        try
        {
            using FileStream stream = new FileStream(
                logPath,
                FileMode.Open,
                FileAccess.Read,
                FileShare.ReadWrite | FileShare.Delete
            );

            if (offset > stream.Length)
            {
                offset = 0;
            }

            stream.Seek(offset, SeekOrigin.Begin);

            using StreamReader reader = new StreamReader(stream);

            string line;
            while ((line = reader.ReadLine()) != null)
            {
                lines.Add(line);
            }

            offset = stream.Position;
        }
        catch
        {
            // Log file may be temporarily locked or being written.
            // Ignore and retry next loop.
        }

        return lines;
    }

    static string FindLatestKyberLogFile()
    {
        string appData = Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData);

        string kyberRoot = Path.Combine(
            appData,
            "ArmchairDevelopers",
            "Kyber"
        );

        if (!Directory.Exists(kyberRoot))
        {
            throw new Exception($"Kyber AppData folder not found: {kyberRoot}");
        }

        string[] logFiles = Directory
            .GetFiles(kyberRoot, "kyber_*.log", SearchOption.AllDirectories)
            .OrderByDescending(path => File.GetLastWriteTimeUtc(path))
            .ToArray();

        if (logFiles.Length == 0)
        {
            throw new Exception($"No Kyber log files found under: {kyberRoot}");
        }

        return logFiles[0];
    }

    static long GetFileLengthSafe(string path)
    {
        try
        {
            if (!File.Exists(path))
            {
                return 0;
            }

            return new FileInfo(path).Length;
        }
        catch
        {
            return 0;
        }
    }

    static string DetectWinnerTeamFromOutroLine(string line)
    {
        if (string.IsNullOrWhiteSpace(line))
        {
            return "";
        }

        // Ground examples:
        // Outro_Team1
        // Outro_Team2
        //
        // Space examples:
        // SB_DroidBattleShip_01_OutroTeam1_NIS
        // SB_DroidBattleShip_01_OutroTeam2_NIS

        if (line.IndexOf("Outro", StringComparison.OrdinalIgnoreCase) < 0)
        {
            return "";
        }

        if (line.IndexOf("Team1", StringComparison.OrdinalIgnoreCase) >= 0)
        {
            return "Team1";
        }

        if (line.IndexOf("Team2", StringComparison.OrdinalIgnoreCase) >= 0)
        {
            return "Team2";
        }

        return "";
    }

    static string ExtractOutroLabel(string line)
    {
        if (string.IsNullOrWhiteSpace(line))
        {
            return "";
        }

        Match match = Regex.Match(
            line,
            @"[A-Za-z0-9_\/\\\-]*Outro[_]?[A-Za-z0-9_\/\\\-]*Team[12][A-Za-z0-9_\/\\\-]*",
            RegexOptions.IgnoreCase
        );

        if (match.Success)
        {
            return match.Value;
        }

        if (line.IndexOf("Team1", StringComparison.OrdinalIgnoreCase) >= 0)
        {
            return "OutroTeam1";
        }

        if (line.IndexOf("Team2", StringComparison.OrdinalIgnoreCase) >= 0)
        {
            return "OutroTeam2";
        }

        return "Outro";
    }

    static void CloseBattlefrontAndKyber(Process cliProcess)
    {
        CloseBattlefrontProcess();

        try
        {
            if (cliProcess != null && !cliProcess.HasExited)
            {
                Console.WriteLine("Stopping kyber_cli.exe...");
                cliProcess.Kill(entireProcessTree: true);
                cliProcess.WaitForExit(5000);
                Console.WriteLine("kyber_cli.exe stopped.");
            }
        }
        catch (Exception ex)
        {
            Console.WriteLine($"Failed to stop kyber_cli.exe cleanly: {ex.Message}");
        }
    }

    static void CloseBattlefrontProcess()
    {
        try
        {
            Process[] processes = Process.GetProcesses();

            foreach (Process process in processes)
            {
                try
                {
                    string processName = process.ProcessName;

                    if (!processName.Contains("starwarsbattlefront", StringComparison.OrdinalIgnoreCase))
                    {
                        continue;
                    }

                    Console.WriteLine($"Closing Battlefront II process: {processName} / PID {process.Id}");

                    bool closeRequested = false;

                    try
                    {
                        closeRequested = process.CloseMainWindow();
                    }
                    catch
                    {
                        closeRequested = false;
                    }

                    if (closeRequested)
                    {
                        if (!process.WaitForExit(8000))
                        {
                            Console.WriteLine("Battlefront II did not close in time. Killing process...");
                            process.Kill(entireProcessTree: true);
                        }
                    }
                    else
                    {
                        process.Kill(entireProcessTree: true);
                    }

                    Console.WriteLine("Battlefront II closed.");
                }
                catch
                {
                    // Ignore individual process access errors.
                }
            }
        }
        catch (Exception ex)
        {
            Console.WriteLine($"Failed while searching for Battlefront II process: {ex.Message}");
        }
    }

    static string GetArg(string[] args, string name, string fallback)
    {
        for (int i = 0; i < args.Length; i++)
        {
            if (args[i].Equals(name, StringComparison.OrdinalIgnoreCase) && i + 1 < args.Length)
            {
                return args[i + 1].Trim('"');
            }

            string prefix = name + "=";

            if (args[i].StartsWith(prefix, StringComparison.OrdinalIgnoreCase))
            {
                return args[i].Substring(prefix.Length).Trim('"');
            }
        }

        return fallback;
    }

    static (string Map, string Mode) ResolveBattleLaunch(string planet, string battleType)
    {
        string normalizedBattleType = battleType.Trim().ToLowerInvariant();

        string mode = normalizedBattleType switch
        {
            "ground" => "Mode1",
            "space" => "SpaceBattle",
            _ => throw new Exception($"Unknown battle type '{battleType}'.")
        };

        string map;

        if (normalizedBattleType == "space")
        {
            map = ResolveSpaceMap(planet);
        }
        else
        {
            map = ResolveGroundMap(planet);
        }

        return (map, mode);
    }

    static string ResolveGroundMap(string planet)
    {
        return planet.Trim().ToLowerInvariant() switch
        {
            "kamino" => "S7_1/Levels/Kamino_03/Kamino_03",
            "geonosis" => "S6_2/Levels/Geonosis_02/Geonosis_02",
            "kashyyyk" => "S7/Levels/Kashyyyk_02/Kashyyyk_02",
            "naboo" => "S7_2/Levels/Naboo_03/Naboo_03",
            "felucia" => "S8/Felucia/Levels/MP-Felucia_01/Felucia_01",
            "tatooine" => "S9_3/Tatooine_02/Tatooine_02",

            // Mygeeto is currently loaded as a mod over Kashyyyk.
            "mygeeto" => "S7/Levels/Kashyyyk_02/Kashyyyk_02",

            _ => throw new Exception($"Unknown ground planet '{planet}'.")
        };
    }

    static string ResolveSpaceMap(string planet)
    {
        return planet.Trim().ToLowerInvariant() switch
        {
            "kamino" => "Levels/Space/SB_Kamino_01/SB_Kamino_01",
            "geonosis" => "Levels/Space/SB_DroidBattleShip_01/SB_DroidBattleShip_01",
            "kashyyyk" => "Levels/Space/SB_DroidBattleShip_01/SB_DroidBattleShip_01",
            "naboo" => "Levels/Space/SB_DroidBattleShip_01/SB_DroidBattleShip_01",
            "felucia" => "Levels/Space/SB_DroidBattleShip_01/SB_DroidBattleShip_01",
            "tatooine" => "Levels/Space/SB_DroidBattleShip_01/SB_DroidBattleShip_01",
            "mygeeto" => "Levels/Space/SB_DroidBattleShip_01/SB_DroidBattleShip_01",

            _ => throw new Exception($"Unknown or unsupported space planet '{planet}'.")
        };
    }

    static async Task<bool> WaitForPort(string host, int port)
    {
        for (int i = 0; i < 120; i++)
        {
            try
            {
                using var client = new TcpClient();

                var connectTask = client.ConnectAsync(host, port);
                var timeoutTask = Task.Delay(1000);

                var completed = await Task.WhenAny(connectTask, timeoutTask);

                if (completed == connectTask && client.Connected)
                {
                    return true;
                }
            }
            catch
            {
            }

            await Task.Delay(1000);
        }

        return false;
    }

    static string GetKyberToken()
    {
        string maximaPath = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
            "ArmchairDevelopers",
            "Maxima",
            "data"
        );

        Console.WriteLine($"Searching for auth file in: {maximaPath}");

        if (!Directory.Exists(maximaPath))
        {
            throw new Exception("Maxima data directory not found.");
        }

        string authFile = Directory
            .GetFiles(maximaPath, "auth.toml", SearchOption.AllDirectories)
            .FirstOrDefault();

        if (authFile == null)
        {
            throw new Exception("Auth file not found.");
        }

        string content = File.ReadAllText(authFile);

        var accountMatch = Regex.Match(content, "\"([0-9]+)\"");

        if (!accountMatch.Success)
        {
            throw new Exception("Could not detect account ID.");
        }

        string accountId = accountMatch.Groups[1].Value;
        Console.WriteLine($"Selected account: {accountId}");

        var tokenMatch = Regex.Match(content, @"access_token\s*=\s*""([^""]+)""");

        if (!tokenMatch.Success)
        {
            throw new Exception("Access token not found in auth file.");
        }

        string token = tokenMatch.Groups[1].Value;
        Console.WriteLine("Access token found.");

        return token;
    }
}
