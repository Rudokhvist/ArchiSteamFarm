﻿/*
    _                _      _  ____   _                           _____
   / \    _ __  ___ | |__  (_)/ ___| | |_  ___   __ _  _ __ ___  |  ___|__ _  _ __  _ __ ___
  / _ \  | '__|/ __|| '_ \ | |\___ \ | __|/ _ \ / _` || '_ ` _ \ | |_  / _` || '__|| '_ ` _ \
 / ___ \ | |  | (__ | | | || | ___) || |_|  __/| (_| || | | | | ||  _|| (_| || |   | | | | | |
/_/   \_\|_|   \___||_| |_||_||____/  \__|\___| \__,_||_| |_| |_||_|   \__,_||_|   |_| |_| |_|

 Copyright 2015-2017 Łukasz "JustArchi" Domeradzki
 Contact: JustArchi@JustArchi.net

 Licensed under the Apache License, Version 2.0 (the "License");
 you may not use this file except in compliance with the License.
 You may obtain a copy of the License at

 http://www.apache.org/licenses/LICENSE-2.0
					
 Unless required by applicable law or agreed to in writing, software
 distributed under the License is distributed on an "AS IS" BASIS,
 WITHOUT WARRANTIES OR CONDITIONS OF ANY KIND, either express or implied.
 See the License for the specific language governing permissions and
 limitations under the License.

*/

using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;
using HtmlAgilityPack;
using Newtonsoft.Json;

namespace ArchiSteamFarm {
	internal sealed class CardsFarmer : IDisposable {
		private const byte HoursToBump = 2; // How many hours are required for restricted accounts

		private static readonly HashSet<uint> UntrustedAppIDs = new HashSet<uint> { 440, 570, 730 };

		[JsonProperty]
		internal readonly ConcurrentHashSet<Game> CurrentGamesFarming = new ConcurrentHashSet<Game>();

		[JsonProperty]
		internal readonly ConcurrentHashSet<Game> GamesToFarm = new ConcurrentHashSet<Game>();

		[JsonProperty]
		internal TimeSpan TimeRemaining => new TimeSpan(
			Bot.BotConfig.CardDropsRestricted ? (int) Math.Ceiling(GamesToFarm.Count / (float) ArchiHandler.MaxGamesPlayedConcurrently) * HoursToBump : 0,
			30 * GamesToFarm.Sum(game => game.CardsRemaining),
			0
		);

		private readonly Bot Bot;
		private readonly SemaphoreSlim FarmingSemaphore = new SemaphoreSlim(1);
		private readonly ManualResetEventSlim FarmResetEvent = new ManualResetEventSlim(false);
		private readonly Timer IdleFarmingTimer;

		[JsonProperty]
		internal bool Paused { get; private set; }

		private bool KeepFarming;
		private bool NowFarming;
		private bool StickyPause;

		internal CardsFarmer(Bot bot) {
			if (bot == null) {
				throw new ArgumentNullException(nameof(bot));
			}

			Bot = bot;

			if (Program.GlobalConfig.IdleFarmingPeriod > 0) {
				IdleFarmingTimer = new Timer(
					e => CheckGamesForFarming(),
					null,
					TimeSpan.FromHours(Program.GlobalConfig.IdleFarmingPeriod) + TimeSpan.FromMinutes(0.5 * Bot.Bots.Count), // Delay
					TimeSpan.FromHours(Program.GlobalConfig.IdleFarmingPeriod) // Period
				);
			}
		}

		public void Dispose() {
			// Those are objects that are always being created if constructor doesn't throw exception
			CurrentGamesFarming.Dispose();
			GamesToFarm.Dispose();
			FarmingSemaphore.Dispose();
			FarmResetEvent.Dispose();

			// Those are objects that might be null and the check should be in-place
			IdleFarmingTimer?.Dispose();
		}

		internal void OnDisconnected() => StopFarming().Forget();

