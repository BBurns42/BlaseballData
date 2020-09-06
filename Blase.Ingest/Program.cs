﻿using System;
using System.Collections.Generic;
using System.Linq;
using System.Net.Http;
using System.Text.Json;
using System.Threading.Tasks;
using Blase.Core;
using Serilog;

namespace Blase.Ingest
{
    class Program
    {
        static async Task Main(string[] args)
        {
            BlaseCore.Init();
            await new Program().Run();
        }
        
        private readonly Datablase _db;
        private readonly HttpClient _client;
        private TeamUpdate[] _lastTeams;
        
        public Program()
        {
            _db = new Datablase();
            _client = new HttpClient();
        }
        
        public async Task Run()
        {
            var streamTask = StreamDataIngestWorker();
            var idolTask = IdolDataIngestWorker();
            var playerTask = PlayerDataIngestWorker();
            await Task.WhenAll(streamTask, idolTask, playerTask);
        }
        

        private async Task StreamDataIngestWorker()
        {
            async Task Callback(string obj)
            {
                var timestamp = DateTimeOffset.UtcNow;

                var doc = JsonDocument.Parse(obj);
                await SaveRawPayload(timestamp, doc);
                await SaveGamesPayload(timestamp, doc);
                await SaveTeamsPayload(timestamp, doc);
            }

            var stream = new EventStream(_client, Log.Logger);
            await stream.Stream("https://www.blaseball.com/events/streamData", async obj =>
            {
                try
                {
                    await Callback(obj);
                }
                catch (Exception e)
                {
                    Log.Error(e, "Error processing stream line");
                }
            });
        }

        private async Task SaveTeamsPayload(DateTimeOffset timestamp, JsonDocument doc)
        {
            var teams = ExtractTeams(doc.RootElement);
            if (teams == null)
                return;

            var updates = teams.Value.EnumerateArray()
                .Select(u => new TeamUpdate(timestamp, u))
                .ToArray();

            await _db.WriteTeamUpdates(updates);
            Log.Information("Saved {TeamCount} teams at {Timestamp}", updates.Length, timestamp);
            _lastTeams = updates;
        }
        private async Task SaveGamesPayload(DateTimeOffset timestamp, JsonDocument doc)
        {
            var scheduleElem = ExtractSchedule(doc.RootElement);
            if (scheduleElem == null)
                return;

            var games = scheduleElem.Value.EnumerateArray()
                .Select(u => ParseUpdate(timestamp, u))
                .Where(u => u != null)
                .ToArray();

            await _db.WriteGameUpdates(games);
            await _db.WriteGameSummaries(games);
            foreach (var gameUpdate in games)
                Log.Information("Saved game update {PayloadHash} (game {GameId})", gameUpdate.Id, gameUpdate.GameId);
        }

        private async Task SaveRawPayload(DateTimeOffset timestamp, JsonDocument doc)
        {
            var update = new RawUpdate(timestamp, doc.RootElement);
            await _db.WriteRaw(update);
            Log.Information("Saved raw event {PayloadHash} at {Timestamp}", update.Id, timestamp);
        }
        
        private async Task PlayerDataIngestWorker()
        {
            while (_lastTeams == null)
                await Task.Delay(1000);
            
            while (true)
            {
                try
                {
                    if (_lastTeams != null)
                    {
                        await FetchAndSavePlayerData(_lastTeams);
                    }
                }
                catch (Exception e)
                {
                    Log.Error(e, "Error processing player data");
                }

                var currentTime = DateTimeOffset.Now;
                var currentMinuteSpan = TimeSpan.FromMinutes(currentTime.Minute % 5)
                    .Add(TimeSpan.FromSeconds(currentTime.Second))
                    .Add(TimeSpan.FromMilliseconds(currentTime.Millisecond));
                
                var delayTime = TimeSpan.FromMinutes(5) - currentMinuteSpan;
                await Task.Delay(delayTime);
            }
        }

        private async Task FetchAndSavePlayerData(TeamUpdate[] teamUpdates)
        {
            var players = teamUpdates.SelectMany(GetTeamPlayers).ToArray();

            var chunks = new List<List<Guid>> {new List<Guid>()};
            foreach (var player in players)
            {
                if (chunks.Last().Count >= 100)
                    chunks.Add(new List<Guid>());
                
                chunks.Last().Add(player);
            }
            
            foreach (var chunk in chunks)
            {
                var ids = string.Join(',', chunk);
                await using var stream = await _client.GetStreamAsync("https://www.blaseball.com/database/players?ids=" + ids);
                
                var timestamp = DateTimeOffset.UtcNow;
                
                var json = await JsonDocument.ParseAsync(stream);
                var updates = json.RootElement.EnumerateArray()
                    .Select(u => new PlayerUpdate(timestamp, u))
                    .ToArray();
                
                await _db.WritePlayerUpdates(updates);
                Log.Information("Saved {PlayerCount} players at {Timestamp}", chunk.Count, timestamp);
            }
        }

        private Guid[] GetTeamPlayers(TeamUpdate teamUpdate)
        {
            var lineup = teamUpdate.Payload["lineup"].AsBsonArray.Select(x => x.AsGuidString());
            var rotation = teamUpdate.Payload["rotation"].AsBsonArray.Select(x => x.AsGuidString());
            return lineup.Concat(rotation).ToArray();
        }

        private async Task IdolDataIngestWorker()
        {
            while (true)
            {
                try
                {
                    await using var resp = await _client.GetStreamAsync("https://www.blaseball.com/api/getIdols");
                    var timestamp = DateTimeOffset.UtcNow;
                    var json = await JsonDocument.ParseAsync(resp);

                    var update = new IdolsUpdate(timestamp, json.RootElement);
                    await _db.WriteIdolsUpdate(update);
                    Log.Information("Saved idols update {PayloadHash} at {Timestamp}", update.Id, timestamp);
                }
                catch (Exception e)
                {
                    Log.Error(e, "Error processing idol data");
                }

                var currentTime = DateTimeOffset.Now;
                var currentMinuteSpan = TimeSpan.FromSeconds(currentTime.Second)
                    .Add(TimeSpan.FromMilliseconds(currentTime.Millisecond));
                var delayTime = TimeSpan.FromMinutes(1) - currentMinuteSpan;
                await Task.Delay(delayTime);
            }
        }
        
        private static GameUpdate ParseUpdate(DateTimeOffset timestamp, JsonElement gameObject)
        {
            var gameUpdate = new GameUpdate(timestamp, gameObject);
            return gameUpdate;
        }
        
        private static JsonElement? ExtractSchedule(JsonElement root)
        {
            if (root.TryGetProperty("value", out var valueProp))
                root = valueProp;
            
            if (root.TryGetProperty("games", out var gamesProp))
                root = gamesProp;
            
            if (!root.TryGetProperty("schedule", out var scheduleProp))
            {
                Log.Warning("Couldn't find schedule property, skipping line");
                return null;
            }
            
            return scheduleProp;
        }

        private static JsonElement? ExtractTeams(JsonElement root)
        {
            if (root.TryGetProperty("value", out var valueProp))
                root = valueProp;

            if (root.TryGetProperty("leagues", out var leaguesProp))
                root = leaguesProp;
            
            if (!root.TryGetProperty("teams", out var teamsProp))
            {
                Log.Warning("Couldn't find teams prop, skipping line");
                return null;
            }
            
            return teamsProp;
        }
    }
}