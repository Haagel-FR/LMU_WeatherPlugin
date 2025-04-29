using SimHub.Plugins;
using System;
using System.Net.Http;
using System.Threading.Tasks;
using System.Timers;
using Newtonsoft.Json.Linq;
using System.Windows.Controls;
using GameReaderCommon;

namespace LMU_WeatherPlugin
{
    [PluginDescription("LMU Weather Fetcher with Dynamic Node Properties")]
    [PluginAuthor("Giacomo Chiappe")]
    [PluginName("LMU_WeatherPlugin")]
    public class WeatherPlugin : IPlugin, IDataPlugin, IWPFSettings
    {
        private Timer _timer;
        private Timer _gameMonitorTimer;
        private HttpClient _httpClient;
        private PluginManager _pluginManager;

        private readonly string[] Nodes = new[] { "START", "NODE_25", "NODE_50", "NODE_75", "FINISH" };
        private readonly string[] Metrics = new[]
        {
            "WNV_TEMPERATURE",
            "WNV_WINDDIRECTION",
            "WNV_RAIN_CHANCE",
            "WNV_WINDSPEED",
            "WNV_STARTTIME",
            "WNV_SKY",
            "WNV_DURATION",
            "WNV_HUMIDITY"
        };

        private string _currentSession = "RACE"; // Default fallback
        private JObject _weatherData;
        private int _currentSessionLengthMinutes = 0;
        private string _currentNode = "START";

        public PluginManager PluginManager
        {
            get => _pluginManager;
            set => _pluginManager = value;
        }

        public WeatherPlugin()
        {
            // Constructor
        }

        public void Init(PluginManager pluginManager)
        {
            _pluginManager = pluginManager;
            _httpClient = new HttpClient();

            // Attach base properties (by node)
            foreach (var node in Nodes)
            {
                foreach (var metric in Metrics)
                {
                    AttachDelegate($"{node}_{metric}");
                    AttachDelegate($"{node}_{metric}Str");
                }
            }

            // Attach session properties
            AttachDelegate("CurrentSessionLengthMinutes");
            AttachDelegate("CurrentNodeDurationMinutes");

            // Attach CURRENTNODE dynamic properties
            foreach (var metric in Metrics)
            {
                AttachDelegate($"CURRENTNODE_{metric}");
                AttachDelegate($"CURRENTNODE_{metric}Str");
            }

            

            _gameMonitorTimer = new Timer(1000); // Checks game state every second
            _gameMonitorTimer.Elapsed += (s, e) => CheckGameState();
            _gameMonitorTimer.Start();
            CheckGameState();

            _timer = new Timer(5000); // Weather fetch timer
            _timer.Elapsed += async (s, e) => await FetchWeather();
            SimHub.Logging.Current.Info("[LMU WeatherPlugin] Game monitoring timer started.");
        }

        private void AttachDelegate(string name)
        {
            SimHub.Logging.Current.Debug($"[LMU WeatherPlugin] Attaching delegate: {name}");
            this.AttachDelegate(name, () => GetWeatherData(name));
        }



        private void CheckGameState()
        {
            try
            {
                var gameRunningValue = _pluginManager?.GetPropertyValue("GameRunning");
                var currentGameValue = _pluginManager?.GetPropertyValue("CurrentGame");

                if (gameRunningValue == null || currentGameValue == null)
                {
                    SimHub.Logging.Current.Debug("[LMU WeatherPlugin] GameRunning or CurrentGame is null.");
                    return;
                }

                var isGameRunning = Convert.ToInt32(gameRunningValue) == 1;
                var currentGame = Convert.ToString(currentGameValue)?.ToUpperInvariant();

                SimHub.Logging.Current.Debug($"[LMU WeatherPlugin] GameRunning = {isGameRunning}, CurrentGame = {currentGame}");

                if (isGameRunning && currentGame == "LMU")
                {
                    if (!_timer.Enabled)
                    {
                        _timer.Start();
                        SimHub.Logging.Current.Info("[LMU WeatherPlugin] Game is LMU and running — starting weather updates.");
                    }
                }
                else
                {
                    if (_timer.Enabled)
                    {
                        _timer.Stop();
                        SimHub.Logging.Current.Info("[LMU WeatherPlugin] Game not LMU or not running — stopping weather updates.");
                    }
                }
            }
            catch (Exception ex)
            {
                SimHub.Logging.Current.Error($"[LMU WeatherPlugin] Error in game state check: {ex.Message}");
            }
        }





