using Common;
using GameInterface;
using GameInterface.Services.Players;
using Serilog;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using TaleWorlds.CampaignSystem;
using TaleWorlds.CampaignSystem.Party;

namespace Coop.Core
{
    /// <summary>
    /// Publishes a bounded public projection of the host's save. Values are recalculated from the
    /// authoritative Hero/Clan/Party objects; there is no second leaderboard database to drift.
    /// </summary>
    internal sealed class CampaignStatsPublisher : IDisposable
    {
        private static readonly ILogger Logger = Log.ForContext<CampaignStatsPublisher>();
        private static readonly TimeSpan PublishInterval = TimeSpan.FromMinutes(1);
        private readonly HttpClient httpClient = new HttpClient { Timeout = TimeSpan.FromSeconds(15) };
        private readonly Uri publishUri;
        private readonly string publishToken;
        private readonly Timer timer;
        private int publishActive;
        private bool disposed;

        internal static CampaignStatsPublisher CreateIfConfigured()
        {
            string portalUrl = Environment.GetEnvironmentVariable("COOP_PORTAL_URL");
            string token = Environment.GetEnvironmentVariable("COOP_PORTAL_PUBLISH_TOKEN");
            if (string.IsNullOrWhiteSpace(portalUrl) && string.IsNullOrWhiteSpace(token)) return null;
            return new CampaignStatsPublisher(portalUrl, token);
        }

        private CampaignStatsPublisher(string portalUrl, string token)
        {
            publishToken = token;
            Uri baseUri;
            if (!string.IsNullOrWhiteSpace(portalUrl) &&
                Uri.TryCreate(portalUrl.TrimEnd('/') + "/", UriKind.Absolute, out baseUri) &&
                baseUri.Scheme == Uri.UriSchemeHttps &&
                !string.IsNullOrWhiteSpace(publishToken))
                publishUri = new Uri(baseUri, "stats/publish");

            Console.WriteLine("[CampaignStats] Publisher initialized (configured={0})", publishUri != null);
            if (publishUri != null)
                timer = new Timer(QueuePublish, null, TimeSpan.Zero, PublishInterval);
        }

        private void QueuePublish(object state)
        {
            if (disposed) return;
            GameThread.EnqueueSafe(PublishOnGameThread, "CampaignStatsPublisher.Publish");
        }

        private void PublishOnGameThread()
        {
            if (disposed || Campaign.Current == null ||
                Interlocked.CompareExchange(ref publishActive, 1, 0) != 0) return;

            StatsSnapshot snapshot;
            try { snapshot = BuildSnapshot(); }
            catch (Exception ex)
            {
                Interlocked.Exchange(ref publishActive, 0);
                Logger.Warning(ex, "Could not build the campaign stats snapshot");
                Console.Error.WriteLine("[CampaignStats] Could not build snapshot: {0}", ex);
                return;
            }

            Task.Run(() => PublishAsync(snapshot));
        }