		internal async Task OnNewGameAdded() {
			// If we're not farming yet, obviously it's worth it to make a check
			if (!NowFarming) {
				StartFarming().Forget();
				return;
			}

			// If we have Complex algorithm and some games to boost, it's also worth to make a re-check, but only in this case
			// That's because we would check for new games after our current round anyway, and having extra games in the queue right away doesn't change anything
			// Therefore, there is no need for extra restart of CardsFarmer if we have no games under HoursToBump hours in current round
			if (Bot.BotConfig.CardDropsRestricted && (GamesToFarm.Count > 0) && (GamesToFarm.Min(game => game.HoursPlayed) < HoursToBump)) {
				await StopFarming().ConfigureAwait(false);
				StartFarming().Forget();
			}
		}

		internal async Task OnNewItemsNotification() {
			if (NowFarming) {
				FarmResetEvent.Set();
				return;
			}

			// If we're not farming, and we got new items, it's likely to be a booster pack or likewise
			// In this case, perform a loot if user wants to do so
			await Bot.LootIfNeeded().ConfigureAwait(false);
		}

		internal async Task Pause(bool sticky) {
			if (sticky) {
				StickyPause = true;
			}

			Paused = true;
			if (NowFarming) {
				await StopFarming().ConfigureAwait(false);
			}
		}

		internal void Resume(bool userAction) {
			if (StickyPause) {
				if (!userAction) {
					Bot.ArchiLogger.LogGenericInfo("Not honoring this request, as sticky pause is enabled!");
					return;
				}

				StickyPause = false;
			}

			Paused = false;
			if (!NowFarming) {
				StartFarming().Forget();
			}
		}

		internal void SetInitialState(bool paused) => StickyPause = Paused = paused;

		internal async Task StartFarming() {
			if (NowFarming || Paused || !Bot.IsPlayingPossible) {
				return;
			}

			if (Bot.IsLimitedUser) {
				await Bot.OnFarmingFinished(false).ConfigureAwait(false);
				return;
			}

			await FarmingSemaphore.WaitAsync().ConfigureAwait(false);

			try {
				if (NowFarming || Paused || !Bot.IsPlayingPossible) {
					return;
				}

				if (!await IsAnythingToFarm().ConfigureAwait(false)) {
					Bot.ArchiLogger.LogGenericInfo("We don't have anything to farm on this account!");
					await Bot.OnFarmingFinished(false).ConfigureAwait(false);
					return;
				}

				if (GamesToFarm.Count == 0) {
					Bot.ArchiLogger.LogNullError(nameof(GamesToFarm));
					return;
				}

				Bot.ArchiLogger.LogGenericInfo("We have a total of " + GamesToFarm.Count + " games (" + GamesToFarm.Sum(game => game.CardsRemaining) + " cards) left to farm (~" + TimeRemaining.ToHumanReadable() + " remaining)...");

				// This is the last moment for final check if we can farm
				if (!Bot.IsPlayingPossible) {
					Bot.ArchiLogger.LogGenericInfo("But farming is currently unavailable, we'll try later!");
					return;
				}

				KeepFarming = NowFarming = true;
			} finally {
				FarmingSemaphore.Release();
			}

			do {
				// Now the algorithm used for farming depends on whether account is restricted or not
				if (Bot.BotConfig.CardDropsRestricted) { // If we have restricted card drops, we use complex algorithm
					Bot.ArchiLogger.LogGenericInfo("Chosen farming algorithm: Complex");
					while (GamesToFarm.Count > 0) {
						HashSet<Game> gamesToFarmSolo = GamesToFarm.Count > 1 ? new HashSet<Game>(GamesToFarm.Where(game => game.HoursPlayed >= HoursToBump)) : new HashSet<Game>(GamesToFarm);
						if (gamesToFarmSolo.Count > 0) {
							while (gamesToFarmSolo.Count > 0) {
								Game game = gamesToFarmSolo.First();
								if (await FarmSolo(game).ConfigureAwait(false)) {
									gamesToFarmSolo.Remove(game);
								} else {
									NowFarming = false;
									return;
								}
							}
						} else {
							if (FarmMultiple(GamesToFarm.OrderByDescending(game => game.HoursPlayed).Take(ArchiHandler.MaxGamesPlayedConcurrently))) {
								Bot.ArchiLogger.LogGenericInfo("Done farming: " + string.Join(", ", GamesToFarm.Select(game => game.AppID)));
							} else {
								NowFarming = false;
								return;
							}
						}
					}
				} else { // If we have unrestricted card drops, we use simple algorithm
					Bot.ArchiLogger.LogGenericInfo("Chosen farming algorithm: Simple");
					while (GamesToFarm.Count > 0) {
						Game game = GamesToFarm.First();
						if (await FarmSolo(game).ConfigureAwait(false)) {
							continue;
						}

						NowFarming = false;
						return;
					}
				}
			} while (await IsAnythingToFarm().ConfigureAwait(false));

			CurrentGamesFarming.ClearAndTrim();
			NowFarming = false;

			Bot.ArchiLogger.LogGenericInfo("Farming finished!");
			await Bot.OnFarmingFinished(true).ConfigureAwait(false);
		}

