using DV.Logic.Job;
using System;
using System.Collections.Generic;
using UnityModManagerNet;

namespace DVDiscordPresenceMod
{
    static class Main
    {
        public const string CLIENT_ID = "716722953340846172";
        public const string DETAILS_IDLE = "No Active Jobs";
        public const string STATE_IDLE = "Idle";
        public const string STATE_NO_CARGO = "No Cargo";
        public static readonly DateTime EPOCH = new DateTime(1970, 1, 1, 0, 0, 0, DateTimeKind.Utc);
        public const string LARGE_ICON = "icon";
        public const float ACTIVITY_UPDATE_TIME = 1;
        public const float MAX_TIMER_DIFFERENCE = 10;
        // public const float RPC_UPDATE_TIME = 15; // RPC dll should handle rate limiting just fine.

        public static UnityModManager.ModEntry mod;

        // Status Info
        private static Job currentJob; // Highest-paying active job.
        private static bool bonusOver; // Whether the bonus time is up.
        private static int numActiveJobs;
        private static Trainset lastTrain;
        private static int lastCarsCount;
        private static bool wasDerailed;
        private static float lastLength;
        private static float lastWeight;

        // Presence Info
        private static string activityState;
        private static string activityDetails;
        private static long activityStart;
        private static long activityEnd;
        private static string smallImageKey;
        private static string smallImageText;
        private static float activityTimer;
        // private static float rpcTimer; // RPC dll should handle rate limiting just fine.
        private static bool updateActivity;

        static bool Load(UnityModManager.ModEntry modEntry)
        {
            mod = modEntry;
            mod.OnToggle = OnToggle;
            mod.OnUnload = OnUnload;
            mod.OnUpdate = OnUpdate;

            activityState = STATE_IDLE;
            activityDetails = DETAILS_IDLE;
            activityStart = UnixTime();
            activityEnd = 0;
            bonusOver = false;
            currentJob = null;
            numActiveJobs = 0;
            updateActivity = true;

            // Train Status Trackers
            lastCarsCount = -1;
            wasDerailed = false;
            lastLength = -1;
            lastWeight = -1;

            // Rate Timers
            activityTimer = ACTIVITY_UPDATE_TIME;
            // rpcTimer = RPC_UPDATE_TIME;

            DiscordRpc.EventHandlers handlers = new DiscordRpc.EventHandlers();
            handlers.readyCallback = ReadyCallback;
            handlers.disconnectedCallback += DisconnectedCallback;
            handlers.errorCallback += ErrorCallback;

            DiscordRpc.Initialize(CLIENT_ID, ref handlers, true, null);

            return true;
        }

        static bool OnToggle(UnityModManager.ModEntry _, bool active)
        {
            if (!active)
            {
                DiscordRpc.RichPresence presence = new DiscordRpc.RichPresence
                {
                    details = "",
                    state = "",
                    startTimestamp = 0,
                    endTimestamp = 0,
                    largeImageKey = LARGE_ICON,
                    largeImageText = "",
                    smallImageKey = "",
                    smallImageText = ""
                };

                DiscordRpc.UpdatePresence(ref presence);
            }
            else
                updateActivity = true;

            return true;
        }

        static void ReadyCallback()
        {
            mod.Logger.Log("Got ready callback.");
        }

        static void DisconnectedCallback(int errorCode, string message)
        {
            mod.Logger.Log(string.Format("Got disconnect {0}: {1}", errorCode, message));
        }

        static void ErrorCallback(int errorCode, string message)
        {
            mod.Logger.Log(string.Format("Got error {0}: {1}", errorCode, message));
        }

        static bool OnUnload(UnityModManager.ModEntry modEntry)
        {
            DiscordRpc.Shutdown();
            return true;
        }

        private static StationInfo ExtractStationInfoWithYardID(string yardId)
        {
            StationController stationController;
            if (SingletonBehaviour<LogicController>.Instance != null && SingletonBehaviour<LogicController>.Instance.YardIdToStationController != null && SingletonBehaviour<LogicController>.Instance.YardIdToStationController.TryGetValue(yardId, out stationController))
            {
                return stationController.stationInfo;
            }
            return null;
        }

        static long UnixTime()
        {
            return (long)(DateTime.UtcNow - EPOCH).TotalSeconds;
        }

        private static Dictionary<string, string> generalCarNames = new Dictionary<string, string>
        {
            {
                "FlatbedMilitary",
                "Military Flatbed"
            },
            {
                "Flatbed",
                "Flatbed"
            },
            {
                "Autorack",
                "Autorack"
            },
            {
                "Tank",
                "Tank Car"
            },
            {
                "Boxcar",
                "Boxcar"
            },
            {
                "Hopper",
                "Hopper"
            },
            {
                "Passenger",
                "Passenger Car"
            },
            {
                "Nuclear",
                "Nuclear Flask"
            }
        };

