using Playnite.Common;
using Playnite.SDK;
using Playnite.SDK.Data;
using Playnite.SDK.Models;
using Playnite.SDK.Plugins;
using SteamKit2;
using SteamLibrary.Models;
using SteamLibrary.Services;
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Net;
using System.Text;
using System.Threading.Tasks;
using System.Windows.Controls;
using System.Windows.Media;

namespace SteamLibrary
{
    [Flags]
    public enum AppStateFlags
    {
        Invalid = 0,
        Uninstalled = 1,
        UpdateRequired = 2,
        FullyInstalled = 4,
        Encrypted = 8,
        Locked = 16,
        FilesMissing = 32,
        AppRunning = 64,
        FilesCorrupt = 128,
        UpdateRunning = 256,
        UpdatePaused = 512,
        UpdateStarted = 1024,
        Uninstalling = 2048,
        BackupRunning = 4096,
        Reconfiguring = 65536,
        Validating = 131072,
        AddingFiles = 262144,
        Preallocating = 524288,
        Downloading = 1048576,
        Staging = 2097152,
        Committing = 4194304,
        UpdateStopping = 8388608
    }
    internal class AppInfo
    {
        public static class Constants
        {
            public static readonly uint magic27 = 0x07564427;
            public static readonly uint magic28 = 0x07564428;
            public static readonly uint magic29 = 0x07564429;
            public static readonly HashSet<uint> magics = new HashSet<uint>
            {
                magic27,
                magic28,
                magic29
            };
        }
    }

    [LoadPlugin]
    public class SteamLibrary : LibraryPluginBase<SteamLibrarySettingsViewModel>
    {
        private static readonly ILogger logger = LogManager.GetLogger();
        private readonly Configuration config;
        internal SteamServicesClient ServicesClient;
        internal TopPanelItem TopPanelFriendsButton;

        private static readonly string[] firstPartyModPrefixes = new string[] { "bshift", "cstrike", "czero", "dmc", "dod", "gearbox", "ricochet", "tfc", "valve" };

        public SteamLibrary(IPlayniteAPI api) : base(
            "Steam",
            Guid.Parse("CB91DFC9-B977-43BF-8E70-55F46E410FAB"),
            new LibraryPluginProperties { CanShutdownClient = true, HasSettings = true },
            new SteamClient(),
            Steam.Icon,
            (_) => new SteamLibrarySettingsView(),
            api)
        {
            SettingsViewModel = new SteamLibrarySettingsViewModel(this, PlayniteApi);
            config = GetPluginConfiguration<Configuration>();
            ServicesClient = new SteamServicesClient(config.ServicesEndpoint, api.ApplicationInfo.ApplicationVersion);
            TopPanelFriendsButton = new TopPanelItem()
            {
                Icon = new TextBlock
                {
                    Text = char.ConvertFromUtf32(0xecf9),
                    FontSize = 20,
                    FontFamily = ResourceProvider.GetResource("FontIcoFont") as FontFamily
                },
                Title = ResourceProvider.GetString(LOC.SteamFriendsTooltip),
                Activated = () =>
                {
                    try
                    {
                        _ = Process.Start(@"steam://open/friends");
                    }
                    catch (Exception e)
                    {
                        logger.Error(e, "Failed to open Steam friends.");
                        _ = PlayniteApi.Dialogs.ShowErrorMessage(e.Message, "");
                    }
                },
                Visible = SettingsViewModel.Settings.ShowFriendsButton
            };
        }

        internal static GameAction CreatePlayTask(GameID gameId)
        {
            return new GameAction()
            {
                Name = "Play",
                Type = GameActionType.URL,
                Path = @"steam://rungameid/" + gameId
            };
        }

        internal static GameMetadata GetInstalledGameFromFile(string path)
        {
            KeyValue kv = new KeyValue();
            using (FileStream fs = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite))
            {
                _ = kv.ReadAsText(fs);
            }

            if (!kv["StateFlags"].Value.IsNullOrEmpty() && Enum.TryParse(kv["StateFlags"].Value, out AppStateFlags appState))
            {
                if (!appState.HasFlag(AppStateFlags.FullyInstalled))
                {
                    return null;
                }
            }
            else
            {
                return null;
            }