		internal async Task StopFarming() {
			if (!NowFarming) {
				return;
			}

			await FarmingSemaphore.WaitAsync().ConfigureAwait(false);

			try {
				if (!NowFarming) {
					return;
				}

				Bot.ArchiLogger.LogGenericInfo("Sending signal to stop farming");
				KeepFarming = false;
				FarmResetEvent.Set();

				Bot.ArchiLogger.LogGenericInfo("Waiting for reaction...");
				for (byte i = 0; (i < 5) && NowFarming; i++) {
					await Task.Delay(1000).ConfigureAwait(false);
				}

				if (NowFarming) {
					Bot.ArchiLogger.LogGenericWarning("Timed out!");
					NowFarming = false;
				}

				Bot.ArchiLogger.LogGenericInfo("Farming stopped!");
				Bot.OnFarmingStopped();
			} finally {
				FarmingSemaphore.Release();
			}
		}

		private async Task CheckGame(uint appID, string name, float hours) {
			if ((appID == 0) || string.IsNullOrEmpty(name) || (hours < 0)) {
				Bot.ArchiLogger.LogNullError(nameof(appID) + " || " + nameof(name) + " || " + nameof(hours));
				return;
			}

			ushort? cardsRemaining = await GetCardsRemaining(appID).ConfigureAwait(false);
			if (!cardsRemaining.HasValue) {
				Bot.ArchiLogger.LogGenericWarning("Could not check cards status for " + appID + " (" + name + "), will try again later!");
				return;
			}

			if (cardsRemaining.Value == 0) {
				return;
			}

			GamesToFarm.Add(new Game(appID, name, hours, cardsRemaining.Value));
		}

		private void CheckGamesForFarming() {
			if (NowFarming || Paused || !Bot.IsConnectedAndLoggedOn) {
				return;
			}

			StartFarming().Forget();
		}