        static string GetEmptyCarName(TrainCarType carType, bool multiple)
        {
            string name = CarTypes.DisplayName(carType).Split(' ')[0];
            // Oh boy here we go being YandereDev again!
            if (generalCarNames.ContainsKey(name))
                generalCarNames.TryGetValue(name, out name);
            return string.Format("Empty {0}{1}", name, multiple ? "s" : "");
        }

        static bool UpdateTrainStatus()
        {
            TrainCar car = PlayerManager.Car;
            Trainset train = car?.trainset;
            TrainCar loco = null; // Current train has loco?
            bool changed = train != lastTrain;

            // If no current train, use last train if job is active.
            if (train == null)
            {
                // If no active job, switch to idle.
                if (currentJob == null)
                {
                    lastTrain = null;
                    lastCarsCount = -1;
                    lastLength = -1;
                    lastWeight = -1;
                    activityState = STATE_IDLE;
                    smallImageKey = "";
                    smallImageText = "";
                    return changed;
                }
                // Otherwise keep using last train.
            }
            // Switch current train if player entered locomotive.
            else if (train == PlayerManager.LastLoco?.trainset)
            {
                lastTrain = train;
                loco = PlayerManager.LastLoco;
            }
            // No changes if otherwise, and we need to check the last train itself.
            if (lastTrain == null)
                return false;

            // Update status based on lastTrain.
            TrainCarType carType = TrainCarType.NotSet; // First found consist car type.
            CargoType cargo = CargoType.None; // First found cargo type.
            int consist = 0; // Number of cars in consist.
            bool derailed = false; // Any car derailed?
            bool mixed = false; // Mixed cargo types?
            float length = 0; // Length of consist, excluding locos.
            float weight = 0; // Weight of consist, excluding locos.

            foreach (TrainCar c in lastTrain.cars)
            {
                derailed = derailed || c.derailed;
                if (CarTypes.IsAnyLocomotiveOrTender(c.carType))
                {
                    if (loco == null)
                        loco = c;
                }
                else
                {
                    cargo = cargo == CargoType.None ? c.logicCar.CurrentCargoTypeInCar : cargo;
                    mixed = mixed || cargo != CargoType.None && cargo != c.logicCar.CurrentCargoTypeInCar;
                    carType = carType == TrainCarType.NotSet ? c.carType : carType;
                    consist++;
                    length += c.logicCar.length;
                    // weight += c.totalMass;
                    weight += c.logicCar.carOnlyMass + c.logicCar.LoadedCargoAmount * CargoTypes.GetCargoUnitMass(c.logicCar.CurrentCargoTypeInCar);
                }
            }

            changed = lastCarsCount != lastTrain.cars.Count || lastLength != length || lastWeight != weight || wasDerailed != derailed;
            lastCarsCount = lastTrain.cars.Count;
            lastLength = length;
            lastWeight = weight;
            wasDerailed = derailed;

            if (changed)
            {
                if (loco != null)
                {
                    switch (loco.carType)
                    {
                        case TrainCarType.LocoSteamHeavy:
                        case TrainCarType.LocoSteamHeavyBlue:
                        case TrainCarType.Tender:
                        case TrainCarType.TenderBlue:
                            smallImageKey = "locosteamgray";
                            smallImageText = "SH 2-8-2";
                            break;
                        case TrainCarType.LocoDiesel:
                            smallImageKey = "locodiesel";
                            smallImageText = "DE6 Diesel";
                            break;
                        case TrainCarType.LocoShunter:
                            smallImageKey = "locoshunteryellow";
                            smallImageText = "DE2 Shunter";
                            break;
                        default:
                            smallImageKey = "";
                            smallImageText = "";
                            break;
                    }
                }

                if (consist > 0)
                {
                    string emptyCarType = GetEmptyCarName(carType, consist > 1);
                    string cargoName = cargo == CargoType.None ? emptyCarType : string.Format("{0}{1}", cargo.GetCargoName(), mixed ? ", etc." : "");
                    activityState = string.Format("{0}: {1:0.00} tons; {2:0.00} meters{3}", cargoName, weight / 1000f, length, derailed ? "; derailed" : "");
                }
                else
                    activityState = derailed ? "Derailed" : STATE_NO_CARGO;
            }

            return changed;
        }