            string name = string.Empty;
            if (string.IsNullOrEmpty(kv["name"].Value))
            {
                if (kv["UserConfig"]["name"].Value != null)
                {
                    name = StringExtensions.NormalizeGameName(kv["UserConfig"]["name"].Value);
                }
            }
            else
            {
                name = StringExtensions.NormalizeGameName(kv["name"].Value);
            }

            GameID gameId = new GameID(kv["appID"].AsUnsignedInteger());
            string installDir = Path.Combine(new FileInfo(path).Directory.FullName, "common", kv["installDir"].Value);
            if (!Directory.Exists(installDir))
            {
                installDir = Path.Combine(new FileInfo(path).Directory.FullName, "music", kv["installDir"].Value);
                if (!Directory.Exists(installDir))
                {
                    installDir = string.Empty;
                }
            }

            GameMetadata game = new GameMetadata()
            {
                Source = new MetadataNameProperty("Steam"),
                GameId = gameId.ToString(),
                Name = name.RemoveTrademarks().Trim(),
                InstallDirectory = installDir,
                IsInstalled = true,
                Platforms = new HashSet<MetadataProperty> { new MetadataSpecProperty("pc_windows") }
            };

            return game;
        }

        internal static List<GameMetadata> GetInstalledGamesFromFolder(string path)
        {
            List<GameMetadata> games = new List<GameMetadata>();

            foreach (string file in Directory.GetFiles(path, @"appmanifest*"))
            {
                if (file.EndsWith("tmp", StringComparison.OrdinalIgnoreCase))
                {
                    continue;
                }

                try
                {
                    GameMetadata game = GetInstalledGameFromFile(Path.Combine(path, file));
                    if (game == null)
                    {
                        continue;
                    }

                    if (game.InstallDirectory.IsNullOrEmpty() || game.InstallDirectory.Contains(@"steamapps\music"))
                    {
                        logger.Info($"Steam game {game.Name} is not properly installed or it's a soundtrack, skipping.");
                        continue;
                    }

                    games.Add(game);
                }
                catch (Exception exc)
                {
                    // Steam can generate invalid acf file according to issue #37
                    logger.Error(exc, $"Failed to get information about installed game from: {file}");
                }
            }

            return games;
        }

        internal static List<GameMetadata> GetInstalledGoldSrcModsFromFolder(string path)
        {
            List<GameMetadata> games = new List<GameMetadata>();
            DirectoryInfo dirInfo = new DirectoryInfo(path);

            foreach (string folder in dirInfo.GetDirectories().Where(a => !firstPartyModPrefixes.Any(prefix => a.Name.StartsWith(prefix))).Select(a => a.FullName))
            {
                try
                {
                    GameMetadata game = GetInstalledModFromFolder(folder, ModInfo.ModType.HL);
                    if (game != null)
                    {
                        games.Add(game);
                    }
                }
                catch (Exception exc)
                {
                    // GameMetadata.txt may not exist or may be invalid
                    logger.Error(exc, $"Failed to get information about installed GoldSrc mod from: {path}");
                }
            }

            return games;
        }

        internal static List<GameMetadata> GetInstalledSourceModsFromFolder(string path)
        {
            List<GameMetadata> games = new List<GameMetadata>();

            foreach (string folder in Directory.GetDirectories(path))
            {
                try
                {
                    GameMetadata game = GetInstalledModFromFolder(folder, ModInfo.ModType.HL2);
                    if (game != null)
                    {
                        games.Add(game);
                    }
                }
                catch (Exception exc)
                {
                    // GameMetadata.txt may not exist or may be invalid
                    logger.Error(exc, $"Failed to get information about installed Source mod from: {path}");
                }
            }

            return games;
        }

        internal static GameMetadata GetInstalledModFromFolder(string path, ModInfo.ModType modType)
        {
            ModInfo modInfo = ModInfo.GetFromFolder(path, modType);
            if (modInfo == null)
            {
                return null;
            }

            GameMetadata game = new GameMetadata()
            {
                Source = new MetadataNameProperty("Steam"),
                GameId = modInfo.GameId.ToString(),
                Name = modInfo.Name.RemoveTrademarks().Trim(),
                InstallDirectory = path,
                IsInstalled = true,
                Developers = new HashSet<MetadataProperty>() { new MetadataNameProperty(modInfo.Developer) },
                Links = modInfo.Links,
                Tags = modInfo.Categories?.Select(a => new MetadataNameProperty(a)).Cast<MetadataProperty>().ToHashSet(),
                Platforms = new HashSet<MetadataProperty> { new MetadataSpecProperty("pc_windows") }
            };

            if (!modInfo.IconPath.IsNullOrEmpty() && File.Exists(modInfo.IconPath))
            {
                game.Icon = new MetadataFile(modInfo.IconPath);
            }

            return game;
        }

