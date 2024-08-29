using CommonLib;
using CommonLib.Configs;
using CommonLib.Logging;
using CommonLib.Utilities;

using System.Collections.Generic;
using System.Threading.Tasks;
using System.Net.Http;
using System.Drawing;
using System.Linq;
using System.IO;
using System;

using RoyaleAPI;
using RoyaleAPI.Features;
using RoyaleAPI.Objects.Enums;
using RoyaleAPI.Objects.Attacks.Responses;
using RoyaleAPI.Objects.Ip.Responses;

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
        public static GetIPsResponse Ips { get; set; }

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

                Config = new ConfigFile($"{Directory.GetCurrentDirectory()}/royale_config.ini");

                Config.Serializer = value => value.JsonSerialize(true);
                Config.Deserializer = (value, type) => value.JsonDeserialize(type, true);

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

                Ips = await Client.GetIPsAsync();

                Log.Info($"Received {Ips.Total} IPs.");
                Log.Info("Client initialized, starting attack watcher");

                Watcher = new AttackWatcher(Client);

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

        private static void OnAttackEnded(GetAttackResponse obj)
        {
            Task.Run(async () =>
            {
                var message = ToMessage(obj, Content);

                foreach (var webhook in Webhooks)
                    await message.PostToWebhookAsync(webhook.Token, webhook.Id, Http);
            });
        }

        private static void OnAttackStarted(GetAttackResponse obj)
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
                    var attacks = await Client.GetAttacksAsync(0);
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

        private static DiscordMessage ToMessage(GetAttackResponse response, string content = null)
        {
            var message = new DiscordMessage();

            message.WithEmbeds(ToEmbed(response));

            if (!string.IsNullOrWhiteSpace(content))
                message.WithContent(content);

            return message;
        }

        private static DiscordEmbed ToEmbed(GetAttackResponse attackResponse)
        {
            var embed = new DiscordEmbed();

            if (attackResponse.BaseInfo.Status is AttackStatus.Ended)
            {
                embed.WithTitle("✅ | Útok skončil");
                embed.WithColor(Color.Green);

                embed.WithField("🌐 | IP", attackResponse.BaseInfo.Destination, false);
                embed.WithField("🔗 | Typ", attackResponse.BaseInfo.Description, false);

                embed.WithField("🕒 | Začátek", attackResponse.BaseInfo.StartTime, false);
                embed.WithField("🕒 | Konec", attackResponse.BaseInfo.EventTime, false);
                embed.WithField("🕒 | Délka", $"{attackResponse.BaseInfo.Duration.TotalSeconds} sekund", false);

                embed.WithField("📶 | Celková data", $"{attackResponse.BaseInfo.Dropped} Mb", false);
                embed.WithField("📶 | Maximální síla", $"{attackResponse.BaseInfo.MegaBitsPerSecond} Mbps | {attackResponse.BaseInfo.PacketsPerSecond} Pps", false);
            }
            else
            {
                embed.WithTitle("⚠️ | Útok detekován");
                embed.WithColor(Color.Red);

                embed.WithField("🌐 | IP", attackResponse.BaseInfo.Destination, false);
                embed.WithField("🔗 | Typ", attackResponse.BaseInfo.Description, false);
                embed.WithField("🕒 | Začátek", attackResponse.BaseInfo.StartTime, false);
                embed.WithField("📶 | Počáteční síla", $"{attackResponse.BaseInfo.MegaBitsPerSecond} Mbps | {attackResponse.BaseInfo.PacketsPerSecond} Pps", false);
            }

            embed.WithField("📡 | Cílové porty", string.Join("\n", attackResponse.ExtendedInfo.DestinationPorts.Where(p => p.Name != "other").Select(p => p.Name)), false);
            embed.WithField("📡 | Zdrojové porty", string.Join("\n", attackResponse.ExtendedInfo.SourcePorts.Where(p => p.Name != "other").Select(p => p.Name)), false);

            embed.WithField("📡 | Zdrojové IP", string.Join("\n", attackResponse.ExtendedInfo.SourceIPs.Where(p => p.Name != "other").Select(p => p.Name)), false);
            embed.WithField("📡 | Zdrojové ASN", string.Join("\n", attackResponse.ExtendedInfo.SourceASNs.Where(p => p.Name != "other").Select(p => p.Name)), false);

            embed.WithField("🌐 | Státy", string.Join("\n", attackResponse.ExtendedInfo.SourceCountries.Where(p => p.Name != "other").Select(p => p.Name)), false);

            embed.WithFooter($"ID: {attackResponse.BaseInfo.Id}");
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