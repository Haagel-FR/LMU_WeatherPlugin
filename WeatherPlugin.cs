using GameReaderCommon;
using Newtonsoft.Json.Linq;
using SimHub.Plugins;
using System.Net.Http;
using System.Threading.Tasks;
using System.Windows.Controls;
using System;

using System.Timers;

namespace LMU_WeatherPlugin
{
    [PluginDescription("LMU Weather Fetcher with Dynamic Node Properties")]
    [PluginAuthor("Giacomo Chiappe")]
    [PluginName("LMU_WeatherPlugin")]
    public class WeatherPlugin : IPlugin, IDataPlugin, IWPFSettings
    {
        private Timer _timer;
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

            // Timer to fetch updates
            _timer = new Timer(1000);
            _timer.Elapsed += async (s, e) => await FetchWeather();
            _timer.Start();
        }

        private void AttachDelegate(string name)
        {
            this.AttachDelegate(name, () => GetWeatherData(name));
        }

        private object GetWeatherData(string name)
        {
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
                var urlWeather = $"http://localhost:6397/rest/sessions/weather/{_currentSession}";
                var responseWeather = await _httpClient.GetStringAsync(urlWeather);
                _weatherData = JObject.Parse(responseWeather);

                var urlSessions = "http://localhost:6397/rest/sessions/GetSessionsInfoForEvent";
                var responseSessions = await _httpClient.GetStringAsync(urlSessions);
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
                            break;
                        }
                    }
                }
            }
            catch (Exception ex)
            {
                SimHub.Logging.Current.Error($"[LMU WeatherPlugin] Failed to fetch weather data: {ex.Message}");
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

            // Now directly pass TimeSpan!
            UpdateCurrentNode(data.NewData.SessionTimeLeft);
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
            _httpClient?.Dispose();
            SimHub.Logging.Current.Info("[LMU WeatherPlugin] Plugin has been stopped.");
        }
    }
}