        internal static Dictionary<string, GameMetadata> GetInstalledGames(bool includeMods = true)
        {
            Dictionary<string, GameMetadata> games = new Dictionary<string, GameMetadata>();
            if (!Steam.IsInstalled)
            {
                throw new Exception("Steam installation not found.");
            }

            foreach (string folder in GetLibraryFolders())
            {
                string libFolder = Path.Combine(folder, "steamapps");
                if (Directory.Exists(libFolder))
                {
                    GetInstalledGamesFromFolder(libFolder).ForEach(a =>
                    {
                        // Ignore redist
                        if (a.GameId == "228980")
                        {
                            return;
                        }

                        if (!games.ContainsKey(a.GameId))
                        {
                            games.Add(a.GameId, a);
                        }
                    });
                }
                else
                {
                    logger.Warn($"Steam library {libFolder} not found.");
                }
            }

            if (includeMods)
            {
                try
                {
                    // In most cases, this will be inside the folder where Half-Life is installed.
                    string modInstallPath = Steam.ModInstallPath;
                    if (!string.IsNullOrEmpty(modInstallPath) && Directory.Exists(modInstallPath))
                    {
                        GetInstalledGoldSrcModsFromFolder(Steam.ModInstallPath).ForEach(a =>
                        {
                            if (!games.ContainsKey(a.GameId))
                            {
                                games.Add(a.GameId, a);
                            }
                        });
                    }

                    // In most cases, this will be inside the library folder where Steam is installed.
                    string sourceModInstallPath = Steam.SourceModInstallPath;
                    if (!string.IsNullOrEmpty(sourceModInstallPath) && Directory.Exists(sourceModInstallPath))
                    {
                        GetInstalledSourceModsFromFolder(Steam.SourceModInstallPath).ForEach(a =>
                        {
                            if (!games.ContainsKey(a.GameId))
                            {
                                games.Add(a.GameId, a);
                            }
                        });
                    }
                }
                catch (Exception e) when (!Environment.IsDebugBuild)
                {
                    logger.Error(e, "Failed to import Steam mods.");
                }
            }

            return games;
        }

        internal static List<string> GetLibraryFolders(KeyValue foldersData)
        {
            List<string> dbs = new List<string>();
            foreach (KeyValue child in foldersData.Children)
            {
                if (int.TryParse(child.Name, out int _))
                {
                    if (!child.Value.IsNullOrEmpty())
                    {
                        dbs.Add(child.Value);
                    }
                    else if (child.Children.HasItems())
                    {
                        KeyValue path = child.Children.FirstOrDefault(a => a.Name?.Equals("path", StringComparison.OrdinalIgnoreCase) == true);
                        if (!path.Value.IsNullOrEmpty())
                        {
                            dbs.Add(path.Value);
                        }
                    }
                }
            }

            return dbs;
        }

        internal static HashSet<string> GetLibraryFolders()
        {
            HashSet<string> dbs = new HashSet<string>(StringComparer.OrdinalIgnoreCase) { Steam.InstallationPath };
            string configPath = Path.Combine(Steam.InstallationPath, "steamapps", "libraryfolders.vdf");
            if (!File.Exists(configPath))
            {
                return dbs;
            }

            try
            {
                using (FileStream fs = new FileStream(configPath, FileMode.Open, FileAccess.Read, FileShare.ReadWrite))
                {
                    KeyValue kv = new KeyValue();
                    _ = kv.ReadAsText(fs);
                    foreach (string dir in GetLibraryFolders(kv))
                    {
                        if (Directory.Exists(dir))
                        {
                            _ = dbs.Add(dir);
                        }
                        else
                        {
                            logger.Warn($"Found external Steam directory, but path doesn't exists: {dir}");
                        }
                    }
                }
            }
            catch (Exception e) when (!Debugger.IsAttached)
            {
                logger.Error(e, "Failed to get additional Steam library folders.");
            }

            return dbs;
        }