        static bool UpdateJobStatus()
        {
            bool changed = false;

            int curActiveJobs = PlayerJobs.Instance.currentJobs.Count;

            if (numActiveJobs != curActiveJobs)
            {
                Job highest = null;

                foreach (Job j in PlayerJobs.Instance.currentJobs)
                    if (highest == null || highest.GetBasePaymentForTheJob() < j.GetBasePaymentForTheJob())
                        highest = j;

                changed = currentJob != highest;
                currentJob = highest;
                // TODO: Determine if this actually happens.
                if (currentJob == null && numActiveJobs > 0)
                    numActiveJobs = -1; // Flag for checking this again.
                else
                    numActiveJobs = curActiveJobs;
            }

            if (currentJob != null)
            {
                long curTime = UnixTime();
                long actualActivityStart = curTime - (long)currentJob.GetTimeOnJob();
                activityEnd = actualActivityStart + (long)currentJob.TimeLimit;
                bool timesUp = activityEnd < curTime;
                changed = changed || bonusOver != timesUp || Math.Abs(actualActivityStart - activityStart) > MAX_TIMER_DIFFERENCE;
                activityStart = actualActivityStart;
                bonusOver = timesUp;
            }

            if (changed)
            {
                if (currentJob == null)
                {
                    activityDetails = DETAILS_IDLE;
                    activityStart = UnixTime();
                    activityEnd = 0;
                    bonusOver = false;
                }
                else
                {
                    
                    StationInfo srcStation = ExtractStationInfoWithYardID(currentJob.chainData.chainOriginYardId);
                    StationInfo stationInfo = ExtractStationInfoWithYardID(currentJob.chainData.chainDestinationYardId);
                    string jobTypeString;
                    string preposition;
                    switch (currentJob.jobType)
                    {
                        case JobType.ShuntingLoad:
                            stationInfo = srcStation;
                            jobTypeString = "Loading Cars";
                            preposition = "in";
                            break;
                        case JobType.ShuntingUnload:
                            jobTypeString = "Unloading Cars";
                            preposition = "in";
                            break;
                        case JobType.Transport:
                            jobTypeString = "Freight Haul";
                            preposition = "to";
                            break;
                        case JobType.EmptyHaul:
                            jobTypeString = "Logistical Haul";
                            preposition = "to";
                            break;
                        default:
                            stationInfo = srcStation;
                            jobTypeString = "Unknown Job";
                            preposition = "from";
                            break;
                    }
                    if (stationInfo != null)
                        activityDetails = string.Format("{0} {1} {2}", jobTypeString, preposition, stationInfo.Name);
                    else
                        activityDetails = jobTypeString;
                    activityStart = UnixTime() - (long)currentJob.GetTimeOnJob();
                    activityEnd = activityStart + (long)currentJob.TimeLimit;
                }
            }

            return changed;
        }

        

        static void OnUpdate(UnityModManager.ModEntry _, float delta)
        {
            activityTimer += delta;
            // rpcTimer += delta;

            if (updateActivity)
            {
                DiscordRpc.RichPresence presence = new DiscordRpc.RichPresence
                {
                    details = activityDetails,
                    state = activityState,
                    startTimestamp = activityStart,
                    endTimestamp = activityEnd > UnixTime() ? activityEnd : 0,
                    largeImageKey = LARGE_ICON,
                    largeImageText = "",
                    smallImageKey = smallImageKey,
                    smallImageText = smallImageText
                };

                DiscordRpc.UpdatePresence(ref presence);
                // mod.Logger.Log("Requested to set presence.");

                updateActivity = false;
            }
            else if (activityTimer > ACTIVITY_UPDATE_TIME)
            {
                activityTimer %= ACTIVITY_UPDATE_TIME;
                bool jobChanged = UpdateJobStatus();
                bool trainChanged = UpdateTrainStatus();
                updateActivity = jobChanged || trainChanged;
            }

            /*if (!updateActivity)
            {
                if (activityTimer > ACTIVITY_UPDATE_TIME)
                {
                    activityTimer %= ACTIVITY_UPDATE_TIME;
                    bool jobChanged = UpdateJobStatus();
                    bool trainChanged = UpdateTrainStatus();
                    updateActivity = jobChanged || trainChanged;
                }
                else
                    return;
            } // Don't update activity and Discord presence on the same frame.
            else if (rpcTimer > RPC_UPDATE_TIME)
            {
                rpcTimer %= RPC_UPDATE_TIME;

                DiscordRpc.RichPresence presence = new DiscordRpc.RichPresence
                {
                    details = activityDetails,
                    state = activityState,
                    startTimestamp = activityStart,
                    // endTimestamp = activityEnd > activityStart ? activityEnd : 0,
                    largeImageKey = LARGE_ICON,
                    largeImageText = "",
                    smallImageKey = smallImageKey,
                    smallImageText = smallImageText
                };
                if (activityEnd > activityStart)
                    presence.endTimestamp = activityEnd;

                DiscordRpc.UpdatePresence(ref presence);
                // mod.Logger.Log("Requested to set presence.");

                updateActivity = false;
            }*/
        }
    }
}
