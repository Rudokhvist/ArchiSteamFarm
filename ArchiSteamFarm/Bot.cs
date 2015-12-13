﻿/*
    _                _      _  ____   _                           _____
   / \    _ __  ___ | |__  (_)/ ___| | |_  ___   __ _  _ __ ___  |  ___|__ _  _ __  _ __ ___
  / _ \  | '__|/ __|| '_ \ | |\___ \ | __|/ _ \ / _` || '_ ` _ \ | |_  / _` || '__|| '_ ` _ \
 / ___ \ | |  | (__ | | | || | ___) || |_|  __/| (_| || | | | | ||  _|| (_| || |   | | | | | |
/_/   \_\|_|   \___||_| |_||_||____/  \__|\___| \__,_||_| |_| |_||_|   \__,_||_|   |_| |_| |_|

 Copyright 2015 Łukasz "JustArchi" Domeradzki
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

using SteamKit2;
using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.IO;
using System.Security.Cryptography;
using System.Threading.Tasks;
using System.Xml;

namespace ArchiSteamFarm {

	public class RedeemEventArgs : EventArgs {
		public RedeemEventArgs(string s) { Text = s; }
		public string Text { get; private set; } // readonly
	}

	internal sealed class Bot {
		private const ushort CallbackSleep = 500; // In miliseconds

		private static readonly ConcurrentDictionary<string, Bot> Bots = new ConcurrentDictionary<string, Bot>();

		private readonly string ConfigFile, SentryFile;

		internal readonly string BotName;

		private bool IsRunning = false;
		private string AuthCode, TwoFactorAuth;

		internal ArchiHandler ArchiHandler { get; private set; }
		internal ArchiWebHandler ArchiWebHandler { get; private set; }
		internal CallbackManager CallbackManager { get; private set; }
		internal CardsFarmer CardsFarmer { get; private set; }
		internal SteamClient SteamClient { get; private set; }
		internal SteamFriends SteamFriends { get; private set; }
		internal SteamUser SteamUser { get; private set; }
		internal Trading Trading { get; private set; }

		// Config variables
		internal bool Enabled { get; private set; } = false;
		internal string SteamLogin { get; private set; } = "null";
		internal string SteamPassword { get; private set; } = "null";
		internal string SteamNickname { get; private set; } = "null";
		internal string SteamApiKey { get; private set; } = "null";
		internal string SteamParentalPIN { get; private set; } = "0";
		internal ulong SteamMasterID { get; private set; } = 0;
		internal ulong SteamMasterClanID { get; private set; } = 0;
		internal bool CardDropsRestricted { get; private set; } = false;
		internal bool ShutdownOnFarmingFinished { get; private set; } = false;
		internal HashSet<uint> Blacklist { get; private set; } = new HashSet<uint> { 303700, 335590, 368020 };
		internal bool Statistics { get; private set; } = true;
		internal bool doanswer { get; private set; } = true;

		// Declare the delegate (if using non-generic pattern).
		public delegate void RedeemEventHandler(object sender, RedeemEventArgs e);
		
		// Declare the event.
		public event RedeemEventHandler RedeemEvent;

		private static bool IsValidCdKey(string key) {
			if (string.IsNullOrEmpty(key)) {
				return false;
			}

			if (key.Length != 17 && key.Length != 29) {
				return false;
			}

			for (byte i = 5; i < key.Length; i += 6) {
				if (key[i] != '-') {
					return false;
				}
			}

			return true;
		}

		internal static int GetRunningBotsCount() {
			return Bots.Count;
		}

		internal static async Task ShutdownAllBots() {
			List<Task> tasks = new List<Task>();
			foreach (Bot bot in Bots.Values) {
				tasks.Add(Task.Run(async () => await bot.Shutdown().ConfigureAwait(false)));
			}
			await Task.WhenAll(tasks).ConfigureAwait(false);
		}

		internal Bot(string botName) {
			if (Bots.ContainsKey(botName)) {
				return;
			}

			BotName = botName;

			ConfigFile = Path.Combine(Program.ConfigDirectoryPath, BotName + ".xml");
			SentryFile = Path.Combine(Program.ConfigDirectoryPath, BotName + ".bin");

			if (!ReadConfig()) {
				return;
			}

			if (!Enabled) {
				return;
			}

			Bots.AddOrUpdate(BotName, this, (key, value) => this);

			// Initialize
			SteamClient = new SteamClient();

			ArchiHandler = new ArchiHandler();
			SteamClient.AddHandler(ArchiHandler);

			CallbackManager = new CallbackManager(SteamClient);
			CallbackManager.Subscribe<SteamClient.ConnectedCallback>(OnConnected);
			CallbackManager.Subscribe<SteamClient.DisconnectedCallback>(OnDisconnected);

			SteamFriends = SteamClient.GetHandler<SteamFriends>();
			CallbackManager.Subscribe<SteamFriends.FriendsListCallback>(OnFriendsList);
			CallbackManager.Subscribe<SteamFriends.FriendMsgCallback>(OnFriendMsg);

			SteamUser = SteamClient.GetHandler<SteamUser>();
			CallbackManager.Subscribe<SteamUser.AccountInfoCallback>(OnAccountInfo);
			CallbackManager.Subscribe<SteamUser.LoggedOffCallback>(OnLoggedOff);
			CallbackManager.Subscribe<SteamUser.LoggedOnCallback>(OnLoggedOn);
			CallbackManager.Subscribe<SteamUser.UpdateMachineAuthCallback>(OnMachineAuth);

			CallbackManager.Subscribe<ArchiHandler.NotificationCallback>(OnNotification);
			CallbackManager.Subscribe<ArchiHandler.PurchaseResponseCallback>(OnPurchaseResponse);

			ArchiWebHandler = new ArchiWebHandler(this, SteamApiKey);
			CardsFarmer = new CardsFarmer(this);
			Trading = new Trading(this);

			// Start
			var fireAndForget = Task.Run(async () => await Start().ConfigureAwait(false));
		}

		private bool ReadConfig() {
			if (!File.Exists(ConfigFile)) {
				return false;
			}

			try {
				using (XmlReader reader = XmlReader.Create(ConfigFile)) {
					while (reader.Read()) {
						if (reader.NodeType != XmlNodeType.Element) {
							continue;
						}

						string key = reader.Name;
						if (string.IsNullOrEmpty(key)) {
							continue;
						}

						string value = reader.GetAttribute("value");
						if (string.IsNullOrEmpty(value)) {
							continue;
						}

						switch (key) {
							case "Enabled":
								Enabled = bool.Parse(value);
								break;
							case "SteamLogin":
								SteamLogin = value;
								break;
							case "SteamPassword":
								SteamPassword = value;
								break;
							case "SteamNickname":
								SteamNickname = value;
								break;
							case "SteamApiKey":
								SteamApiKey = value;
								break;
							case "SteamParentalPIN":
								SteamParentalPIN = value;
								break;
							case "SteamMasterID":
								SteamMasterID = ulong.Parse(value);
								break;
							case "SteamMasterClanID":
								SteamMasterClanID = ulong.Parse(value);
								break;
							case "CardDropsRestricted":
								CardDropsRestricted = bool.Parse(value);
								break;
							case "ShutdownOnFarmingFinished":
								ShutdownOnFarmingFinished = bool.Parse(value);
								break;
							case "Blacklist":
								Blacklist.Clear();
								foreach (string appID in value.Split(',')) {
									Blacklist.Add(uint.Parse(appID));
								}
								break;
							case "Statistics":
								Statistics = bool.Parse(value);
								break;
							default:
								Logging.LogGenericWarning(BotName, "Unrecognized config value: " + key + "=" + value);
								break;
						}
					}
				}
			} catch (Exception e) {
				Logging.LogGenericException(BotName, e);
				Logging.LogGenericError(BotName, "Your config for this bot instance is invalid, it won't run!");
				return false;
			}

			return true;
		}

		internal async Task Start() {
			if (IsRunning) {
				return;
			}

			IsRunning = true;

			Logging.LogGenericInfo(BotName, "Starting...");
			await Program.LimitSteamRequestsAsync().ConfigureAwait(false);
			SteamClient.Connect();

			var fireAndForget = Task.Run(() => HandleCallbacks());
		}

		internal async Task Stop() {
			if (!IsRunning) {
				return;
			}

			await CardsFarmer.StopFarming().ConfigureAwait(false);
			IsRunning = false;
			SteamClient.Disconnect();
		}

		private async Task<bool> Shutdown(string botNameToShutdown) {
			Bot botToShutdown;
			if (!Bots.TryGetValue(botNameToShutdown, out botToShutdown)) {
				return false;
			}

			await botToShutdown.Stop().ConfigureAwait(false);
			Bots.TryRemove(botNameToShutdown, out botToShutdown);

			Program.OnBotShutdown(botToShutdown);
			return true;
		}

		internal async Task<bool> Shutdown() {
			return await Shutdown(BotName).ConfigureAwait(false);
		}

		internal async Task OnFarmingFinished() {
			if (ShutdownOnFarmingFinished) {
				await Shutdown().ConfigureAwait(false);
			}
		}

		private void HandleCallbacks() {
			TimeSpan timeSpan = TimeSpan.FromMilliseconds(CallbackSleep);
			while (IsRunning) {
				CallbackManager.RunWaitCallbacks(timeSpan);
			}
		}

		private void SendMessageToUser(ulong steamID, string message) {
			if (steamID == 0 || string.IsNullOrEmpty(message)) {
				return;
			}

			SteamFriends.SendChatMessage(steamID, EChatEntryType.ChatMsg, message);
		}


		private void ResponseStatus(ulong steamID, string botName = null) {
			if (steamID == 0) {
				return;
			}

<<<<<<< HEAD
			Bot bot;

			if (string.IsNullOrEmpty(botName)) {
				bot = this;
			} else {
				if (botName.Equals("all")) {
					foreach (var curbot in Bots) {
						if (curbot.Value.CardsFarmer.CurrentGamesFarming.Count == 0)
							SendMessageToUser(steamID, "Bot " + curbot.Key + " is not farming.");
						else
							SendMessageToUser(steamID, "Bot " + curbot.Key + " is currently farming appIDs: " + string.Join(", ", 	curbot.Value.CardsFarmer.CurrentGamesFarming) + " and has a total of " + curbot.Value.CardsFarmer.GamesToFarm.Count + " games left to farm");
					}
					SendMessageToUser(steamID, "Currently " + Bots.Count + " bots are running");
					return;
				}

				if (!Bots.TryGetValue(botName, out bot)) {
					SendMessageToUser(steamID, "Couldn't find any bot named " + botName + "!");
					return;
				}
			}

			if (bot.CardsFarmer.CurrentGamesFarming.Count > 0) {
				SendMessageToUser(steamID, "Bot " + bot.BotName + " is currently farming appIDs: " + string.Join(", ", bot.CardsFarmer.CurrentGamesFarming) + " and has a total of " + bot.CardsFarmer.GamesToFarm.Count + " games left to farm");
			}
			SendMessageToUser(steamID, "Currently " + Bots.Count + " bots are running");
		}

		private void ResponseStart(ulong steamID, string botNameToStart) {
			if (steamID == 0 || string.IsNullOrEmpty(botNameToStart)) {
				return;
			}

			if (Bots.ContainsKey(botNameToStart)) {
				SendMessageToUser(steamID, "That bot instance is already running!");
				return;
			}

			new Bot(botNameToStart);
			if (Bots.ContainsKey(botNameToStart)) {
				SendMessageToUser(steamID, "Done!");
			} else {
				SendMessageToUser(steamID, "That bot instance failed to start, make sure that " + botNameToStart + ".xml config exists and bot is active!");
			}
		}

		private async Task ResponseStop(ulong steamID, string botNameToShutdown) {
			if (steamID == 0 || string.IsNullOrEmpty(botNameToShutdown)) {
				return;
			}

			if (!Bots.ContainsKey(botNameToShutdown)) {
				SendMessageToUser(steamID, "That bot instance is already inactive!");
				return;
			}

			if (await Shutdown(botNameToShutdown).ConfigureAwait(false)) {
				SendMessageToUser(steamID, "Done!");
			} else {
				SendMessageToUser(steamID, "That bot instance failed to shutdown!");
			}
		}

        public static Task<string> PurchaseResultAsync(Bot bot, string gamekey)
        {
            var tcs = new TaskCompletionSource<string>();
            bot.doanswer = false;
            bot.RedeemEvent += (s, e) =>
            {
                bot.doanswer = true;
                tcs.TrySetResult(e.Text);
            };
            bot.ArchiHandler.RedeemKey(gamekey);
            return tcs.Task;
        }

        private async Task ResponseActivate(ulong steamID, string botName, string gamekey)
        {
            if (steamID == 0 || string.IsNullOrEmpty(botName))
            {
                return;
            }
            Bot bot;

            if (!Bots.TryGetValue(botName, out bot))
            {
                SendMessageToUser(steamID, "Bot is inactive and can't activate keys");
                return;
            }

            SendMessageToUser(steamID, botName + " answer: " + await PurchaseResultAsync(bot, gamekey));

        }

        private async Task ResponseMultiActivate(ulong steamID, string[] gamekeys)
        {
            string results="";
            int i = 0;
            foreach (var curbot in Bots) {
                results+=curbot.Key+ " answer: " + await PurchaseResultAsync(curbot.Value, gamekeys[i].Trim())+"\n";
                i++;
                if (gamekeys.Length <= i)
                    break;
            }

            SendMessageToUser(steamID, results);

        }



        private void OnConnected(SteamClient.ConnectedCallback callback) {
			if (callback == null) {
				return;
			}

			if (callback.Result != EResult.OK) {
				Logging.LogGenericError(BotName, "Unable to connect to Steam: " + callback.Result);
				return;
			}

			Logging.LogGenericInfo(BotName, "Connected to Steam!");

			byte[] sentryHash = null;
			if (File.Exists(SentryFile)) {
				byte[] sentryFileContent = File.ReadAllBytes(SentryFile);
				sentryHash = CryptoHelper.SHAHash(sentryFileContent);
			}

			if (SteamLogin.Equals("null")) {
				SteamLogin = Program.GetUserInput(BotName, Program.EUserInputType.Login);
			}

			if (SteamPassword.Equals("null")) {
				SteamPassword = Program.GetUserInput(BotName, Program.EUserInputType.Password);
			}

			SteamUser.LogOn(new SteamUser.LogOnDetails {
				Username = SteamLogin,
				Password = SteamPassword,
				AuthCode = AuthCode,
				TwoFactorCode = TwoFactorAuth,
				SentryFileHash = sentryHash
			});
		}

		private async void OnDisconnected(SteamClient.DisconnectedCallback callback) {
			if (callback == null) {
				return;
			}

			if (!IsRunning) {
				return;
			}

			if (SteamClient == null) {
				return;
			}

			Logging.LogGenericWarning(BotName, "Disconnected from Steam, reconnecting...");
			await Program.LimitSteamRequestsAsync().ConfigureAwait(false);
			SteamClient.Connect();
		}

		private void OnFriendsList(SteamFriends.FriendsListCallback callback) {
			if (callback == null) {
				return;
			}

			foreach (var friend in callback.FriendList) {
				if (friend.Relationship != EFriendRelationship.RequestRecipient) {
					continue;
				}

				SteamID steamID = friend.SteamID;
				switch (steamID.AccountType) {
					case EAccountType.Clan:
						ArchiHandler.DeclineClanInvite(steamID);
						break;
					default:
						if (steamID == SteamMasterID) {
							SteamFriends.AddFriend(steamID);
						} else {
							SteamFriends.RemoveFriend(steamID);
						}
						break;
				}
			}
		}

<<<<<<< HEAD
		private async void OnFriendMsg(SteamFriends.FriendMsgCallback callback) {
			if (callback == null) {
				return;
			}

			if (callback.EntryType != EChatEntryType.ChatMsg) {
				return;
			}

			ulong steamID = callback.Sender;
			if (steamID != SteamMasterID) {
				return;
			}

			string message = callback.Message;
			if (string.IsNullOrEmpty(message)) {
				return;
			}

			if (IsValidCdKey(message)) {
				ArchiHandler.RedeemKey(message);
				return;
			}

			if (message.Contains("\n")) {
				string[] args = message.Split('\n');
				if (args[0].Contains("-")) {
					await ResponseMultiActivate(steamID, args);
				}
			}      

			if (!message.StartsWith("!")) {
				return;
			}

			if (!message.Contains(" ")) {
				switch (message) {
					case "!exit":
						await ShutdownAllBots().ConfigureAwait(false);
						break;
					case "!farm":
						SendMessageToUser(steamID, "Please wait...");
						await CardsFarmer.StartFarming().ConfigureAwait(false);
						SendMessageToUser(steamID, "Done!");
						break;
					case "!restart":
						await Program.Restart().ConfigureAwait(false);
						break;
					case "!status":
						ResponseStatus(steamID);
						break;
					case "!stop":
						await Shutdown().ConfigureAwait(false);
						break;
				}
			} else {
				string[] args = message.Split(' ');
				switch (args[0]) {
					case "!farm": {
						Bot botToFarm;
						if (!Bots.TryGetValue(args[1], out botToFarm)) {
							SendMessageToUser(steamID, "Bot is inactive and cant farm");
							break;
						}
						SendMessageToUser(steamID, "Please wait...");
						await botToFarm.CardsFarmer.StartFarming().ConfigureAwait(false);
						SendMessageToUser(steamID, "Done!");
						break;
						}
					case "!redeem":
						if (args.Length == 2) {
							ArchiHandler.RedeemKey(args[1]);
						} else if (args.Length == 3) {
							await ResponseActivate(steamID, args[1], args[2]);
						}
						break;
					case "!start":
						ResponseStart(steamID, args[1]);
						break;
					case "!stop":
						await ResponseStop(steamID, args[1]).ConfigureAwait(false);
						break;
					case "!status":
						ResponseStatus(steamID, args[1]);
						break;
				}
			}
		}

		private void OnAccountInfo(SteamUser.AccountInfoCallback callback) {
			if (callback == null) {
				return;
			}

			SteamFriends.SetPersonaState(EPersonaState.Online);
		}

		private void OnLoggedOff(SteamUser.LoggedOffCallback callback) {
			if (callback == null) {
				return;
			}

			Logging.LogGenericInfo(BotName, "Logged off of Steam: " + callback.Result);
		}

		private async void OnLoggedOn(SteamUser.LoggedOnCallback callback) {
			if (callback == null) {
				return;
			}

			EResult result = callback.Result;
			switch (result) {
				case EResult.AccountLogonDenied:
					AuthCode = Program.GetUserInput(SteamLogin, Program.EUserInputType.SteamGuard);
					break;
				case EResult.AccountLoginDeniedNeedTwoFactor:
					TwoFactorAuth = Program.GetUserInput(SteamLogin, Program.EUserInputType.TwoFactorAuthentication);
					break;
				case EResult.InvalidPassword:
					Logging.LogGenericWarning(BotName, "Unable to login to Steam: " + result + ", will retry after a longer while");
					await Stop().ConfigureAwait(false);
					await Utilities.SleepAsync(25 * 60 * 1000).ConfigureAwait(false); // Steam removes requirement of captcha after around 20 minutes
					await Start().ConfigureAwait(false);
					break;
				case EResult.OK:
					Logging.LogGenericInfo(BotName, "Successfully logged on!");

					if (!SteamNickname.Equals("null")) {
						SteamFriends.SetPersonaName(SteamNickname);
					}

					if (SteamParentalPIN.Equals("null")) {
						SteamParentalPIN = Program.GetUserInput(BotName, Program.EUserInputType.SteamParentalPIN);
					}

					await ArchiWebHandler.Init(SteamClient, callback.WebAPIUserNonce, callback.VanityURL, SteamParentalPIN).ConfigureAwait(false);

					ulong clanID = SteamMasterClanID;
					if (clanID != 0) {
						SteamFriends.JoinChat(clanID);
					}

					if (Statistics) {
						SteamFriends.JoinChat(Program.ArchiSCFarmGroup);
						await ArchiWebHandler.JoinClan(Program.ArchiSCFarmGroup).ConfigureAwait(false);
					}

					await CardsFarmer.StartFarming().ConfigureAwait(false);
					break;
				case EResult.ServiceUnavailable:
				case EResult.Timeout:
				case EResult.TryAnotherCM:
					Logging.LogGenericWarning(BotName, "Unable to login to Steam: " + result + ", retrying...");
					await Stop().ConfigureAwait(false);
					await Start().ConfigureAwait(false);
					break;
				default:
					Logging.LogGenericWarning(BotName, "Unable to login to Steam: " + result);
					await Shutdown().ConfigureAwait(false);
					break;
			}
		}

		private void OnMachineAuth(SteamUser.UpdateMachineAuthCallback callback) {
			if (callback == null) {
				return;
			}

			Logging.LogGenericInfo(BotName, "Updating sentryfile...");

			int fileSize;
			byte[] sentryHash;

			using (FileStream fileStream = File.Open(SentryFile, FileMode.OpenOrCreate, FileAccess.ReadWrite)) {
				fileStream.Seek(callback.Offset, SeekOrigin.Begin);
				fileStream.Write(callback.Data, 0, callback.BytesToWrite);
				fileSize = (int) fileStream.Length;

				fileStream.Seek(0, SeekOrigin.Begin);
				using (SHA1CryptoServiceProvider sha = new SHA1CryptoServiceProvider()) {
					sentryHash = sha.ComputeHash(fileStream);
				}
			}


			// Inform the steam servers that we're accepting this sentry file
			SteamUser.SendMachineAuthResponse(new SteamUser.MachineAuthDetails {
				JobID = callback.JobID,
				FileName = callback.FileName,
				BytesWritten = callback.BytesToWrite,
				FileSize = fileSize,
				Offset = callback.Offset,
				Result = EResult.OK,
				LastError = 0,
				OneTimePassword = callback.OneTimePassword,
				SentryFileHash = sentryHash,
			});

			Logging.LogGenericInfo(BotName, "Sentryfile updated successfully!");
		}

		private void OnNotification(ArchiHandler.NotificationCallback callback) {
			if (callback == null) {
				return;
			}

			switch (callback.NotificationType) {
				case ArchiHandler.NotificationCallback.ENotificationType.Trading:
					Trading.CheckTrades();
					break;
			}
		}

		private async void OnPurchaseResponse(ArchiHandler.PurchaseResponseCallback callback) {
			if (callback == null) {
				return;
			}

			var purchaseResult = callback.PurchaseResult;
			var items = callback.Items;
            if (doanswer)
            {
                SendMessageToUser(SteamMasterID, "Status: " + purchaseResult + " | Items: " + string.Join("", items));
            } else
            {
                RedeemEvent(this, new RedeemEventArgs("Status: " + purchaseResult + " | Items: " + string.Join("", items)));
            }

			if (purchaseResult == ArchiHandler.PurchaseResponseCallback.EPurchaseResult.OK) {
				await CardsFarmer.StartFarming().ConfigureAwait(false);
			}
		}
	}
}
