using CommonLib;
using CommonLib.Configs;
using CommonLib.Logging;
using CommonLib.Utilities;

using System.Collections.Generic;
using System.Threading.Tasks;
using System.Net.Sockets;
using System.Net.Http;
using System.Drawing;
using System.Linq;
using System.IO;
using System;

using RoyaleAPI;
using RoyaleAPI.Objects.Ips;
using RoyaleAPI.Objects.Attacks;

using SimpleWebhooks;
using SimpleWebhooks.Embeds;

namespace RoyaleWebhook
{
    public static class Program
    {
        public class WebhookObject
        {
            public string Token { get; set; } = "";
            public ulong Id { get; set; } = 0;
        }

        public static ConfigFile Config { get; set; }
        public static LogOutput Log { get; set; }

        public static RoyaleClient Client { get; set; }
        public static AttackWatcher Watcher { get; set; }

        public static HttpClient Http { get; set; }
        public static IpList Ips { get; set; }

        [Config("Interval", "Attack list refresh interval (in ms).")]
        public static double Interval { get; set; } = 5000;

        [Config("Key", "Key to Royale Hosting's web API.")]
        public static string ApiKey { get; set; } = "";

        [Config("Content", "Content of the message.")]
        public static string Content { get; set; } = "";

        [Config("Webhooks", "A list of webhook URLs.")]
        public static List<WebhookObject> Webhooks { get; set; } = new List<WebhookObject>()
        {
            new WebhookObject(),
            new WebhookObject()
        };

        public static async Task Main(string[] args)
        {
            try
            {
                CommonLibrary.Initialize(args);

                Log = new LogOutput("Royale Webhook").Setup();
                Log.Info("Initialized! Loading config ..");

                Config = new ConfigFile($"{Directory.GetCurrentDirectory()}/config.json");

                Config.Serializer = value => value.JsonSerialize();
                Config.Deserializer = (value, type) => value.JsonDeserialize(type);

                if (!Config.Bind())
                    Log.Warn("Failed to bind config keys.");

                LogConfig(Config.Load());

                Log.Info("Config file loaded, registering commands ..");

                ConsoleCommands.Enable();

                ConsoleCommands.Add("add", AddWebhookCommand);
                ConsoleCommands.Add("remove", RemoveWebhookCommand);
                ConsoleCommands.Add("latest", SendLatestCommand);
                ConsoleCommands.Add("key", SetKeyCommand);

                Log.Info("Commands registered, starting the Royale API client ..");

                Http = new HttpClient();

                Client = new RoyaleClient(ApiKey);
                Client.InitializeClient(Http, msg => Log.Info(msg));

                Ips = await Client.GetIpsAsync();

                Log.Info($"Received {Ips.Count} IPs.");
                Log.Info("Client initialized, starting attack watcher");

                Watcher = new AttackWatcher(Client);

                Watcher.OnAttackListRefreshed += OnAttackListRefreshed;
                Watcher.OnAttackDetected += OnAttackStarted;
                Watcher.OnAttackEnded += OnAttackEnded;
                Watcher.OnError += OnError;

                Watcher.Start(Interval);

                Log.Info("Attack watcher initialized.");
            }
            catch (Exception ex)
            {
                LogOutput.Raw(ex, ConsoleColor.Red);
            }

            await Task.Delay(-1);
        }

        private static void OnError(Exception obj)
        {
            Log.Error(obj);
        }

        private static void OnAttackListRefreshed(AttackList obj)
        {
            Log.Info($"Attack list refreshed ({obj.Attacks.Length} attacks)");
        }

        private static void OnAttackEnded(AttackResponse obj)
        {
            Task.Run(async () =>
            {
                var message = ToMessage(obj, Content);

                foreach (var webhook in Webhooks)
                    await message.PostToWebhookAsync(webhook.Token, webhook.Id, Http);
            });
        }

        private static void OnAttackStarted(AttackResponse obj)
        {
            Task.Run(async () =>
            {
                var message = ToMessage(obj, Content);

                foreach (var webhook in Webhooks)
                    await message.PostToWebhookAsync(webhook.Token, webhook.Id, Http);
            });
        }

        private static string SetKeyCommand(string[] args)
        {
            if (args.Length != 1)
                return "Missing arguments! key <key>";

            ApiKey = args[0];
            Client.Token = ApiKey;

            LogConfig(Config.Save());
            return "Key saved.";
        }