		private async Task CheckPage(HtmlDocument htmlDocument) {
			if (htmlDocument == null) {
				Bot.ArchiLogger.LogNullError(nameof(htmlDocument));
				return;
			}

			HtmlNodeCollection htmlNodes = htmlDocument.DocumentNode.SelectNodes("//div[@class='badge_title_stats_content']");
			if (htmlNodes == null) {
				// No eligible badges whatsoever
				return;
			}

			HashSet<Task> extraTasks = new HashSet<Task>();

			foreach (HtmlNode htmlNode in htmlNodes) {
				HtmlNode appIDNode = htmlNode.SelectSingleNode(".//div[@class='card_drop_info_dialog']");
				if (appIDNode == null) {
					// It's just a badge, nothing more
					continue;
				}

				string appIDString = appIDNode.GetAttributeValue("id", null);
				if (string.IsNullOrEmpty(appIDString)) {
					Bot.ArchiLogger.LogNullError(nameof(appIDString));
					continue;
				}

				string[] appIDSplitted = appIDString.Split('_');
				if (appIDSplitted.Length < 5) {
					Bot.ArchiLogger.LogNullError(nameof(appIDSplitted));
					continue;
				}

				appIDString = appIDSplitted[4];

				uint appID;
				if (!uint.TryParse(appIDString, out appID) || (appID == 0)) {
					Bot.ArchiLogger.LogNullError(nameof(appID));
					continue;
				}

				if (GlobalConfig.GlobalBlacklist.Contains(appID) || Program.GlobalConfig.Blacklist.Contains(appID)) {
					// We have this appID blacklisted, so skip it
					continue;
				}

				// Cards
				HtmlNode progressNode = htmlNode.SelectSingleNode(".//span[@class='progress_info_bold']");
				if (progressNode == null) {
					Bot.ArchiLogger.LogNullError(nameof(progressNode));
					continue;
				}

				string progressText = progressNode.InnerText;
				if (string.IsNullOrEmpty(progressText)) {
					Bot.ArchiLogger.LogNullError(nameof(progressText));
					continue;
				}

				ushort cardsRemaining = 0;
				Match progressMatch = Regex.Match(progressText, @"\d+");

				// This might fail if we have no card drops remaining, that's fine
				if (progressMatch.Success) {
					if (!ushort.TryParse(progressMatch.Value, out cardsRemaining)) {
						Bot.ArchiLogger.LogNullError(nameof(cardsRemaining));
						continue;
					}
				}

				if (cardsRemaining == 0) {
					// Normally we'd trust this information and simply skip the rest
					// However, Steam is so fucked up that we can't simply assume that it's correct
					// It's entirely possible that actual game page has different info, and badge page lied to us
					// We can't check every single game though, as this will literally kill people with cards from games they don't own
					if (!UntrustedAppIDs.Contains(appID)) {
						continue;
					}

					// To save us on extra work, check cards earned so far first
					HtmlNode cardsEarnedNode = htmlNode.SelectSingleNode(".//div[@class='card_drop_info_header']");
					string cardsEarnedText = cardsEarnedNode.InnerText;
					if (string.IsNullOrEmpty(cardsEarnedText)) {
						Bot.ArchiLogger.LogNullError(nameof(cardsEarnedText));
						continue;
					}

					Match cardsEarnedMatch = Regex.Match(cardsEarnedText, @"\d+");
					if (!cardsEarnedMatch.Success) {
						Bot.ArchiLogger.LogNullError(nameof(cardsEarnedMatch));
						continue;
					}

					ushort cardsEarned;
					if (!ushort.TryParse(cardsEarnedMatch.Value, out cardsEarned)) {
						Bot.ArchiLogger.LogNullError(nameof(cardsEarned));
						continue;
					}

					if (cardsEarned > 0) {
						// If we already earned some cards for this game, it's very likely that it's done
						// Let's hope that trusting cardsRemaining AND cardsEarned is enough
						// If I ever hear that it's not, I'll most likely need a doctor
						continue;
					}

					// If we have no cardsRemaining and no cardsEarned, it's either:
					// - A game we don't own physically, but we have cards from it in inventory
					// - F2P game that we didn't spend any money in, but we have cards from it in inventory
					// - Steam fuckup
					// As you can guess, we must follow the rest of the logic in case of Steam fuckup
					// Please kill me ;_;
				}

				// Hours
				HtmlNode timeNode = htmlNode.SelectSingleNode(".//div[@class='badge_title_stats_playtime']");
				if (timeNode == null) {
					Bot.ArchiLogger.LogNullError(nameof(timeNode));
					continue;
				}

				string hoursString = timeNode.InnerText;
				if (string.IsNullOrEmpty(hoursString)) {
					Bot.ArchiLogger.LogNullError(nameof(hoursString));
					continue;
				}

				float hours = 0;
				Match hoursMatch = Regex.Match(hoursString, @"[0-9\.,]+");

				// This might fail if we have exactly 0.0 hours played, that's fine
				if (hoursMatch.Success) {
					if (!float.TryParse(hoursMatch.Value, NumberStyles.Number, CultureInfo.InvariantCulture, out hours)) {
						Bot.ArchiLogger.LogNullError(nameof(hours));
						continue;
					}
				}

				// Names
				HtmlNode nameNode = htmlNode.SelectSingleNode("(.//div[@class='card_drop_info_body'])[last()]");
				if (nameNode == null) {
					Bot.ArchiLogger.LogNullError(nameof(nameNode));
					continue;
				}

				string name = nameNode.InnerText;
				if (string.IsNullOrEmpty(name)) {
					Bot.ArchiLogger.LogNullError(nameof(name));
					continue;
				}

				// We handle two cases here - normal one, and no card drops remaining
				int nameStartIndex = name.IndexOf(" by playing ", StringComparison.Ordinal);
				if (nameStartIndex <= 0) {
					nameStartIndex = name.IndexOf("You don't have any more drops remaining for ", StringComparison.Ordinal);
					if (nameStartIndex <= 0) {
						Bot.ArchiLogger.LogNullError(nameof(nameStartIndex));
						continue;
					}

					nameStartIndex += 32; // + 12 below
				}

				nameStartIndex += 12;

				int nameEndIndex = name.LastIndexOf('.');
				if (nameEndIndex <= nameStartIndex) {
					Bot.ArchiLogger.LogNullError(nameof(nameEndIndex));
					continue;
				}

				name = name.Substring(nameStartIndex, nameEndIndex - nameStartIndex);

				// We have two possible cases here
				// Either we have decent info about appID, name, hours and cardsRemaining (cardsRemaining > 0)
				// OR we strongly believe that Steam lied to us, in this case we will need to check game invidually (cardsRemaining == 0)

				if (cardsRemaining > 0) {
					GamesToFarm.Add(new Game(appID, name, hours, cardsRemaining));
				} else {
					extraTasks.Add(CheckGame(appID, name, hours));
				}
			}

			// If we have any pending tasks, wait for them
			await Task.WhenAll(extraTasks).ConfigureAwait(false);
		}