        internal List<LocalSteamUser> GetSteamUsers()
        {
            List<LocalSteamUser> users = new List<LocalSteamUser>();
            if (File.Exists(Steam.LoginUsersPath))
            {
                KeyValue config = new KeyValue();

                try
                {
                    _ = config.ReadFileAsText(Steam.LoginUsersPath);
                    foreach (KeyValue user in config.Children)
                    {
                        users.Add(new LocalSteamUser()
                        {
                            Id = ulong.Parse(user.Name),
                            AccountName = user["AccountName"].Value,
                            PersonaName = user["PersonaName"].Value,
                            Recent = user["mostrecent"].AsBoolean()
                        });
                    }
                }
                catch (Exception e) when (!Environment.IsDebugBuild)
                {
                    Logger.Error(e, "Failed to get list of local users.");
                }
            }

            return users;
        }

        internal List<GameMetadata> GetLibraryGames(SteamLibrarySettings settings)
        {
            if (settings.UserId.IsNullOrEmpty())
            {
                throw new Exception(PlayniteApi.Resources.GetString(LOC.SteamNotLoggedInError));
            }

            ulong userId = ulong.Parse(settings.UserId);
            if (settings.IsPrivateAccount)
            {
                return GetLibraryGames(userId, GetPrivateOwnedGames(userId, settings.RutnimeApiKey, settings.IncludeFreeSubGames)?.response?.games);
            }
            else
            {
                ProfilePageOwnedGames profile = GetLibraryGamesViaProfilePage(settings);
                if (!profile.rgGames.HasItems())
                {
                    return new List<GameMetadata>();
                }

                List<GameMetadata> games = new List<GameMetadata>();
                HashSet<int> gamesIds = new HashSet<int>(profile.rgGames.Select(a => a.appid));
                foreach (RgGame profGame in profile.rgGames)
                {
                    GameMetadata game = new GameMetadata()
                    {
                        GameId = profGame.appid.ToString(),
                        Name = profGame.name,
                        SortingName = profGame.sort_as,
                        Playtime = (ulong)profGame.playtime_forever * 60,
                        Source = new MetadataNameProperty("Steam")
                    };

                    DateTime lastDate = DateTimeOffset.FromUnixTimeSeconds(profGame.rtime_last_played).LocalDateTime;
                    if (lastDate.Year > 1970)
                    {
                        game.LastActivity = lastDate;
                    }

                    games.Add(game);
                }
                foreach (GameMetadata family in LoadAppInfo())
                {
                    if (family.GameId.IsNullOrEmpty() || gamesIds.Contains(int.Parse(family.GameId)))
                    {
                        continue;
                    }

                    games.Add(family);
                }

                return games;
            }
        }

        internal ProfilePageOwnedGames GetLibraryGamesViaProfilePage(SteamLibrarySettings settings)
        {
            if (settings.UserId.IsNullOrWhiteSpace())
            {
                throw new Exception("Steam user not authenticated.");
            }

            JavaScriptEvaluationResult jsRes = null;
            using (IWebView view = PlayniteApi.WebViews.CreateOffscreenView())
            {
                view.NavigateAndWait($"https://steamcommunity.com/profiles/{settings.UserId}/games");
                jsRes = Task.Run(async () =>
                    await view.EvaluateScriptAsync(@"document.querySelector('#gameslist_config').attributes['data-profile-gameslist'].value")).GetAwaiter().GetResult();
            }

            if (!jsRes.Success || !(jsRes.Result is string))
            {
                logger.Error("Failed to get games list from Steam profile page:");
                logger.Error(jsRes.Message);
                throw new Exception("Failed to fetch Steam games from user profile.");
            }

            if (Serialization.TryFromJson(jsRes.Result as string, out ProfilePageOwnedGames profileData, out Exception error))
            {
                return profileData;
            }

            logger.Error(error, "Failed deserialize Steam profile page data.");
            logger.Debug(jsRes.Result as string);
            return null;
        }

        internal GetOwnedGamesResult GetPrivateOwnedGames(ulong userId, string apiKey, bool freeSub)
        {
            string libraryUrl = @"https://api.steampowered.com/IPlayerService/GetOwnedGames/v0001/?key={0}&include_appinfo=1&include_played_free_games=1&format=json&steamid={1}&skip_unvetted_apps=0";
            if (freeSub)
            {
                libraryUrl += "&include_free_sub=1";
            }

            using (WebClient webClient = new WebClient { Encoding = Encoding.UTF8 })
            {
                string stringLibrary = webClient.DownloadString(string.Format(libraryUrl, apiKey, userId));
                return Serialization.FromJson<GetOwnedGamesResult>(stringLibrary);
            }
        }