        private object GetWeatherData(string name)
        {
            SimHub.Logging.Current.Debug($"[LMU WeatherPlugin] Requested property: {name}");

            if (name == "CurrentSessionLengthMinutes")
            {
                return _currentSessionLengthMinutes;
            }
            if (name == "CurrentNodeDurationMinutes")
            {
                return _currentSessionLengthMinutes / 5.0;
            }

            if (name.StartsWith("CURRENTNODE_"))
            {
                return GetCurrentNodeMetric(name);
            }

            if (_weatherData == null)
            {
                SimHub.Logging.Current.Warn($"[LMU WeatherPlugin] Weather data is null when accessing: {name}");

                if (name.EndsWith("Str")) return "";
                else return 0.0;
            }

            var isStringValue = name.EndsWith("Str");
            var cleanName = isStringValue ? name.Substring(0, name.Length - 3) : name;

            var parts = cleanName.Split('_');
            if (parts.Length < 2)
            {
                if (isStringValue) return "";
                else return 0.0;
            }

            string node;
            string metric;

            if (parts[0] == "NODE" && parts.Length >= 3)
            {
                node = $"{parts[0]}_{parts[1]}"; // NODE_25
                metric = string.Join("_", parts, 2, parts.Length - 2);
            }
            else
            {
                node = parts[0]; // START or FINISH
                metric = string.Join("_", parts, 1, parts.Length - 1);
            }

            var nodeData = _weatherData?[node];
            var metricData = nodeData?[metric];

            if (metricData == null)
            {
                SimHub.Logging.Current.Warn($"[LMU WeatherPlugin] Metric '{metric}' not found under node '{node}'");

                if (isStringValue) return "";
                else return 0.0;
            }

            if (isStringValue)
            {
                return (string)metricData["stringValue"] ?? "";
            }
            else
            {
                return (double?)metricData["currentValue"] ?? 0.0;
            }
        }

        private object GetCurrentNodeMetric(string name)
        {
            if (_weatherData == null)
            {
                if (name.EndsWith("Str")) return "";
                else return 0.0;
            }

            if (string.IsNullOrEmpty(_currentNode))
            {
                if (name.EndsWith("Str")) return "";
                else return 0.0;
            }

            var isStringValue = name.EndsWith("Str");
            var cleanMetricName = isStringValue
                ? name.Substring("CURRENTNODE_".Length, name.Length - "CURRENTNODE_".Length - 3)
                : name.Substring("CURRENTNODE_".Length);

            var nodeData = _weatherData[_currentNode];
            var metricData = nodeData?[cleanMetricName];

            if (metricData == null)
            {
                if (isStringValue) return "";
                else return 0.0;
            }

            if (isStringValue)
            {
                return (string)metricData["stringValue"] ?? "";
            }
            else
            {
                return (double?)metricData["currentValue"] ?? 0.0;
            }
        }

        private async Task FetchWeather()
        {
            try
            {
                SimHub.Logging.Current.Info($"[LMU WeatherPlugin] Fetching weather for session: {_currentSession}");

                var urlWeather = $"http://localhost:6397/rest/sessions/weather/{_currentSession}";
                var responseWeather = await _httpClient.GetStringAsync(urlWeather);
                SimHub.Logging.Current.Debug($"[LMU WeatherPlugin] Weather response: {responseWeather}");
                _weatherData = JObject.Parse(responseWeather);

                var urlSessions = "http://localhost:6397/rest/sessions/GetSessionsInfoForEvent";
                var responseSessions = await _httpClient.GetStringAsync(urlSessions);
                SimHub.Logging.Current.Debug($"[LMU WeatherPlugin] Session info response: {responseSessions}");
                var sessionInfo = JObject.Parse(responseSessions);

                var scheduledSessions = sessionInfo["scheduledSessions"];
                if (scheduledSessions != null)
                {
                    foreach (var session in scheduledSessions)
                    {
                        var name = (string)session["name"] ?? "";
                        if (IsMatchingSessionName(name, _currentSession))
                        {
                            _currentSessionLengthMinutes = (int?)session["lengthTime"] ?? 0;
                            SimHub.Logging.Current.Info($"[LMU WeatherPlugin] Found session length: {_currentSessionLengthMinutes} minutes");
                            break;
                        }
                    }
                }
                else
                {
                    SimHub.Logging.Current.Warn("[LMU WeatherPlugin] scheduledSessions is null — session length not updated.");
                }
            }
            catch (Exception ex)
            {
                SimHub.Logging.Current.Error($"[LMU WeatherPlugin] Failed to fetch weather data: {ex.Message}\n{ex.StackTrace}");
            }
        }