		private async Task CheckPage(byte page) {
			if (page == 0) {
				Bot.ArchiLogger.LogNullError(nameof(page));
				return;
			}

			HtmlDocument htmlDocument = await Bot.ArchiWebHandler.GetBadgePage(page).ConfigureAwait(false);
			if (htmlDocument == null) {
				return;
			}

			await CheckPage(htmlDocument).ConfigureAwait(false);
		}

		private async Task<bool> Farm(Game game) {
			if (game == null) {
				Bot.ArchiLogger.LogNullError(nameof(game));
				return false;
			}

			Bot.ArchiHandler.PlayGame(game.AppID, Bot.BotConfig.CustomGamePlayedWhileFarming);
			DateTime endFarmingDate = DateTime.Now.AddSeconds(Program.GlobalConfig.MaxFarmingTime);

			bool success = true;
			bool? keepFarming = await ShouldFarm(game).ConfigureAwait(false);

			while (keepFarming.GetValueOrDefault(true) && (DateTime.Now < endFarmingDate)) {
				Bot.ArchiLogger.LogGenericInfo("Still farming: " + game.AppID + " (" + game.GameName + ")");

				DateTime startFarmingPeriod = DateTime.Now;
				if (FarmResetEvent.Wait(60 * 1000 * Program.GlobalConfig.FarmingDelay)) {
					FarmResetEvent.Reset();
					success = KeepFarming;
				}

				// Don't forget to update our GamesToFarm hours
				game.HoursPlayed += (float) DateTime.Now.Subtract(startFarmingPeriod).TotalHours;

				if (!success) {
					break;
				}

				keepFarming = await ShouldFarm(game).ConfigureAwait(false);
			}

			Bot.ArchiLogger.LogGenericInfo("Stopped farming: " + game.AppID + " (" + game.GameName + ")");
			return success;
		}

		private bool FarmHours(ConcurrentHashSet<Game> games) {
			if ((games == null) || (games.Count == 0)) {
				Bot.ArchiLogger.LogNullError(nameof(games));
				return false;
			}

			float maxHour = games.Max(game => game.HoursPlayed);
			if (maxHour < 0) {
				Bot.ArchiLogger.LogNullError(nameof(maxHour));
				return false;
			}

			if (maxHour >= HoursToBump) {
				Bot.ArchiLogger.LogGenericError("Received request for already boosted games!");
				return true;
			}

			Bot.ArchiHandler.PlayGames(games.Select(game => game.AppID), Bot.BotConfig.CustomGamePlayedWhileFarming);

			bool success = true;
			while (maxHour < 2) {
				Bot.ArchiLogger.LogGenericInfo("Still farming: " + string.Join(", ", games.Select(game => game.AppID)));

				DateTime startFarmingPeriod = DateTime.Now;
				if (FarmResetEvent.Wait(60 * 1000 * Program.GlobalConfig.FarmingDelay)) {
					FarmResetEvent.Reset();
					success = KeepFarming;
				}

				// Don't forget to update our GamesToFarm hours
				float timePlayed = (float) DateTime.Now.Subtract(startFarmingPeriod).TotalHours;
				foreach (Game game in games) {
					game.HoursPlayed += timePlayed;
				}

				if (!success) {
					break;
				}

				maxHour += timePlayed;
			}

			Bot.ArchiLogger.LogGenericInfo("Stopped farming: " + string.Join(", ", games.Select(game => game.AppID)));
			return success;
		}