        internal List<GameMetadata> GetLibraryGames(ulong userId, List<GetOwnedGamesResult.Game> ownedGames, bool includePlayTime = true)
        {
            if (ownedGames == null)
            {
                throw new Exception("No games found on specified Steam account.");
            }

            IDictionary<string, DateTime> lastActivity = null;
            try
            {
                if (includePlayTime)
                {
                    lastActivity = GetGamesLastActivity(userId);
                }
            }
            catch (Exception exc)
            {
                Logger.Warn(exc, "Failed to import Steam last activity.");
            }

            List<GameMetadata> games = new List<GameMetadata>();
            foreach (GetOwnedGamesResult.Game game in ownedGames)
            {
                // Ignore games without name, like 243870
                if (string.IsNullOrEmpty(game.name))
                {
                    continue;
                }

                GameMetadata newGame = new GameMetadata()
                {
                    Source = new MetadataNameProperty("Steam"),
                    Name = game.name.RemoveTrademarks().Trim(),
                    GameId = game.appid.ToString(),
                    Platforms = new HashSet<MetadataProperty> { new MetadataSpecProperty("pc_windows") }
                };

                if (includePlayTime)
                {
                    newGame.Playtime = (ulong)(game.playtime_forever * 60);
                }

                if (lastActivity != null && lastActivity.TryGetValue(newGame.GameId, out DateTime gameLastActivity) && newGame.Playtime > 0)
                {
                    newGame.LastActivity = gameLastActivity;
                }

                games.Add(newGame);
            }

            return games;

        }
        internal List<GameMetadata> LoadAppInfo()
        {

            string vdf = Path.Combine(Steam.InstallationPath, "appcache", "appinfo.vdf");
            List<GameMetadata> result = new List<GameMetadata>();
            if (!FileSystem.FileExists(vdf))
            {
                return result;
            }

            BinaryReader reader = new BinaryReader(File.Open(vdf, FileMode.Open));
            uint magic = reader.ReadUInt32();
            if (!AppInfo.Constants.magics.Contains(magic))
            {
                logger.Error($"Invalid appinfo.vdf magic number: {magic:X8}");
                return result;
            }

            _ = reader.ReadUInt32();
            List<string> stringPool = new List<string>();
            if (magic == AppInfo.Constants.magic29)
            {
                ulong stringTableOffset = reader.ReadUInt64();
                long oldPos = reader.BaseStream.Position;
                reader.BaseStream.Position = (long)stringTableOffset;
                uint stringCount = reader.ReadUInt32();
                for (uint i = 0; i < stringCount; i++)
                {
                    stringPool.Add(ReadNullTerminatedString(reader));
                }
                reader.BaseStream.Position = oldPos;
            }
            while (reader.BaseStream.Position < reader.BaseStream.Length)
            {
                uint appid = reader.ReadUInt32();
                if (appid == 0)
                {
                    // End of appinfo
                    break;
                }
                uint size = reader.ReadUInt32();
                _ = reader.BaseStream.Position + size;
                Dictionary<string, object> app = new Dictionary<string, object>{
                    {"infoState", reader.ReadUInt32() },
                    {"lastUpdated", reader.ReadUInt32() },
                    {"token", reader.ReadUInt64() },
                    {"hash",reader.ReadBytes(20)},
                    {"changeNumber",reader.ReadUInt32()}
                };
                if (magic == AppInfo.Constants.magic29 || magic == AppInfo.Constants.magic28)
                {
                    app.Add("binaryDataHash", reader.ReadBytes(20));
                };
                Dictionary<string, object> appInfo = AppInfoDescrialize(reader, stringPool, magic)["appinfo"] as Dictionary<string, object>;
                if (!appInfo.ContainsKey("common"))
                {
                    continue;
                }
                Dictionary<string, object> common = appInfo["common"] as Dictionary<string, object>;
                if ((common["type"] as string) != "Game")
                {
                    continue;
                }
                string name = common.ContainsKey("name_localized") && common["name_localized"] is Dictionary<string, object> localizedNames && localizedNames.ContainsKey("schinese")
                    ? (string)localizedNames["schinese"]
                    : common.ContainsKey("name") ? common["name"] as string : string.Empty;
                GameMetadata game = new GameMetadata()
                {
                    Source = new MetadataNameProperty("Steam"),
                    Name = name,
                    GameId = appid.ToString(),
                    Platforms = new HashSet<MetadataProperty> { new MetadataSpecProperty("pc_windows") }
                };

                result.Add(game);
            }
            return result;
        }
        internal Dictionary<string, object> AppInfoDescrialize(BinaryReader reader, List<string> stringPool, uint magic)
        {
            Dictionary<string, object> emtries = new Dictionary<string, object>();
            while (reader.BaseStream.Position < reader.BaseStream.Length)
            {
                byte btype = reader.ReadByte();
                if (btype == 0x08)
                {
                    break;
                }
                Tuple<string, object> entry = ReadEntry(reader, stringPool, btype, magic);
                emtries.Add(entry.Item1, entry.Item2);
            }
            return emtries;
        }
        internal Tuple<string, object> ReadEntry(BinaryReader reader, List<string> stringPool, uint type, uint magic)
        {
            string key;
            if (magic == AppInfo.Constants.magic27 || magic == AppInfo.Constants.magic28)
            {
                key = ReadNullTerminatedString(reader);
            }
            else
            {
                uint index = reader.ReadUInt32();
                key = stringPool[(int)index];
            }
            switch (type)
            {
                case 0x00:
                    return new Tuple<string, object>(key, AppInfoDescrialize(reader, stringPool, magic));
                case 0x01:
                    return new Tuple<string, object>(key, ReadNullTerminatedString(reader));
                case 0x02:
                    return new Tuple<string, object>(key, reader.ReadUInt32());
                default:
                    throw new NotSupportedException($"Unsupported appinfo.vdf entry type: {type:X2} for key: {key}");
            }
        }
        internal string ReadNullTerminatedString(BinaryReader reader)
        {
            List<byte> bufferArray = new List<byte>();
            while (true)
            {
                byte c = reader.ReadByte();
                if (c == 0)
                {
                    break;
                }
                bufferArray.Add(c);
            }
            return Encoding.UTF8.GetString(bufferArray.ToArray());
        }