        private static StatsSnapshot BuildSnapshot()
        {
            IPlayerManager players;
            if (!ContainerProvider.TryResolve(out players))
                throw new InvalidOperationException("Player registry is unavailable");

            var registrations = players.Players.ToList();
            var rows = new List<CampaignLordStats>();
            foreach (Hero hero in Hero.AllAliveHeroes)
            {
                if (hero == null || !hero.IsLord) continue;

                var registration = registrations.FirstOrDefault(candidate =>
                    string.Equals(candidate.HeroId, hero.StringId, StringComparison.OrdinalIgnoreCase));
                Clan clan = hero.Clan;
                MobileParty party = hero.PartyBelongedTo;
                bool playerControlled = registration != null;

                rows.Add(new CampaignLordStats
                {
                    Id = hero.StringId ?? string.Empty,
                    Name = hero.Name == null ? hero.StringId : hero.Name.ToString(),
                    Controller = playerControlled ? "player" : "ai",
                    Clan = clan == null || clan.Name == null ? "Independent" : clan.Name.ToString(),
                    Kingdom = clan == null || clan.Kingdom == null || clan.Kingdom.Name == null
                        ? "Independent"
                        : clan.Kingdom.Name.ToString(),
                    Culture = hero.Culture == null || hero.Culture.Name == null
                        ? "Unknown"
                        : hero.Culture.Name.ToString(),
                    Level = hero.Level,
                    Gold = hero.Gold,
                    Renown = clan == null ? 0 : (int)Math.Round(clan.Renown),
                    Influence = clan == null ? 0 : (int)Math.Round(clan.Influence),
                    ClanTier = clan == null ? 0 : clan.Tier,
                    PartySize = party == null ? 0 : party.MemberRoster.TotalManCount,
                    Fiefs = clan == null ? 0 : clan.Fiefs.Count,
                    Online = playerControlled && players.IsConnected(registration),
                    Status = ResolveStatus(hero),
                    CurrentAction = ResolveAction(hero, party),
                    Location = hero.CurrentSettlement == null || hero.CurrentSettlement.Name == null
                        ? "Unknown"
                        : hero.CurrentSettlement.Name.ToString(),
                });
            }

            rows.Sort((left, right) => string.Compare(left.Name, right.Name, StringComparison.OrdinalIgnoreCase));
            return new StatsSnapshot
            {
                UpdatedAt = DateTimeOffset.UtcNow,
                GameVersion = "1.4.8",
                CampaignDay = Math.Max(0, (int)CampaignTime.Now.ToDays),
                OnlinePlayers = rows.Count(row => row.Online),
                Lords = rows,
            };
        }

        private static string ResolveStatus(Hero hero)
        {
            if (hero.IsPrisoner) return "prisoner";
            if (hero.IsWounded) return "wounded";
            return "active";
        }

        private static string ResolveAction(Hero hero, MobileParty party)
        {
            if (hero.IsPrisoner) return "captured";
            if (party != null) return "traveling";
            if (hero.CurrentSettlement != null) return "in-settlement";
            return "unknown";
        }

        private async Task PublishAsync(StatsSnapshot snapshot)
        {
            try
            {
                string json = JsonSerializer.Serialize(snapshot, new JsonSerializerOptions
                {
                    PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
                });
                using (var request = new HttpRequestMessage(HttpMethod.Post, publishUri))
                {
                    request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", publishToken);
                    request.Content = new StringContent(json, Encoding.UTF8, "application/json");
                    using (HttpResponseMessage response = await httpClient.SendAsync(request).ConfigureAwait(false))
                    {
                        if (!response.IsSuccessStatusCode)
                        {
                            Logger.Warning("Campaign stats portal returned HTTP {StatusCode}", (int)response.StatusCode);
                            Console.Error.WriteLine(
                                "[CampaignStats] Portal returned HTTP {0}",
                                (int)response.StatusCode);
                        }
                    }
                }
            }
            catch (Exception ex)
            {
                Logger.Warning(ex, "Could not publish campaign stats");
                Console.Error.WriteLine("[CampaignStats] Could not publish snapshot: {0}", ex);
            }
            finally { Interlocked.Exchange(ref publishActive, 0); }
        }

        public void Dispose()
        {
            disposed = true;
            timer?.Dispose();
            httpClient.Dispose();
        }

        private sealed class StatsSnapshot
        {
            public int SchemaVersion { get { return 2; } }
            public DateTimeOffset UpdatedAt { get; set; }
            public string GameVersion { get; set; }
            public int CampaignDay { get; set; }
            public int OnlinePlayers { get; set; }
            public List<CampaignLordStats> Lords { get; set; }
        }

        private sealed class CampaignLordStats
        {
            public string Id { get; set; }
            public string Name { get; set; }
            public string Controller { get; set; }
            public string Clan { get; set; }
            public string Kingdom { get; set; }
            public string Culture { get; set; }
            public int Level { get; set; }
            public int Gold { get; set; }
            public int Renown { get; set; }
            public int Influence { get; set; }
            public int ClanTier { get; set; }
            public int PartySize { get; set; }
            public int Fiefs { get; set; }
            public bool Online { get; set; }
            public string Status { get; set; }
            public string CurrentAction { get; set; }
            public string Location { get; set; }
        }
    }
}