        private static string AddWebhookCommand(string[] args)
        {
            if (args.Length != 2)
                return "Missing arguments! add <token> <id>";

            var token = args[0];
            var id = ulong.Parse(args[1]);

            if (Webhooks.Any(w => w.Token == token || w.Id == id))
                return "This webhook has already been added.";

            Webhooks.Add(new WebhookObject() { Id = id, Token = token });

            LogConfig(Config.Save());
            return "Webhook added.";
        }

        private static string RemoveWebhookCommand(string[] args)
        {
            if (args.Length != 1)
                return "Missing arguments! remove <idOrToken>";

            var str = args[0];

            if (Webhooks.RemoveAll(w => w.Token == str || w.Id.ToString() == str) < 1)
                return "No webhooks were found.";

            LogConfig(Config.Save());
            return "Webhook removed.";
        }

        private static string SendLatestCommand(string[] args)
        {
            Task.Run(async () =>
            {
                try
                {
                    var attacks = await Client.GetAttacksAsync();
                    var latest = attacks.Attacks.First();
                    var info = await Client.GetAttackAsync(latest.Id);
                    var message = ToMessage(info);

                    foreach (var webhook in Webhooks)
                        await message.PostToWebhookAsync(webhook.Token, webhook.Id, Http);
                }
                catch (Exception ex)
                {
                    Log.Error(ex);
                }
            });

            return "Requesting attacks ..";
        }

        private static DiscordMessage ToMessage(AttackResponse response, string content = null)
        {
            var message = new DiscordMessage();

            message.WithEmbeds(ToEmbed(response));

            if (!string.IsNullOrWhiteSpace(content))
                message.WithContent(content);

            return message;
        }

        private static DiscordEmbed ToEmbed(AttackResponse attackResponse)
        {
            var embed = new DiscordEmbed();

            if (attackResponse.Attack.HasEnded)
            {
                embed.WithTitle("✅ | Útok skončil");
                embed.WithColor(Color.Green);
            }
            else
            {
                embed.WithTitle("⚠️ | Útok detekován");
                embed.WithColor(Color.Red);
            }

            embed.WithField("🌐 | IP", attackResponse.Attack.Target, false);
            embed.WithField("🔗 | Typ", attackResponse.Attack.Description, false);
            embed.WithField("📶 | Síla", $"{attackResponse.Attack.Mbps} Mbps | {attackResponse.Attack.Pps} Pps", false);
            embed.WithField("🕒 | Začátek", attackResponse.Attack.StartedAtString, false);

            if (attackResponse.Attack.HasEnded)
            {
                embed.WithField("🕒 | Délka", $"{attackResponse.Attack.TotalDuration} sekund", false);
                embed.WithField("📶 | Celková data", $"{attackResponse.Attack.TotalVolume} Mb", false);
            }

            embed.WithField("📡 | Cílové porty", string.Join("\n", attackResponse.Info.DestinationPorts.Where(p => p.Name != "other").Select(p => p.Name)), false);
            embed.WithField("📡 | Zdrojové porty", string.Join("\n", attackResponse.Info.SourcePorts.Where(p => p.Name != "other").Select(p => p.Name)), false);

            embed.WithField("🔗 | Protokoly", string.Join("\n", attackResponse.Info.Protocols.Where(p => p.Name != "other").Select(p => (ProtocolType)int.Parse(p.Name))), false);
            embed.WithField("🔗 | Data", string.Join("\n", attackResponse.Info.Packets.Where(p => p.Name != "other").Select(p => p.Name)));

            embed.WithField("📡 | Zdrojové IP", string.Join("\n", attackResponse.Info.SourceIps.Where(p => p.Name != "other").Select(p => p.Name)), false);
            embed.WithField("📡 | Zdrojové ASN", string.Join("\n", attackResponse.Info.SourceAsns.Where(p => p.Name != "other").Select(p => p.Name)), false);

            embed.WithField("🌐 | Státy", string.Join("\n", attackResponse.Info.SourceCountries.Where(p => p.Name != "other").Select(p => p.Name)), false);

            embed.WithFooter($"ID: {attackResponse.Attack.Id}");
            return embed;
        }

        private static void LogConfig(Dictionary<string, string> result)
        {
            if (result.Count < 1)
                return;

            Log.Warn($"Config file failed to save/load {result.Count} keys.");

            foreach (var pair in result)
                Log.Warn($"{pair.Key}: {pair.Value}");
        }
    }
}