        private bool IsMatchingSessionName(string apiSessionName, string internalSession)
        {
            if (string.IsNullOrEmpty(apiSessionName) || string.IsNullOrEmpty(internalSession))
                return false;

            var apiName = apiSessionName.ToUpperInvariant();
            var internalName = internalSession.ToUpperInvariant();

            if (internalName == "PRACTICE" && apiName.Contains("PRACTICE")) return true;
            if (internalName == "QUALIFY" && apiName.Contains("QUALIFY")) return true;
            if (internalName == "RACE" && apiName.Contains("RACE")) return true;

            return false;
        }

        public void DataUpdate(PluginManager pluginManager, ref GameData data)
        {
            try
            {
                if (pluginManager == null || data?.NewData == null)
                    return;

                var sessionName = data.NewData.SessionTypeName?.ToUpperInvariant() ?? "RACE";

                switch (sessionName)
                {
                    case "PRACTICE":
                    case "FREEPRACTICE":
                    case "PRACTICE1":
                    case "PRACTICE2":
                        _currentSession = "PRACTICE";
                        break;

                    case "QUALIFY":
                    case "QUALIFYING":
                    case "QUALIFY1":
                    case "QUALIFY2":
                        _currentSession = "QUALIFY";
                        break;

                    case "RACE":
                    case "RACEMAIN":
                        _currentSession = "RACE";
                        break;

                    default:
                        _currentSession = "RACE";
                        break;
                }

                // Ensure SessionTimeLeft is valid
                if (data.NewData.SessionTimeLeft != null)
                {
                    UpdateCurrentNode(data.NewData.SessionTimeLeft);
                }
            }
            catch (Exception ex)
            {
                SimHub.Logging.Current.Error($"[LMU WeatherPlugin] Error in DataUpdate: {ex.Message}");
            }
        }


        private void UpdateCurrentNode(TimeSpan sessionTimeLeft)
        {
            if (_currentSessionLengthMinutes <= 0)
            {
                _currentNode = "START";
                return;
            }

            double totalSessionSeconds = _currentSessionLengthMinutes * 60.0;
            double timeLeftSeconds = sessionTimeLeft.TotalSeconds;
            double elapsedSeconds = totalSessionSeconds - timeLeftSeconds;

            if (elapsedSeconds < 0) elapsedSeconds = 0;
            if (elapsedSeconds > totalSessionSeconds) elapsedSeconds = totalSessionSeconds;

            double progress = elapsedSeconds / totalSessionSeconds;

            if (progress < 0.25)
                _currentNode = "START";
            else if (progress < 0.5)
                _currentNode = "NODE_25";
            else if (progress < 0.75)
                _currentNode = "NODE_50";
            else if (progress < 1.0)
                _currentNode = "NODE_75";
            else
                _currentNode = "FINISH";
        }

        public Control GetWPFSettingsControl(PluginManager pluginManager)
        {
            return null;
        }

        public void End(PluginManager pluginManager)
        {
            _timer?.Stop();
            _timer?.Dispose();
            _gameMonitorTimer?.Stop();
            _gameMonitorTimer?.Dispose();
            _httpClient?.Dispose();
            SimHub.Logging.Current.Info("[LMU WeatherPlugin] Plugin has been stopped.");
        }

    }
}