        public IDictionary<string, DateTime> GetGamesLastActivity(ulong steamId)
        {
            SteamID id = new SteamID(steamId);
            Dictionary<string, DateTime> result = new Dictionary<string, DateTime>();
            string vdf = Path.Combine(Steam.InstallationPath, "userdata", id.AccountID.ToString(), "config", "localconfig.vdf");
            if (!FileSystem.FileExists(vdf))
            {
                return result;
            }

            KeyValue sharedconfig = new KeyValue();
            _ = sharedconfig.ReadFileAsText(vdf);

            KeyValue apps = sharedconfig["Software"]["Valve"]["Steam"]["apps"];
            foreach (KeyValue app in apps.Children)
            {
                if (app.Children.Count == 0)
                {
                    continue;
                }

                string gameId = app.Name;
                if (app.Name.Contains('_'))
                {
                    // Mods are keyed differently, "<appId>_<modId>"
                    // Ex. 215_2287856061
                    string[] parts = app.Name.Split('_');
                    if (uint.TryParse(parts[0], out uint appId) && uint.TryParse(parts[1], out uint modId))
                    {
                        GameID gid = new GameID()
                        {
                            AppID = appId,
                            AppType = GameID.GameType.GameMod,
                            ModID = modId
                        };
                        gameId = gid;
                    }
                    else
                    {
                        // Malformed app id?
                        continue;
                    }
                }

                DateTime dt = DateTimeOffset.FromUnixTimeSeconds(app["LastPlayed"].AsLong()).LocalDateTime;
                if (dt.Year > 1970)
                {
                    result.Add(gameId, dt);
                }
            }

            return result;
        }

        public override ISettings GetSettings(bool firstRunSettings)
        {
            SettingsViewModel.IsFirstRunUse = firstRunSettings;
            return SettingsViewModel;
        }

        public override LibraryMetadataProvider GetMetadataDownloader()
        {
            return new SteamMetadataProvider(this);
        }

        public override IEnumerable<InstallController> GetInstallActions(GetInstallActionsArgs args)
        {
            if (args.Game.PluginId != Id)
            {
                yield break;
            }

            yield return new SteamInstallController(args.Game);
        }

        public override IEnumerable<UninstallController> GetUninstallActions(GetUninstallActionsArgs args)
        {
            if (args.Game.PluginId != Id)
            {
                yield break;
            }

            yield return new SteamUninstallController(args.Game);
        }