		private bool FarmMultiple(IEnumerable<Game> games) {
			if (games == null) {
				Bot.ArchiLogger.LogNullError(nameof(games));
				return false;
			}

			CurrentGamesFarming.ReplaceWith(games);

			Bot.ArchiLogger.LogGenericInfo("Now farming: " + string.Join(", ", CurrentGamesFarming.Select(game => game.AppID)));

			bool result = FarmHours(CurrentGamesFarming);
			CurrentGamesFarming.ClearAndTrim();
			return result;
		}

		private async Task<bool> FarmSolo(Game game) {
			if (game == null) {
				Bot.ArchiLogger.LogNullError(nameof(game));
				return true;
			}

			CurrentGamesFarming.Add(game);

			Bot.ArchiLogger.LogGenericInfo("Now farming: " + game.AppID + " (" + game.GameName + ")");

			bool result = await Farm(game).ConfigureAwait(false);
			CurrentGamesFarming.ClearAndTrim();

			if (!result) {
				return false;
			}

			GamesToFarm.Remove(game);

			Bot.ArchiLogger.LogGenericInfo("Done farming: " + game.AppID + " (" + game.GameName + ")" + (game.HoursPlayed > 0 ? " after " + TimeSpan.FromHours(game.HoursPlayed).ToHumanReadable() + " of playtime!" : ""));
			return true;
		}

		private async Task<ushort?> GetCardsRemaining(uint appID) {
			if (appID == 0) {
				Bot.ArchiLogger.LogNullError(nameof(appID));
				return 0;
			}

			HtmlDocument htmlDocument = await Bot.ArchiWebHandler.GetGameCardsPage(appID).ConfigureAwait(false);

			HtmlNode progressNode = htmlDocument?.DocumentNode.SelectSingleNode("//span[@class='progress_info_bold']");
			if (progressNode == null) {
				return null;
			}

			string progress = progressNode.InnerText;
			if (string.IsNullOrEmpty(progress)) {
				Bot.ArchiLogger.LogNullError(nameof(progress));
				return null;
			}

			Match match = Regex.Match(progress, @"\d+");
			if (!match.Success) {
				return 0;
			}

			ushort cardsRemaining;
			if (ushort.TryParse(match.Value, out cardsRemaining) && (cardsRemaining != 0)) {
				return cardsRemaining;
			}

			Bot.ArchiLogger.LogNullError(nameof(cardsRemaining));
			return null;
		}

		private async Task<bool> IsAnythingToFarm() {
			Bot.ArchiLogger.LogGenericInfo("Checking badges...");

			// Find the number of badge pages
			Bot.ArchiLogger.LogGenericInfo("Checking first page...");
			HtmlDocument htmlDocument = await Bot.ArchiWebHandler.GetBadgePage(1).ConfigureAwait(false);
			if (htmlDocument == null) {
				Bot.ArchiLogger.LogGenericWarning("Could not get badges information, will try again later!");
				return false;
			}

			byte maxPages = 1;

			HtmlNode htmlNode = htmlDocument.DocumentNode.SelectSingleNode("(//a[@class='pagelink'])[last()]");
			if (htmlNode != null) {
				string lastPage = htmlNode.InnerText;
				if (string.IsNullOrEmpty(lastPage)) {
					Bot.ArchiLogger.LogNullError(nameof(lastPage));
					return false;
				}

				if (!byte.TryParse(lastPage, out maxPages) || (maxPages == 0)) {
					Bot.ArchiLogger.LogNullError(nameof(maxPages));
					return false;
				}
			}

			GamesToFarm.ClearAndTrim();

			List<Task> tasks = new List<Task>(maxPages - 1) { CheckPage(htmlDocument) };

			if (maxPages > 1) {
				Bot.ArchiLogger.LogGenericInfo("Checking other pages...");

				for (byte page = 2; page <= maxPages; page++) {
					byte currentPage = page; // We need a copy of variable being passed when in for loops, as loop will proceed before task is launched
					tasks.Add(CheckPage(currentPage));
				}
			}

			await Task.WhenAll(tasks).ConfigureAwait(false);
			SortGamesToFarm();
			return GamesToFarm.Count > 0;
		}