        public override IEnumerable<PlayController> GetPlayActions(GetPlayActionsArgs args)
        {
            if (args.Game.PluginId != Id)
            {
                yield break;
            }

            yield return new SteamPlayController(args.Game, SettingsViewModel.Settings, PlayniteApi);
        }

        public override IEnumerable<GameMetadata> GetGames(LibraryGetGamesArgs args)
        {
            List<GameMetadata> allGames = new List<GameMetadata>();
            Dictionary<string, GameMetadata> installedGames = new Dictionary<string, GameMetadata>();
            Exception importError = null;

            if (SettingsViewModel.Settings.ImportInstalledGames)
            {
                try
                {
                    installedGames = GetInstalledGames();
                    Logger.Debug($"Found {installedGames.Count} installed Steam games.");
                    allGames.AddRange(installedGames.Values.ToList());
                }
                catch (Exception e)
                {
                    Logger.Error(e, "Failed to import installed Steam games.");
                    importError = e;
                }
            }

            if (SettingsViewModel.Settings.ConnectAccount)
            {
                try
                {
                    List<GameMetadata> libraryGames = GetLibraryGames(SettingsViewModel.Settings);
                    if (SettingsViewModel.Settings.AdditionalAccounts.HasItems())
                    {
                        foreach (AdditionalSteamAcccount account in SettingsViewModel.Settings.AdditionalAccounts)
                        {
                            if (ulong.TryParse(account.AccountId, out ulong id))
                            {
                                try
                                {
                                    GetOwnedGamesResult accGames = GetPrivateOwnedGames(id, account.RutnimeApiKey, SettingsViewModel.Settings.IncludeFreeSubGames);
                                    List<GameMetadata> parsedGames = GetLibraryGames(id, accGames.response.games, account.ImportPlayTime);
                                    foreach (GameMetadata accGame in parsedGames)
                                    {
                                        if (!libraryGames.Any(a => a.GameId == accGame.GameId))
                                        {
                                            libraryGames.Add(accGame);
                                        }
                                    }
                                }
                                catch (Exception e)
                                {
                                    logger.Error(e, $"Failed to import games from {account.AccountId} Steam account.");
                                    importError = e;
                                }
                            }
                            else
                            {
                                Logger.Error("Steam account ID provided is not valid account ID.");
                            }
                        }
                    }

                    Logger.Debug($"Found {libraryGames.Count} library Steam games.");

                    if (SettingsViewModel.Settings.IgnoreOtherInstalled && SettingsViewModel.Settings.AdditionalAccounts.HasItems())
                    {
                        foreach (string installedGameId in installedGames.Keys.ToList())
                        {
                            if (new GameID(ulong.Parse(installedGameId)).IsMod)
                            {
                                continue;
                            }

                            if (libraryGames.FirstOrDefault(a => a.GameId == installedGameId) == null)
                            {
                                _ = allGames.Remove(installedGames[installedGameId]);
                                _ = installedGames.Remove(installedGameId);
                            }
                        }
                    }

                    if (!SettingsViewModel.Settings.ImportUninstalledGames)
                    {
                        libraryGames = libraryGames.Where(lg => installedGames.ContainsKey(lg.GameId)).ToList();
                    }

                    foreach (GameMetadata game in libraryGames)
                    {
                        if (installedGames.TryGetValue(game.GameId, out GameMetadata installed))
                        {
                            installed.Playtime = game.Playtime;
                            installed.LastActivity = game.LastActivity;
                        }
                        else
                        {
                            allGames.Add(game);
                        }
                    }
                }
                catch (Exception e)
                {
                    Logger.Error(e, "Failed to import linked account Steam games details.");
                    importError = e;
                }
            }

            if (importError != null)
            {
                PlayniteApi.Notifications.Add(new NotificationMessage(
                    ImportErrorMessageId,
                    string.Format(PlayniteApi.Resources.GetString("LOCLibraryImportError"), Name) +
                    System.Environment.NewLine + importError.Message,
                    NotificationType.Error,
                    () => OpenSettingsView()));
            }
            else
            {
                PlayniteApi.Notifications.Remove(ImportErrorMessageId);
            }

            return allGames;
        }

        public override IEnumerable<TopPanelItem> GetTopPanelItems()
        {
            yield return TopPanelFriendsButton;
        }

    }
}