		private async Task<bool?> ShouldFarm(Game game) {
			if (game == null) {
				Bot.ArchiLogger.LogNullError(nameof(game));
				return false;
			}

			ushort? cardsRemaining = await GetCardsRemaining(game.AppID).ConfigureAwait(false);
			if (!cardsRemaining.HasValue) {
				Bot.ArchiLogger.LogGenericWarning("Could not check cards status for " + game.AppID + " (" + game.GameName + "), will try again later!");
				return null;
			}

			game.CardsRemaining = cardsRemaining.Value;

			Bot.ArchiLogger.LogGenericInfo("Status for " + game.AppID + " (" + game.GameName + "): " + game.CardsRemaining + " cards remaining");
			return game.CardsRemaining > 0;
		}

		private void SortGamesToFarm() {
			IOrderedEnumerable<Game> gamesToFarm;
			switch (Bot.BotConfig.FarmingOrder) {
				case BotConfig.EFarmingOrder.Unordered:
					return;
				case BotConfig.EFarmingOrder.AppIDsAscending:
					gamesToFarm = GamesToFarm.OrderBy(game => game.AppID);
					break;
				case BotConfig.EFarmingOrder.AppIDsDescending:
					gamesToFarm = GamesToFarm.OrderByDescending(game => game.AppID);
					break;
				case BotConfig.EFarmingOrder.CardDropsAscending:
					gamesToFarm = GamesToFarm.OrderBy(game => game.CardsRemaining);
					break;
				case BotConfig.EFarmingOrder.CardDropsDescending:
					gamesToFarm = GamesToFarm.OrderByDescending(game => game.CardsRemaining);
					break;
				case BotConfig.EFarmingOrder.HoursAscending:
					gamesToFarm = GamesToFarm.OrderBy(game => game.HoursPlayed);
					break;
				case BotConfig.EFarmingOrder.HoursDescending:
					gamesToFarm = GamesToFarm.OrderByDescending(game => game.HoursPlayed);
					break;
				case BotConfig.EFarmingOrder.NamesAscending:
					gamesToFarm = GamesToFarm.OrderBy(game => game.GameName);
					break;
				case BotConfig.EFarmingOrder.NamesDescending:
					gamesToFarm = GamesToFarm.OrderByDescending(game => game.GameName);
					break;
				default:
					Bot.ArchiLogger.LogGenericError("Unhandled case: " + Bot.BotConfig.FarmingOrder);
					return;
			}

			GamesToFarm.ReplaceWith(gamesToFarm.ToList()); // We must call ToList() here as we can't enumerate during replacing
		}

		internal sealed class Game {
			[JsonProperty]
			internal readonly uint AppID;

			[JsonProperty]
			internal readonly string GameName;

			[JsonProperty]
			internal ushort CardsRemaining { get; set; }

			[JsonProperty]
			internal float HoursPlayed { get; set; }

			//internal string HeaderURL => "https://steamcdn-a.akamaihd.net/steam/apps/" + AppID + "/header.jpg";

			internal Game(uint appID, string gameName, float hoursPlayed, ushort cardsRemaining) {
				if ((appID == 0) || string.IsNullOrEmpty(gameName) || (hoursPlayed < 0) || (cardsRemaining == 0)) {
					throw new ArgumentOutOfRangeException(nameof(appID) + " || " + nameof(gameName) + " || " + nameof(hoursPlayed) + " || " + nameof(cardsRemaining));
				}

				AppID = appID;
				GameName = gameName;
				HoursPlayed = hoursPlayed;
				CardsRemaining = cardsRemaining;
			}

			public override bool Equals(object obj) {
				if (obj == null) {
					return false;
				}

				if (obj == this) {
					return true;
				}

				Game game = obj as Game;
				return (game != null) && Equals(game);
			}

			public override int GetHashCode() => (int) AppID;

			private bool Equals(Game other) => AppID == other.AppID;
		}
	}
}